using System.Text.Json;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

/// <summary>
/// The relations of a database that hold a vector, for the administrator to pick from (spec 008 D-5): tables, views, materialized views
/// and partitioned tables the connection can read, with their vector columns.
/// </summary>
internal sealed class PgVectorSourceListing(PgVectorConnector connector) : ISourceListingCapability
{
    public Result<IStoreConnection> ReadConnection(JsonElement? connection)
    {
        var read = PgVectorConnectionReader.Read(connection, requireTable: false);
        return read.IsSuccess ? read.Value : read.Error;
    }

    public async Task<SourceListing> ListAsync(IStoreConnection connection, string? filter, int limit, CancellationToken cancellationToken)
    {
        var pg = connection as PgVectorConnection ?? throw new ArgumentException($"A {connection.GetType().Name} is not a PgVector connection.", nameof(connection));
        try
        {
            var found = await connector.ListAsync(pg, filter, limit, cancellationToken);
            List<InspectWarning> warnings = [];
            if (found.Extension is null)
            {
                warnings.Add(PgVectorWarnings.ExtensionMissing());
            }

            return new SourceListing(Reachable: true, found.Sources, found.Truncated, warnings);
        }
        catch (StoreException exception)
        {
            return new SourceListing(Reachable: false, [], false, [PgVectorWarnings.Of(exception)]);
        }
    }
}

/// <summary>What an inspection or a verification says about a PostgreSQL database: the cause, and what follows from it for RAG.</summary>
internal static class PgVectorWarnings
{
    public static InspectWarning Of(StoreException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.Failure switch
        {
            StoreFailure.Unauthorized => new InspectWarning(
                WarningCodes.PgVectorUnauthorized,
                "The server refused the credentials, or the user cannot read the relation. Until that is fixed, RAG on this relation will fail.",
                Path: null),
            StoreFailure.Unreachable => new InspectWarning(
                WarningCodes.PgVectorUnreachable,
                $"{exception.Message} Until the server is reachable, RAG on this relation will fail.",
                Path: null),
            StoreFailure.NotFound => new InspectWarning(
                WarningCodes.PgVectorRelationNotFound,
                "The database, the schema or the relation does not exist. It can still be registered, but RAG on it will fail until it does.",
                Path: null),
            _ => new InspectWarning(
                WarningCodes.PgVectorUnexpected,
                $"{exception.Message} RAG on this relation may malfunction.",
                Path: null),
        };
    }

    public static InspectWarning ExtensionMissing() => new(
        WarningCodes.PgVectorExtensionMissing,
        "The vector extension is not installed in this database, so no relation can be searched by meaning. The data owner has to run CREATE EXTENSION vector.",
        Path: null);
}
