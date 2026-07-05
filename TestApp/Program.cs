using ManagedDotnetGC.Api;
using Spectre.Console;
using TestApp.TestFramework;
using TestApp.Tests;

// Child-process entry points must be handled before anything else (they run under special
// runtime configurations, e.g. a tiny heap hard limit, and must avoid extra allocations).
if (args.Length > 0 && args[0] == HeapHardLimitOomTest.ChildArgument)
{
    return HeapHardLimitOomTest.RunChild();
}

if (args.Length > 0 && args[0] == TestApp.SoakRunner.Argument)
{
    return TestApp.SoakRunner.Run(args.Length > 1 && int.TryParse(args[1], out var soakSeconds) ? soakSeconds : 120);
}

// Features not implemented yet in ManagedDotnetGC. Tests gated on them are skipped unless
// explicitly requested with --feature <name> or --all-features. To start working on a feature,
// remove it from this list and watch its tests fail.
var pendingFeatures = new HashSet<GcFeature>
{
    GcFeature.CollectibleAssemblies,
    GcFeature.RefCountedHandles,
    GcFeature.FinalizationQueueRoots,
    GcFeature.FrozenDependentHandles,
    GcFeature.NewHandleTypes,
    GcFeature.SuppressFinalizeDrop,
    GcFeature.ApiSurface,
    GcFeature.LatencyMode,
    GcFeature.NoGCRegion,
    GcFeature.EventCounters,
    GcFeature.AllocationAccounting,
    GcFeature.MemoryInfo,
    GcFeature.GcEvents,
    GcFeature.GcInternals,
};

string? singleTest = null;
var featureFilter = new List<GcFeature>();
bool allFeatures = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--feature":
            if (i + 1 >= args.Length)
            {
                return Usage("--feature requires a value");
            }

            foreach (var part in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<GcFeature>(part, ignoreCase: true, out var feature))
                {
                    return Usage($"Unknown feature '{part}'. Valid features: {string.Join(", ", Enum.GetNames<GcFeature>())}");
                }

                featureFilter.Add(feature);
            }
            break;

        case "--all-features":
            allFeatures = true;
            break;

        default:
            if (args[i].StartsWith("--"))
            {
                return Usage($"Unknown argument '{args[i]}'");
            }

            singleTest = args[i];
            break;
    }
}

bool isCustomGcApiAvailable = GcApi.TryCreate() != null;

var runner = new TestRunner(pendingFeatures, isCustomGcApiAvailable);

runner.RegisterTest(new BasicAllocationTest());
runner.RegisterTest(new LargeObjectTest());
runner.RegisterTest(new ArrayVariantTest());
runner.RegisterTest(new EmptyObjectTest());
runner.RegisterTest(new ZeroLengthArrayTest());
runner.RegisterTest(new MultiDimensionalArrayTest());
runner.RegisterTest(new JaggedArrayTest());
runner.RegisterTest(new ArrayOfStructsTest());
runner.RegisterTest(new WeakReferenceTest());
runner.RegisterTest(new InteriorPointerTest());
runner.RegisterTest(new InteriorPointerObjectStartTest());
runner.RegisterTest(new StaticRootTest());
runner.RegisterTest(new ReferenceGraphTest());
runner.RegisterTest(new CircularReferenceTest());
runner.RegisterTest(new SelfReferencingObjectTest());
runner.RegisterTest(new MultipleCollectionTest());
runner.RegisterTest(new BoxingTest());
runner.RegisterTest(new StructWithReferencesTest());
runner.RegisterTest(new StringTest());
runner.RegisterTest(new NullReferenceTest());
runner.RegisterTest(new GCHandleTest());
runner.RegisterTest(new PinnedObjectTest());
runner.RegisterTest(new PohPinnedAllocTest());
runner.RegisterTest(new DependentHandleTest());
runner.RegisterTest(new DependentHandleResurrectionTest());
runner.RegisterTest(new FinalizerTest());
runner.RegisterTest(new CriticalFinalizerTest());
runner.RegisterTest(new FinalizerWeakReferenceTest());
runner.RegisterTest(new DeepCallStackTest());
runner.RegisterTest(new MixedAllocationPatternTest());
runner.RegisterTest(new FragmentationTest());
runner.RegisterTest(new ConcurrentAllocationTest());
runner.RegisterTest(new FrozenSegmentTest());
runner.RegisterTest(new SyncBlockCacheTest());
runner.RegisterTest(new StressTest());
runner.RegisterTest(new GcTriggerTest());
runner.RegisterTest(new MemoryFootprintTest());
runner.RegisterTest(new HeapHardLimitOomTest());
runner.RegisterTest(new GenerationApiTest());
runner.RegisterTest(new LatencyModeTest());
runner.RegisterTest(new NoGCRegionTest());
runner.RegisterTest(new MiscGcApiTest());
runner.RegisterTest(new EventCountersTest());
runner.RegisterTest(new WeakInteriorHandleTest());
runner.RegisterTest(new CrossReferenceHandleTest());
runner.RegisterTest(new CollectibleAssemblyTest());
runner.RegisterTest(new DynamicMethodTest());
runner.RegisterTest(new ComWrappersTest());
runner.RegisterTest(new PendingFinalizerRootsTest());
runner.RegisterTest(new FrozenDependentHandleTest());
runner.RegisterTest(new SuppressFinalizeSemanticsTest());
runner.RegisterTest(new AllocationAccountingTest());
runner.RegisterTest(new MemoryInfoTest());
runner.RegisterTest(new GcInternalsProbeTest());
runner.RegisterTest(new GcCallbackBracketTest());
runner.RegisterTest(new WriteBarrierParamsTest());

bool success;

if (singleTest != null)
{
    success = runner.RunSingle(singleTest);
}
else if (featureFilter.Count > 0)
{
    success = runner.RunFeatures(featureFilter);
}
else
{
    success = runner.RunAll(includePendingFeatures: allFeatures);
}

return success ? 0 : 1;

static int Usage(string error)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("Usage: TestApp.exe [[TestName]] [[--feature Name[[,Name...]]]] [[--all-features]]");
    AnsiConsole.MarkupLine("  TestName        Run a single test by name (bypasses the pending-feature gate)");
    AnsiConsole.MarkupLine("  --feature X     Run only the tests of feature X, even if pending");
    AnsiConsole.MarkupLine("  --all-features  Run every test, including pending features");
    return 1;
}
