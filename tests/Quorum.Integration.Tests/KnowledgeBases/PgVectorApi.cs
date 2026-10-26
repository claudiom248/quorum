using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>Route and body shapes of the PgVector side of the knowledge base API, as a client would use them.</summary>
internal static class PgVectorApi
{
    public static Uri Relations { get; } = new("/api/knowledge-bases/relations", UriKind.Relative);

    /// <summary>The connection of the shared test database with separate fields, to a relation of <paramref name="schema"/>.</summary>
    public static Dictionary<string, object?> Fields(
        PgVectorEndpoint endpoint,
        string? schema = null,
        string? table = null,
        string? vectorColumn = null,
        string? distance = null,
        string? password = null,
        string? username = null,
        string? database = null) => new()
        {
            ["host"] = endpoint.Host,
            ["port"] = endpoint.Port,
            ["database"] = database ?? endpoint.Database,
            ["username"] = username ?? endpoint.Username,
            ["password"] = password ?? endpoint.Password,
            ["schema"] = schema,
            ["table"] = table,
            ["vectorColumn"] = vectorColumn,
            ["distance"] = distance,
        };

    /// <summary>The same database through a connection string.</summary>
    public static Dictionary<string, object?> ConnectionString(PgVectorEndpoint endpoint, string? schema = null, string? table = null) => new()
    {
        ["connectionString"] = $"Host={endpoint.Host};Port={endpoint.Port};Database={endpoint.Database};Username={endpoint.Username};Password={endpoint.Password}",
        ["schema"] = schema,
        ["table"] = table,
    };

    public static Task<HttpResponseMessage> PostInspectAsync(HttpClient client, object connection, object? schema = null, string model = InspectApi.EmbeddingModel, string kind = "PgVector") =>
        client.PostAsJsonAsync(
            InspectApi.Inspect,
            new { kind, connection, embeddingModel = model, queryPrefix = (string?)null, schema },
            TestContext.Current.CancellationToken);

    /// <summary>Inspects a relation that must be accepted and returns what was found.</summary>
    public static async Task<InspectApi.Report> InspectOkAsync(HttpClient client, object connection, object? schema = null, string model = InspectApi.EmbeddingModel)
    {
        using var response = await PostInspectAsync(client, connection, schema, model);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InspectApi.Report>(TestContext.Current.CancellationToken))!;
    }

    public static Task<HttpResponseMessage> PostRelationsAsync(HttpClient client, object connection, string? filter = null, string kind = "PgVector") =>
        client.PostAsJsonAsync(Relations, new { kind, connection, filter }, TestContext.Current.CancellationToken);

    public static async Task<RelationsReport> RelationsOkAsync(HttpClient client, object connection, string? filter = null)
    {
        using var response = await PostRelationsAsync(client, connection, filter);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RelationsReport>(TestContext.Current.CancellationToken))!;
    }

    public sealed record RelationsReport(bool Reachable, List<RelationBody> Relations, bool Truncated, List<InspectApi.WarningBody> Warnings)
    {
        public IEnumerable<string> WarningCodes => Warnings.Select(w => w.Code);
    }

    public sealed record RelationBody(string Schema, string Name, string Type, long? ApproximateRows, List<VectorColumnBody> VectorColumns);

    public sealed record VectorColumnBody(string Name, string Type, int? Dimensions);

    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response) => await InspectApi.ProblemCodeAsync(response);

    public static string Json(object value) => JsonSerializer.Serialize(value);
}
