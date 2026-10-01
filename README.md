# .NET 10 Regression Repros

Minimal standalone benchmarks reproducing .NET 10 vs .NET 9 regressions, found while migrating a ~18 700-test MSTest suite from .NET 9 to .NET 10.

| Repro | Regression | Status |
|-------|------------|--------|
| [`linq-stub-pgo/`](linq-stub-pgo/) | Dynamic PGO over-inlining into `DynamicMethod` | **Open** — [#127203](https://github.com/dotnet/runtime/issues/127203), milestone 12.0.0 |
| [`generics-helpers-lock/`](generics-helpers-lock/) | `GenericsHelpers` lock contention under parallel LINQ | Fix shipped in 10.0.12 — [#123124](https://github.com/dotnet/runtime/issues/123124) still open |

---

# 1. Dynamic PGO Over-Inlining into DynamicMethod

**Status:** open, tracked in [dotnet/runtime#127203](https://github.com/dotnet/runtime/issues/127203) (milestone 12.0.0 — no servicing fix for 10.0.x yet).

An in-memory `IQueryable` stub, of the kind used to fake a database in unit tests. Single-threaded: the regression does not depend on core count, unlike repro 2.

## Results

GitHub Actions `ubuntu-latest` (4 vCPU), mean of rounds 1-4, two consecutive runs:

| Runtime | us/query | Factor |
|---------|----------|--------|
| .NET 9.0.20 | 874–890 | — |
| .NET 9.0.20 + `DOTNET_TieredPGO=0` | 857 | 0.98x |
| .NET 10.0.12 | 7 250–7 690 | **~8.5x** |
| .NET 10.0.12 + `DOTNET_TieredPGO=0` | 1 000–1 010 | 1.14x |

The flag leaves .NET 9 unchanged, so it is not a general speed-up — it specifically removes this regression.

Every push and pull request runs all four configurations; the numbers above come from [this run](https://github.com/korchak-aleksandr/net10-regression-repro/actions/runs/36846237059). Because the regression does not depend on core count, free 4-vCPU runners are enough to measure it.

## How to Run

```sh
cd linq-stub-pgo
dotnet build LinqStubPgo.csproj -c Release
dotnet bin/Release/net9.0/LinqStubPgo.dll
dotnet bin/Release/net10.0/LinqStubPgo.dll
DOTNET_TieredPGO=0 dotnet bin/Release/net10.0/LinqStubPgo.dll
```

## What Happens

Every query runs through `EnumerableQuery`, which `Expression.Compile()`s a fresh lambda into a `DynamicMethod`.

`DynamicMethod`s do not participate in tiering and are always compiled in FullOpts. On .NET 10 the JIT trusts the synthesized profile of such a method. The callees (`Queryable.Where`, `Queryable.SingleOrDefault`, the `Expression.Call` factories) are hot by then and carry Dynamic PGO data, so the inliner pulls hundreds of them into the lambda.

For the same `closure => Queryable.SingleOrDefault(Queryable.Where(src, p1), p2)` lambda (50 bytes of IL):

- net9: **202 bytes** of machine code, two `call`s inside.
- net10: **7 135 bytes**, reported as `71 inlinees with PGO data; 138 single block inlinees; 16 inlinees without PGO data`.

On the real test suite this showed up as a 3.5x slowdown that survived the 10.0.12 fix for repro 2, and that `DOTNET_TieredPGO=0` removed entirely.

---

# 2. GenericsHelpers Lock Contention under Parallel LINQ

**Status:** [PR #129592](https://github.com/dotnet/runtime/pull/129592) shipped in **10.0.12**. [dotnet/runtime#123124](https://github.com/dotnet/runtime/issues/123124) is still open. Our test suite was still 3.5x slower on 10.0.12 — but that remainder turned out to be repro 1 above, a different regression.

A throughput regression in parallel LINQ workloads on Linux.

## Background

Test suite: ~18 700 MSTest tests (not xUnit)

| Runtime | CI time | Factor |
|---------|---------|--------|
| net9.0  | ~7 min  | —      |
| net10.0 | ~40–45 min | **~6x slower** |

That's a ~6x slowdown with no changes to test code or logic — purely a runtime upgrade.
The slowdown made CI completely impractical for every MR on this monolith, so we've rolled back to .NET 9 for now.

This benchmark distills the regression to ~50 lines of code.

## How to Run

```sh
cd generics-helpers-lock
bash generate.sh          # generates BenchmarkData.cs (1000 entity types)
dotnet build Benchmark.csproj -c Release
dotnet run --project Benchmark.csproj -c Release --framework net9.0  --no-build
dotnet run --project Benchmark.csproj -c Release --framework net10.0 --no-build
```

**CI results — Kubernetes pod, 24 logical CPUs ([full logs](generics-helpers-lock/ci-logs/)):**

| Runtime | Time | Regression |
|---------|------|------------|
| net9.0  | 27 390 ms | — |
| net10.0 | 53 971 ms | **2x** |

> **Note: regression magnitude scales with CPU count.**
> The `GenericsHelpers` lock contention grows with the number of threads competing simultaneously.
> On a 24-vCPU Kubernetes pod (logs above) it is **2x**.
> On 32-core GitLab CI workers running the full test suite it reaches **~6x**.
> The benchmark prints logical CPU count in its output for easy comparison.

**Do not read the GitHub Actions runs of this benchmark as a measurement.** On 4 vCPU the effect is below run-to-run variance: two consecutive runs of the identical configuration gave 1.57x and 0.81x, the latter meaning .NET 10 came out faster. The workflow is kept as a build-and-run smoke test only. Measuring this regression needs a high-core-count machine — the Kubernetes logs above, or your own.

## What the Benchmark Does

Each of 20 parallel workers runs 20 000 iterations of:

```csharp
List<EntityN>.AsQueryable()
    .Where(e => e.Id >= threshold)
    .OrderBy(e => e.Name)
    .Count()
```

`AsQueryable()` creates a fresh `EnumerableQuery<EntityN>` on every call → new expression tree →
`EnumerableQueryProvider.Execute<int>()` creates a new `EnumerableExecutor<int>` → `Expression.Compile()` →
JIT compiles a new `DynamicMethod` → acquires the **GenericsHelpers** lock to resolve the generic type handle for `EntityN`.

With 1000 unique entity types × 20 parallel workers, this creates continuous lock contention throughout the run.

## Root Cause (dotnet-trace Analysis)

### Benchmark trace

`dotnet-sampled-thread-time` profile of the benchmark on Linux:

| Method | net9.0 | net10.0 |
|--------|--------|---------|
| `GenericsHelpers.Class(int,int)` | absent | **39.91% exclusive CPU** |
| `RuntimeMethodHandle.GetStubIfNeededWorker` | absent | 15.01% |
| `GenericsHelpers.ClassWithSlotAndModule` | absent | 1.51% |
| Actual LINQ / compile work | ~40% | largely absent |

### Production monolith trace

We also collected `dotnet-sampled-thread-time` traces from actual test suite runs on the monolith — once on net9 and once on net10, same machine, same tests. The pattern is the same:

| Method | net9.0 | net10.0 |
|--------|--------|---------|
| `GenericsHelpers.ClassWithSlotAndModule` | absent | **5.09%** |
| `LambdaCompiler.CreateDelegate()` | 1.02% | 2.03% |
| `DynamicResolver+DestroyScout.Finalize()` | 3.5% | 3.97% |
| `LowLevelLifoSemaphore.WaitForSignal` | 15.3% | 20.46% |
| `Monitor.Wait` | 11.91% | 18.28% |
| `Monitor.Enter_Slowpath` | absent | 0.18% (6 653 events) |

`GenericsHelpers.ClassWithSlotAndModule` is the dominant new entrant on net10, absent on net9.
`Monitor.Wait` and `LowLevelLifoSemaphore.WaitForSignal` both increase significantly — consistent with threads piling up waiting for the same lock.
`Monitor.Enter_Slowpath` appearing with 6 653 contention events is a direct fingerprint of lock contention that did not exist on net9.

**Why `ClassWithSlotAndModule` here vs `Class(int,int)` in the benchmark:**
the monolith uses compiled assembly types (loaded from disk), which go through `ClassWithSlotAndModule`.
The benchmark uses pre-compiled entity types generated by `generate.sh`, which go through `Class(int,int)`.
Same underlying `GenericsHelpers` lock — two different call sites.

## Connection to the Windows Fix

Windows PR [#126331](https://github.com/dotnet/runtime/pull/126331) fixed `UnwindInfoTable::AddToUnwindInfoTable` lock contention — not relevant on Linux.
The Linux bottleneck is in the `GenericsHelpers` family, a **different code path** in the same JIT parallelism regression.

---

# Repository Structure

```
linq-stub-pgo/          repro 1 — single-threaded, in-memory IQueryable stub
  Program.cs
  LinqStubPgo.csproj      net9.0 / net10.0
generics-helpers-lock/  repro 2 — 20 parallel workers x 20 000 ops
  BenchmarkProgram.cs
  Benchmark.csproj        net9.0 / net10.0
  generate.sh             generates BenchmarkData.cs (1000 entity types, not committed)
  ci-logs/                CI job logs from the 24-vCPU Kubernetes run
```

Each repro has its own workflow under `.github/workflows/` and runs on every push and pull request.
