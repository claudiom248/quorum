using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>
/// The filters of the admin lists of servers and tools (spec 010), one rule for all of them: <c>scope</c> is the kind; <c>tenant</c>
/// matches the owner or a tenant the item is shared with; <c>assistant</c> matches an item scoped to it or any item that is not
/// scoped to assistants (it applies to every assistant); <c>status</c> is the state of the server or the tool. Values of one filter
/// are alternatives; different filters all have to match. <c>type</c> means nothing for these lists and is refused, which is how a
/// client learns not to offer it.
/// </summary>
internal static class ScopeFilter
{
    private const string EscapeCharacter = LikePattern.EscapeCharacter;

    public static Error NotForTools { get; } =
        Error.Validation("list.invalid_filter", "The built-in tools list is filtered by status and search only.");

    /// <summary>Everything at once: scope, state and search on the members the list is made of.</summary>
    public static Result<IQueryable<T>> Apply<T, TState>(
        IQueryable<T> source,
        ListQuery query,
        Expression<Func<T, ToolScopeKind>> scope,
        Expression<Func<T, Guid>> owner,
        Expression<Func<T, List<Guid>>> tenantIds,
        Expression<Func<T, List<Guid>>> assistantIds,
        Expression<Func<T, TState>> state,
        params Expression<Func<T, string?>>[] searched)
        where TState : struct, Enum
    {
        var scoped = Scope(source, query, scope, owner, tenantIds, assistantIds);
        if (!scoped.IsSuccess)
        {
            return scoped;
        }

        var statused = Status(scoped.Value, query, state);
        return statused.IsSuccess ? Result<IQueryable<T>>.Success(Search(statused.Value, query, searched)) : statused;
    }
    /// <summary>
    /// Narrows <paramref name="source"/> by the scope filters; the caller applies <c>status</c> and the search on its own members,
    /// through <see cref="Status{TState}"/> and <see cref="Search{T}"/>.
    /// </summary>
    public static Result<IQueryable<T>> Scope<T>(
        IQueryable<T> source,
        ListQuery query,
        Expression<Func<T, ToolScopeKind>> scope,
        Expression<Func<T, Guid>> owner,
        Expression<Func<T, List<Guid>>> tenantIds,
        Expression<Func<T, List<Guid>>> assistantIds)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        if (query.Type.Count > 0)
        {
            return Error.Validation("list.invalid_filter", "Servers and tools have no type to filter by.");
        }

        if (!TryParse<ToolScopeKind>(query.Scope, "scope", out var kinds, out var error))
        {
            return error;
        }

        var tenants = query.Tenant.ToList();
        var assistants = query.Assistant.ToList();
        var row = Expression.Parameter(typeof(T), "row");

        Expression Body<TMember>(Expression<Func<T, TMember>> member) => new Rebase(member.Parameters[0], row).Visit(member.Body);

        if (kinds.Count > 0)
        {
            source = source.Where(Lambda<T>(row, Contains(kinds, Body(scope))));
        }

        if (tenants.Count > 0)
        {
            var ownerIsOne = Contains(tenants, Body(owner));
            var sharedWithOne = AnyIn(Body(tenantIds), tenants);
            source = source.Where(Lambda<T>(row, Expression.OrElse(ownerIsOne, sharedWithOne)));
        }

        if (assistants.Count > 0)
        {
            var notForAssistants = Expression.NotEqual(Body(scope), Expression.Constant(ToolScopeKind.Assistant));
            source = source.Where(Lambda<T>(row, Expression.OrElse(notForAssistants, AnyIn(Body(assistantIds), assistants))));
        }

        return Result<IQueryable<T>>.Success(source);
    }

    /// <summary>The <c>status</c> filter on a state member.</summary>
    public static Result<IQueryable<T>> Status<T, TState>(IQueryable<T> source, ListQuery query, Expression<Func<T, TState>> state)
        where TState : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(state);

        if (!TryParse<TState>(query.Status, "status", out var states, out var error))
        {
            return error;
        }

        if (states.Count == 0)
        {
            return Result<IQueryable<T>>.Success(source);
        }

        var row = Expression.Parameter(typeof(T), "row");
        var body = new Rebase(state.Parameters[0], row).Visit(state.Body);
        return Result<IQueryable<T>>.Success(source.Where(Lambda<T>(row, Contains(states, body))));
    }

    /// <summary>The search text matches any of the members, as text and not as a pattern.</summary>
    public static IQueryable<T> Search<T>(IQueryable<T> source, ListQuery query, params Expression<Func<T, string?>>[] members)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.Q) || members.Length == 0)
        {
            return source;
        }

        var pattern = LikePattern.Contains(query.Q);
        var row = Expression.Parameter(typeof(T), "row");
        var like = typeof(NpgsqlDbFunctionsExtensions).GetMethod(nameof(NpgsqlDbFunctionsExtensions.ILike), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

        Expression? any = null;
        foreach (var member in members)
        {
            var body = new Rebase(member.Parameters[0], row).Visit(member.Body);
            var test = Expression.AndAlso(
                Expression.NotEqual(body, Expression.Constant(null, typeof(string))),
                Expression.Call(like, Expression.Constant(EF.Functions), body, Expression.Constant(pattern), Expression.Constant(EscapeCharacter)));
            any = any is null ? test : Expression.OrElse(any, test);
        }

        return source.Where(Lambda<T>(row, any!));
    }

    private static Expression<Func<T, bool>> Lambda<T>(ParameterExpression row, Expression body) => Expression.Lambda<Func<T, bool>>(body, row);

    // values.Contains(member)
    private static MethodCallExpression Contains<TValue>(List<TValue> values, Expression member) =>
        Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(TValue)], Expression.Constant(values), member);

    // list.Any(item => values.Contains(item))
    private static MethodCallExpression AnyIn(Expression list, List<Guid> values)
    {
        var item = Expression.Parameter(typeof(Guid), "item");
        var test = Expression.Lambda<Func<Guid, bool>>(Contains(values, item), item);
        return Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [typeof(Guid)], list, test);
    }

    private static bool TryParse<TEnum>(IReadOnlyList<string> values, string filter, out List<TEnum> parsed, out Error error)
        where TEnum : struct, Enum
    {
        parsed = [];
        error = null!;
        foreach (var value in values)
        {
            // "To review" is how the UI says PendingReview; both are accepted so that a URL written either way works.
            var name = value.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (name.Equals("ToReview", StringComparison.OrdinalIgnoreCase))
            {
                name = nameof(ToolState.PendingReview);
            }

            if (!Enum.TryParse<TEnum>(name, ignoreCase: true, out var member) || !Enum.IsDefined(member) || int.TryParse(name, out _))
            {
                error = Error.Validation("list.invalid_filter", $"'{value}' is not a value of the {filter} filter: use {string.Join(", ", Enum.GetNames<TEnum>())}.");
                return false;
            }

            parsed.Add(member);
        }

        return true;
    }

    private sealed class Rebase(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
