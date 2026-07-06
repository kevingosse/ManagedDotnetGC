# Canonical GCPerfSim protocol for the per-step performance archive.
#
# Runs the fixed scenario set N times against the stock GC or a given ManagedDotnetGC.dll
# and appends raw rows to experiments/results/perf-history.csv — one archive, one row per
# iteration, so every milestone lands in the same file with the same protocol.
#
#   .\bench-gcperfsim.ps1 -Label stock
#   .\bench-gcperfsim.ps1 -Label m1-complete -Sha 3c25f87 -GcDll <path>\ManagedDotnetGC.dll
#
# Rules: Release NativeAOT GC builds only (Debug costs ~2.5x); note the machine state in
# perf-history.md if anything heavy ran concurrently.
param(
    [string]$GcDll = "",          # full path to a Release ManagedDotnetGC.dll; "" = stock GC
    [Parameter(Mandatory = $true)][string]$Label,
    [string]$Sha = "",
    [int]$Iterations = 3,
    [string[]]$Scenario = @(),    # subset of scenario names; empty = all
    # M7 fairness-matrix knobs (2026-07-06). They alter the `gc` column so rows stay
    # distinguishable: stock-svr, stock-svr-bgc, stock-svr-h8, custom-h32, ...
    [switch]$ServerGC,            # stock only: DOTNET_gcServer=1
    [switch]$Concurrent,          # stock only: DOTNET_gcConcurrent=1 (BGC)
    [int]$HeapCount = 0,          # GC threads/heaps; 0 = collector default. Decimal here;
                                  # the script converts to hex for DOTNET_GCHeapCount.
    [int]$HardLimitMB = 0,        # DOTNET_GCHeapHardLimit in MB; 0 = none. For the
                                  # "memory-lean" fairness rows (cap ours at stock-SVR's peak).
    [int]$AllocShards = 0         # custom only: DOTNET_GCAllocShards; 0 = collector default,
                                  # 1 = single supply lock (M7 sharded-supply A/B rows)
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$simDir = "$PSScriptRoot\GCPerfSim\bin\Release\net10.0\win-x64"
$exe = "$simDir\GCPerfSim.exe"
$csv = "$PSScriptRoot\results\perf-history.csv"

if (-not (Test-Path $exe)) {
    dotnet build $PSScriptRoot\GCPerfSim -c Release | Out-Null
}

if (-not $Sha) { $Sha = (git -C $repoRoot rev-parse --short HEAD).Trim() }

# The canonical scenarios. Do not edit lightly: changing them invalidates cross-step
# comparability of the whole archive. Add new scenarios instead.
#
# pinheavy (added 2026-07-06): the structural-win shape the plain 'pin' scenario misses —
# a rotating population of long-lived pinned survivors (10% of a 1 GB live set) amid
# churn, the async-socket-buffer pattern that blocks ephemeral compaction in a moving GC.
$scenarios = [ordered]@{
    'soh'      = '-tc 4 -tagb 20 -tlgb 0.5 -sohsi 50 -sohsr 100-4000 -tk time'
    'lohmix'   = '-tc 4 -tagb 20 -tlgb 0.5 -sohsi 50 -sohsr 100-4000 -lohar 50 -lohsr 100000-2000000 -lohsi 50 -tk time'
    'pin'      = '-tc 4 -tagb 20 -tlgb 0.5 -sohsi 50 -sohsr 100-4000 -sohpi 100 -tk time'
    'pinheavy' = '-tc 4 -tagb 20 -tlgb 1 -sohsi 50 -sohsr 100-4000 -sohpi 10 -tk time'
}

if ($GcDll) {
    # The previous run's dll can stay locked for a moment after process exit (AV scan)
    foreach ($attempt in 1..20) {
        try { Copy-Item $GcDll $simDir -Force -ErrorAction Stop; break }
        catch { if ($attempt -eq 20) { throw }; Start-Sleep -Milliseconds 500 }
    }

    $env:DOTNET_GCName = 'ManagedDotnetGC.dll'
    $gcName = 'custom'
}
else {
    Remove-Item Env:DOTNET_GCName -ErrorAction SilentlyContinue
    $gcName = if ($ServerGC) { 'stock-svr' } else { 'stock-wks' }
}

$env:DOTNET_gcServer = if ($ServerGC -and -not $GcDll) { '1' } else { '0' }
# Custom rows measure the collector's shipping default: since M6 stage 3 that honors
# stock gcConcurrent, default-on — set explicitly because GCPerfSim's own runtimeconfig
# pins System.GC.Concurrent=false. -Concurrent stays stock-only (BGC).
$env:DOTNET_gcConcurrent = if ($GcDll -or $Concurrent) { '1' } else { '0' }
$env:DOTNET_gcConservative = '0'

if ($Concurrent -and -not $GcDll) { $gcName += '-bgc' }

# Leftover knobs from a previous shell would silently skew results
Remove-Item Env:DOTNET_GCgen0size, Env:DOTNET_GCStatsFile, Env:DOTNET_GCHeapHardLimit, Env:DOTNET_GCConcurrentCycles, Env:DOTNET_GCConcurrentSweep, Env:DOTNET_GCDynamicAdaptationMode, Env:DOTNET_GCWindowStash, Env:DOTNET_GCNtZero, Env:DOTNET_GCCardPreDrain, Env:DOTNET_GCAllocShards -ErrorAction SilentlyContinue

if ($AllocShards -gt 0) {
    $env:DOTNET_GCAllocShards = '{0:x}' -f $AllocShards   # runtime config ints parse as HEX
    $gcName += "-shards$AllocShards"
}

if ($HeapCount -gt 0) {
    $env:DOTNET_GCHeapCount = '{0:x}' -f $HeapCount   # runtime config ints parse as HEX

    if (-not $GcDll) {
        # A fixed heap count must mean a fixed heap count: pin DATAS off so the row
        # measures classic N-heap Server GC, not adaptation seeded at N
        $env:DOTNET_GCDynamicAdaptationMode = '0'
    }

    $gcName += "-h$HeapCount"
}
else {
    Remove-Item Env:DOTNET_GCHeapCount -ErrorAction SilentlyContinue

    if ($ServerGC -and -not $GcDll) {
        # Bare gcServer=1 on .NET 9+ is NOT a fixed heap-per-core config: DATAS is on
        # by default and adapts the heap count to the workload (discovered 2026-07-06
        # — it is why bare-server rows ran ~1.2 GB with high-variance walls; every
        # archive row labeled plain stock-svr is really this). Classic heap-per-core
        # measurements need -HeapCount 32, which pins adaptation off above.
        $gcName += '-datas'
    }
}

if ($HardLimitMB -gt 0) {
    $env:DOTNET_GCHeapHardLimit = '{0:x}' -f ([long]$HardLimitMB * 1MB)
    $gcName += "-cap$($HardLimitMB)m"
}

if (-not (Test-Path $csv)) {
    'utc,sha,label,scenario,gc,iter,wall_s,sim_s,peak_ws_mb,gc_counts,final_heap_mb,avg_ws_mb' | Set-Content $csv
}
elseif ((Get-Content $csv -TotalCount 1) -notmatch 'avg_ws_mb') {
    # avg_ws_mb added 2026-07-06 (memory is half the SVR-comparison story); older rows
    # simply lack the trailing column
    $lines = Get-Content $csv
    $lines[0] += ',avg_ws_mb'
    Set-Content $csv $lines
}

foreach ($name in $scenarios.Keys) {
    if ($Scenario.Count -gt 0 -and $Scenario -notcontains $name) { continue }

    $walls = @()

    for ($i = 1; $i -le $Iterations; $i++) {
        $out = Join-Path $env:TEMP "gcperfsim-$name-$i.txt"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process -FilePath $exe -ArgumentList $scenarios[$name] -NoNewWindow -PassThru -RedirectStandardOutput $out

        $peak = 0
        $wsSum = [long]0
        $wsSamples = 0
        while (-not $p.HasExited) {
            try {
                $p.Refresh()
                if ($p.PeakWorkingSet64 -gt $peak) { $peak = $p.PeakWorkingSet64 }
                $wsSum += $p.WorkingSet64
                $wsSamples++
            } catch {}
            Start-Sleep -Milliseconds 50
        }

        $sw.Stop()

        if ($p.ExitCode -ne 0) { throw "GCPerfSim failed ($Label/$name iter $i): exit $($p.ExitCode)" }

        $text = Get-Content $out -Raw
        $simS = if ($text -match 'seconds_taken: ([\d.]+)') { [double]$Matches[1] } else { -1 }
        $counts = if ($text -match 'collection_counts: \[([\d, ]+)\]') { ($Matches[1] -replace '[ ,]+', '/') } else { '' }
        $heapMb = if ($text -match 'final_heap_size_bytes: (\d+)') { [long]$Matches[1] / 1MB } else { -1 }

        $wall = [math]::Round($sw.Elapsed.TotalSeconds, 3)
        $walls += $wall

        $avgWs = if ($wsSamples -gt 0) { $wsSum / $wsSamples / 1MB } else { -1 }

        "{0},{1},{2},{3},{4},{5},{6},{7},{8:F1},{9},{10:F1},{11:F1}" -f `
            (Get-Date -AsUTC -Format s), $Sha, $Label, $name, $gcName, $i, $wall, $simS, ($peak / 1MB), $counts, $heapMb, $avgWs |
            Add-Content $csv
    }

    $median = ($walls | Sort-Object)[[int](($walls.Count - 1) / 2)]
    "{0,-14} {1,-7} median_wall_s={2,-8} iters=({3})" -f $Label, $name, $median, ($walls -join ' ')
}
