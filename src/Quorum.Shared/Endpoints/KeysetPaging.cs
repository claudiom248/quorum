using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Quorum.Shared.Results;

namespace Quorum.Shared.Endpoints;

/// <summary>Keyset paging in name then id order, the order every admin list has.</summary>
public static class KeysetPaging
{
    /// <summary>Reads one page of <paramref name="source"/>; <paramref name="name"/> must not be null in the rows read.</summary>
    public static async Task<Result<Page<TItem>>> ToPageAsync<TEntity, TItem>(
        this IQueryable<TEntity> source,
        ListQuery query,
        string listKind,
        Expression<Func<TEntity, string>> name,
        Expression<Func<TEntity, Guid>> id,
        Func<TEntity, TItem> map,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(map);

        var decoded = ListCursor.Decode(listKind, query.Cursor);
        if (!decoded.IsSuccess)
        {
            return decoded.Error;
        }

        if (decoded.Value is { } after)
        {
            source = source.Where(After(name, id, after.Name, after.Id));
        }

        var rows = await source.OrderBy(name).ThenBy(id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var items = rows.Take(query.Limit).ToList();
        var cursor = rows.Count > query.Limit
            ? ListCursor.Encode(listKind, name.Compile()(items[^1]), id.Compile()(items[^1]))
            : null;

        return new Page<TItem>([.. items.Select(map)], cursor);
    }

    // name > @name OR (name = @name AND id > @id), compared by the database like the ORDER BY is.
    private static Expression<Func<TEntity, bool>> After<TEntity>(
        Expression<Func<TEntity, string>> name,
        Expression<Func<TEntity, Guid>> id,
        string afterName,
        Guid afterId)
    {
        var row = Expression.Parameter(typeof(TEntity), "row");
        var rowName = new ParameterReplacer(name.Parameters[0], row).Visit(name.Body);
        var rowId = new ParameterReplacer(id.Parameters[0], row).Visit(id.Body);

        var compareName = Expression.Call(rowName, typeof(string).GetMethod(nameof(string.CompareTo), [typeof(string)])!, Expression.Constant(afterName));
        var compareId = Expression.Call(rowId, typeof(Guid).GetMethod(nameof(Guid.CompareTo), [typeof(Guid)])!, Expression.Constant(afterId));
        var zero = Expression.Constant(0);

        var body = Expression.OrElse(
            Expression.GreaterThan(compareName, zero),
            Expression.AndAlso(Expression.Equal(compareName, zero), Expression.GreaterThan(compareId, zero)));
        return Expression.Lambda<Func<TEntity, bool>>(body, row);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
