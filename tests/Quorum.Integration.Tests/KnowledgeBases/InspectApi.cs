using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>Route and body shapes of the inspect API, as a client would use them.</summary>
internal static class InspectApi
{
    public const string EmbeddingModel = "nomic-embed-text";

    public static Uri Inspect { get; } = new("/api/knowledge-bases/inspect", UriKind.Relative);

    /// <summary>Makes the fake model endpoint serve the embedding model with the deterministic embedding the sample collections use.</summary>
    public static void ServeEmbeddings(QuorumApp app, string model = EmbeddingModel, int dimensions = DeterministicEmbedding.DefaultDimensions) =>
        app.ModelServer.ServeEmbeddings(QuorumApp.ModelBaseUrl, model, text => DeterministicEmbedding.Embed(text, dimensions));

    public static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string url,
        string collection,
        string? apiKey = null,
        string? vectorName = null,
        object? schema = null,
        string model = EmbeddingModel) =>
        client.PostAsJsonAsync(
            Inspect,
            new
            {
                kind = "Qdrant",
                connection = new { url, apiKey, collection, vectorName },
                embeddingModel = model,
                queryPrefix = (string?)null,
                schema,
            },
            TestContext.Current.CancellationToken);

    /// <summary>Inspects a collection that must be accepted and returns what was found.</summary>
    public static async Task<Report> InspectOkAsync(
        HttpClient client,
        Uri url,
        string collection,
        string? apiKey = null,
        string? vectorName = null,
        object? schema = null,
        string model = EmbeddingModel)
    {
        using var response = await PostAsync(client, url.ToString(), collection, apiKey, vectorName, schema, model);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Report>(TestContext.Current.CancellationToken))!;
    }

    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public sealed record Report(
        bool Reachable,
        bool CollectionExists,
        List<VectorBody> Vectors,
        int? EmbeddingSize,
        bool? DimensionMatch,
        DraftBody DraftMapping,
        List<ProposedBody> ProposedFields,
        List<string> UnindexedFields,
        List<WarningBody> Warnings)
    {
        public IEnumerable<string> WarningCodes => Warnings.Select(w => w.Code);
    }

    public sealed record VectorBody(string? Name, int Size, string Distance);

    public sealed record DraftBody(RolesBody Roles, List<FieldBody> Fields);

    public sealed record RolesBody(string? Text, string? DocumentId, string? Title, string? Position, string? SourceUrl);

    public sealed record FieldBody(string Path, string Name, string Type, Guid? DictionaryFieldId, List<string> Issues, string? Cluster = null, List<CandidateBody>? Candidates = null);

    public sealed record CandidateBody(Guid Id, string? Cluster, string Type);

    public sealed record ProposedBody(string Name, string Type);

    public sealed record WarningBody(string Code, string Message, string? Path);
}
