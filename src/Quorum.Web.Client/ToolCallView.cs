using System.Text.Json;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

public enum ToolCallState
{
    /// <summary>The tool is running, or the model is being given the result.</summary>
    Running,

    /// <summary>The answer is paused until the user approves or denies the call.</summary>
    WaitingForConfirmation,

    /// <summary>The answer is paused until the user answers the model's question.</summary>
    WaitingForAnswer,

    /// <summary>The user's reply was sent; the result follows.</summary>
    Replied,

    Succeeded,
    Failed,
    Denied,
    Cancelled,
    TimedOut,
}

/// <summary>A tool call of the answer being watched: what the model asked, where it stands and what came of it.</summary>
public sealed class ToolCallView
{
    // The default encoder would show "+" as +: the user must read the arguments as the model sent them. The text is shown
    // as text, never as markup, so relaxed escaping is safe here.
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private ToolCallView()
    {
    }

    public Guid CallId { get; private init; }

    /// <summary>The name the model used, e.g. <c>native__calculator</c>.</summary>
    public string ToolName { get; private init; } = string.Empty;

    /// <summary>The arguments as the user reads them before approving: indented JSON, or the text the model sent.</summary>
    public string ArgumentsText { get; private init; } = string.Empty;

    public bool HasArguments { get; private init; }

    public ToolCallState State { get; private set; }

    /// <summary>When a call waiting for the user counts as unanswered.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public string? Question { get; private set; }

    public string? Result { get; private set; }

    /// <summary>A reply is being sent.</summary>
    public bool Busy { get; set; }

    public bool IsWaiting => State is ToolCallState.WaitingForConfirmation or ToolCallState.WaitingForAnswer;

    /// <summary><c>calculator</c> for <c>native__calculator</c>, <c>files · read</c> for <c>files__read</c>.</summary>
    public string DisplayName => DisplayNameOf(ToolName);

    /// <summary>The name of a tool as people read it, from the name the model uses.</summary>
    public static string DisplayNameOf(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        var split = toolName.IndexOf("__", StringComparison.Ordinal);
        if (split < 0)
        {
            return toolName;
        }

        var origin = toolName[..split];
        var name = toolName[(split + 2)..];
        return origin == "native" ? name : $"{origin} · {name}";
    }

    public string StatusLabel => State switch
    {
        ToolCallState.Running => "Running…",
        ToolCallState.WaitingForConfirmation => "Waiting for your approval",
        ToolCallState.WaitingForAnswer => "Waiting for your answer",
        ToolCallState.Replied => "Reply sent…",
        ToolCallState.Succeeded => "Done",
        ToolCallState.Failed => "Failed",
        ToolCallState.Denied => "Denied",
        ToolCallState.Cancelled => "Stopped",
        ToolCallState.TimedOut => "Timed out",
        _ => throw new InvalidOperationException($"Unknown state {State}."),
    };

    public static ToolCallView From(ToolCallEvent call)
    {
        ArgumentNullException.ThrowIfNull(call);

        var arguments = call.Arguments;
        var text = arguments.ValueKind switch
        {
            JsonValueKind.String => arguments.GetString() ?? string.Empty,
            JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
            _ => JsonSerializer.Serialize(arguments, Indented),
        };

        return new ToolCallView
        {
            CallId = call.CallId,
            ToolName = call.Tool,
            ArgumentsText = text,
            HasArguments = text.Length > 0 && !(arguments.ValueKind == JsonValueKind.Object && !arguments.EnumerateObject().Any()),
            State = call.RequiresConfirmation ? ToolCallState.WaitingForConfirmation : ToolCallState.Running,
            ExpiresAt = call.RequiresConfirmation ? call.ExpiresAt : null,
        };
    }

    public void Apply(AskUserEvent ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        Question = ask.Question;
        ExpiresAt = ask.ExpiresAt;
        State = ToolCallState.WaitingForAnswer;
    }

    public void Apply(ToolResultEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Result = result.Content;
        ExpiresAt = null;
        Busy = false;
        State = result.Status switch
        {
            "succeeded" => ToolCallState.Succeeded,
            "denied" => ToolCallState.Denied,
            "cancelled" => ToolCallState.Cancelled,
            "timed_out" => ToolCallState.TimedOut,
            _ => ToolCallState.Failed,
        };
    }

    /// <summary>The answer ended with no result for this call (stopped, connection lost): nothing waits for the user any more.</summary>
    public void Abandon()
    {
        if (Result is not null || State is ToolCallState.Cancelled)
        {
            return;
        }

        Busy = false;
        ExpiresAt = null;
        State = ToolCallState.Cancelled;
    }

    /// <summary>The user's reply reached the server; the dialog or prompt goes away until the result arrives.</summary>
    public void MarkReplied()
    {
        Busy = false;
        if (IsWaiting)
        {
            State = ToolCallState.Replied;
        }
    }
}
