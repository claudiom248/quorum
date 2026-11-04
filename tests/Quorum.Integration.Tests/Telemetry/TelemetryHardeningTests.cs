using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Quorum.Host.Infrastructure.Telemetry;
using Quorum.Modules.Agents.Tools;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Quorum.Integration.Tests.Telemetry;

/// <summary>The rules around what telemetry sends and when, checked without starting the app (spec 016 D-2, D-8, D-10, D-18).</summary>
[Collection("Telemetry")]
public sealed class TelemetryHardeningTests
{
    [Fact] // AC-7: JSON carried in a string value, as a tool result is written on the span, is opened and redacted
    public void TheRedactorOpensJsonCarriedInAStringValue()
    {
        var written = "\"{\\\"api_key\\\":\\\"resultsecret999\\\",\\\"note\\\":\\\"fine\\\"}\"";

        var redacted = ContentGuardProcessor.RedactText(written);

        Assert.DoesNotContain("resultsecret999", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
        Assert.Contains("fine", redacted, StringComparison.Ordinal);
    }

    [Fact] // AC-7: the messages carry tool results again; their text is kept
    public void TheRedactorRedactsSecretsInMessageHistoryAndKeepsTheText()
    {
        var messages = "[{\"role\":\"tool\",\"parts\":[{\"type\":\"tool_call_response\",\"response\":\"{\\\"token\\\":\\\"historysecret777\\\"}\"}]},"
            + "{\"role\":\"user\",\"content\":\"[1] a source that starts with a bracket\"}]";

        var redacted = ContentGuardProcessor.RedactText(messages);

        Assert.DoesNotContain("historysecret777", redacted, StringComparison.Ordinal);
        Assert.Contains("[1] a source that starts with a bracket", redacted, StringComparison.Ordinal);
    }

    [Theory] // finding 11: the signal-specific endpoint turns the export on too
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", true)]
    [InlineData("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", true)]
    [InlineData("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", false)]
    public void TheExportIsOnWhenTheTracesEndpointOrTheGeneralOneIsSet(string variable, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [variable] = "http://collector:4317" }).Build();

        Assert.Equal(expected, TelemetrySetup.IsExportEnabled(configuration));
    }

    [Theory] // finding 9: only the upload of an attachment is always traced, not the reads of the list
    [InlineData("POST", "/api/conversations/abc/attachments", true)]
    [InlineData("GET", "/api/conversations/abc/attachments", false)]
    [InlineData("GET", "/api/conversations/abc/attachments/def/text", false)]
    [InlineData("POST", "/api/conversations/abc/messages", true)]
    [InlineData("GET", "/api/agents", false)]
    public void OnlyTheAnswerPathAndAttachmentUploadsAreAlwaysTraced(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(expected, TelemetryFilters.ShouldTrace(context, adminRequests: false));
    }

    [Fact] // finding 6: no ids for a log written outside a recorded trace
    public void ALogEventKeepsNoTraceIdWhenItsActivityIsNotRecorded()
    {
        var capture = new CapturingSink();
        var sink = new RecordedTraceLogSink(capture);

        sink.Emit(EventWithTrace());

        var received = Assert.Single(capture.Events);
        Assert.Null(received.TraceId);
        Assert.Equal("hello", received.MessageTemplate.Text);
    }

    [Fact] // finding 6: the ids stay when the activity is recorded
    public void ALogEventKeepsItsTraceIdWhenItsActivityIsRecorded()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == nameof(TelemetryHardeningTests),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource(nameof(TelemetryHardeningTests));
        using var activity = source.StartActivity("recorded");
        Assert.True(activity is { Recorded: true });

        var capture = new CapturingSink();
        new RecordedTraceLogSink(capture).Emit(EventWithTrace());

        Assert.NotNull(Assert.Single(capture.Events).TraceId);
    }

    [Fact] // spec 016 D-11, D-20: the audit links only to a trace that is recorded, so a sampler that drops it leaves no dead link
    public void TheAuditGetsATraceIdOnlyForARecordedTrace()
    {
        var sampling = ActivitySamplingResult.PropagationData;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "TelemetryHardeningTests.Audit",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("TelemetryHardeningTests.Audit");

        using var dropped = source.StartActivity("dropped");
        Assert.NotNull(dropped);
        Assert.False(dropped.Recorded);
        Assert.Null(ToolCallInvoker.TraceIdFor(dropped, exporting: true));

        sampling = ActivitySamplingResult.AllDataAndRecorded;
        using var kept = source.StartActivity("kept");
        Assert.NotNull(kept);
        Assert.Equal(kept.TraceId.ToHexString(), ToolCallInvoker.TraceIdFor(kept, exporting: true));
        Assert.Null(ToolCallInvoker.TraceIdFor(kept, exporting: false));
        Assert.Null(ToolCallInvoker.TraceIdFor(null, exporting: true));
    }

    private static LogEvent EventWithTrace() => new(
        DateTimeOffset.UtcNow,
        LogEventLevel.Information,
        null,
        new MessageTemplateParser().Parse("hello"),
        [],
        ActivityTraceId.CreateRandom(),
        ActivitySpanId.CreateRandom());

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
