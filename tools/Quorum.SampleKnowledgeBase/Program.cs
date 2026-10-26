using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quorum.SampleKnowledgeBase;

var options = ParseArguments(args);
if (options.ContainsKey("help"))
{
    Console.WriteLine(
        """
        Seeds sample knowledge bases into a Qdrant server (spec 003, D-20).

          --url <url>        Qdrant REST address (default http://localhost:6333, the dev compose)
          --api-key <key>    Qdrant API key, if the server needs one
          --prefix <text>    prepended to every collection name
          --schema <name>    prints the pasteable JSON Schema of 'flat', 'nested', 'relation-flat' or 'relation-langchain' and exits

        To seed sample relations into a PostgreSQL with pgvector instead (spec 008, D-18; Qdrant is then left alone):

          --pgvector <connection string>  e.g. "Host=localhost;Database=samples;Username=quorum;Password=quorum-dev"
          --relation-schema <name>        the schema the relations are created in (default public)

        By default the vectors are the deterministic 64-dimension fake of the tests. To query the collections from a real
        Quorum, embed them with the model the knowledge base will be registered with:

          --embedding-url <url>      OpenAI-compatible base address, e.g. http://localhost:4000/v1
          --embedding-model <name>   the model, e.g. nomic-embed-text
          --embedding-key <key>      API key of that endpoint, if it needs one
          --document-prefix <text>   prepended to every chunk before embedding, e.g. "search_document: "
        """);
    return 0;
}

if (options.TryGetValue("schema", out var schemaName))
{
    var schema = schemaName switch
    {
        "flat" => SampleSchemas.Flat,
        "relation-flat" => SampleSchemas.RelationFlat,
        "relation-langchain" => SampleSchemas.RelationLangChain,
        "nested" => SampleSchemas.Nested,
        _ => null,
    };
    if (schema is null)
    {
        Console.Error.WriteLine("--schema takes 'flat', 'nested', 'relation-flat' or 'relation-langchain'.");
        return 1;
    }

    Console.WriteLine(schema);
    return 0;
}

var url = options.GetValueOrDefault("url", "http://localhost:6333");
using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
if (options.TryGetValue("pgvector", out var connectionString))
{
    return await SeedRelationsAsync(connectionString);
}

var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
if (options.TryGetValue("api-key", out var apiKey))
{
    http.DefaultRequestHeaders.Add("api-key", apiKey);
}

http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

try
{
    ModelEmbedder? embedder = null;
    var datasets = SampleCollections.All(options.GetValueOrDefault("prefix", string.Empty));
    if (options.TryGetValue("embedding-url", out var embeddingUrl))
    {
        if (!options.TryGetValue("embedding-model", out var embeddingModel) || embeddingModel.Length == 0)
        {
            Console.Error.WriteLine("--embedding-url needs --embedding-model.");
            return 1;
        }

        embedder = ModelEmbedder.Create(
            services.GetRequiredService<IHttpClientFactory>().CreateClient(),
            new Uri(embeddingUrl),
            embeddingModel,
            options.GetValueOrDefault("document-prefix", string.Empty),
            options.GetValueOrDefault("embedding-key"));
        var dimensions = await embedder.ProbeDimensionsAsync(CancellationToken.None);
        Console.WriteLine($"Embedding with {embeddingModel}: {dimensions} dimensions.");
        datasets = datasets.Select(dataset => dataset.UsingModel(dimensions)).ToList();
    }

    var seeder = new QdrantSeeder(http, embedder);
    foreach (var collection in datasets)
    {
        await seeder.SeedAsync(collection, CancellationToken.None);
        Console.WriteLine($"{collection.Name}: {collection.Points.Count} points. {collection.Description}");
    }

    return 0;
}
catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or UriFormatException)
{
    Console.Error.WriteLine($"Cannot seed {url}: {exception.Message}");
    return 1;
}

async Task<int> SeedRelationsAsync(string connectionString)
{
    try
    {
        var schema = options.GetValueOrDefault("relation-schema", "public");
        var embedder = CreateEmbedder();
        var dimensions = embedder is null ? DeterministicEmbedding.DefaultDimensions : await embedder.ProbeDimensionsAsync(CancellationToken.None);
        if (embedder is not null)
        {
            Console.WriteLine($"Embedding with {options["embedding-model"]}: {dimensions} dimensions.");
        }

        var relations = SampleRelations.All(schema, dimensions, fromModel: embedder is not null);
        await using var dataSource = PgVectorSeeder.CreateDataSource(connectionString);
        var seeder = new PgVectorSeeder(dataSource, embedder);
        await seeder.SeedAsync(schema, relations, CancellationToken.None);
        foreach (var relation in relations)
        {
            Console.WriteLine($"{schema}.{relation.Name}: {relation.Rows.Count} rows. {relation.Description}");
        }

        return 0;
    }
    catch (Exception exception) when (exception is NpgsqlException or HttpRequestException or InvalidOperationException or UriFormatException or ArgumentException)
    {
        Console.Error.WriteLine($"Cannot seed the PostgreSQL database: {exception.Message}");
        return 1;
    }
}

ModelEmbedder? CreateEmbedder()
{
    if (!options.TryGetValue("embedding-url", out var embeddingUrl))
    {
        return null;
    }

    if (!options.TryGetValue("embedding-model", out var embeddingModel) || embeddingModel.Length == 0)
    {
        throw new InvalidOperationException("--embedding-url needs --embedding-model.");
    }

    return ModelEmbedder.Create(
        services.GetRequiredService<IHttpClientFactory>().CreateClient(),
        new Uri(embeddingUrl),
        embeddingModel,
        options.GetValueOrDefault("document-prefix", string.Empty),
        options.GetValueOrDefault("embedding-key"));
}

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < arguments.Length; i++)
    {
        if (!arguments[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = arguments[i][2..];
        parsed[key] = i + 1 < arguments.Length && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal) ? arguments[++i] : string.Empty;
    }

    return parsed;
}
