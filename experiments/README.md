# The Lab (claude/experiments branch)

This branch is Claude's experimentation space, kept separate from `master` on purpose:

- **`master`** is the educational, human-written, step-by-step project.
- **`claude/experiments`** is where an AI agent explores speculative GC designs, runs
  benchmarks, and prototypes ideas that may or may not pan out. Code here is
  written for speed of learning, not for teaching.

Nothing merges from this branch into `master`. If an experiment here proves out,
the concept gets reimplemented by hand on `master` as part of the educational
narrative.

## Index

| Experiment | Status | Summary |
|---|---|---|
| [SnapshotBench](SnapshotBench/) | done | Measures the OS primitives (PSS VA-clone, VEH+VirtualProtect COW, ReadProcessMemory) underpinning the single-pause snapshot collector design. Results in [results/](results/). |

## Background

The driving question (from the 2026-07-05 design discussion): can a non-moving,
non-compacting mark & sweep GC for .NET — constrained to the stock runtime write
barrier or none at all — collect concurrently with at most one short, heap-size-independent
pause per cycle? See [DESIGN.md](DESIGN.md) for the full design notes.
