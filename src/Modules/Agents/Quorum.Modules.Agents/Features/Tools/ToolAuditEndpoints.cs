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
        var errors = Validate(query, out var outcome);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var entries = db.ToolAuditEntries.AsNoTracking().AsQueryable();
        if (query.From is { } from)
        {
            entries = entries.Where(e => e.StartedAt >= from);
        }

        if (query.To is { } to)
        {
            entries = entries.Where(e => e.StartedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Tool))
        {
            // The name the model saw (native__calculator, files__read) or the tool's own name.
            var tool = query.Tool.Trim();
            entries = entries.Where(e =>
                e.ToolName == tool
                || (e.ServerName == null ? "native__" + e.ToolName : e.ServerName + "__" + e.ToolName) == tool);
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
        var page = await entries
            .OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new ToolAuditPage(page.Select(ToolAuditItem.From).ToList(), total));
    }

    private static Dictionary<string, string[]> Validate(ToolAuditQuery query, out ToolOutcome? outcome)
    {
        var errors = new Dictionary<string, string[]>();
        outcome = null;

        if (query.Page < 1)
        {
            errors["page"] = ["The page starts at 1."];
        }

        if (query.PageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"The page size must be between 1 and {MaxPageSize}."];
        }

        if (query.From is { } from && query.To is { } to && from > to)
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
}

/// <param name="From">Entries started at or after this moment.</param>
/// <param name="To">Entries started at or before this moment.</param>
/// <param name="Tool">The name the model saw (<c>native__calculator</c>) or the tool's own name.</param>
/// <param name="Outcome">A <see cref="ToolOutcome"/> name, in any case.</param>
internal sealed record ToolAuditQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
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
        entry.DurationMs,
        entry.ErrorCode);
}
