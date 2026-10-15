using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// Stands in for OpenAI-compatible endpoints: base URLs registered with <see cref="Serve"/> answer
/// <c>GET {baseUrl}/models</c>; every other host behaves as unreachable.
/// </summary>
public sealed class FakeModelServer : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, string[]> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    public string? LastAuthorization { get; private set; }

    public void Serve(string baseUrl, params string[] models) => _endpoints[baseUrl.TrimEnd('/')] = models;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        LastAuthorization = request.Headers.Authorization?.ToString();

        var url = request.RequestUri!.ToString();
        foreach (var (baseUrl, models) in _endpoints)
        {
            if (url.Equals($"{baseUrl}/models", StringComparison.OrdinalIgnoreCase))
            {
                var body = new { @object = "list", data = models.Select(id => new { id, @object = "model" }) };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
            }
        }

        throw new HttpRequestException($"Connection refused ({request.RequestUri!.Authority})");
    }
}
