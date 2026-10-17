using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Quorum.Modules.Agents.Probing;

namespace Quorum.Integration.Tests.Tools;

/// <summary>
/// Runs the real probe against a real Ollama, to check the assumption behind the "supports tools" flag (the shape of
/// <c>POST /api/show</c>). Skipped unless <c>QUORUM_TEST_OLLAMA_URL</c> is set, e.g. <c>http://localhost:11434/v1</c>, together with
/// <c>QUORUM_TEST_OLLAMA_TOOLS_MODEL</c> (a model that supports tools) and optionally <c>QUORUM_TEST_OLLAMA_NO_TOOLS_MODEL</c>.
/// </summary>
public sealed class RealOllamaTests
{
    private const string UrlVariable = "QUORUM_TEST_OLLAMA_URL";
    private const string ToolsModelVariable = "QUORUM_TEST_OLLAMA_TOOLS_MODEL";
    private const string NoToolsModelVariable = "QUORUM_TEST_OLLAMA_NO_TOOLS_MODEL";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AModelWithToolsIsReportedAsSupportingThem()
    {
        var (probe, baseUrl) = CreateProbe();
        var model = Environment.GetEnvironmentVariable(ToolsModelVariable);
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(model), $"Set {ToolsModelVariable} to a model that supports tools.");

        var result = await probe.ProbeAsync(baseUrl, apiKey: null, model, Ct);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Description);
        Assert.Contains(model!, result.Value.Models);
        Assert.True(result.Value.SupportsTools);
    }

    [Fact]
    public async Task AModelWithoutToolsIsReportedAsNotSupportingThem()
    {
        var (probe, baseUrl) = CreateProbe();
        var model = Environment.GetEnvironmentVariable(NoToolsModelVariable);
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(model), $"Set {NoToolsModelVariable} to a model that does not support tools.");

        var result = await probe.ProbeAsync(baseUrl, apiKey: null, model, Ct);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Description);
        Assert.False(result.Value.SupportsTools);
    }

    [Fact]
    public async Task AnUnknownModelLeavesTheSupportUnknown()
    {
        var (probe, baseUrl) = CreateProbe();

        var result = await probe.ProbeAsync(baseUrl, apiKey: null, "quorum-test-no-such-model:1b", Ct);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Description);
        Assert.Null(result.Value.SupportsTools);
    }

    private static (ModelEndpointProbe Probe, Uri BaseUrl) CreateProbe()
    {
        var url = Environment.GetEnvironmentVariable(UrlVariable);
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(url), $"Set {UrlVariable} to a real Ollama's OpenAI base URL to run this test.");

        var services = new ServiceCollection();
        services.AddHttpClient(ModelEndpointProbe.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return (new ModelEndpointProbe(factory, NullLogger<ModelEndpointProbe>.Instance), new Uri(url!));
    }
}
