# Real-world workload benchmark: OrchardCore CMS (added 2026-07-07, M9 aftermath).
#
# Drives a published OrchardCore Blog-recipe site (Razor + YesSql/SQLite + full CMS
# middleware pipeline — a real allocation profile, not a synthetic shape) with
# bombardier and appends rows to experiments/results/orchard-history.csv.
#
# Prereqs: app published to E:\git\oc-bench\publish with completed auto-setup
# (App_Data + SQLite db present — NEVER delete App_Data or the bench becomes a
# setup benchmark). See results/2026-07-07 orchard record for the setup story.
#
#   .\bench-orchard.ps1 -Label stock-h8 -ServerGC -HeapCount 8
#   .\bench-orchard.ps1 -Label m9.1 -GcDll <publish>\ManagedDotnetGC.dll
param(
    [string]$GcDll = "",
    [Parameter(Mandatory = $true)][string]$Label,
    [string]$Sha = "",
    [int]$Connections = 128,
    [int]$DurationS = 15,
    [int]$Iterations = 3,
    [switch]$ServerGC,
    [int]$HeapCount = 0,
    [string[]]$Endpoint = @(),    # subset; empty = all
    [string]$StatsDir = "",
    [string]$AppDir = 'E:\git\oc-bench\publish',
    [string]$AppDll = 'OcBench.dll',
    [string]$PostPath = '/blog/post-1'   # sample post created by the Blog recipe
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$bombardier = "$PSScriptRoot\tools\bombardier.exe"
$csv = "$PSScriptRoot\results\orchard-history.csv"
$base = 'http://127.0.0.1:9080'

if (-not $Sha) { $Sha = (git -C $repoRoot rev-parse --short HEAD).Trim() }

$endpoints = [ordered]@{
    'home' = "$base/"                # blog list: Razor + YesSql content queries
    'post' = "$base$PostPath"        # single content item render
}

$env:ASPNETCORE_URLS = $base
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:DOTNET_gcConservative = '0'
Remove-Item Env:DOTNET_GCHeapHardLimit, Env:DOTNET_GCStatsFile, Env:DOTNET_GCAllocShards, Env:DOTNET_GCHeapCount, Env:DOTNET_GCDynamicAdaptationMode, Env:DOTNET_GCgen0size -ErrorAction SilentlyContinue

if ($GcDll) {
    # Deploy under the loaded name (bench-techempower.ps1 scar: any other filename
    # silently benches a stale copy)
    foreach ($attempt in 1..20) {
        try { Copy-Item $GcDll (Join-Path $AppDir 'ManagedDotnetGC.dll') -Force -ErrorAction Stop; break }
        catch { if ($attempt -eq 20) { throw }; Start-Sleep -Milliseconds 500 }
    }
    $env:DOTNET_GCName = 'ManagedDotnetGC.dll'
    $env:DOTNET_gcServer = '0'
    $env:DOTNET_gcConcurrent = '1'
    $gcName = 'custom'
}
else {
    Remove-Item Env:DOTNET_GCName -ErrorAction SilentlyContinue
    $env:DOTNET_gcServer = if ($ServerGC) { '1' } else { '0' }
    $env:DOTNET_gcConcurrent = '1'
    $gcName = if ($ServerGC) { 'stock-svr' } else { 'stock-wks' }
}

if ($HeapCount -gt 0) {
    $env:DOTNET_GCHeapCount = '{0:x}' -f $HeapCount
    if (-not $GcDll) { $env:DOTNET_GCDynamicAdaptationMode = '0' }
    $gcName += "-h$HeapCount"
}
elseif ($ServerGC -and -not $GcDll) { $gcName += '-datas' }

if (-not (Test-Path $csv)) {
    'utc,sha,label,endpoint,gc,iter,conns,dur_s,rps,lat_mean_ms,lat_p50_ms,lat_p90_ms,lat_p99_ms,errors,peak_ws_mb,avg_ws_mb' | Set-Content $csv
}

foreach ($name in $endpoints.Keys) {
    if ($Endpoint.Count -gt 0 -and $Endpoint -notcontains $name) { continue }

    if ($StatsDir) {
        $env:DOTNET_GCStatsFile = Join-Path $StatsDir "gcstats-$Label-$name.csv"
    }
    $p = Start-Process dotnet -ArgumentList "$AppDir\$AppDll" -WorkingDirectory $AppDir -PassThru -RedirectStandardOutput "$env:TEMP\oc-app-$name.txt"
    try {
        # OrchardCore boots the shell + tenant pipeline: slower start than the TFB app
        foreach ($attempt in 1..120) {
            Start-Sleep -Milliseconds 500
            try { Invoke-WebRequest "$base/" -UseBasicParsing | Out-Null; break }
            catch { if ($attempt -eq 120) { throw "app did not start (see $env:TEMP\oc-app-$name.txt)" } }
        }

        # Warmup: Razor compile, YesSql/session caches, JIT — OrchardCore's first
        # requests are orders slower than steady state
        & $bombardier -c $Connections -d 15s --fasthttp -o json $endpoints[$name] | Out-Null

        for ($i = 1; $i -le $Iterations; $i++) {
            $bombOut = Join-Path $env:TEMP "bombardier-oc-$name-$i.json"
            $bp = Start-Process $bombardier -ArgumentList "-c $Connections -d ${DurationS}s --fasthttp -l -o json $($endpoints[$name])" -NoNewWindow -PassThru -RedirectStandardOutput $bombOut

            $peak = [long]0; $wsSum = [long]0; $wsN = 0
            while (-not $bp.HasExited) {
                try {
                    $p.Refresh()
                    if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 }
                    $wsSum += $p.WorkingSet64; $wsN++
                } catch {}
                Start-Sleep -Milliseconds 100
            }

            $json = (Get-Content $bombOut | Where-Object { $_ -match '^\{' } | Select-Object -Last 1) | ConvertFrom-Json
            $peakMb = if ($wsN -gt 0) { $peak / 1MB } else { -1 }
            $avgMb = if ($wsN -gt 0) { $wsSum / $wsN / 1MB } else { -1 }

            $r = $json.result
            $errors = [long]$r.others + [long]$r.req4xx + [long]$r.req5xx
            "{0},{1},{2},{3},{4},{5},{6},{7},{8:F0},{9:F2},{10:F2},{11:F2},{12:F2},{13},{14:F1},{15:F1}" -f `
                (Get-Date -AsUTC -Format s), $Sha, $Label, $name, $gcName, $i, $Connections, $DurationS,
                $r.rps.mean, ($r.latency.mean / 1000), ($r.latency.percentiles.'50' / 1000),
                ($r.latency.percentiles.'90' / 1000), ($r.latency.percentiles.'99' / 1000),
                $errors, $peakMb, $avgMb |
                Add-Content $csv

            "{0,-14} {1,-5} {2,-14} iter {3}: rps={4:F0} p50={5:F2}ms p99={6:F2}ms err={7} peak_ws={8:F0}MB" -f `
                $Label, $name, $gcName, $i, $r.rps.mean, ($r.latency.percentiles.'50' / 1000), ($r.latency.percentiles.'99' / 1000), $errors, $peakMb
        }
    }
    finally {
        Stop-Process $p -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
}

# Don't leak the GC env into the calling session
Remove-Item Env:DOTNET_GCName, Env:DOTNET_gcServer, Env:DOTNET_gcConcurrent, Env:DOTNET_GCHeapCount, Env:DOTNET_GCHeapHardLimit, Env:DOTNET_GCStatsFile, Env:DOTNET_GCgen0size, Env:DOTNET_GCDynamicAdaptationMode -ErrorAction SilentlyContinue
