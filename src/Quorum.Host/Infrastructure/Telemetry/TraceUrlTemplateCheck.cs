using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quorum.Shared.Telemetry;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// Says once at startup which rule the trace link template breaks (spec 016 D-21). It names the rule and never the template,
/// which may hold a token. The app starts either way: the link is simply not shown.
/// </summary>
internal sealed partial class TraceUrlTemplateCheck(IOptions<TelemetryOptions> options, ILogger<TraceUrlTemplateCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (TraceLinks.ProblemOf(options.Value.TraceUrlTemplate) is { } problem)
        {
            LogBrokenTemplate(logger, problem);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Quorum:Telemetry:TraceUrlTemplate is ignored because it {Problem}; audit entries keep their trace id without a link")]
    private static partial void LogBrokenTemplate(ILogger logger, string problem);
}
