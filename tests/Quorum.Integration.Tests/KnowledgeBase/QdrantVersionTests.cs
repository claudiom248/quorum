using Quorum.Modules.KnowledgeBase.Storage.Qdrant;

namespace Quorum.Integration.Tests.KnowledgeBase;

public sealed class QdrantVersionTests
{
    [Theory]
    [InlineData("1.10.0", true)]
    [InlineData("1.12.0", true)]
    [InlineData("1.9.7", false)]
    [InlineData("1.7.4", false)]
    [InlineData("2.0.0", true)]
    public void SearchNeedsTheQueryApiOfQdrantOnePointTenOrLater(string version, bool supported) =>
        Assert.Equal(supported, QdrantConnector.IsSupported(Version.Parse(version)));

    [Fact]
    public void TheConnectionNeverPrintsItsKey()
    {
        var connection = new QdrantConnection(new Uri("http://qdrant:6333"), "docs", "top-secret", VectorName: null);

        Assert.DoesNotContain("top-secret", connection.ToString(), StringComparison.Ordinal);
    }
}
