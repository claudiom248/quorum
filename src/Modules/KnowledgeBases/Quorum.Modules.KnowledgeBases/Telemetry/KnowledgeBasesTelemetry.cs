using System.Diagnostics;
using Quorum.Shared.Telemetry;

namespace Quorum.Modules.KnowledgeBases.Telemetry;

/// <summary>
/// The activity source of the KnowledgeBases module and the names of its spans (spec 016 D-8). Kinds, counts and durations only:
/// never a query, a chunk or a document name.
/// </summary>
internal static class KnowledgeBasesTelemetry
{
    public static readonly ActivitySource Source = new(QuorumActivityNames.KnowledgeBasesSource);

    public const string VectorQueryActivityName = "quorum.vector_query";
    public const string VectorStoreKindTag = "quorum.vector_store.kind";
    public const string ResultCountTag = "quorum.result_count";

    // Background work has no request to hang under: each cycle is a trace of its own (spec 016 D-7).
    public const string LookupWarmupActivityName = "quorum.background.lookup_warmup";
    public const string LookupRefreshActivityName = "quorum.background.lookup_refresh";
    public const string LookupListsTag = "quorum.lookup.lists";
    public const string LookupSlicesRefreshedTag = "quorum.lookup.slices_refreshed";
}
