namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// A <see cref="SharedQuorumApp"/> that exports spans to the in-memory collector, for the tests of spans that need no other setting
/// of the start (spec 016 D-23). Tests that change the configuration of the start, or move the clock, keep an app of their own.
/// </summary>
public sealed class SharedTelemetryApp(PostgresFixture postgres) : SharedQuorumApp(postgres)
{
    protected override bool Telemetry => true;
}
