using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Storage;

/// <summary>
/// A connection to a source as the adapter of its kind understands it, secrets in the clear. It is only ever held for the length of
/// a call: what is stored is <see cref="ConnectionSettings"/>, with the secrets encrypted.
/// </summary>
internal interface IStoreConnection;

/// <summary>What the search asks of a store (spec 003 D-11).</summary>
/// <param name="Conditions">All of them must hold.</param>
/// <param name="ScoreThreshold">Passed to the store, which knows whether a higher or a lower score is better for the metric.</param>
/// <param name="Paths">The paths to read from each record, and nothing else.</param>
internal sealed record StoreQuery(float[] Vector, IReadOnlyList<FilterCondition> Conditions, int Limit, double? ScoreThreshold, IReadOnlyList<string> Paths);

/// <summary>What looking at a source found.</summary>
/// <param name="Info">The source, or <see langword="null"/> when it does not exist or the store could not be used.</param>
/// <param name="Sample">Rows as JSON, empty unless sampling was asked for and the source has records.</param>
/// <param name="Warnings">Every problem found, with its cause and what follows from it; none stops registration.</param>
internal sealed record SourceProbe(bool Reachable, SourceInfo? Info, IReadOnlyList<JsonObject> Sample, IReadOnlyList<InspectWarning> Warnings);

/// <summary>The vector that will be searched, or the warning that says why it cannot be told yet.</summary>
internal sealed record VectorSelection(VectorInfo? Vector, InspectWarning? Warning);

/// <summary>
/// What is specific to one <see cref="KnowledgeBaseKind"/> and nothing else (spec 008 D-1): reading and checking the connection of a
/// request, keeping a stored secret, turning input into stored settings and back, looking at the source, choosing the vector and
/// searching it, and naming failures. What is shared stays out: the dimension check against an embedding, discovery, the mapping,
/// the interleaving of results, thresholds and status handling work on what the adapter returns.
/// </summary>
internal interface IStoreAdapter
{
    KnowledgeBaseKind Kind { get; }

    /// <summary>The <c>connection</c> object of a request, checked. A secret in it is the plain one the administrator typed.</summary>
    Result<IStoreConnection> ReadConnection(JsonElement? connection);

    /// <summary>
    /// Applies the rule that keeps a stored secret when the request omits it, so a form can be saved without retyping it; a secret
    /// that cannot be kept is an error. <paramref name="stored"/> is <see langword="null"/> for a new knowledge base.
    /// </summary>
    Result<IStoreConnection> KeepSecrets(IStoreConnection connection, ConnectionSettings? stored);

    /// <summary>The settings to store, with every secret encrypted.</summary>
    ConnectionSettings ToSettings(IStoreConnection connection);

    /// <summary>The connection as a response shows it: the members that are not secret, and whether the secrets are set.</summary>
    JsonObject Describe(ConnectionSettings settings);

    /// <summary>Opens stored settings. <see langword="false"/> when a secret cannot be read (the installation's keys were replaced).</summary>
    bool TryOpen(ConnectionSettings settings, [NotNullWhen(true)] out IStoreConnection? connection);

    /// <summary>What a registration or a verification answers when <see cref="TryOpen"/> fails.</summary>
    Error SecretRequired { get; }

    /// <summary>What a search tells the model when <see cref="TryOpen"/> fails; it is safe to show.</summary>
    Error SecretUnreadable { get; }

    /// <summary>
    /// Looks at the source: is the store reachable, does the source exist, can the store search, and optionally a sample of its rows.
    /// A store that cannot be used is a warning, never an exception.
    /// </summary>
    Task<SourceProbe> ProbeAsync(IStoreConnection connection, bool sample, CancellationToken cancellationToken);

    /// <summary>The vector that will be searched, which is the connection's choice when the source has several.</summary>
    VectorSelection SelectVector(SourceInfo source, IStoreConnection connection);

    /// <summary>The check that the vector the mapping will search exists; <see langword="null"/> when it does, or when it cannot be told.</summary>
    Error? ValidateVector(SourceInfo? source, IStoreConnection connection);

    InspectWarning EmptySourceWarning();

    InspectWarning UnindexedPathWarning(string path);

    InspectWarning DimensionMismatchWarning(VectorInfo vector, int embeddingSize);

    /// <summary>The records nearest to the query vector that satisfy every condition, best first.</summary>
    /// <exception cref="StoreException">The store cannot be used.</exception>
    Task<IReadOnlyList<StoreHit>> SearchAsync(IStoreConnection connection, StoreQuery query, CancellationToken cancellationToken);

    /// <summary>The code a failed search reports to the administrator and the model.</summary>
    string CodeOf(StoreFailure failure);

    /// <summary>The optional capability <typeparamref name="T"/> of this kind, or <see langword="null"/> when the kind does not declare it.</summary>
    T? Capability<T>()
        where T : class, IStoreCapability;
}
