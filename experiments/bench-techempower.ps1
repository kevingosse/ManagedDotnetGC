# TechEmpower-derived web workload benchmark (added 2026-07-07).
#
# Runs the TFB aspnetcore/Mvc app (EF Core + Npgsql + Razor: fortunes/queries/updates —
# the realistic allocation-heavy endpoints, NOT plaintext/json) on Windows against the
# tfb-database Postgres container, drives it with bombardier, and appends rows to
# experiments/results/techempower-history.csv.
#
# Prereqs (see results/2026-07-07 record for setup):
#   - docker container 'tfb-database' running (postgres:16 + TFB create-postgres.sql)
#   - app published to E:\git\tfb\publish\mvc (dotnet publish src\Mvc -c Release -r win-x64 --self-contained false)
#   - experiments\tools\bombardier.exe
#
#   .\bench-techempower.ps1 -Label stock -ServerGC
#   .\bench-techempower.ps1 -Label m7 -GcDll <path>\ManagedDotnetGC.dll
param(
    [string]$GcDll = "",
    [Parameter(Mandatory = $true)][string]$Label,
    [string]$Sha = "",
    [int]$Connections = 256,
    [int]$DurationS = 15,
    [int]$Iterations = 3,
    [switch]$ServerGC,
    [int]$HeapCount = 0,
    [string[]]$Endpoint = @(),    # subset; empty = all
    [string]$StatsDir = "",       # custom GC only: write DOTNET_GCStatsFile per endpoint
    [int]$Gen0MB = 0              # DOTNET_GCgen0size override (0 = unset)
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$appDir = 'E:\git\tfb\publish\mvc'
$bombardier = "$PSScriptRoot\tools\bombardier.exe"
$csv = "$PSScriptRoot\results\techempower-history.csv"
$base = 'http://127.0.0.1:8080'

if (-not $Sha) { $Sha = (git -C $repoRoot rev-parse --short HEAD).Trim() }

$endpoints = [ordered]@{
    'fortunes' = "$base/fortunes"       # Razor render + 12-row query: string/HTML churn
    'queries'  = "$base/queries/20"     # 20 random-row queries per request, JSON out
    'updates'  = "$base/updates/20"     # 20 queries + 20 updates per request
}

# App environment
$env:ConnectionString = 'Server=localhost;Database=hello_world;User Id=benchmarkdbuser;Password=benchmarkdbpass;SSL Mode=Disable;Maximum Pool Size=18;Enlist=false;Max Auto Prepare=4;Multiplexing=true;Write Coalescing Buffer Threshold Bytes=1000;'
$env:Database = 'postgresql'
$env:ASPNETCORE_URLS = $base
$env:DOTNET_gcConservative = '0'
Remove-Item Env:DOTNET_GCHeapHardLimit, Env:DOTNET_GCStatsFile, Env:DOTNET_GCAllocShards, Env:DOTNET_GCHeapCount, Env:DOTNET_GCDynamicAdaptationMode, Env:DOTNET_GCgen0size -ErrorAction SilentlyContinue
if ($Gen0MB -gt 0) { $env:DOTNET_GCgen0size = '{0:x}' -f ($Gen0MB * 1MB) }

if ($GcDll) {
    # Always deploy under the loaded name: a -GcDll not literally named
    # ManagedDotnetGC.dll otherwise lands beside a stale copy that silently wins
    # (2026-07-07: three "A/B" runs benched the same old dll)
    foreach ($attempt in 1..20) {
        try { Copy-Item $GcDll (Join-Path $appDir 'ManagedDotnetGC.dll') -Force -ErrorAction Stop; break }
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

    # Fresh app process per endpoint so working-set numbers are per-endpoint
    if ($StatsDir) {
        $env:DOTNET_GCStatsFile = Join-Path $StatsDir "gcstats-$Label-$name.csv"
    }
    $p = Start-Process dotnet -ArgumentList "$appDir\Mvc.dll" -WorkingDirectory $appDir -PassThru -RedirectStandardOutput "$env:TEMP\tfb-app-$name.txt"
    try {
        foreach ($attempt in 1..60) {
            Start-Sleep -Milliseconds 500
            try { Invoke-WebRequest "$base/json" -UseBasicParsing | Out-Null; break }
            catch { if ($attempt -eq 60) { throw "app did not start" } }
        }

        # Warmup: JIT + connection pool + touch the endpoint itself
        & $bombardier -c $Connections -d 10s --fasthttp -o json $endpoints[$name] | Out-Null

        for ($i = 1; $i -le $Iterations; $i++) {
            # bombardier runs as a child process while we sample the app's working set inline
            $bombOut = Join-Path $env:TEMP "bombardier-$name-$i.json"
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

            "{0,-14} {1,-9} {2,-14} iter {3}: rps={4:F0} p50={5:F2}ms p99={6:F2}ms err={7} peak_ws={8:F0}MB" -f `
                $Label, $name, $gcName, $i, $r.rps.mean, ($r.latency.percentiles.'50' / 1000), ($r.latency.percentiles.'99' / 1000), $errors, $peakMb
        }
    }
    finally {
        Stop-Process $p -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
}

# Don't leak the GC env into the calling session (see bench-gcperfsim.ps1: a leftover
# DOTNET_GCName kills the next dotnet publish with 0x8007007E)
Remove-Item Env:DOTNET_GCName, Env:DOTNET_gcServer, Env:DOTNET_gcConcurrent, Env:DOTNET_GCHeapCount, Env:DOTNET_GCHeapHardLimit, Env:DOTNET_GCStatsFile, Env:DOTNET_GCgen0size, Env:DOTNET_GCDynamicAdaptationMode -ErrorAction SilentlyContinue
