using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Quorum.McpDemoServer;

[McpServerToolType]
internal sealed class DemoTools
{
    [McpServerTool(Name = "get_weather", ReadOnly = true), Description("Returns the current weather of a city (demo data).")]
    public static string GetWeather([Description("City name")] string city) => $"{city}: 21 C, sunny, light wind.";

    [McpServerTool(Name = "add_note"), Description("Saves a note (demo: nothing is stored).")]
    public static string AddNote([Description("Note text")] string text) => $"Saved note: {text}";

    [McpServerTool(Name = "huge_result", ReadOnly = true), Description("Returns a very large text, to test the result limit.")]
    public static string HugeResult() => new('x', 50_000);

    [McpServerTool(Name = "always_fails"), Description("Always fails, to test error handling.")]
    public static string AlwaysFails() => throw new McpException("The demo backend is down.");
}
