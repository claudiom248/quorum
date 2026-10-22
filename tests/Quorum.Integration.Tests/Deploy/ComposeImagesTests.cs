using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Deploy;

/// <summary>The vector stores the compose file offers are pinned to the images the integration tests start (spec 008 D-20).</summary>
public sealed class ComposeImagesTests
{
    [Fact]
    public void TheQdrantImageIsPinnedToAVersion()
    {
        Assert.Matches(@"^qdrant/qdrant:v\d+\.\d+\.\d+$", ComposeFile.Image("qdrant/qdrant"));
    }

    [Fact]
    public void ThePgVectorImageIsAPgVectorImagePinnedToAVersion()
    {
        // pgvector/pgvector:<extension version>-pg<major>: a floating tag such as "pg17" would change the extension under the tests.
        Assert.Matches(@"^pgvector/pgvector:\d+\.\d+\.\d+-pg\d+$", ComposeFile.Image("pgvector/pgvector"));
    }

    [Fact]
    public void AnImageTheComposeFileDoesNotUseIsReported()
    {
        Assert.Throws<InvalidOperationException>(() => ComposeFile.Image("example/missing"));
    }
}
