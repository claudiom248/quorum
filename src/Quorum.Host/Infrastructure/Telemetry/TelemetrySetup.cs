using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Quorum.Shared.Telemetry;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// Wires OpenTelemetry into the Host (spec 016). Export is on only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (D-1); with no
/// endpoint no tracer provider exists, so no activity is created and nothing leaves the installation.
/// </summary>
public static class TelemetrySetup
{
    private const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVariable = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string HeadersVariable = "OTEL_EXPORTER_OTLP_HEADERS";

    /// <summary>Whether this installation exports telemetry: the standard endpoint variable is set (spec 016 D-1, D-5).</summary>
    public static bool IsExportEnabled(IConfiguration configuration) => !string.IsNullOrWhiteSpace(configuration[EndpointVariable]);

    public static WebApplicationBuilder AddQuorumTelemetry(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<TelemetryOptions>().BindConfiguration(TelemetryOptions.SectionName);
        var exporting = IsExportEnabled(builder.Configuration);
        builder.Services.PostConfigure<TelemetryOptions>(options => options.Exporting = exporting);
        if (!exporting)
        {
            return builder;
        }

        var telemetry = builder.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();
        var version = typeof(TelemetrySetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(QuorumActivityNames.ServiceName, serviceVersion: version))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(
                        QuorumActivityNames.AgentsSource,
                        QuorumActivityNames.ConversationsSource,
                        QuorumActivityNames.KnowledgeBasesSource,
                        QuorumActivityNames.MicrosoftExtensionsAiSource,
                        QuorumActivityNames.MicrosoftAgentsAiSource)
                    .AddAspNetCoreInstrumentation(options =>
                        options.Filter = context => TelemetryFilters.ShouldTrace(context, telemetry.AdminRequests))
                    .AddHttpClientInstrumentation()
                    .AddNpgsql()
                    .AddProcessor(new QuorumTagsProcessor());

                // Before the exporter: the content of tool calls is redacted and cut before a span can be sent (spec 016 D-2).
                if (telemetry.CaptureContent)
                {
                    tracing.AddProcessor(new ContentGuardProcessor(ContentGuardProcessor.LimitFrom(Environment.GetEnvironmentVariable("OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT"))));
                }

                // Batched and exported off the request path; a slow or dead collector only fills the bounded queue (spec 016 D-19).
                tracing.AddProcessor(serviceProvider => new BatchActivityExportProcessor(new OtlpExportFailureReporter(
                    new OtlpTraceExporter(new OtlpExporterOptions()),
                    serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Quorum.Telemetry"))));
            });

        // A template that breaks a rule is ignored and named once at startup; only with export on, since there are no traces to link otherwise (D-21, AC-23, AC-24).
        builder.Services.AddHostedService<TraceUrlTemplateCheck>();

        return builder;
    }

    /// <summary>Sends the log events to the collector too, correlated with the span they were written in (spec 016 D-18).</summary>
    public static LoggerConfiguration WriteOpenTelemetryLogs(this LoggerConfiguration logger, IConfiguration configuration)
    {
        if (!IsExportEnabled(configuration))
        {
            return logger;
        }

        var endpoint = configuration[EndpointVariable]!;
        var protocol = configuration[ProtocolVariable];
        var useHttp = string.Equals(protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase);
        var version = typeof(TelemetrySetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        return logger.WriteTo.OpenTelemetry(options =>
        {
            // The HTTP endpoint of the logs signal is the base URL plus its path; the gRPC one is the base URL itself.
            options.Endpoint = useHttp && !endpoint.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase)
                ? endpoint.TrimEnd('/') + "/v1/logs"
                : endpoint;
            options.Protocol = useHttp ? OtlpProtocol.HttpProtobuf : OtlpProtocol.Grpc;
            options.Headers = ParseHeaders(configuration[HeadersVariable]);
            options.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = QuorumActivityNames.ServiceName,
                ["service.version"] = version,
            };
        });
    }

    /// <summary>Parses <c>key=value,key=value</c> (values percent-encoded, as the standard defines them). Never logged.</summary>
    internal static Dictionary<string, string> ParseHeaders(string? raw)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return headers;
        }

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            headers[pair[..separator].Trim()] = Uri.UnescapeDataString(pair[(separator + 1)..].Trim());
        }

        return headers;
    }
}
