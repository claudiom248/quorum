using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
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
          --schema <name>    prints the pasteable JSON Schema of 'flat' or 'nested' and exits
        """);
    return 0;
}

if (options.TryGetValue("schema", out var schemaName))
{
    var schema = schemaName switch
    {
        "flat" => SampleSchemas.Flat,
        "nested" => SampleSchemas.Nested,
        _ => null,
    };
    if (schema is null)
    {
        Console.Error.WriteLine("--schema takes 'flat' or 'nested'.");
        return 1;
    }

    Console.WriteLine(schema);
    return 0;
}

var url = options.GetValueOrDefault("url", "http://localhost:6333");
using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
if (options.TryGetValue("api-key", out var apiKey))
{
    http.DefaultRequestHeaders.Add("api-key", apiKey);
}

http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

try
{
    var seeder = new QdrantSeeder(http);
    foreach (var collection in SampleCollections.All(options.GetValueOrDefault("prefix", string.Empty)))
    {
        await seeder.SeedAsync(collection, CancellationToken.None);
        Console.WriteLine($"{collection.Name}: {collection.Points.Count} points. {collection.Description}");
    }

    return 0;
}
catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
{
    Console.Error.WriteLine($"Cannot seed {url}: {exception.Message}");
    return 1;
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
