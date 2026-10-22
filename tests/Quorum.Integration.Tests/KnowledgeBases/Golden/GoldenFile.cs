using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Quorum.Integration.Tests.KnowledgeBases.Golden;

/// <summary>
/// Pins the JSON a route returns. Everything that changes from run to run (ids, instants, the address and the collections of the
/// store, scores below the fourth decimal) is replaced by a placeholder, so what is compared is the shape, the codes and the messages.
/// Set <c>QUORUM_UPDATE_GOLDEN=1</c> to write the files instead of comparing, after reviewing why the contract changed.
/// </summary>
internal sealed partial class GoldenFile(string name)
{
    private const string UpdateVariable = "QUORUM_UPDATE_GOLDEN";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly JsonObject _scenarios = [];
    private readonly List<(string Value, string Placeholder)> _replacements = [];

    /// <summary>Text that is replaced wherever it appears in a string of the output.</summary>
    public GoldenFile Replace(string value, string placeholder)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _replacements.Add((value, placeholder));
        }

        return this;
    }

    /// <summary>Records what a route answered: the status and the body, as JSON when it is JSON.</summary>
    public async Task RecordAsync(string scenario, HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        JsonNode? body;
        try
        {
            body = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            body = JsonValue.Create(text);
        }

        Record(scenario, (int)response.StatusCode, body);
    }

    public void Record(string scenario, HttpStatusCode status, JsonNode? body) => Record(scenario, (int)status, body);

    public void Verify()
    {
        var actual = Normalize(_scenarios)!.ToJsonString(Pretty).ReplaceLineEndings("\n") + "\n";
        var path = PathOf(name);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"The golden file {path} does not exist: run once with {UpdateVariable}=1 to write it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private void Record(string scenario, int status, JsonNode? body)
    {
        var entry = new JsonObject { ["status"] = status, ["body"] = body?.DeepClone() };
        _scenarios[scenario] = entry;
    }

    // The vectors of a collection come in the order the store sends them, which is not fixed: they are compared by name.
    private JsonNode? Normalize(JsonNode? node, bool sorted = false) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .Where(property => property.Key != "traceId")
            .Select(property => KeyValuePair.Create(property.Key, Normalize(property.Value, sorted: property.Key == "vectors")))),
        JsonArray array => new JsonArray(array
            .Select(item => Normalize(item))
            .OrderBy(item => sorted ? item?["name"]?.GetValue<string>() : null, StringComparer.Ordinal)
            .ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(NormalizeText(text)),
        JsonValue value when value.TryGetValue<double>(out var number) && number != Math.Floor(number) => JsonValue.Create(Math.Round(number, 4)),
        _ => node?.DeepClone(),
    };

    private string NormalizeText(string text)
    {
        foreach (var (value, placeholder) in _replacements.OrderByDescending(r => r.Value.Length))
        {
            text = text.Replace(value, placeholder, StringComparison.Ordinal);
        }

        text = GuidPattern().Replace(text, "<guid>");
        return InstantPattern().Replace(text, "<instant>");
    }

    private static string PathOf(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Quorum.slnx")))
            {
                return Path.Combine(directory.FullName, "tests", "Quorum.Integration.Tests", "KnowledgeBases", "Golden", file + ".json");
            }
        }

        throw new InvalidOperationException("Quorum.slnx not found above the test output directory.");
    }

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex InstantPattern();
}
