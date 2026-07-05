# A .NET garbage collector written in C#.

This is an educational project, not meant to evolve into a production-grade GC. The goal is to explore the inner mechanics of the .NET GC by building a new one from scratch.

At the current time, the GC is:

- **Non-generational** — the whole heap is collected every time.
- **Non-compacting** — objects never move.
- **Stop-the-world** — mark & sweep runs with the execution engine suspended, no concurrency.

Only the latest version of .NET is supported, and only on Windows x64. The GC is built with NativeAOT and loaded into the CoreCLR runtime as a native library.

**As an educational project, most of the code is written by hand. AI is involved only for writing tests, brainstorming the design, and occasionally writing isolated and well-specified functions.**

## Blog series

The development of this GC is documented on [minidump.net](https://minidump.net/tags/garbage-collection/):

- [Part 1:](https://minidump.net/2025-28-01-writing-a-net-gc-in-c-part-1/) Introduction and setting up the project
- [Part 2:](https://minidump.net/writing-a-net-gc-in-c-part-2/) Implementing a minimal GC
- [Part 3:](https://minidump.net/writing-a-net-gc-in-c-part-3/) Using the DAC to inspect the managed objects
- [Part 4:](https://minidump.net/writing-a-net-gc-in-c-part-4/) Walking the managed heap
- [Part 5:](https://minidump.net/writing-a-net-gc-in-c-part-5/) Decoding the GCDesc to find the references of a managed object
- [Part 6:](https://minidump.net/writing-a-net-gc-in-c-part-6/) Implementing the mark and sweep phases
- [Part 7:](https://minidump.net/writing-a-net-gc-in-c-part-7/) Marking handles
- [Part 8:](https://minidump.net/writing-a-net-gc-in-c-part-8/) Interior pointers
- [Part 9:](https://minidump.net/writing-a-net-gc-in-c-part-9/) Frozen segments and new allocation strategy
- [Part 10:](https://minidump.net/writing-a-net-gc-in-c-part-10/) Finalizers

## Current status

- Loads into .NET and runs real .NET 10 applications on win-x64.
- Mark & sweep
- Supports pinned objects implicitly (the memory is never moved so *everything* is pinned)
- Supports common types of handles, and interior pointers
- Supports finalization
- Forward-only: at the moment, the GC isn't capable of reusing memory after freeing an object, so the working set will keep increasing

## Trying it

Requirements: .NET 10 SDK, Windows x64.

```cmd
publish.cmd     :: builds the GC with NativeAOT and copies it next to TestApp
launch.cmd      :: runs the test suite against the custom GC
```

Or `run-tests.cmd` to do all of the above in one go.

To use the GC in your own application, copy the published `ManagedDotnetGC.dll` next to it and set:

```cmd
set DOTNET_GCName=ManagedDotnetGC.dll
```

## Repository layout

| Directory | Contents |
|---|---|
| `ManagedDotnetGC/` | The GC itself, published as a native library with NativeAOT |
| `TestApp/` | The behavioral test suite ([details](TestApp/README.md)) |
| `ManagedDotnetGC.Tests/` | NUnit unit tests for the GC's data structures |
| `ManagedDotnetGC.Api/` | In-process API to query GC internals from tests |
| `docs/` | [missing-features.md](docs/missing-features.md), the gap analysis against the EE contract |

## Tests

The test suite is designed to run on both the official GC and this one: the stock-GC run validates that the tests assert real runtime behavior. Tests for features that aren't implemented yet are written ahead of time and feature-gated, so the suite stays green while serving as an executable to-do list — see [TestApp/README.md](TestApp/README.md).
