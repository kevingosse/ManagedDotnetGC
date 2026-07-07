// CPU-sample aggregator for PerfView ETL traces (2026-07-07, mutator-side diff).
// Usage: EtlCpu <trace.etl> [--proc dotnet] [--pid N] [--top N] [--symdir <dir>]...
//        [--callers <substring>]
// Prints machine-wide process rollup, then for the target process: module rollup,
// top exclusive symbols, top inclusive symbols, per-thread rollup. With --callers,
// prints the caller chains of samples whose stack contains a matching frame.

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

string etlPath = args[0];
string procName = "dotnet";
int pid = 0, topN = 50;
var symDirs = new List<string>();
var matches = new List<string>();
string? callersOf = null;
string gcModule = "manageddotnetgc";

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--proc": procName = args[++i]; break;
        case "--pid": pid = int.Parse(args[++i]); break;
        case "--top": topN = int.Parse(args[++i]); break;
        case "--symdir": symDirs.Add(args[++i]); break;
        case "--callers": callersOf = args[++i]; break;
        case "--match": matches.Add(args[++i]); break;
        case "--gcmodule": gcModule = args[++i]; break;
        default: Console.Error.WriteLine($"unknown arg {args[i]}"); return 1;
    }
}

using var traceLog = TraceLog.OpenOrConvert(etlPath);
Console.WriteLine($"trace {Path.GetFileName(etlPath)}: {traceLog.SessionDuration.TotalSeconds:F1}s, {traceLog.NumberOfProcessors} cores, lost {traceLog.EventsLost} events");

// Machine-wide rollup: who used CPU during the trace (zero-page thread shows under Idle (0))
Console.WriteLine("\n== machine-wide CPU ms by process ==");
foreach (var p in traceLog.Processes.Where(p => p.CPUMSec > 0).OrderByDescending(p => p.CPUMSec).Take(12))
    Console.WriteLine($"{p.CPUMSec,10:F0}  {p.Name} ({p.ProcessID})");

var proc = pid != 0
    ? traceLog.Processes.First(p => p.ProcessID == pid)
    : traceLog.Processes.Where(p => string.Equals(p.Name, procName, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(p => p.CPUMSec).First();
Console.WriteLine($"\ntarget: {proc.Name} pid={proc.ProcessID} cpu={proc.CPUMSec:F0}ms");

// Pass 1: exclusive samples per module; collect module files for symbol lookup
var modSamples = new Dictionary<string, long>();
var modFiles = new Dictionary<string, TraceModuleFile>();
long total = 0;
foreach (var s in proc.EventsInProcess.ByEventType<SampledProfileTraceData>())
{
    total++;
    var cai = s.IntructionPointerCodeAddressIndex();
    var mf = traceLog.CodeAddresses.ModuleFile(cai);
    string mod = mf?.Name ?? "?";
    modSamples[mod] = modSamples.GetValueOrDefault(mod) + 1;
    if (mf != null && !modFiles.ContainsKey(mod)) modFiles[mod] = mf;
}
Console.WriteLine($"samples={total} (~{total} CPU ms)");

// Resolve native symbols for any module with >=0.2% of exclusive samples
var symPath = new SymbolPath("SRV*E:\\symbols*https://msdl.microsoft.com/download/symbols");
foreach (var d in symDirs) symPath = symPath.Add(d);
using var symReader = new SymbolReader(Console.Error, symPath.ToString());
symReader.SecurityCheck = _ => true;
foreach (var kv in modSamples.Where(kv => kv.Value >= Math.Max(5, total / 500)))
{
    if (!modFiles.TryGetValue(kv.Key, out var mf)) continue;
    try { traceLog.CodeAddresses.LookupSymbolsForModule(symReader, mf); }
    catch (Exception e) { Console.Error.WriteLine($"sym {kv.Key}: {e.Message}"); }
}

string NameOf(CodeAddressIndex cai)
{
    if (cai == CodeAddressIndex.Invalid) return "?!?";
    var mf = traceLog.CodeAddresses.ModuleFile(cai);
    var mi = traceLog.CodeAddresses.MethodIndex(cai);
    string mod = mf?.Name ?? "?";
    return mi != MethodIndex.Invalid
        ? $"{mod}!{traceLog.CodeAddresses.Methods.FullMethodName(mi)}"
        : $"{mod}!0x{traceLog.CodeAddresses.Address(cai):x}";
}

// Pass 2: exclusive + inclusive per symbol, thread rollup, optional caller chains
var excl = new Dictionary<string, long>();
var incl = new Dictionary<string, long>();
var byThread = new Dictionary<int, (long samples, Dictionary<string, long> mods)>();
var callerChains = new Dictionary<string, long>();
long callersMatched = 0;
var stackNames = new List<string>(64);

foreach (var s in proc.EventsInProcess.ByEventType<SampledProfileTraceData>())
{
    var ipCai = s.IntructionPointerCodeAddressIndex();
    string ipName = NameOf(ipCai);
    excl[ipName] = excl.GetValueOrDefault(ipName) + 1;

    var tid = s.ThreadID;
    if (!byThread.TryGetValue(tid, out var t)) { t = (0, new Dictionary<string, long>()); }
    string ipMod = ipName[..ipName.IndexOf('!')];
    t.mods[ipMod] = t.mods.GetValueOrDefault(ipMod) + 1;
    byThread[tid] = (t.samples + 1, t.mods);

    var csi = s.CallStackIndex();
    stackNames.Clear();
    var seen = new HashSet<string>();
    while (csi != CallStackIndex.Invalid)
    {
        var cai = traceLog.CallStacks.CodeAddressIndex(csi);
        string n = NameOf(cai);
        stackNames.Add(n);
        if (seen.Add(n)) incl[n] = incl.GetValueOrDefault(n) + 1;
        csi = traceLog.CallStacks.Caller(csi);
    }

    if (callersOf != null)
    {
        // stackNames[0] = leaf. Find deepest matching frame; record its caller chain
        int m = stackNames.FindIndex(n => n.Contains(callersOf, StringComparison.OrdinalIgnoreCase));
        if (m >= 0)
        {
            callersMatched++;
            string chain = string.Join(" <- ", stackNames.Skip(m).Take(6));
            callerChains[chain] = callerChains.GetValueOrDefault(chain) + 1;
        }
    }
}

Console.WriteLine("\n== module rollup (exclusive) ==");
foreach (var kv in modSamples.OrderByDescending(kv => kv.Value).Take(25))
    Console.WriteLine($"{kv.Value,8} {100.0 * kv.Value / total,6:F2}%  {kv.Key}");

Console.WriteLine($"\n== top {topN} exclusive ==");
foreach (var kv in excl.OrderByDescending(kv => kv.Value).Take(topN))
    Console.WriteLine($"{kv.Value,8} {100.0 * kv.Value / total,6:F2}%  {kv.Key}");

Console.WriteLine($"\n== top {topN} inclusive ==");
foreach (var kv in incl.OrderByDescending(kv => kv.Value).Take(topN))
    Console.WriteLine($"{kv.Value,8} {100.0 * kv.Value / total,6:F2}%  {kv.Key}");

Console.WriteLine("\n== threads (top 25 by samples; top-3 exclusive modules) ==");
foreach (var kv in byThread.OrderByDescending(kv => kv.Value.samples).Take(25))
{
    var mods = string.Join(", ", kv.Value.mods.OrderByDescending(m => m.Value).Take(3)
        .Select(m => $"{m.Key}:{100.0 * m.Value / kv.Value.samples:F0}%"));
    Console.WriteLine($"tid {kv.Key,6} {kv.Value.samples,8}  {mods}");
}

// GC-worker vs mutator split: a thread that spends >=half its samples in the GC
// module is a dedicated worker (stock: coreclr-module GC threads can't be told apart
// this way; use --match on gc_heap instead)
long workerSamples = 0, mutatorSamples = 0, mutatorGcDll = 0;
foreach (var kv in byThread)
{
    long gcSamps = kv.Value.mods.GetValueOrDefault(gcModule);
    if (kv.Value.samples >= 100 && gcSamps * 2 >= kv.Value.samples) workerSamples += kv.Value.samples;
    else { mutatorSamples += kv.Value.samples; mutatorGcDll += gcSamps; }
}
Console.WriteLine($"\n== thread classes ==");
Console.WriteLine($"gc-worker threads ({gcModule}>=50%): {workerSamples} samples ({100.0 * workerSamples / total:F2}%)");
Console.WriteLine($"mutator threads: {mutatorSamples} samples; {gcModule} exclusive on them: {mutatorGcDll} ({100.0 * mutatorGcDll / total:F2}%)");

foreach (var m in matches)
{
    Console.WriteLine($"\n== exclusive symbols matching '{m}' ==");
    long sum = 0;
    foreach (var kv in excl.Where(kv => kv.Key.Contains(m, StringComparison.OrdinalIgnoreCase)).OrderByDescending(kv => kv.Value))
    {
        sum += kv.Value;
        if (kv.Value >= 5) Console.WriteLine($"{kv.Value,8} {100.0 * kv.Value / total,6:F2}%  {kv.Key}");
    }
    Console.WriteLine($"{sum,8} {100.0 * sum / total,6:F2}%  TOTAL '{m}'");
}

if (callersOf != null)
{
    Console.WriteLine($"\n== caller chains containing '{callersOf}' ({callersMatched} samples) ==");
    foreach (var kv in callerChains.OrderByDescending(kv => kv.Value).Take(30))
        Console.WriteLine($"{kv.Value,8}  {kv.Key}");
}
return 0;
