using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Quorum.Modules.Agents.Chat;

/// <summary>Builds a chat client for an OpenAI-compatible endpoint, over the factory's HTTP client.</summary>
internal sealed class ChatClientFactory(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "model-chat";

    // Some local servers (e.g. Ollama) ignore the key but the OpenAI client requires one.
    private const string PlaceholderApiKey = "unused";

    public IChatClient Create(Uri baseUrl, string modelName, string? apiKey)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = baseUrl,
            Transport = new HttpClientPipelineTransport(httpClientFactory.CreateClient(HttpClientName)),
            // A failed answer is reported to the user, who can regenerate it; silent retries only add latency.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            // Small local models can take minutes before the first token on modest hardware.
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };

        return new OpenAIClient(new ApiKeyCredential(apiKey ?? PlaceholderApiKey), options)
            .GetChatClient(modelName)
            .AsIChatClient();
    }
}
