using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.Skills;

namespace Quorum.Modules.Agents.Chat;

/// <summary>What an endpoint is called with and how much it holds.</summary>
internal sealed record EndpointConfiguration(string BaseUrl, string ModelName, string? ProtectedApiKey, int ContextTokenBudget, bool SupportsTools);

/// <summary>The root agent of an assistant with the endpoint it runs on.</summary>
internal sealed record AssistantConfiguration(
    Guid AgentId,
    string SystemPrompt,
    string BaseUrl,
    string ModelName,
    string? ProtectedApiKey,
    int ContextTokenBudget,
    bool SupportsTools,
    int MaxParallelSubAgents);

internal sealed record SubAgentSource(
    Guid AgentId,
    string Slug,
    string Description,
    string SystemPrompt,
    SubAgentFailurePolicy FailurePolicy,
    EndpointConfiguration Endpoint,
    IReadOnlyList<ActiveTool> Active,
    SkillQuorumTool? Skill = null);

/// <summary>
/// The tools of an assistant as stored, before the catalog gives the rag and lookup ones their definition: what the root agent has
/// activated, its sub-agents with theirs, the limits of the tenant and the web configuration when a web tool needs it.
/// </summary>
internal sealed record AssistantTools(
    IReadOnlyList<ActiveTool> Active,
    IReadOnlyList<SubAgentSource> SubAgents,
    ToolLimits Limits,
    WebConfiguration? Web);

/// <summary><see cref="AssistantTools"/> once the catalog has given the rag and lookup tools their definition, the sub-agents' tools in the same order.</summary>
internal sealed record ResolvedAssistantTools(IReadOnlyList<ActiveTool> Active, IReadOnlyList<SubAgentSource> SubAgents);

/// <summary>
/// Keeps the assistant graph (the agents, their tools and bindings, the sub-agent edges and the settings that go with them) between
/// turns, so an answer, and the context gauge asked for as the user types, do not read it all again. It is valid until something in
/// the <c>agents</c> schema changes: a save through the module's DbContext other than an audit entry, which every tool call writes,
/// bumps one version for the whole process (a change by one tenant can reach others through a scope), and a graph read under an older
/// version is read again. A time limit stays as a safety net.
/// </summary>
/// <remarks>
/// The definition of rag and lookup tools (spec 003 D-11, spec 007 D-12) is made from the knowledge base catalog, so that part of the
/// graph also depends on the knowledge base module: it is kept under the sum of this version and the module's change stamp, which
/// grows with every write that module sends, bulk ones included. The checks at call time (policy, activation, definition hash) and
/// the audit writes never read the graph.
/// </remarks>
internal sealed class AssistantGraphCache : SaveChangesInterceptor, IDisposable
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromMinutes(1);

    private readonly MemoryCache _entries = new(new MemoryCacheOptions());
    private readonly ConditionalWeakTable<DbContext, object> _pending = [];
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    /// <summary>Makes every graph read before this moment stale. For a change that is committed after the save that made it.</summary>
    public void Invalidate() => Interlocked.Increment(ref _version);

    /// <param name="part">Which part of the graph; the parts of one assistant are read apart.</param>
    /// <param name="load">Reads the part; a <see langword="null"/> result (the assistant does not exist) is not kept.</param>
    public async Task<T?> GetOrLoadAsync<T>(string part, Guid tenantId, Guid assistantId, long externalVersion, Func<Task<T?>> load)
        where T : class
    {
        var key = (part, tenantId, assistantId);

        // The version is read before the graph is: a change that lands while it is read leaves the entry stale, not fresh.
        var version = Version + externalVersion;
        if (_entries.TryGetValue(key, out Entry<T>? kept) && kept is not null && kept.Version == version)
        {
            return kept.Value;
        }

        var value = await load();
        if (value is not null)
        {
            _entries.Set(key, new Entry<T>(version, value), SafetyNet);
        }

        return value;
    }

    public void Dispose() => _entries.Dispose();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Mark(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Mark(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Forget(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return Task.CompletedTask;
    }

    // The entries are looked at before the save, because afterwards they all read as unchanged.
    private void Mark(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var changesTheGraph = context.ChangeTracker.Entries()
            .Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && entry.Entity is not ToolAuditEntry);
        if (changesTheGraph)
        {
            _pending.AddOrUpdate(context, this);
        }
    }

    private void Flush(DbContext? context)
    {
        if (context is not null && _pending.Remove(context))
        {
            Invalidate();
        }
    }

    private void Forget(DbContext? context)
    {
        if (context is not null)
        {
            _pending.Remove(context);
        }
    }

    private sealed record Entry<T>(long Version, T Value);
}
