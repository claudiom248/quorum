using System.Text.Json;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Storage;

/// <param name="Name">The column.</param>
/// <param name="Type">The store's name for the column's type, shown as it is.</param>
/// <param name="Dimensions">The dimensions the column fixes, when it does.</param>
internal sealed record ListedVector(string Name, string Type, int? Dimensions);

/// <param name="Type">What kind of relation it is: table, view, materialized view or partitioned table.</param>
/// <param name="ApproximateRows">What the store estimates, for the relations it can estimate.</param>
internal sealed record ListedSource(string Schema, string Name, string Type, long? ApproximateRows, IReadOnlyList<ListedVector> Vectors);

/// <param name="Truncated">There are more sources than were returned.</param>
internal sealed record SourceListing(bool Reachable, IReadOnlyList<ListedSource> Sources, bool Truncated, IReadOnlyList<InspectWarning> Warnings);

/// <summary>
/// The optional capability of a kind whose sources the administrator picks from a list instead of typing a name (spec 008 D-5): the
/// relations of a database. A kind that does not declare it answers the listing as an unsupported operation.
/// </summary>
internal interface ISourceListingCapability : IStoreCapability
{
    /// <summary>The <c>connection</c> object of a listing request: everything but the source, which is what is being looked for.</summary>
    Result<IStoreConnection> ReadConnection(JsonElement? connection);

    /// <summary>The sources the connection can read that hold a vector, with at most <paramref name="limit"/> of them.</summary>
    /// <param name="filter">Keeps the sources whose name contains it, ignoring case; <see langword="null"/> keeps them all.</param>
    Task<SourceListing> ListAsync(IStoreConnection connection, string? filter, int limit, CancellationToken cancellationToken);
}
