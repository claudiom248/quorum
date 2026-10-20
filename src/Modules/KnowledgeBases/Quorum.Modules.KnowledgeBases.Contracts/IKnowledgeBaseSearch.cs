using System.Text.Json;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Contracts;

/// <summary>Searches knowledge bases on behalf of an assistant (the rag tool of the Agents module).</summary>
public interface IKnowledgeBaseSearch
{
    /// <summary>
    /// Embeds the query, queries the knowledge bases of the request that the tenant and the assistant can see, and returns the
    /// best chunks. A knowledge base the tenant cannot see is ignored as if it did not exist. Failures of single knowledge bases
    /// are part of the response; the result is an error when nothing could be searched at all.
    /// </summary>
    Task<Result<KnowledgeBaseSearchResponse>> SearchAsync(KnowledgeBaseSearchRequest request, CancellationToken cancellationToken);
}

/// <summary>What to search and with which limits. Filters use the parameter names of the generated tool schema (spec 003 D-11).</summary>
public sealed record KnowledgeBaseSearchRequest
{
    /// <summary>The tenant searching. It must be the tenant of the current request: the search never acts for another one.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The assistant answering; a knowledge base with the Assistant scope is only searched for the assistants it lists.</summary>
    public required Guid AssistantId { get; init; }

    public required IReadOnlyList<Guid> KnowledgeBaseIds { get; init; }

    public required string Query { get; init; }

    /// <summary>The model's filters: <c>department</c>, <c>year_min</c>, <c>published_on_from</c>... A <see langword="null"/> or empty value is no filter.</summary>
    public IReadOnlyDictionary<string, JsonElement> Filters { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>The binding's filters, in the same shape. They always apply and are never visible to the model.</summary>
    public IReadOnlyDictionary<string, JsonElement> FixedFilters { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>Chunks to return, 1 to 20.</summary>
    public int TopK { get; init; } = 5;

    public double? ScoreThreshold { get; init; }

    /// <summary>The most characters of chunk text to return; whole chunks are dropped to fit. <see langword="null"/> is no cap.</summary>
    public int? MaxResultCharacters { get; init; }
}

public sealed record KnowledgeBaseSearchResponse(
    IReadOnlyList<RetrievedChunk> Chunks,
    IReadOnlyList<string> SkippedKnowledgeBases,
    IReadOnlyList<FailedKnowledgeBase> FailedKnowledgeBases,
    int UnusableChunks,
    bool FirstChunkExceededCap);

/// <summary>A chunk with the reference to its source. <see cref="Score"/> is the store's and only means something among the chunks of the same knowledge base.</summary>
public sealed record RetrievedChunk(
    string KnowledgeBase,
    double Score,
    string Text,
    string? DocumentId,
    string? Title,
    string? Position,
    string? Url);

/// <param name="Message">What went wrong, safe to show to the model: no address, key or internal detail.</param>
public sealed record FailedKnowledgeBase(string Name, string Code, string Message);

/// <summary>The codes of the errors <see cref="IKnowledgeBaseSearch"/> returns.</summary>
public static class KnowledgeBaseSearchErrors
{
    public const string InvalidRequest = "knowledge_base.invalid_search";
    public const string InvalidFilter = "knowledge_base.invalid_filter";
    public const string InvalidFixedFilter = "knowledge_base.invalid_fixed_filter";
    public const string NotFound = "knowledge_base.not_found";
    public const string FilterUnsupported = "knowledge_base.filter_unsupported";
    public const string Unavailable = "knowledge_base.unavailable";
}
