# ManagedDotnetGC

A .NET garbage collector written in C#, compiled with NativeAOT and loaded into CoreCLR as a standalone GC. Educational project: **non-generational, non-compacting, stop-the-world mark & sweep by design**. The target is the latest version of .NET, on win-x64. 32-bit, ARM, and Android are permanently out of scope.

`docs/missing-features.md` is the roadmap: every known gap against the EE contract, with `runtime-file:line` references into the CoreCLR sources.

## Division of labor

This is an educational project — the point is that the user writes the GC.

- **`ManagedDotnetGC/` (the GC itself): the user writes the code.** Discuss design, investigate, point at runtime sources, review — but do not edit it. Parts may occasionally be delegated, and that will always be stated explicitly.
- **The tests (`TestApp/`, `ManagedDotnetGC.Tests/`) are managed by the agent**: writing, extending, and fixing tests is your job.

## Development process

- Always commit to a branch, never directly to `master`, and only commit when asked.
- When the user asks to push, push the branch and open a pull request against `master`.

## Testing strategy

- Whenever possible, a test must run on **both** the official GC and the custom GC. The stock-GC run is what validates that the test asserts real runtime behavior rather than an assumption.
- `ManagedDotnetGC.Api` (in-process side channel into the GC, obtained through `GC.GetConfigurationVariables()`) extends the range of testable scenarios, but use it **only when there is no other choice**: tests that need it (`RequiresCustomGcApi => true`) are skipped on the stock GC and lose that validation. Anything is game to avoid it, including reflection into runtime internals — `CrossReferenceHandleTest` calling `GCHandle.InternalAlloc` through reflection to create a handle type no public API exposes is the reference example of how far to go.
- Feature gating: every test declares a `GcFeature`. Unimplemented features are listed in `pendingFeatures` at the top of `TestApp/Program.cs` and their tests are skipped by default, so tests are written ahead of the implementation. The feature ↔ missing-features-item mapping is the *Test gate* column of the TL;DR table in `docs/missing-features.md`. Workflow: remove the enum value from `pendingFeatures`, watch the tests fail, implement.

## Commands

- `run-tests.cmd` — publishes the GC (Debug), builds TestApp, copies the DLL, runs the suite on the custom GC. Forwards `--feature X[,Y]`, `--all-features`, or a single test name (which bypasses the pending gate).
- Custom GC runs need `DOTNET_GCName=ManagedDotnetGC.dll` and `DOTNET_gcConservative=0`.
- Stock-GC validation run: build TestApp (`dotnet build .\TestApp -c Release`) and run `TestApp.exe` without `DOTNET_GCName`; use `--all-features` — every test, including pending-feature ones, must pass on the stock GC.
- Unit tests: `dotnet test .\ManagedDotnetGC.Tests`.
- CI (`.github/workflows/ci.yml`) runs the unit tests plus the no-argument suite on the custom GC, so the suite must stay green with no arguments.
