using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Secrets;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.Mcp;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Features.Tools;

/// <summary>The MCP servers of the tenant: registered, tested, refreshed and scoped by the admin (D-1, D-14, D-17, D-25).</summary>
internal sealed partial class McpServerEndpoints : IEndpointGroup
{
    private static readonly Error ServerNotFound = Error.NotFound("tool.server_not_found", "The server does not exist.");

    private static readonly Error NameTaken = Error.Conflict("tool.server_name_taken", "Another server already has this name.");

    private static readonly Error RefreshConflict = Error.Conflict("tool.refresh_conflict", "The server was changed at the same time. Try again.");

    public void Map(IEndpointRouteBuilder app)
    {
        var servers = app.MapGroup("/api/tools/servers")
            .WithTags("Tool servers")
            .RequireAuthorization(QuorumPolicies.ToolsManage);

        servers.MapPost("/test", TestAsync).WithName("TestToolServer");
        servers.MapGet("/", ListAsync).WithName("ListToolServers");
        servers.MapPost("/", CreateAsync).WithName("CreateToolServer");
        servers.MapGet("/{id:guid}", GetAsync).WithName("GetToolServer");
        servers.MapGet("/{id:guid}/tools", ListServerToolsAsync).WithName("ListToolServerTools");
        servers.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateToolServer");
        servers.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteToolServer");
        servers.MapPost("/{id:guid}/refresh", RefreshAsync).WithName("RefreshToolServer");
    }

    private static async Task<Results<Ok<TestServerResponse>, ProblemHttpResult>> TestAsync(
        TestServerRequest request,
        McpServerClient client,
        CancellationToken cancellationToken)
    {
        if (ServerInput.CheckConnection(request.Url, request.Headers) is { } invalid)
        {
            return invalid.ToProblem();
        }

        var url = new Uri(request.Url!.Trim());
        try
        {
            var tools = await client.ListToolsAsync(url, request.Headers, cancellationToken);
            return TypedResults.Ok(new TestServerResponse(
                Reachable: true,
                [.. tools.Select(t => new TestedToolResponse(t.Name, t.Description, Json.Parse(t.Annotations)))]));
        }
        catch (McpServerException exception)
        {
            return Error.Unprocessable(exception.Code, exception.Message).ToProblem();
        }
    }

    private static async Task<Results<Ok<Page<ServerResponse>>, ProblemHttpResult>> ListAsync(HttpRequest request, AgentsDbContext db, CancellationToken cancellationToken)
    {
        var query = ListQuery.Parse(request.Query);
        if (!query.IsSuccess)
        {
            return query.Error.ToProblem();
        }

        var filtered = ScopeFilter.Apply(ToolVisibility.Servers(db).AsNoTracking(), query.Value, s => s.Scope, s => s.TenantId, s => s.ScopeTenantIds, s => s.ScopeAssistantIds, s => s.Status, s => s.Name, s => s.Url);
        if (!filtered.IsSuccess)
        {
            return filtered.Error.ToProblem();
        }

        var page = await filtered.Value.ToPageAsync(query.Value, "tool-servers", s => s.Name, s => s.Id, s => s, cancellationToken);
        if (!page.IsSuccess)
        {
            return page.Error.ToProblem();
        }

        // The tool counts are read for the servers of the page only.
        var ids = page.Value.Items.Select(s => s.Id).ToList();
        var tools = await ToolVisibility.Tools(db)
            .Where(t => t.Tool.ServerId != null && ids.Contains(t.Tool.ServerId.Value))
            .Select(t => new { t.Tool.ServerId, t.Tool.State })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new Page<ServerResponse>(
            [.. page.Value.Items.Select(server => ServerResponse.From(
                server,
                db.CurrentTenantId,
                tools.Count(t => t.ServerId == server.Id),
                tools.Count(t => t.ServerId == server.Id && t.State == ToolState.PendingReview),
                tools: null))],
            page.Value.Cursor));
    }

    /// <summary>The tools of one server, paged, with the state and search filters; the tools of a server shared with the tenant are the enabled ones.</summary>
    private static async Task<Results<Ok<Page<ToolResponse>>, ProblemHttpResult>> ListServerToolsAsync(Guid id, HttpRequest request, AgentsDbContext db, CancellationToken cancellationToken)
    {
        var query = ListQuery.Parse(request.Query);
        if (!query.IsSuccess)
        {
            return query.Error.ToProblem();
        }

        if (query.Value.Scope.Count + query.Value.Tenant.Count + query.Value.Assistant.Count + query.Value.Type.Count > 0)
        {
            return ScopeFilter.NotForTools.ToProblem();
        }

        if (!await ToolVisibility.Servers(db).AnyAsync(s => s.Id == id, cancellationToken))
        {
            return ServerNotFound.ToProblem();
        }

        var statused = ScopeFilter.Status(ToolVisibility.Tools(db).AsNoTracking().Where(t => t.Tool.ServerId == id), query.Value, t => t.Tool.State);
        if (!statused.IsSuccess)
        {
            return statused.Error.ToProblem();
        }

        var searched = ScopeFilter.Search(statused.Value, query.Value, t => t.Tool.Name, t => t.Tool.Description);
        var page = await searched.ToPageAsync(query.Value, "server-tools", t => t.Tool.Name, t => t.Tool.Id, t => t, cancellationToken);
        if (!page.IsSuccess)
        {
            return page.Error.ToProblem();
        }

        var tenantId = db.CurrentTenantId;
        return TypedResults.Ok(new Page<ToolResponse>([.. page.Value.Items.Select(t => ToolResponse.From(t.Tool, t.Server, tenantId))], page.Value.Cursor));
    }

    private static async Task<Results<Ok<ServerResponse>, ProblemHttpResult>> GetAsync(
        Guid id,
        AgentsDbContext db,
        CancellationToken cancellationToken)
    {
        if (await ToolVisibility.Servers(db).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } server)
        {
            return ServerNotFound.ToProblem();
        }

        var tools = await ToolVisibility.Tools(db)
            .AsNoTracking()
            .Where(t => t.Tool.ServerId == id)
            .OrderBy(t => t.Tool.Name)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(ServerResponse.From(
            server,
            db.CurrentTenantId,
            tools.Count,
            tools.Count(t => t.Tool.State == ToolState.PendingReview),
            [.. tools.Select(t => ToolResponse.From(t.Tool, t.Server, db.CurrentTenantId))]));
    }

    private static async Task<Results<CreatedAtRoute<CreateServerResponse>, ProblemHttpResult>> CreateAsync(
        ServerRequest request,
        AgentsDbContext db,
        ITenantContext tenant,
        McpServerClient client,
        McpHeadersProtector protector,
        TimeProvider clock,
        ILogger<McpServerEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");

        var input = await ServerInput.ValidateAsync(request, db, cancellationToken);
        if (input.Error is { } invalid)
        {
            return invalid.ToProblem();
        }

        if (await db.McpServers.AnyAsync(s => s.Name == input.Name, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var now = clock.GetUtcNow();
        var server = McpServer.Create(
            tenantId,
            input.Name!,
            input.Url!.AbsoluteUri,
            protector.ProtectHeaders(input.Headers),
            input.Scope,
            input.TenantIds,
            input.AssistantIds,
            now);
        db.McpServers.Add(server);

        // An unreachable server is still registered (D-25): the admin fixes it, then refreshes.
        string? warning = null;
        try
        {
            var listed = await client.ListToolsAsync(input.Url, input.Headers, cancellationToken);
            server.MarkAvailable(now);
            McpToolSync.Apply(db, server, [], listed);
        }
        catch (McpServerException exception)
        {
            server.MarkUnavailable(exception.Message, now);
            warning = $"The server could not be reached, so it is saved as unavailable: {exception.Message}";
            LogServerUnavailable(logger, server.Id, exception.Code);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        var tools = await db.Tools.AsNoTracking().Where(t => t.ServerId == server.Id).OrderBy(t => t.Name).ToListAsync(cancellationToken);
        LogServerRegistered(logger, server.Id, server.Status, tools.Count);
        return TypedResults.CreatedAtRoute(
            new CreateServerResponse(server.Id, server.Status.ToString(), warning, [.. tools.Select(t => ToolResponse.From(t, server, tenantId))]),
            "GetToolServer",
            new RouteValueDictionary { ["id"] = server.Id });
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> UpdateAsync(
        Guid id,
        ServerRequest request,
        AgentsDbContext db,
        McpHeadersProtector protector,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // The tenant filter makes a server another tenant shares with this one not found here: it is read-only.
        if (await db.McpServers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } server)
        {
            return ServerNotFound.ToProblem();
        }

        var input = await ServerInput.ValidateAsync(request, db, cancellationToken);
        if (input.Error is { } invalid)
        {
            return invalid.ToProblem();
        }

        if (await db.McpServers.AnyAsync(s => s.Name == input.Name && s.Id != id, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        // Omitted headers keep the stored ones; an empty object removes them.
        var headers = request.Headers is null ? server.ProtectedHeaders : protector.ProtectHeaders(input.Headers);
        var now = clock.GetUtcNow();
        var urlChanged = !string.Equals(server.Url, input.Url!.AbsoluteUri, StringComparison.Ordinal);
        server.Edit(input.Name!, input.Url.AbsoluteUri, headers, input.Scope, input.TenantIds, input.AssistantIds, now);
        if (urlChanged)
        {
            // What was approved was approved for the old address: the server offers nothing until a refresh reads
            // the tools from the new one and the review flow (D-14) judges them.
            server.MarkUnavailable("The address was changed: refresh the server to read its tools again.", now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        return TypedResults.NoContent();
    }

    /// <summary>Removes the server with its tools and their activations. Audit entries keep the names they recorded.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (await db.McpServers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } server)
        {
            return ServerNotFound.ToProblem();
        }

        db.McpServers.Remove(server);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<RefreshResponse>, ProblemHttpResult>> RefreshAsync(
        Guid id,
        AgentsDbContext db,
        McpServerClient client,
        McpHeadersProtector protector,
        TimeProvider clock,
        ILogger<McpServerEndpoints> logger,
        CancellationToken cancellationToken)
    {
        if (await db.McpServers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } server)
        {
            return ServerNotFound.ToProblem();
        }

        var now = clock.GetUtcNow();
        try
        {
            var listed = await client.ListToolsAsync(new Uri(server.Url), protector.RevealHeaders(server.ProtectedHeaders), cancellationToken);
            var stored = await db.Tools.Where(t => t.ServerId == id).ToListAsync(cancellationToken);

            server.MarkAvailable(now);
            var result = McpToolSync.Apply(db, server, stored, listed);
            await db.SaveChangesAsync(cancellationToken);

            LogServerRefreshed(logger, server.Id, result.Added.Count, result.Changed.Count, result.Removed.Count);
            return TypedResults.Ok(new RefreshResponse(server.Status.ToString(), result.Added, result.Changed, result.Removed));
        }
        catch (McpServerException exception)
        {
            // What was approved stays stored, but an unavailable server offers nothing until a refresh succeeds.
            server.MarkUnavailable(exception.Message, now);
            await db.SaveChangesAsync(cancellationToken);
            LogServerUnavailable(logger, server.Id, exception.Code);
            return Error.Unprocessable(exception.Code, exception.Message).ToProblem();
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return RefreshConflict.ToProblem();
        }
    }

    // Only ids, counts and codes: never headers, URLs or what the server said.
    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server {ServerId} registered as {Status} with {ToolCount} tools")]
    private static partial void LogServerRegistered(ILogger logger, Guid serverId, McpServerStatus status, int toolCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server {ServerId} refreshed: {Added} added, {Changed} changed, {Removed} removed")]
    private static partial void LogServerRefreshed(ILogger logger, Guid serverId, int added, int changed, int removed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server {ServerId} is unavailable: {Code}")]
    private static partial void LogServerUnavailable(ILogger logger, Guid serverId, string code);
}

/// <summary>What the admin entered for a server, checked and normalized; <see cref="Error"/> says what is wrong when it cannot be used.</summary>
internal sealed record ServerInput(
    Error? Error,
    string? Name,
    Uri? Url,
    Dictionary<string, string>? Headers,
    ToolScopeKind Scope,
    List<Guid> TenantIds,
    List<Guid> AssistantIds)
{
    private static readonly Error InvalidName = Error.Validation(
        "tool.invalid_name",
        $"The name must be 1 to {McpServerRules.MaxNameLength} characters: lower-case letters, digits, '-' and '_', starting with a letter or digit, without '__' and not 'builtin' or 'rag'.");

    private static readonly Error InvalidUrl = Error.Validation(
        "tool.invalid_url",
        "The URL must be an absolute http or https address without a user name or password. Put credentials in the headers: the URL is stored unencrypted and shown in the API, only headers are encrypted.");

    public static Error? CheckConnection(string? url, IReadOnlyDictionary<string, string>? headers)
    {
        if (!McpServerRules.TryParseUrl(url, out _))
        {
            return InvalidUrl;
        }

        return McpServerRules.ValidateHeaders(headers) is { } problem
            ? Error.Validation("tool.invalid_headers", problem)
            : null;
    }

    public static async Task<ServerInput> ValidateAsync(ServerRequest request, AgentsDbContext db, CancellationToken cancellationToken)
    {
        var failed = (Error error) => new ServerInput(error, null, null, null, ToolScopeKind.Tenant, [], []);

        if (!McpServerRules.IsValidName(request.Name))
        {
            return failed(InvalidName);
        }

        if (CheckConnection(request.Url, request.Headers) is { } connection)
        {
            return failed(connection);
        }

        var scope = await ToolScopeRules.ValidateAsync(request.Scope, db, cancellationToken);
        if (!scope.IsSuccess)
        {
            return failed(scope.Error);
        }

        return new ServerInput(null, request.Name, new Uri(request.Url!.Trim()), request.Headers, scope.Value.Kind, scope.Value.TenantIds, scope.Value.AssistantIds);
    }
}

internal sealed record ScopeRequest(string? Kind, List<Guid>? TenantIds, List<Guid>? AssistantIds);

/// <param name="Headers">Static request headers (for example <c>Authorization</c>). Omitted on an update: the stored ones stay.</param>
internal sealed record ServerRequest(string? Name, string? Url, Dictionary<string, string>? Headers, ScopeRequest? Scope);

internal sealed record TestServerRequest(string? Url, Dictionary<string, string>? Headers);

internal sealed record TestedToolResponse(string Name, string Description, JsonElement? Annotations);

internal sealed record TestServerResponse(bool Reachable, List<TestedToolResponse> Tools);

internal sealed record ScopeResponse(string Kind, IReadOnlyList<Guid> TenantIds, IReadOnlyList<Guid> AssistantIds);

/// <param name="HasHeaders">Whether static headers are stored. Their values are never returned.</param>
/// <param name="Shared"><see langword="true"/> for a server of another tenant made visible by its scope: read-only here.</param>
internal sealed record ServerResponse(
    Guid Id,
    string Name,
    string Url,
    bool HasHeaders,
    string Status,
    string? LastError,
    DateTimeOffset? LastRefreshedAt,
    ScopeResponse Scope,
    int ToolCount,
    int PendingReviewCount,
    bool Shared,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<ToolResponse>? Tools)
{
    public static ServerResponse From(McpServer server, Guid? currentTenantId, int toolCount, int pendingReviewCount, List<ToolResponse>? tools) => new(
        server.Id,
        server.Name,
        server.Url,
        server.ProtectedHeaders is not null,
        server.Status.ToString(),
        server.LastError,
        server.LastRefreshedAt,
        new ScopeResponse(server.Scope.ToString(), server.ScopeTenantIds, server.ScopeAssistantIds),
        toolCount,
        pendingReviewCount,
        server.TenantId != currentTenantId,
        tools);
}

internal sealed record CreateServerResponse(Guid Id, string Status, string? Warning, List<ToolResponse> Tools);

internal sealed record RefreshResponse(string Status, IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed);

internal static class Json
{
    public static JsonElement? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
