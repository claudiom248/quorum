using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// <see cref="IChatClient"/> over an OpenAI-compatible endpoint (<c>POST {baseUrl}/chat/completions</c>), one instance per answer.
/// It is our own adapter, not the OpenAI SDK's: the stream is read directly so that servers' reasoning fields are surfaced
/// (Ollama sends <c>delta.reasoning</c>, LiteLLM and others <c>delta.reasoning_content</c>) and so that every tool-call shape
/// servers stream is accepted.
/// </summary>
internal sealed class OpenAiChatClient(IHttpClientFactory httpClientFactory, Uri baseUrl, string modelName, string? apiKey, bool offersTools) : IChatClient
{
    public const string HttpClientName = "model-chat";

    /// <summary>
    /// <see cref="AIContent.AdditionalProperties"/> key under which a <see cref="FunctionCallContent"/> keeps the arguments text exactly
    /// as the model sent it: audit and events show that, and it is all there is when the text is not valid JSON.
    /// </summary>
    public const string RawArgumentsKey = "quorum.rawArguments";

    private const int MaxErrorBodyLength = 300;

    // The body is not HTML: keep accents and symbols as they are instead of escaping them.
    private static readonly JsonSerializerOptions BodyOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ChatClientMetadata _metadata = new("openai-compatible", baseUrl, modelName);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var tools = options?.Tools?.OfType<AIFunctionDeclaration>().Select(ToolDefinition.From).ToList() ?? [];
        var toolsJson = tools.Count == 0 ? null : ToolDefinitionRenderer.Render(tools);

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl.ToString().TrimEnd('/')}/chat/completions"))
        {
            Content = new StringContent(BuildBody(messages, options?.Instructions, toolsJson), Encoding.UTF8, "application/json"),
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

            // Calls are only collected when the answer offers tools: a model that calls a tool it was not given is ignored.
            var assembler = offersTools ? new ToolCallAssembler() : null;
            var messageId = Guid.NewGuid().ToString("N");
            var answered = false;
            var ended = false;

            while (!ended)
            {
                List<AIContent> contents;
                try
                {
                    if (!await items.MoveNextAsync() || items.Current.Data == "[DONE]")
                    {
                        ended = true;
                        contents = [];
                    }
                    else
                    {
                        contents = ParseContents(items.Current.Data, assembler);
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

                foreach (var content in contents)
                {
                    answered |= content is TextContent;
                    yield return Update(content, messageId);
                }
            }

            var calls = assembler?.Build() ?? [];

            // E.g. a reasoning model that spent its whole budget thinking: an empty answer is a failure, not a success.
            if (!answered && calls.Count == 0)
            {
                throw new ModelStreamException("The model finished without an answer. It may have run out of tokens while thinking.");
            }

            foreach (var call in calls)
            {
                yield return Update(call, messageId);
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata) ? _metadata : serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    /// <summary>The arguments of a call as the wire carries them: a JSON object, <c>{}</c> when there are none.</summary>
    public static string ArgumentsJson(FunctionCallContent call) =>
        call.Arguments is { Count: > 0 } ? JsonSerializer.Serialize(call.Arguments, BodyOptions) : "{}";

    /// <summary>The arguments of a call as the model sent them, when they are known; otherwise as the wire would carry them.</summary>
    public static string RawArguments(FunctionCallContent call) =>
        call.AdditionalProperties?.TryGetValue(RawArgumentsKey, out var raw) == true && raw is string text ? text : ArgumentsJson(call);

    /// <summary>The text a tool result becomes on the wire.</summary>
    public static string ResultText(FunctionResultContent result) => result.Result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        var other => JsonSerializer.Serialize(other, BodyOptions),
    };

    private static ChatResponseUpdate Update(AIContent content, string messageId) =>
        new(ChatRole.Assistant, [content]) { MessageId = messageId };

    private string BuildBody(IEnumerable<ChatMessage> messages, string? instructions, string? toolsJson)
    {
        var wire = new JsonArray();
        if (!string.IsNullOrEmpty(instructions))
        {
            wire.Add(new JsonObject { ["role"] = "system", ["content"] = instructions });
        }

        foreach (var message in messages)
        {
            AddWire(wire, message);
        }

        var body = new JsonObject
        {
            ["model"] = modelName,
            ["stream"] = true,
            ["messages"] = wire,
        };

        if (toolsJson is not null)
        {
            body["tools"] = JsonNode.Parse(toolsJson);
        }

        return body.ToJsonString(BodyOptions);

    }

    private static void AddWire(JsonArray wire, ChatMessage message)
    {
        // Reasoning is shown to the user, never sent back to the model.
        var text = string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));

        if (message.Role == ChatRole.Tool)
        {
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                wire.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.CallId, ["content"] = ResultText(result) });
            }

            return;
        }

        var role = message.Role == ChatRole.Assistant ? "assistant" : message.Role == ChatRole.System ? "system" : "user";
        var calls = message.Contents.OfType<FunctionCallContent>().ToList();
        var node = new JsonObject { ["role"] = role, ["content"] = text };
        if (calls.Count > 0)
        {
            node["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode)new JsonObject
            {
                ["id"] = call.CallId,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = ArgumentsJson(call) },
            }).ToArray());
        }

        wire.Add(node);
    }

    private static List<AIContent> ParseContents(string data, ToolCallAssembler? assembler)
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

        List<AIContent> contents = [];
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return contents;
        }

        if (!choices[0].TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
        {
            return contents;
        }

        AddIfPresent(contents, delta, "reasoning_content", text => new TextReasoningContent(text));
        AddIfPresent(contents, delta, "reasoning", text => new TextReasoningContent(text));
        AddIfPresent(contents, delta, "content", text => new TextContent(text));

        if (assembler is not null && delta.TryGetProperty("tool_calls", out var toolCalls))
        {
            assembler.Add(toolCalls);
        }

        return contents;
    }

    private static void AddIfPresent(List<AIContent> contents, JsonElement delta, string property, Func<string, AIContent> create)
    {
        if (delta.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
        {
            contents.Add(create(text));
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
