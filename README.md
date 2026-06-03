# .NET 10 JIT Regression Repro — GenericsHelpers Lock Contention on Linux

Minimal standalone benchmark reproducing a **~2x throughput regression** on .NET 10 vs .NET 9 in parallel LINQ workloads on Linux.

Tracked in: [dotnet/runtime#123124](https://github.com/dotnet/runtime/issues/123124)

---

## Background

Verified in a production monolith (2682 MSTest tests, Linux/Kubernetes):

| Runtime | CI time | Factor |
|---------|---------|--------|
| net9.0  | 1m 47s  | —      |
| net10.0 | 12m 51s | **7.2x slower** |

This benchmark distills the regression to ~50 lines of code.

---

## How to Run

```sh
bash generate.sh          # generates BenchmarkData.cs (1000 entity types)
dotnet build Benchmark.csproj -c Release
dotnet run --project Benchmark.csproj -c Release --framework net9.0  --no-build
dotnet run --project Benchmark.csproj -c Release --framework net10.0 --no-build
```

**CI results (GitHub Actions / Ubuntu):**

| Runtime | Time    | Regression |
|---------|---------|------------|
| net9.0  | ~27 390 ms | —       |
| net10.0 | ~53 971 ms | **2x** |

---

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

---

## Root Cause (dotnet-trace Analysis)

`dotnet-sampled-thread-time` profile of the benchmark on Linux:

| Method | net9.0 | net10.0 |
|--------|--------|---------|
| `GenericsHelpers.Class(int,int)` | absent | **39.91% exclusive CPU** |
| `RuntimeMethodHandle.GetStubIfNeededWorker` | absent | 15.01% |
| `GenericsHelpers.ClassWithSlotAndModule` | absent | 1.51% |
| Actual LINQ / compile work | ~40% | largely absent |

Both `GenericsHelpers.Class` and `ClassWithSlotAndModule` are JIT helpers for generic type dictionary lookups.
On net9 neither appears in the top-20 methods. On net10 they dominate — the lock is significantly more contended.

- `ClassWithSlotAndModule` is the dominant path in the production monolith trace (compiled assembly types).
- `Class(int,int)` is the dominant path in this benchmark (pre-compiled entity types via `generate.sh`).

Same underlying mechanism, two manifestations.

---

## Connection to the Windows Fix

Windows PR [#126331](https://github.com/dotnet/runtime/pull/126331) fixed `UnwindInfoTable::AddToUnwindInfoTable` lock contention — not relevant on Linux.
The Linux bottleneck is in the `GenericsHelpers` family, a **different code path** in the same JIT parallelism regression.

---

## Repository Structure

| File | Description |
|------|-------------|
| `generate.sh` | Generates `BenchmarkData.cs` — 1000 entity classes + one typed LINQ lambda per type |
| `BenchmarkProgram.cs` | Entry point — runs 20 parallel workers × 20 000 ops |
| `Benchmark.csproj` | Targets `net9.0` and `net10.0` |
| `BenchmarkData.cs` | Auto-generated, not committed |
