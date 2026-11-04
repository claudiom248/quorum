namespace Quorum.Integration.Tests.Telemetry;

/// <summary>
/// The tests that export spans run alone: an activity listener is process-wide, so the spans of apps that run at the same time
/// reach every app's in-memory exporter (spec 016 D-23).
/// </summary>
[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class SpanExportGroup;
