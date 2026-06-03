// Repro for .NET 10 JIT regression on Linux
// https://github.com/dotnet/runtime/issues/123124
//
// The hot path:
//   List<EntityN>.AsQueryable().Where(...).OrderBy(...).Count()
//     → EnumerableQueryProvider.Execute<int>(expression)
//     → new EnumerableExecutor<int>(expr)    ← no cross-call cache
//     → Expression.Compile()                 ← new DynamicMethod each call
//     → JIT compiles it                      → GenericsHelpers lock contention
//
// Each parallel worker compiles expressions for a unique entity type.
// First use of each type triggers GenericsHelpers.ClassWithSlotAndModule.
// On .NET 10 this lock is significantly more contended than on .NET 9.

using System.Diagnostics;
using Repro;

const int Workers = 20;
const int Ops     = 20_000;

var queries = BenchmarkData.Queries; // typed lambdas, one per entity type

// Warm up the thread pool
ThreadPool.SetMinThreads(Workers, Workers);

Console.WriteLine($"[.NET {Environment.Version}] {queries.Length} types, {Workers} workers, {Ops} ops");
Console.Write("Running ... ");

var sw = Stopwatch.StartNew();

Parallel.For(0, Ops, new ParallelOptions { MaxDegreeOfParallelism = Workers }, i =>
    _ = queries[i % queries.Length](i % 10));

sw.Stop();
Console.WriteLine($"{sw.ElapsedMilliseconds} ms");
