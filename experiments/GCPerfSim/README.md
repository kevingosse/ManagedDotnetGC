# GCPerfSim (vendored)

`GCPerfSim.cs` and `Harness.cs` are vendored unmodified from
[dotnet/performance](https://github.com/dotnet/performance)
`src/benchmarks/gc/GCPerfSim` at commit `17f0d0f8ff4b8c5e4e0c0395d21309ac211b4048` (MIT).
The csproj is ours (single TFM, win-x64, workstation GC defaults — the GC under test is
selected with `DOTNET_GCName`).

This is the M3 workhorse: the standard allocation-mix simulator the .NET GC team uses,
driven under both the stock GC and ManagedDotnetGC. See `experiments/results/` for runs.
