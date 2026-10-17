using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quorum.Modules.Agents.Contracts;

namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// Streams one round of a chat completion from an OpenAI-compatible endpoint (<c>POST {baseUrl}/chat/completions</c>).
/// The stream is read directly rather than through an SDK so that servers' reasoning fields are surfaced: Ollama
/// sends <c>delta.reasoning</c>, LiteLLM and others <c>delta.reasoning_content</c>.
/// </summary>
internal sealed class OpenAiChatStream(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "model-chat";

    private const int MaxErrorBodyLength = 300;

    // The body is not HTML: keep accents and symbols as they are instead of escaping them.
    private static readonly JsonSerializerOptions BodyOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// One model call: yields text and reasoning as they arrive and, when the round ends, the tool calls the model asked for.
    /// </summary>
    /// <param name="toolsJson">The rendered tool definitions, or <see langword="null"/> to send none (and ignore any call the model makes).</param>
    public async IAsyncEnumerable<RoundPart> StreamRoundAsync(
        Uri baseUrl,
        string modelName,
        string? apiKey,
        IReadOnlyList<ModelMessage> messages,
        string? toolsJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl.ToString().TrimEnd('/')}/chat/completions"))
        {
            Content = new StringContent(BuildBody(modelName, messages, toolsJson), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ModelStreamException($"Cannot reach the model at {baseUrl}: {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The HTTP client's timeout, not the user stopping the answer: the model never started to answer.
            throw new ModelStreamException($"The model at {baseUrl} did not start answering in time.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorAsync(response, cancellationToken);
                throw new ModelStreamException(
                    response.StatusCode == HttpStatusCode.BadRequest && toolsJson is not null
                        ? $"The model refused the request while tools were attached (HTTP 400: {detail}). If this model does not support tools, " +
                          "turn off \"Supports tools\" for its endpoint in the settings, or deactivate the assistant's tools."
                        : $"The model answered with an error (HTTP {(int)response.StatusCode}): {detail}");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var items = SseParser.Create(body).EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

            var assembler = toolsJson is null ? null : new ToolCallAssembler();
            var answered = false;
            var ended = false;

            while (!ended)
            {
                List<AnswerChunk> chunks;
                try
                {
                    if (!await items.MoveNextAsync() || items.Current.Data == "[DONE]")
                    {
                        ended = true;
                        chunks = [];
                    }
                    else
                    {
                        chunks = ParseChunks(items.Current.Data, assembler);
                    }
                }
                catch (IOException exception)
                {
                    throw new ModelStreamException($"The connection to the model broke: {exception.Message}", exception);
                }
                catch (HttpRequestException exception)
                {
                    throw new ModelStreamException($"The connection to the model broke: {exception.Message}", exception);
                }
                catch (JsonException exception)
                {
                    throw new ModelStreamException("The model sent a response that is not OpenAI-compatible.", exception);
                }

                foreach (var chunk in chunks)
                {
                    answered |= chunk.Kind == AnswerChunkKind.Text;
                    yield return RoundPart.Of(chunk);
                }
            }

            var calls = assembler?.Build() ?? [];

            // E.g. a reasoning model that spent its whole budget thinking: an empty answer is a failure, not a success.
            if (!answered && calls.Count == 0)
            {
                throw new ModelStreamException("The model finished without an answer. It may have run out of tokens while thinking.");
            }

            if (calls.Count > 0)
            {
                yield return RoundPart.Calls(calls);
            }
        }
    }

    private static string BuildBody(string modelName, IReadOnlyList<ModelMessage> messages, string? toolsJson)
    {
        var body = new JsonObject
        {
            ["model"] = modelName,
            ["stream"] = true,
            ["messages"] = new JsonArray(messages.Select(ToWire).ToArray()),
        };

        if (toolsJson is not null)
        {
            body["tools"] = JsonNode.Parse(toolsJson);
        }

        return body.ToJsonString(BodyOptions);
    }

    private static JsonNode ToWire(ModelMessage message) => message switch
    {
        SystemMessage system => new JsonObject { ["role"] = "system", ["content"] = system.Content },
        UserMessage user => new JsonObject { ["role"] = "user", ["content"] = user.Content },
        AssistantMessage { ToolCalls.Count: 0 } assistant => new JsonObject { ["role"] = "assistant", ["content"] = assistant.Content },
        AssistantMessage assistant => new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = assistant.Content,
            ["tool_calls"] = new JsonArray(assistant.ToolCalls.Select(call => (JsonNode)new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments },
            }).ToArray()),
        },
        ToolMessage tool => new JsonObject { ["role"] = "tool", ["tool_call_id"] = tool.ToolCallId, ["content"] = tool.Content },
        _ => throw new ArgumentOutOfRangeException(nameof(message)),
    };

    private static List<AnswerChunk> ParseChunks(string data, ToolCallAssembler? assembler)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var text)
                ? text.GetString()
                : error.ToString();
            throw new ModelStreamException($"The model reported an error: {message}");
        }

        List<AnswerChunk> chunks = [];
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return chunks;
        }

        if (!choices[0].TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
        {
            return chunks;
        }

        AddIfPresent(chunks, delta, "reasoning_content", AnswerChunkKind.Reasoning);
        AddIfPresent(chunks, delta, "reasoning", AnswerChunkKind.Reasoning);
        AddIfPresent(chunks, delta, "content", AnswerChunkKind.Text);

        if (assembler is not null && delta.TryGetProperty("tool_calls", out var toolCalls))
        {
            assembler.Add(toolCalls);
        }

        return chunks;
    }

    private static void AddIfPresent(List<AnswerChunk> chunks, JsonElement delta, string property, AnswerChunkKind kind)
    {
        if (delta.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
        {
            chunks.Add(new AnswerChunk(kind, text));
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return response.ReasonPhrase ?? "no details";
        }

        body = body.Trim();
        return body.Length <= MaxErrorBodyLength ? body : body[..MaxErrorBodyLength] + "…";
    }
}
