using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

/// <summary>
/// What is specific to a PostgreSQL database with pgvector (spec 008): the shape of its connection, the password or connection string
/// and the rule that keeps it, what is read from the catalog and sampled from a relation, the vector column, and the mapping of its
/// failures to the codes of the kind. It declares two optional capabilities: listing its relations and the lookups (A2-1).
/// </summary>
internal sealed class PgVectorStoreAdapter(PgVectorConnector connector, KnowledgeBaseSecretProtector protector, PgVectorSourceListing listing, PgVectorLookups lookups) : IStoreAdapter
{
    private static readonly Error PasswordRequired = Error.Validation(
        "knowledge_base.password_required",
        "The stored password cannot be used here: enter the password (or the connection string) again. It is only reused for the same host, port, database and username, and it cannot be read if the installation's keys were replaced.");

    private static readonly Error PasswordUnreadable = Error.Unprocessable(
        "knowledge_base.password_unreadable",
        "The knowledge base's password cannot be read. An administrator has to enter it again.");

    private static readonly Error ConnectionRequired = Error.Validation(
        "knowledge_base.invalid_connection",
        "The connection is required: host, database and username (with the password), or a connectionString.");

    // Iterative index scans arrived in 0.8; before it a filtered approximate search returns too few rows (D-12).
    private static readonly Version MinimumRecommendedVersion = new(0, 8);

    public KnowledgeBaseKind Kind => KnowledgeBaseKind.PgVector;

    public Error SecretRequired => PasswordRequired;

    public Error SecretUnreadable => PasswordUnreadable;

    public Result<IStoreConnection> ReadConnection(JsonElement? connection)
    {
        var read = PgVectorConnectionReader.Read(connection, requireTable: true);
        return read.IsSuccess ? read.Value : read.Error;
    }

    public Result<IStoreConnection> KeepSecrets(IStoreConnection connection, ConnectionSettings? stored)
    {
        var pg = Own(connection);
        var previous = stored?.Read<PgVectorConnectionSettings>();

        if (pg.UsesConnectionString)
        {
            return pg;
        }

        if (pg.UsesFields)
        {
            if (pg.Password is not null)
            {
                return pg;
            }

            // An omitted password keeps the stored one, but only for the same server and account: sent anywhere else it could be read there.
            // Leaving the connection string for fields is a change of how the secret is given, so the new secret is needed.
            if (previous is { ProtectedPassword: { } protectedPassword })
            {
                return SameAccount(previous, pg) && protector.TryUnprotect(protectedPassword, out var plain)
                    ? pg with { Password = plain }
                    : PasswordRequired;
            }

            return previous is { ProtectedConnectionString: not null } ? PasswordRequired : pg;
        }

        // Neither fields nor a string: an edit that leaves the connection string as it was.
        if (previous is { ProtectedConnectionString: { } protectedString })
        {
            return protector.TryUnprotect(protectedString, out var plain) && plain is not null
                ? pg with { ConnectionString = plain }
                : PasswordRequired;
        }

        return ConnectionRequired;
    }

    public ConnectionSettings ToSettings(IStoreConnection connection)
    {
        var pg = Own(connection);
        return ConnectionSettings.From(
            new PgVectorConnectionSettings(
                pg.Host,
                pg.Port,
                pg.Database,
                pg.Username,
                pg.SslMode,
                protector.Protect(pg.Password),
                protector.Protect(pg.ConnectionString),
                pg.Schema,
                pg.Table ?? throw new InvalidOperationException("A knowledge base is stored with its relation."),
                pg.VectorColumn,
                pg.Distance.ToString()));
    }

    public JsonObject Describe(ConnectionSettings settings)
    {
        var pg = settings.Read<PgVectorConnectionSettings>();
        return new JsonObject
        {
            ["host"] = pg.Host,
            ["port"] = pg.Port,
            ["database"] = pg.Database,
            ["username"] = pg.Username,
            ["sslMode"] = pg.SslMode,
            ["hasPassword"] = pg.ProtectedPassword is not null,
            ["hasConnectionString"] = pg.ProtectedConnectionString is not null,
            ["schema"] = pg.Schema,
            ["table"] = pg.Table,
            ["vectorColumn"] = pg.VectorColumn,
            ["distance"] = pg.Distance,
        };
    }

    public bool TryOpen(ConnectionSettings settings, [NotNullWhen(true)] out IStoreConnection? connection)
    {
        var pg = settings.Read<PgVectorConnectionSettings>();
        if (!protector.TryUnprotect(pg.ProtectedPassword, out var password) || !protector.TryUnprotect(pg.ProtectedConnectionString, out var connectionString))
        {
            connection = null;
            return false;
        }

        connection = new PgVectorConnection(
            pg.Host,
            pg.Port,
            pg.Database,
            pg.Username,
            password,
            pg.SslMode,
            connectionString,
            pg.Schema,
            pg.Table,
            pg.VectorColumn,
            Enum.TryParse<PgVectorDistance>(pg.Distance, out var distance) ? distance : PgVectorDistance.Cosine);
        return true;
    }

    public async Task<SourceProbe> ProbeAsync(IStoreConnection connection, bool sample, CancellationToken cancellationToken)
    {
        var pg = Own(connection);
        List<InspectWarning> warnings = [];
        PgVectorProbe found;
        try
        {
            found = await connector.ProbeAsync(pg, sample, cancellationToken);
        }
        catch (StoreException exception)
        {
            warnings.Add(PgVectorWarnings.Of(exception));
            return new SourceProbe(Reachable: false, Info: null, [], warnings);
        }

        if (found.Extension is null)
        {
            warnings.Add(PgVectorWarnings.ExtensionMissing());
        }

        if (found.Relation is not { } relation)
        {
            warnings.Add(new InspectWarning(
                WarningCodes.PgVectorRelationNotFound,
                $"The database has no table, view, materialized view or partitioned table named '{pg.SourceName}'. It can still be registered, but RAG on it will fail until the relation exists.",
                Path: null));
            return new SourceProbe(Reachable: true, Info: null, [], warnings);
        }

        List<InspectWarning> notes = [];
        var vectors = relation.Columns
            .Where(column => column.IsVector)
            .Select(column => VectorOf(relation, column, found.MeasuredDimensions, notes))
            .ToList();
        if (vectors.Count == 0)
        {
            warnings.Add(new InspectWarning(
                WarningCodes.VectorColumnNotFound,
                $"'{pg.SourceName}' has no vector or halfvec column, so there is nothing to search by meaning. Register a relation that has one.",
                Path: null));
        }

        var rows = found.Rows;
        var recordCount = RecordCount(relation, rows.Count, sampled: sample);
        return new SourceProbe(
            Reachable: true,
            new SourceInfo(vectors, IndexedPaths(relation), recordCount, IndexesKnown: !relation.IsView),
            rows,
            warnings,
            AddNotes(notes, pg, found.Extension, relation, vectors),
            Declared(relation));
    }

    /// <summary>What never changes the status (D-13): worth fixing, none of it stops a search.</summary>
    private static List<InspectWarning> AddNotes(List<InspectWarning> notes, PgVectorConnection pg, Version? extension, PgVectorRelation relation, List<VectorInfo> vectors)
    {
        if (extension is not null && extension < MinimumRecommendedVersion)
        {
            notes.Add(new InspectWarning(
                WarningCodes.PgVectorOldVersion,
                $"pgvector {extension} is older than {MinimumRecommendedVersion}: a search with filters on an approximate index can return fewer rows than asked for. Upgrade the extension.",
                Path: null));
        }

        var searched = pg.VectorColumn is { } named ? vectors.FirstOrDefault(v => v.Name == named) : vectors.Count == 1 ? vectors[0] : null;
        if (searched is not null && !relation.IsView && !relation.Indexes.Any(index => index.Column == searched.Name && index.Method is "hnsw" or "ivfflat"))
        {
            notes.Add(new InspectWarning(
                WarningCodes.PgVectorVectorUnindexed,
                $"'{searched.Name}' has no vector index, so every search reads the whole relation: exact, but slow on large ones. Ask the data owner to create an HNSW or IVFFlat index.",
                searched.Name));
        }

        if (relation.CanWrite)
        {
            notes.Add(new InspectWarning(
                WarningCodes.PgVectorWritable,
                "The user Quorum connects with can change this relation (INSERT, UPDATE, DELETE or TRUNCATE). Quorum never writes to it: use a read-only role.",
                Path: null));
        }

        return notes;
    }

    /// <summary>
    /// A column the mapping reads that is gone is reported by name: it stops the search when it is the text, and warns otherwise.
    /// Keys inside a <c>json</c> column are left to the sample. A filter on a mapped column or on the document id with no index is slow.
    /// </summary>
    public MappingCheck CheckMapping(SourceInfo source, DeclaredProperties? declared, KnowledgeBaseMapping mapping)
    {
        List<InspectWarning> problems = [];
        List<InspectWarning> notes = [];
        HashSet<string> covered = [];

        if (declared is not null)
        {
            var known = declared.Properties.Select(p => p.Path).Concat(declared.Warnings.Select(w => w.Path).OfType<string>()).ToHashSet(StringComparer.Ordinal);
            var roles = mapping.Roles;
            var rolePaths = new[] { roles.Text, roles.DocumentId, roles.Title, roles.Position, roles.SourceUrl }.OfType<string>();
            foreach (var path in rolePaths.Concat(mapping.Fields.Select(f => f.Path)).Distinct(StringComparer.Ordinal))
            {
                if (path.Contains('.', StringComparison.Ordinal) || known.Contains(path) || source.Vectors.Any(v => v.Name == path))
                {
                    continue;
                }

                var isText = path == roles.Text;
                var warning = new InspectWarning(
                    WarningCodes.PgVectorColumnMissing,
                    isText
                        ? $"The column '{path}' the text is read from no longer exists, so RAG answers have nothing to read. Map the text to another column."
                        : $"The column '{path}' no longer exists: filters and results that use it are ignored until the mapping is corrected.",
                    path);
                (isText ? problems : notes).Add(warning);
                covered.Add(path);
            }
        }

        // What lookups read besides the fields: the counted point, the entity name and the document id (A2-8).
        var indexed = mapping.Fields.Select(f => f.Path)
            .Concat(new[] { mapping.Roles.DocumentId, mapping.CountedPoint?.Path, mapping.EntityName }.OfType<string>())
            .Distinct(StringComparer.Ordinal);
        foreach (var path in indexed.Where(path => !covered.Contains(path)))
        {
            if (UnindexedPathWarning(source, path) is { } unindexed)
            {
                notes.Add(path == mapping.CountedPoint?.Path ? lookups.UnindexedWarning(path, "counted point")
                    : path == mapping.EntityName ? lookups.UnindexedWarning(path, "entity name")
                    : path == mapping.Roles.DocumentId ? unindexed with { Message = $"'{path}' identifies the document and has no index: a search limited to some documents, and the lookups that count documents, can be slow." }
                    : unindexed);
            }
        }

        return new MappingCheck(problems, notes, covered);
    }

    public VectorSelection SelectVector(SourceInfo source, IStoreConnection connection)
    {
        var column = Own(connection).VectorColumn;
        if (column is not null)
        {
            var named = source.Vectors.FirstOrDefault(v => v.Name == column);
            return named is not null
                ? new VectorSelection(named, null)
                : new VectorSelection(
                    null,
                    new InspectWarning(
                        WarningCodes.VectorColumnNotFound,
                        $"The relation has no vector column named '{column}'. Its vector columns are: {Names(source)}.",
                        Path: null));
        }

        return source.Vectors.Count == 1
            ? new VectorSelection(source.Vectors[0], null)
            : new VectorSelection(
                null,
                new InspectWarning(
                    WarningCodes.VectorColumnRequired,
                    $"The relation has several vector columns ({Names(source)}): choose the one to search before saving.",
                    Path: null));
    }

    /// <summary>The vector column the mapping will search must exist: required when the relation has several, and it must be one of them.</summary>
    public Error? ValidateVector(SourceInfo? source, IStoreConnection connection)
    {
        var column = Own(connection).VectorColumn;
        if (source is null || source.Vectors.Count == 0)
        {
            return null;
        }

        if (column is null)
        {
            return source.Vectors.Count > 1
                ? Error.Validation(WarningCodes.VectorColumnRequired, $"The relation has several vector columns ({Names(source)}): choose the one to search.")
                : null;
        }

        return source.Vectors.Any(v => v.Name == column)
            ? null
            : Error.Validation(WarningCodes.VectorColumnNotFound, $"The relation has no vector column named '{column}'. Its vector columns are: {Names(source)}.");
    }

    public InspectWarning EmptySourceWarning() => new(
        WarningCodes.PgVectorEmptyRelation,
        "The relation has no rows yet, so there is nothing to sample: write the mapping by hand or paste the row's JSON Schema.",
        Path: null);

    /// <summary>Keys inside a <c>json</c> column are not checked, and a plain view has no indexes of its own to look at.</summary>
    public InspectWarning? UnindexedPathWarning(SourceInfo source, string path)
    {
        if (!source.IndexesKnown || path.Contains('.', StringComparison.Ordinal) || source.IndexedPaths.ContainsKey(path))
        {
            return null;
        }

        return new InspectWarning(
            WarningCodes.PgVectorFieldUnindexed,
            $"'{path}' has no index: filters on it work but can be slow on large relations. Ask the data owner to index it if you plan to filter on it.",
            path);
    }

    public InspectWarning DimensionMismatchWarning(VectorInfo vector, int embeddingSize) => new(
        WarningCodes.PgVectorDimensionMismatch,
        $"The vector column has {vector.Size} dimensions but the embedding model produces {embeddingSize}. RAG searches would fail: ingestion and retrieval must use the same model.",
        Path: null);

    public InspectWarning DimensionsUnknownWarning(VectorInfo vector) => new(
        WarningCodes.PgVectorDimensionsUnknown,
        $"The vector column '{vector.Name}' does not declare its dimensions and the sample has no value in it, so they cannot be compared with the embedding model's. A search fails if they differ.",
        Path: null);

    public Task<IReadOnlyList<StoreHit>> SearchAsync(IStoreConnection connection, StoreQuery query, CancellationToken cancellationToken) =>
        connector.SearchAsync(Own(connection), query, cancellationToken);

    public T? Capability<T>()
        where T : class, IStoreCapability => listing as T ?? lookups as T;

    public string CodeOf(StoreFailure failure) => failure switch
    {
        StoreFailure.Unreachable => WarningCodes.PgVectorUnreachable,
        StoreFailure.Unauthorized => WarningCodes.PgVectorUnauthorized,
        StoreFailure.NotFound => WarningCodes.PgVectorRelationNotFound,
        StoreFailure.Rejected => WarningCodes.PgVectorDimensionMismatch,
        _ => WarningCodes.PgVectorUnexpected,
    };

    // The distance is the operator class of the index on the column; with none, or with different ones, the draft is Cosine and says so.
    private static VectorInfo VectorOf(PgVectorRelation relation, PgVectorColumn column, IReadOnlyDictionary<string, int> measured, List<InspectWarning> notes)
    {
        var classes = relation.Indexes
            .Where(index => index.Column == column.Name && index.Method is "hnsw" or "ivfflat")
            .Select(index => DistanceOf(index.OperatorClass))
            .Where(distance => distance is not null)
            .Distinct()
            .ToList();

        PgVectorDistance distance;
        if (classes is [{ } only])
        {
            distance = only;
        }
        else
        {
            distance = PgVectorDistance.Cosine;
            notes.Add(new InspectWarning(
                WarningCodes.PgVectorDistanceDefaulted,
                classes.Count == 0
                    ? $"'{column.Name}' has no vector index to tell the distance from, so Cosine is proposed. Check which distance the embedding model was trained for."
                    : $"'{column.Name}' has indexes with different distances, so Cosine is proposed. Check which distance the embedding model was trained for.",
                column.Name));
        }

        var size = column.Dimensions ?? (measured.TryGetValue(column.Name, out var dimensions) ? dimensions : 0);
        return new VectorInfo(column.Name, size, distance.ToString());
    }

    private static PgVectorDistance? DistanceOf(string operatorClass) => operatorClass switch
    {
        "vector_cosine_ops" or "halfvec_cosine_ops" => PgVectorDistance.Cosine,
        "vector_l2_ops" or "halfvec_l2_ops" => PgVectorDistance.L2,
        "vector_ip_ops" or "halfvec_ip_ops" => PgVectorDistance.InnerProduct,
        _ => null,
    };

    // Only the leading column of an index helps a filter on that column alone; the vector indexes are about the vector, not about a filter.
    private static Dictionary<string, string> IndexedPaths(PgVectorRelation relation)
    {
        var types = relation.Columns.ToDictionary(column => column.Name, column => column.DisplayType, StringComparer.Ordinal);
        return relation.Indexes
            .Where(index => index.Method is not ("hnsw" or "ivfflat") && types.ContainsKey(index.Column))
            .Select(index => index.Column)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(column => column, column => types[column], StringComparer.Ordinal);
    }

    // The sample is exact: no rows in it means no rows in the relation. Otherwise the catalog's estimate is all there is (negative before the first analyse).
    private static long? RecordCount(PgVectorRelation relation, int sampledRows, bool sampled)
    {
        if (sampled && sampledRows == 0)
        {
            return 0;
        }

        return relation.ApproximateRows > 0 || sampledRows > 0 ? Math.Max(relation.ApproximateRows, sampledRows) : null;
    }

    // The column types are better evidence than a sample of values (D-9): a column of dates is a date column, and an empty one still has a type.
    private static DeclaredProperties Declared(PgVectorRelation relation)
    {
        List<DiscoveredProperty> properties = [];
        List<InspectWarning> warnings = [];
        foreach (var column in relation.Columns.Where(column => !column.IsVector))
        {
            if (column.HasUnsupportedName)
            {
                warnings.Add(new InspectWarning(
                    WarningCodes.PgVectorColumnNameUnsupported,
                    $"The column '{column.Name}' has a '.' (or no name) in its name, which a path cannot address, so it is left out.",
                    column.Name));
            }
            else if (column.FieldType is { } type)
            {
                properties.Add(new DiscoveredProperty(column.Name, type));
            }
            else if (column.Kind != PgVectorColumnKind.Json)
            {
                warnings.Add(new InspectWarning(
                    WarningCodes.PgVectorColumnNotMapped,
                    string.Create(CultureInfo.InvariantCulture, $"The column '{column.Name}' has the type {column.DisplayType}, which cannot be mapped to a metadata field, so it is left out."),
                    column.Name));
            }
        }

        return new DeclaredProperties(properties, warnings);
    }

    private static bool SameAccount(PgVectorConnectionSettings stored, PgVectorConnection requested) =>
        string.Equals(stored.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
        && (stored.Port ?? PgVectorConnection.DefaultPort) == (requested.Port ?? PgVectorConnection.DefaultPort)
        && string.Equals(stored.Database, requested.Database, StringComparison.Ordinal)
        && string.Equals(stored.Username, requested.Username, StringComparison.Ordinal);

    private static string Names(SourceInfo source) => string.Join(", ", source.Vectors.Select(v => $"'{v.Name}'"));

    private static PgVectorConnection Own(IStoreConnection connection) =>
        connection as PgVectorConnection ?? throw new ArgumentException($"A {connection.GetType().Name} is not a PgVector connection.", nameof(connection));
}
