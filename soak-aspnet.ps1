# Runs the ASP.NET sample under ManagedDotnetGC and hammers it with the built-in load
# driver (which runs on the stock GC). M2 exit criterion: the server survives for hours
# with a flat working set and a ~zero error rate.
#
#   .\soak-aspnet.ps1 -Seconds 3600 -Workers 32
param(
    [int]$Seconds = 3600,
    [int]$Workers = 32,
    [int]$Port = 5211,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $SkipBuild) {
    dotnet publish $root\ManagedDotnetGC /p:SelfContained=true -r win-x64 -c Debug
    if ($LASTEXITCODE) { exit 1 }
    dotnet build $root\AspNetSample -c Release
    if ($LASTEXITCODE) { exit 1 }
}

$outDir = "$root\AspNetSample\bin\Release\net10.0\win-x64"
Copy-Item "$root\ManagedDotnetGC\bin\Debug\net10.0\win-x64\publish\*" $outDir -Force

$env:DOTNET_GCName = 'ManagedDotnetGC.dll'
$env:DOTNET_gcConservative = '0'
$env:DOTNET_gcServer = '0'

$serverLog = "$root\AspNetSample\server.log"
$serverErr = "$root\AspNetSample\server.err.log"
$server = Start-Process -FilePath "$outDir\AspNetSample.exe" -ArgumentList 'serve', $Port `
    -RedirectStandardOutput $serverLog -RedirectStandardError $serverErr -PassThru -NoNewWindow

# The load driver must run on the stock GC
Remove-Item Env:DOTNET_GCName

try {
    # Verify the custom GC is loaded: its GetTotalBytesInUse stub reports 0, the stock GC never does
    $stats = $null
    foreach ($attempt in 1..30) {
        try { $stats = Invoke-RestMethod "http://127.0.0.1:$Port/stats"; break } catch { Start-Sleep 1 }
    }

    if (-not $stats) {
        Write-Error "Server did not come up; see $serverLog / $serverErr"
    }

    if ($stats.gcTotalMemory -ne 0) {
        Write-Error "Server is running on the STOCK GC (GC.GetTotalMemory = $($stats.gcTotalMemory)) - aborting"
    }

    Write-Host "# ManagedDotnetGC confirmed loaded (GC.GetTotalMemory reports 0)"

    & "$outDir\AspNetSample.exe" load "http://127.0.0.1:$Port" $Seconds $Workers
    $clientExit = $LASTEXITCODE
}
finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}

Write-Host "# client exit code: $clientExit"
exit $clientExit
