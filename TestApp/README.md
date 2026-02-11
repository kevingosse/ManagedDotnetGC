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
2. Inherit from `TestBase`
3. Implement the `Run()` method
4. Register the test in `TestHarnessProgram.cs`

Example:

```csharp
using TestApp.TestFramework;

namespace TestApp.Tests;

public class MyNewTest : TestBase
{
    public MyNewTest()
        : base("My Test Name", "Description of what this test does")
    {
    }

    public override bool Run()
    {
        // Test implementation
        // Return true if test passes, false otherwise
        return true;
    }
}
```

Then register it in `TestHarnessProgram.cs`:

```csharp
runner.RegisterTest(new MyNewTest());
```

## Exit Codes

- `0` - All tests passed
- `1` - One or more tests failed

## Environment Variables

The tests use the following environment variables:

- `DOTNET_GCName=ManagedDotnetGC.dll` - Specifies the custom GC to use
- `DOTNET_gcConservative=0` - Disables conservative GC mode
