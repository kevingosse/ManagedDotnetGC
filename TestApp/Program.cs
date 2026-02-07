using TestApp.TestFramework;
using TestApp.Tests;

var runner = new TestRunner();

// Basic Functionality Tests
runner.RegisterTest(new BasicAllocationTest());
runner.RegisterTest(new LargeObjectTest());
runner.RegisterTest(new ArrayVariantTest());
runner.RegisterTest(new EmptyObjectTest());
runner.RegisterTest(new ZeroLengthArrayTest());
runner.RegisterTest(new MultiDimensionalArrayTest());
runner.RegisterTest(new JaggedArrayTest());
runner.RegisterTest(new ArrayOfStructsTest());

// GC Correctness Tests
runner.RegisterTest(new WeakReferenceTest());
runner.RegisterTest(new InteriorPointerTest());
runner.RegisterTest(new StaticRootTest());
runner.RegisterTest(new ReferenceGraphTest());
runner.RegisterTest(new CircularReferenceTest());
runner.RegisterTest(new SelfReferencingObjectTest());
runner.RegisterTest(new MultipleCollectionTest());

// Type System Tests
runner.RegisterTest(new BoxingTest());
runner.RegisterTest(new StructWithReferencesTest());
runner.RegisterTest(new StringTest());
runner.RegisterTest(new NullReferenceTest());

// GCHandle Tests
runner.RegisterTest(new GCHandleTest());
runner.RegisterTest(new PinnedObjectTest());

// Advanced Tests
runner.RegisterTest(new FinalizerTest());
runner.RegisterTest(new DeepCallStackTest());
runner.RegisterTest(new MixedAllocationPatternTest());
runner.RegisterTest(new FragmentationTest());
runner.RegisterTest(new ConcurrentAllocationTest());

// Stress Tests
runner.RegisterTest(new StressTest());

// Run all tests
var success = runner.RunAll();

// Return appropriate exit code
return success ? 0 : 1;
