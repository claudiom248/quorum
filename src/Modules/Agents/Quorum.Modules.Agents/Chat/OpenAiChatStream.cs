using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Quorum.Modules.Agents.Contracts;

namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// Streams a chat completion from an OpenAI-compatible endpoint (<c>POST {baseUrl}/chat/completions</c>).
/// The stream is read directly rather than through an SDK so that servers' reasoning fields are surfaced: Ollama
/// sends <c>delta.reasoning</c>, LiteLLM and others <c>delta.reasoning_content</c>.
/// </summary>
internal sealed class OpenAiChatStream(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "model-chat";

    private const int MaxErrorBodyLength = 300;

    public async IAsyncEnumerable<AnswerChunk> StreamAsync(
        Uri baseUrl,
        string modelName,
        string? apiKey,
        IReadOnlyList<ChatTurn> turns,
        string systemPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl.ToString().TrimEnd('/')}/chat/completions"))
        {
            Content = new StringContent(BuildBody(modelName, systemPrompt, turns), Encoding.UTF8, "application/json"),
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
                throw new ModelStreamException(
                    $"The model answered with an error (HTTP {(int)response.StatusCode}): {await ReadErrorAsync(response, cancellationToken)}");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var items = SseParser.Create(body).EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

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
                        chunks = ParseChunks(items.Current.Data);
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
                    yield return chunk;
                }
            }

            // E.g. a reasoning model that spent its whole budget thinking: an empty answer is a failure, not a success.
            if (!answered)
            {
                throw new ModelStreamException("The model finished without an answer. It may have run out of tokens while thinking.");
            }
        }
    }

    private static string BuildBody(string modelName, string systemPrompt, IReadOnlyList<ChatTurn> turns)
    {
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        messages.AddRange(turns.Select(turn => new
        {
            role = turn.Role == ChatTurnRole.User ? "user" : "assistant",
            content = turn.Content,
        }));

        return JsonSerializer.Serialize(new { model = modelName, stream = true, messages });
    }

    private static List<AnswerChunk> ParseChunks(string data)
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
