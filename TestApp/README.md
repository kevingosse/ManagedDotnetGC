# ManagedDotnetGC Tests

This project provides comprehensive testing for the custom .NET garbage collector.

## Running Tests

### Option 1: Using the run-tests script (Recommended)

From the repository root:

```cmd
run-tests.cmd
```

This script will:
1. Publish the ManagedDotnetGC project
2. Build the TestApp
3. Copy the GC DLL to the TestApp directory
4. Run all tests
5. Return exit code 0 if all tests pass, 1 if any fail

### Option 2: Manual steps

1. Publish the GC:
   ```cmd
   publish.cmd
   ```

2. Launch the tests:
   ```cmd
   launch.cmd
   ```

## Adding New Tests

To add a new test:

1. Create a new class in the `Tests/` directory
2. Inherit from `TestBase` and declare the `GcFeature` the test exercises
3. Implement the `Run()` method (throw with a descriptive message on failure)
4. Register the test in `Program.cs`

Example:

```csharp
using TestApp.TestFramework;

namespace TestApp.Tests;

public class MyNewTest() : TestBase("My Test Name", GcFeature.Marking)
{
    public override void Run()
    {
        // Test implementation; throw to fail
    }
}
```

Then register it in `Program.cs`:

```csharp
runner.RegisterTest(new MyNewTest());
```

Tests that need the custom GC API (`ManagedDotnetGC.Api`) override `RequiresCustomGcApi => true`;
they are reported as skipped when running on the stock GC.

## Feature gating

Every test declares a `GcFeature`. Features listed in `PendingFeatures` (top of `Program.cs`) are
not implemented in ManagedDotnetGC yet: their tests are skipped by default so the suite stays green
while features are developed. The mapping between features and `docs/missing-features.md` items is
in that document's TL;DR table (*Test gate* column).

- `TestApp.exe` — run everything except pending features (what CI does)
- `TestApp.exe --feature CollectibleAssemblies` — run only that feature's tests, even if pending
- `TestApp.exe --all-features` — run everything, including pending features
- `TestApp.exe "Test Name"` — run a single test by name (bypasses the pending gate)

`run-tests.cmd` forwards `--feature` and `--all-features`. To start working on a feature, remove its
enum value from `PendingFeatures` and watch its tests fail until the feature is implemented.

## Exit Codes

- `0` - All tests passed (skipped tests don't count as failures)
- `1` - One or more tests failed

## Environment Variables

The tests use the following environment variables:

- `DOTNET_GCName=ManagedDotnetGC.dll` - Specifies the custom GC to use
- `DOTNET_gcConservative=0` - Disables conservative GC mode
