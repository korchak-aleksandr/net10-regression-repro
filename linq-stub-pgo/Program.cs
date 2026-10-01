// Repro for https://github.com/dotnet/runtime/issues/127203
//
// An in-memory IQueryable stub, of the kind used to fake a database in unit tests.
// Every query goes through EnumerableQuery, which Expression.Compile()s a fresh lambda
// into a DynamicMethod. DynamicMethods are always compiled in FullOpts, and on .NET 10
// the JIT trusts their synthesized profile, so the inliner pulls in hundreds of
// already-hot Queryable.* methods.

using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;

var n = args.Length > 0 ? int.Parse(args[0]) : 2000;
var rows = Enumerable.Range(0, 300).Select(i => new Row { Id = i, OwnerId = i % 50, Name = "n" + i }).ToList();

// Two layers. `inner` is an IQueryable but not an EnumerableQuery, so the lambda compiled
// from the tree keeps calling Queryable.* instead of being rewritten to Enumerable.*.
// It then swaps itself for the real EnumerableQuery that does the work.
var inner = new SwapQueryable<Row>(rows, () => Expression.Constant(rows.AsQueryable()));
var table = new SwapQueryable<Row>(rows, () => Expression.Constant(inner, typeof(IQueryable<Row>)));

Console.WriteLine(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

for (var round = 0; round < 5; round++)
{
    var sw = Stopwatch.StartNew();
    long acc = 0;
    for (var i = 0; i < n; i++)
    {
        var owner = i % 50;
        acc += table.Where(r => r.OwnerId == owner).SingleOrDefault(r => r.Id == owner)?.Id ?? 0;
    }
    Console.WriteLine($"round {round}: {sw.Elapsed.TotalMilliseconds / n * 1000:F0} us/query (acc={acc})");
}

class Row { public int Id { get; set; } public int OwnerId { get; set; } public string? Name { get; set; } }

// An IQueryable that is its own provider: on execution it replaces itself in the
// expression tree with `target`, then lets EnumerableQuery run the result.
class SwapQueryable<T>(IEnumerable<T> rows, Func<Expression> target)
    : ExpressionVisitor, IOrderedQueryable<T>, IQueryProvider
{
    static readonly IQueryProvider Enumerable = Array.Empty<object>().AsQueryable().Provider;

    public Type ElementType => typeof(T);
    public Expression Expression => Expression.Constant(this, typeof(IQueryable<T>));
    public IQueryProvider Provider => this;
    public IEnumerator<T> GetEnumerator() => rows.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IQueryable CreateQuery(Expression e) => throw new NotSupportedException();
    public IQueryable<TElement> CreateQuery<TElement>(Expression e) => new Node<TElement>(this, e);
    public object? Execute(Expression e) => Enumerable.Execute(Visit(e));
    public TResult Execute<TResult>(Expression e) => Enumerable.Execute<TResult>(Visit(e));

    protected override Expression VisitConstant(ConstantExpression node) => node.Value == this ? target() : node;

    // A query built on top of this source, carrying the expression so far.
    class Node<TElement>(IQueryProvider provider, Expression expression) : IOrderedQueryable<TElement>
    {
        public Type ElementType => typeof(TElement);
        public Expression Expression => expression;
        public IQueryProvider Provider => provider;
        public IEnumerator<TElement> GetEnumerator() => provider.Execute<IEnumerable<TElement>>(expression).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
