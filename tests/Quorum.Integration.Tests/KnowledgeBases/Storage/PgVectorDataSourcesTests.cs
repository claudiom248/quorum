using Quorum.Modules.KnowledgeBases.Storage.PgVector;

namespace Quorum.Integration.Tests.KnowledgeBases.Storage;

/// <summary>The pools of connections of the PgVector knowledge bases: bounded, and never handed out disposed.</summary>
public sealed class PgVectorDataSourcesTests
{
    private static PgVectorConnection Connection(int number) =>
        new($"db{number}.example.test", 5432, "knowledge", "reader", "secret", null, null, "public", "chunks", null, PgVectorDistance.Cosine);

    [Fact]
    public void ADataSourceIsNeverDisposedWhenMoreThanTheMostConnectionsAreInUse()
    {
        using var sources = new PgVectorDataSources();

        var last = sources.For(Connection(0));
        for (var i = 1; i <= 70; i++)
        {
            last = sources.For(Connection(i));
        }

        // A disposed data source refuses to make a connection; one that is alive does (nothing is opened here).
        using var connection = last.CreateConnection();
        Assert.NotNull(connection);
        Assert.Same(last, sources.For(Connection(70)));
    }

    [Fact]
    public void TheCommandTimeoutOfTheConnectionIsNotTheDriversThirtySeconds()
    {
        var connectionString = Connection(1).ToNpgsqlConnectionString();

        Assert.Contains("Command Timeout=0", connectionString, StringComparison.Ordinal);
    }
}
