using Spectre.Console;
using TestApp.TestFramework;
using TestApp.Tests;

var runner = new TestRunner();

var managedGc = ManagedDotnetGC.Api.GcApi.TryCreate();

if (managedGc == null)
{
    AnsiConsole.MarkupLine("[red]Failed to initialize GC API.[/]");
}

managedGc?.Test();

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
runner.RegisterTest(new DependentHandleTest());
runner.RegisterTest(new FinalizerTest());
runner.RegisterTest(new DeepCallStackTest());
runner.RegisterTest(new MixedAllocationPatternTest());
runner.RegisterTest(new FragmentationTest());
runner.RegisterTest(new ConcurrentAllocationTest());
runner.RegisterTest(new FrozenSegmentTest());

// Stress Tests
runner.RegisterTest(new StressTest());

// Run all tests
var success = runner.RunAll();

// Return appropriate exit code
return success ? 0 : 1;
