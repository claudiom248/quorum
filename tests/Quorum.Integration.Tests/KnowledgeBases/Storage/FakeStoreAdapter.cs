using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Shared.Results;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Storage;

/// <summary>An optional capability that only the tests declare.</summary>
internal interface IFakeCapability : IStoreCapability
{
    string Describe();
}

internal sealed class FakeCapability : IFakeCapability
{
    public string Describe() => "fake";
}

/// <summary>
/// A kind of store that lives in memory, to show that nothing shared between the kinds assumes Qdrant. It takes the place of the
/// only kind there is, <see cref="KnowledgeBaseKind.Qdrant"/>, and names everything its own way ("fake.*").
/// </summary>
internal sealed class FakeStoreAdapter(IReadOnlyList<FakeStoreAdapter.Row> rows, int dimensions = DeterministicEmbedding.DefaultDimensions) : IStoreAdapter
{
    public sealed record Row(string Text, JsonObject Fields)
    {
        public JsonObject ToJson()
        {
            var json = (JsonObject)Fields.DeepClone();
            json["text"] = Text;
            return json;
        }
    }

    private sealed record FakeConnection(string Name) : IStoreConnection;

    /// <summary>The queries the store received, in order.</summary>
    public List<StoreQuery> Queries { get; } = [];

    public IFakeCapability? Declared { get; init; }

    public KnowledgeBaseKind Kind => KnowledgeBaseKind.Qdrant;

    public Error SecretRequired { get; } = Error.Validation("fake.secret_required", "Fake: the secret is required.");

    public Error SecretUnreadable { get; } = Error.Unprocessable("fake.secret_unreadable", "Fake: the secret cannot be read.");

    public Result<IStoreConnection> ReadConnection(JsonElement? connection) =>
        connection is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } value
            ? new FakeConnection(value)
            : Error.Validation("knowledge_base.invalid_connection", "Fake: the connection needs a name.");

    public Result<IStoreConnection> KeepSecrets(IStoreConnection connection, ConnectionSettings? stored) => Result<IStoreConnection>.Success(connection);

    public ConnectionSettings ToSettings(IStoreConnection connection) => new(new JsonObject { ["Name"] = ((FakeConnection)connection).Name });

    public JsonObject Describe(ConnectionSettings settings) => new() { ["name"] = settings.Members["Name"]!.GetValue<string>() };

    public bool TryOpen(ConnectionSettings settings, [NotNullWhen(true)] out IStoreConnection? connection)
    {
        var name = settings.Members["Name"]!.GetValue<string>();
        connection = name == "locked" ? null : new FakeConnection(name);
        return connection is not null;
    }

    public Task<SourceProbe> ProbeAsync(IStoreConnection connection, bool sample, CancellationToken cancellationToken)
    {
        var info = new SourceInfo([new VectorInfo(null, dimensions, "Dot")], new Dictionary<string, string> { ["year"] = "integer" }, rows.Count);
        IReadOnlyList<JsonObject> sampled = sample ? [.. rows.Select(row => row.ToJson())] : [];
        return Task.FromResult(new SourceProbe(Reachable: true, info, sampled, []));
    }

    public VectorSelection SelectVector(SourceInfo source, IStoreConnection connection) => new(source.Vectors[0], null);

    public Error? ValidateVector(SourceInfo? source, IStoreConnection connection) => null;

    public InspectWarning EmptySourceWarning() => new("fake.empty", "Fake: nothing stored.", Path: null);

    public InspectWarning UnindexedPathWarning(string path) => new("fake.unindexed", $"Fake: {path} has no index.", path);

    public InspectWarning DimensionMismatchWarning(VectorInfo vector, int embeddingSize) => new("fake.dimension_mismatch", "Fake: wrong size.", Path: null);

    public Task<IReadOnlyList<StoreHit>> SearchAsync(IStoreConnection connection, StoreQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        if (((FakeConnection)connection).Name == "broken")
        {
            throw new StoreException(StoreFailure.Unreachable, "Fake: the store is down.");
        }

        IReadOnlyList<StoreHit> hits =
        [
            .. rows
                .Select(row => (Row: row, Score: Cosine(query.Vector, DeterministicEmbedding.Embed(row.Text, dimensions))))
                .Where(entry => query.Conditions.All(condition => Holds(condition, entry.Row.ToJson())))
                .Where(entry => query.ScoreThreshold is not { } threshold || entry.Score >= threshold)
                .OrderByDescending(entry => entry.Score)
                .Take(query.Limit)
                .Select(entry => new StoreHit(entry.Score, Project(entry.Row.ToJson(), query.Paths))),
        ];
        return Task.FromResult(hits);
    }

    public string CodeOf(StoreFailure failure) => $"fake.{failure}";

    public T? Capability<T>()
        where T : class, IStoreCapability => Declared as T;

    private static bool Holds(FilterCondition condition, JsonObject row) => condition switch
    {
        MatchAnyCondition any => IsOneOf(row, any.Path, any.Values),
        DocumentIdsCondition ids => IsOneOf(row, ids.Path, ids.Ids),
        _ => throw new NotSupportedException($"The fake store does not know {condition.GetType().Name}."),
    };

    private static bool IsOneOf(JsonObject row, string path, IReadOnlyList<string> values) =>
        row[path] is JsonValue value && value.TryGetValue<string>(out var text) && values.Contains(text);

    private static JsonObject Project(JsonObject row, IReadOnlyList<string> paths)
    {
        var projected = new JsonObject();
        foreach (var path in paths.Where(row.ContainsKey))
        {
            projected[path] = row[path]!.DeepClone();
        }

        return projected;
    }

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            a += left[i] * left[i];
            b += right[i] * right[i];
        }

        return dot / (Math.Sqrt(a) * Math.Sqrt(b));
    }
}
