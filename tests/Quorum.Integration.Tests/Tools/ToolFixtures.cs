using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;

namespace Quorum.Integration.Tests.Tools;

/// <summary>Sets up the tools an answer can use: the active set of the default assistant and the scripted probe tool.</summary>
internal static class ToolFixtures
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public const string ProbeSchema = """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""";

    /// <summary>Creates the probe tool (enabled, with the given policy) and activates it next to the tools already active.</summary>
    public static async Task<Guid> AddProbeAsync(QuorumApp app, ToolPolicy policy = ToolPolicy.Auto)
    {
        var tenantId = await ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");
        var assistantId = await ScalarAsync<Guid>(app, "SELECT \"Id\" FROM agents.assistants LIMIT 1");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var tool = Tool.CreateBuiltin(tenantId, ProbeTool.ToolName, "A tool that tests control.", ProbeSchema);
        tool.Review(ToolState.Enabled, policy);
        db.Tools.Add(tool);
        db.AssistantTools.Add(AssistantTool.Create(tenantId, assistantId, tool.Id));
        await db.SaveChangesAsync(Ct);
        return tool.Id;
    }

    /// <summary>Activates exactly the named tools (exposed names such as <c>builtin__calculator</c>) on the default assistant.</summary>
    public static async Task ActivateOnlyAsync(HttpClient client, params string[] exposedNames)
    {
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(client);
        var pool = (await ToolsApi.GetAssistantToolsAsync(client, assistantId)).Pool;
        using var response = await client.PutAsJsonAsync(
            ToolsApi.AssistantTools(assistantId),
            new { toolIds = pool.Where(t => exposedNames.Contains(t.Name)).Select(t => t.ToolId).ToArray() },
            Ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Writes one audit entry straight to the database, as a finished call unless <paramref name="outcome"/> is
    /// <see cref="ToolOutcome.Started"/>. <paramref name="tenantId"/> defaults to the tenant of the default assistant, and
    /// <paramref name="conversationId"/> to a conversation of its own.
    /// </summary>
    public static async Task AddAuditEntryAsync(
        QuorumApp app,
        DateTimeOffset startedAt,
        string tool = "calculator",
        string? server = null,
        ToolOutcome outcome = ToolOutcome.Succeeded,
        Guid? userId = null,
        Guid? tenantId = null,
        string arguments = "{}",
        Guid? conversationId = null)
    {
        var tenant = tenantId ?? await ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");
        var assistant = await ScalarAsync<Guid>(app, "SELECT \"Id\" FROM agents.assistants LIMIT 1");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        db.ToolAuditEntries.Add(NewAuditEntry(
            tenant, assistant, startedAt, tool, server, outcome, userId ?? Guid.NewGuid(), arguments, conversationId));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Writes many audit entries in one save, for tests that need a log larger than one batch.</summary>
    public static async Task AddManyAuditEntriesAsync(QuorumApp app, DateTimeOffset startedAt, int count, string tool = "bulk")
    {
        var tenant = await ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");
        var assistant = await ScalarAsync<Guid>(app, "SELECT \"Id\" FROM agents.assistants LIMIT 1");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        db.ToolAuditEntries.AddRange(Enumerable.Range(0, count)
            .Select(i => NewAuditEntry(tenant, assistant, startedAt.AddSeconds(i), tool, null, ToolOutcome.Succeeded, Guid.NewGuid(), "{}")));
        await db.SaveChangesAsync(Ct);
    }

    private static ToolAuditEntry NewAuditEntry(
        Guid tenant,
        Guid assistant,
        DateTimeOffset startedAt,
        string tool,
        string? server,
        ToolOutcome outcome,
        Guid userId,
        string arguments,
        Guid? conversationId = null)
    {
        var entry = ToolAuditEntry.Start(
            tenant, userId, assistant, conversationId ?? Guid.NewGuid(), Guid.NewGuid(), server, tool, ToolDecision.Auto, arguments, false, startedAt);
        if (outcome != ToolOutcome.Started)
        {
            entry.Complete(outcome, 5, null, startedAt.AddMilliseconds(40), TimeSpan.FromMilliseconds(40));
        }

        return entry;
    }

    /// <summary>Sets the tool-calling limits of the tenant; the values are the defaults unless a test needs another.</summary>
    public static async Task SetLimitsAsync(
        HttpClient client,
        int maxRounds = 8,
        int totalSeconds = 300,
        int callSeconds = 30,
        int confirmationSeconds = 300,
        int maxResult = 20_000)
    {
        using var response = await client.PutAsJsonAsync(
            ToolsApi.ToolSettings,
            new
            {
                maxRounds,
                totalTimeoutSeconds = totalSeconds,
                callTimeoutSeconds = callSeconds,
                confirmationTimeoutSeconds = confirmationSeconds,
                maxResultCharacters = maxResult,
                auditRetentionDays = 90,
            },
            Ct);
        response.EnsureSuccessStatusCode();
    }

    public static async Task<T> ScalarAsync<T>(QuorumApp app, string sql)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    public static async Task ExecuteAsync(QuorumApp app, string sql)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The audit entries, oldest first, read straight from the table.</summary>
    public static async Task<IReadOnlyList<AuditRow>> AuditRowsAsync(QuorumApp app)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT \"ToolName\", \"ServerName\", \"Decision\", \"Outcome\", \"Arguments\", \"ArgumentsTruncated\", \"ResultCharacters\", \"ErrorCode\", \"UserId\", \"AssistantId\", \"ConversationId\", \"CompletedAt\", \"DurationMs\", \"CallId\", \"SourceCount\" " +
            "FROM agents.tool_audit_entries ORDER BY \"StartedAt\", \"Id\"",
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new AuditRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetGuid(8),
                reader.GetGuid(9),
                reader.GetGuid(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12),
                reader.GetGuid(13),
                reader.IsDBNull(14) ? null : reader.GetInt32(14)));
        }

        return rows;
    }

    public sealed record AuditRow(
        string ToolName,
        string? ServerName,
        string? Decision,
        string Outcome,
        string Arguments,
        bool ArgumentsTruncated,
        int? ResultCharacters,
        string? ErrorCode,
        Guid UserId,
        Guid AssistantId,
        Guid ConversationId,
        DateTimeOffset? CompletedAt,
        int? DurationMs,
        Guid CallId,
        int? SourceCount = null);
}
