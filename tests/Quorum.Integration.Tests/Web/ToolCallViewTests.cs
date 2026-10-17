using System.Text.Json;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

public sealed class ToolCallViewTests
{
    private static ToolCallEvent Event(string arguments, bool confirm = false, string tool = "builtin__calculator") =>
        new(Guid.NewGuid(), tool, JsonDocument.Parse(arguments).RootElement.Clone(), confirm, confirm ? DateTimeOffset.UtcNow.AddMinutes(5) : null);

    [Theory]
    [InlineData("builtin__calculator", "calculator")]
    [InlineData("files__read_file", "files · read_file")]
    [InlineData("plain", "plain")]
    public void TheNameIsShownWithoutThePrefixOfBuiltinTools(string exposed, string shown) =>
        Assert.Equal(shown, ToolCallView.DisplayNameOf(exposed));

    [Fact]
    public void TheArgumentsAreShownIndentedAndAnEmptyObjectCountsAsNone()
    {
        var withArguments = ToolCallView.From(Event("""{"expression":"2+2"}"""));
        var withoutArguments = ToolCallView.From(Event("{}"));

        Assert.True(withArguments.HasArguments);
        Assert.Contains("\"expression\": \"2+2\"", withArguments.ArgumentsText, StringComparison.Ordinal);
        Assert.Contains('\n', withArguments.ArgumentsText);
        Assert.False(withoutArguments.HasArguments);
    }

    [Fact]
    public void ArgumentsThatWereNotValidJsonAreShownAsTheModelSentThem()
    {
        var call = ToolCallView.From(Event("\"{\\\"expression\\\": \""));

        Assert.True(call.HasArguments);
        Assert.Equal("{\"expression\": ", call.ArgumentsText);
    }

    [Fact]
    public void ACallThatNeedsConfirmationWaitsForItAndOneThatDoesNotRuns()
    {
        Assert.Equal(ToolCallState.WaitingForConfirmation, ToolCallView.From(Event("{}", confirm: true)).State);
        Assert.Equal(ToolCallState.Running, ToolCallView.From(Event("{}")).State);
        Assert.True(ToolCallView.From(Event("{}", confirm: true)).IsWaiting);
    }

    [Theory]
    [InlineData("succeeded", ToolCallState.Succeeded)]
    [InlineData("failed", ToolCallState.Failed)]
    [InlineData("denied", ToolCallState.Denied)]
    [InlineData("cancelled", ToolCallState.Cancelled)]
    [InlineData("timed_out", ToolCallState.TimedOut)]
    [InlineData("something_new", ToolCallState.Failed)]
    public void TheResultSetsTheFinalStateAndEndsTheWait(string status, ToolCallState expected)
    {
        var call = ToolCallView.From(Event("{}", confirm: true));
        call.Busy = true;

        call.Apply(new ToolResultEvent(Guid.NewGuid(), status, "text", 4));

        Assert.Equal(expected, call.State);
        Assert.Equal("text", call.Result);
        Assert.Null(call.ExpiresAt);
        Assert.False(call.Busy);
    }

    [Fact]
    public void AQuestionPutsTheCallInWaitingForAnswerAndARepliedWaitIsNotWaiting()
    {
        var call = ToolCallView.From(Event("""{"question":"Which?"}""", tool: "builtin__ask_user"));

        call.Apply(new AskUserEvent(call.CallId, "Which?", DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.Equal(ToolCallState.WaitingForAnswer, call.State);
        Assert.Equal("Which?", call.Question);

        call.MarkReplied();
        Assert.Equal(ToolCallState.Replied, call.State);
        Assert.False(call.IsWaiting);
    }

    [Theory]
    [InlineData(ToolCallState.Running)]
    [InlineData(ToolCallState.WaitingForConfirmation)]
    [InlineData(ToolCallState.WaitingForAnswer)]
    [InlineData(ToolCallState.Replied)]
    public void AbandoningACallThatHasNoResultMarksItStoppedAndEndsTheWait(ToolCallState before)
    {
        var call = ToolCallView.From(Event("{}", confirm: before == ToolCallState.WaitingForConfirmation));
        switch (before)
        {
            case ToolCallState.WaitingForAnswer:
                call.Apply(new AskUserEvent(call.CallId, "Which?", DateTimeOffset.UtcNow.AddMinutes(5)));
                break;
            case ToolCallState.Replied:
                call.Apply(new AskUserEvent(call.CallId, "Which?", DateTimeOffset.UtcNow.AddMinutes(5)));
                call.MarkReplied();
                break;
        }

        call.Abandon();

        Assert.Equal(ToolCallState.Cancelled, call.State);
        Assert.False(call.IsWaiting);
        Assert.Null(call.ExpiresAt);
    }

    [Fact]
    public void AbandoningACallThatHasItsResultChangesNothing()
    {
        var call = ToolCallView.From(Event("{}"));
        call.Apply(new ToolResultEvent(call.CallId, "succeeded", "4", 1));

        call.Abandon();

        Assert.Equal(ToolCallState.Succeeded, call.State);
    }

    [Fact]
    public void AReplyAfterTheResultDoesNotBringTheWaitBack()
    {
        var call = ToolCallView.From(Event("{}", confirm: true));
        call.Apply(new ToolResultEvent(call.CallId, "timed_out", "late", 4));

        call.MarkReplied();

        Assert.Equal(ToolCallState.TimedOut, call.State);
    }
}
