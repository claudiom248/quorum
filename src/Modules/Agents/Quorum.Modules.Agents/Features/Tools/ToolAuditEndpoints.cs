using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>
/// Reads the audit log of tool calls. Read-only by design: there is no route to change or delete an entry; old ones leave only
/// through the retention purge (D-13).
/// </summary>
internal sealed class ToolAuditEndpoints : IEndpointGroup
{
    internal const int DefaultPageSize = 50;
    internal const int MaxPageSize = 200;

    public void Map(IEndpointRouteBuilder app)
    {
        var audit = app.MapGroup("/api/tools/audit")
            .WithTags("Tools")
            .RequireAuthorization(QuorumPolicies.ToolsManage);

        audit.MapGet("/", ListAsync).WithName("ListToolAudit");
    }

    private static async Task<Results<Ok<ToolAuditPage>, ValidationProblem>> ListAsync(
        [AsParameters] ToolAuditQuery query,
        AgentsDbContext db,
        CancellationToken cancellationToken)
    {
        var errors = Validate(query, out var outcome, out var from, out var to);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var entries = db.ToolAuditEntries.AsNoTracking().AsQueryable();
        if (from is { } start)
        {
            entries = entries.Where(e => e.StartedAt >= start);
        }

        if (to is { } end)
        {
            entries = entries.Where(e => e.StartedAt <= end);
        }

        if (!string.IsNullOrWhiteSpace(query.Tool))
        {
            // The name the model saw (builtin__calculator, files__read) or the tool's own name.
            // Compared column by column, never on a concatenation, so the index on (TenantId, ToolName) stays usable.
            var tool = query.Tool.Trim();
            var split = tool.IndexOf("__", StringComparison.Ordinal);
            if (split > 0)
            {
                var origin = tool[..split];
                var name = tool[(split + 2)..];
                var builtin = origin == "builtin";
                entries = entries.Where(e => e.ToolName == tool || (e.ToolName == name && (builtin ? e.ServerName == null : e.ServerName == origin)));
            }
            else
            {
                entries = entries.Where(e => e.ToolName == tool);
            }
        }

        if (query.UserId is { } userId)
        {
            entries = entries.Where(e => e.UserId == userId);
        }

        if (outcome is { } wanted)
        {
            entries = entries.Where(e => e.Outcome == wanted);
        }

        var total = await entries.CountAsync(cancellationToken);

        // A product of two ints wraps to a negative offset for a huge page, which the database rejects: past int range there is nothing to skip to.
        var offset = (long)(query.Page - 1) * query.PageSize;
        var page = offset > int.MaxValue
            ? []
            : await entries
                .OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.Id)
                .Skip((int)offset)
                .Take(query.PageSize)
                .ToListAsync(cancellationToken);

        return TypedResults.Ok(new ToolAuditPage(page.Select(ToolAuditItem.From).ToList(), total));
    }

    private static Dictionary<string, string[]> Validate(ToolAuditQuery query, out ToolOutcome? outcome, out DateTimeOffset? from, out DateTimeOffset? to)
    {
        var errors = new Dictionary<string, string[]>();
        outcome = null;
        from = null;
        to = null;

        if (query.Page < 1)
        {
            errors["page"] = ["The page starts at 1."];
        }

        if (query.PageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"The page size must be between 1 and {MaxPageSize}."];
        }

        if (!TryParseMoment(query.From, endOfDay: false, out from))
        {
            errors["from"] = ["Use a date (2026-10-12) or a date and time (2026-10-12T08:30:00Z)."];
        }

        if (!TryParseMoment(query.To, endOfDay: true, out to))
        {
            errors["to"] = ["Use a date (2026-10-12) or a date and time (2026-10-12T08:30:00Z)."];
        }

        if (from is { } periodStart && to is { } periodEnd && periodStart > periodEnd)
        {
            errors["from"] = ["The start of the period is after its end."];
        }

        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            var name = query.Outcome.Trim();

            // Names only: Enum.TryParse would also take "2" or "99".
            if (name.All(char.IsLetter) && Enum.TryParse<ToolOutcome>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                outcome = parsed;
            }
            else
            {
                errors["outcome"] = [$"The outcome must be one of: {string.Join(", ", Enum.GetNames<ToolOutcome>())}."];
            }
        }

        return errors;
    }

    /// <summary>
    /// A date covers its whole day in UTC (<paramref name="endOfDay"/> picks which end it stands for); a time without an offset
    /// is UTC, never the server's own zone.
    /// </summary>
    private static bool TryParseMoment(string? text, bool endOfDay, out DateTimeOffset? moment)
    {
        moment = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var value = text.Trim();
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            moment = endOfDay ? start.AddDays(1).AddTicks(-1) : start;
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            // Npgsql writes only UTC instants: an offset in the query would be a 500, not a filter.
            moment = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }
}

/// <param name="From">Entries started at or after this moment: a date (<c>2026-10-12</c>, from 00:00 UTC) or a time (UTC when it has no offset).</param>
/// <param name="To">Entries started at or before this moment: a date covers the whole day in UTC; a time without offset is UTC.</param>
/// <param name="Tool">The name the model saw (<c>builtin__calculator</c>) or the tool's own name.</param>
/// <param name="Outcome">A <see cref="ToolOutcome"/> name, in any case.</param>
internal sealed record ToolAuditQuery(
    string? From,
    string? To,
    string? Tool,
    Guid? UserId,
    string? Outcome,
    int Page = 1,
    int PageSize = ToolAuditEndpoints.DefaultPageSize);

internal sealed record ToolAuditPage(List<ToolAuditItem> Items, int Total);

internal sealed record ToolAuditItem(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    Guid UserId,
    Guid AssistantId,
    Guid ConversationId,
    Guid CallId,
    string? ServerName,
    string ToolName,
    string? Decision,
    string Outcome,
    string Arguments,
    bool ArgumentsTruncated,
    int? ResultCharacters,
    int? SourceCount,
    int? DurationMs,
    string? ErrorCode)
{
    public static ToolAuditItem From(ToolAuditEntry entry) => new(
        entry.Id,
        entry.StartedAt,
        entry.CompletedAt,
        entry.UserId,
        entry.AssistantId,
        entry.ConversationId,
        entry.CallId,
        entry.ServerName,
        entry.ToolName,
        entry.Decision?.ToString(),
        entry.Outcome.ToString(),
        entry.Arguments,
        entry.ArgumentsTruncated,
        entry.ResultCharacters,
        entry.SourceCount,
        entry.DurationMs,
        entry.ErrorCode);
}
