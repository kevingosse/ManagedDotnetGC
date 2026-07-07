# ETW CPU-sampling capture for the TechEmpower web workload (added 2026-07-07).
#
# Mirrors bench-techempower.ps1's app/env setup, then collects a PerfView kernel
# CPU-sampling trace (elevated via gsudo) in the middle of a bombardier load window.
# Reports the bombardier RPS for the profiled window so every trace is anchored to
# its throughput.
#
#   .\profile-techempower.ps1 -Label stock-h8 -ServerGC -HeapCount 8 -OutDir E:\etl
#   .\profile-techempower.ps1 -Label custom -GcDll <publish>\ManagedDotnetGC.dll -OutDir E:\etl
param(
    [string]$GcDll = "",
    [Parameter(Mandatory = $true)][string]$Label,
    [string]$Endpoint = 'fortunes',
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Connections = 256,
    [int]$CollectSec = 25,
    [int]$LoadSec = 45,
    [switch]$ServerGC,
    [int]$HeapCount = 0
)

$ErrorActionPreference = 'Stop'
$appDir = 'E:\git\tfb\publish\mvc'
$bombardier = "$PSScriptRoot\tools\bombardier.exe"
$perfview = "$PSScriptRoot\tools\PerfView.exe"
$base = 'http://127.0.0.1:8080'
$urls = @{
    'fortunes' = "$base/fortunes"
    'queries'  = "$base/queries/20"
    'updates'  = "$base/updates/20"
}
$url = $urls[$Endpoint]
New-Item -ItemType Directory -Force $OutDir | Out-Null
$etl = Join-Path $OutDir "$Label-$Endpoint.etl"

# App environment (same as bench-techempower.ps1)
$env:ConnectionString = 'Server=localhost;Database=hello_world;User Id=benchmarkdbuser;Password=benchmarkdbpass;SSL Mode=Disable;Maximum Pool Size=18;Enlist=false;Max Auto Prepare=4;Multiplexing=true;Write Coalescing Buffer Threshold Bytes=1000;'
$env:Database = 'postgresql'
$env:ASPNETCORE_URLS = $base
$env:DOTNET_gcConservative = '0'
Remove-Item Env:DOTNET_GCHeapHardLimit, Env:DOTNET_GCStatsFile, Env:DOTNET_GCAllocShards, Env:DOTNET_GCHeapCount, Env:DOTNET_GCDynamicAdaptationMode, Env:DOTNET_GCgen0size -ErrorAction SilentlyContinue

if ($GcDll) {
    foreach ($attempt in 1..20) {
        try { Copy-Item $GcDll (Join-Path $appDir 'ManagedDotnetGC.dll') -Force -ErrorAction Stop; break }
        catch { if ($attempt -eq 20) { throw }; Start-Sleep -Milliseconds 500 }
    }
    $env:DOTNET_GCName = 'ManagedDotnetGC.dll'
    $env:DOTNET_gcServer = '0'
    $env:DOTNET_gcConcurrent = '1'
}
else {
    Remove-Item Env:DOTNET_GCName -ErrorAction SilentlyContinue
    $env:DOTNET_gcServer = if ($ServerGC) { '1' } else { '0' }
    $env:DOTNET_gcConcurrent = '1'
}
if ($HeapCount -gt 0) {
    $env:DOTNET_GCHeapCount = '{0:x}' -f $HeapCount
    if (-not $GcDll) { $env:DOTNET_GCDynamicAdaptationMode = '0' }
}

$p = Start-Process dotnet -ArgumentList "$appDir\Mvc.dll" -WorkingDirectory $appDir -PassThru -RedirectStandardOutput "$env:TEMP\tfb-app-profile.txt"

# The app has inherited the GC env; scrub it from the session NOW. gsudo's elevated
# host is itself a framework-dependent .NET app: with DOTNET_GCName still set it dies
# with 0x8007007E before ever launching PerfView (2026-07-07, scar #0's cousin).
Remove-Item Env:DOTNET_GCName, Env:DOTNET_gcServer, Env:DOTNET_gcConcurrent, Env:DOTNET_GCHeapCount, Env:DOTNET_GCDynamicAdaptationMode, Env:DOTNET_gcConservative -ErrorAction SilentlyContinue

try {
    foreach ($attempt in 1..60) {
        Start-Sleep -Milliseconds 500
        try { Invoke-WebRequest "$base/json" -UseBasicParsing | Out-Null; break }
        catch { if ($attempt -eq 60) { throw "app did not start" } }
    }

    & $bombardier -c $Connections -d 10s --fasthttp -o json $url | Out-Null

    $bombOut = Join-Path $env:TEMP "bombardier-profile-$Label.json"
    $bp = Start-Process $bombardier -ArgumentList "-c $Connections -d ${LoadSec}s --fasthttp -l -o json $url" -NoNewWindow -PassThru -RedirectStandardOutput $bombOut

    Start-Sleep -Seconds 5
    # Elevated machine-wide kernel CPU sampling; merged plain .etl for TraceEvent.
    # PerfView.exe is a GUI-subsystem exe: gsudo returns IMMEDIATELY, so we must poll
    # the PerfView process to completion (rundown at stop needs the app still alive).
    Remove-Item "$etl", "$etl.new", ($etl -replace '\.etl$', '.kernel.etl'), ($etl -replace '\.etl$', '.clrRundown.etl') -Force -ErrorAction SilentlyContinue
    gsudo $perfview collect /DataFile:"$etl" /AcceptEula /NoGui /LogFile:"$etl.pvlog" `
        /MaxCollectSec:$CollectSec /BufferSizeMB:1024 /CircularMB:4096 /Merge:true /Zip:false /NoNGenRundown
    $deadline = (Get-Date).AddMinutes(8)
    Start-Sleep -Seconds 5
    while (Get-Process PerfView* -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) { throw "PerfView still running after 8 min, see $etl.pvlog" }
        Start-Sleep -Seconds 2
    }
    if (-not (Test-Path $etl) -or (Get-Item $etl).Length -eq 0) { throw "PerfView produced no ETL at $etl, see $etl.pvlog" }

    $bp.WaitForExit()
    $json = (Get-Content $bombOut | Where-Object { $_ -match '^\{' } | Select-Object -Last 1) | ConvertFrom-Json
    $r = $json.result
    $errors = [long]$r.others + [long]$r.req4xx + [long]$r.req5xx
    "{0} {1} pid={2}: rps={3:F0} p50={4:F2}ms p99={5:F2}ms err={6} -> {7}" -f `
        $Label, $Endpoint, $p.Id, $r.rps.mean, ($r.latency.percentiles.'50' / 1000), ($r.latency.percentiles.'99' / 1000), $errors, $etl
}
finally {
    Stop-Process $p -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Remove-Item Env:DOTNET_GCName, Env:DOTNET_gcServer, Env:DOTNET_gcConcurrent, Env:DOTNET_GCHeapCount, Env:DOTNET_GCHeapHardLimit, Env:DOTNET_GCStatsFile, Env:DOTNET_GCgen0size, Env:DOTNET_GCDynamicAdaptationMode -ErrorAction SilentlyContinue
}
