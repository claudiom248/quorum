using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Mapping;

public sealed class DeterministicEmbeddingTests
{
    private static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();

    [Fact]
    public void TheSameTextGivesTheSameVectorEveryTime()
    {
        Assert.Equal(DeterministicEmbedding.Embed("annual leave policy"), DeterministicEmbedding.Embed("annual leave policy"));
    }

    [Fact]
    public void TheVectorHasTheRequestedSizeAndLengthOne()
    {
        var vector = DeterministicEmbedding.Embed("annual leave policy", 128);

        Assert.Equal(128, vector.Length);
        Assert.Equal(1.0, Math.Sqrt(vector.Sum(x => (double)x * x)), precision: 5);
    }

    [Fact]
    public void CaseAndPunctuationDoNotMatter()
    {
        Assert.Equal(DeterministicEmbedding.Embed("Annual leave, policy!"), DeterministicEmbedding.Embed("annual   leave policy"));
    }

    [Fact]
    public void TextsThatShareWordsAreCloserThanUnrelatedOnes()
    {
        var query = DeterministicEmbedding.Embed("how many days of annual leave");
        var related = DeterministicEmbedding.Embed("employees accrue days of annual leave per year");
        var unrelated = DeterministicEmbedding.Embed("passwords need fourteen characters and multi factor authentication");

        Assert.True(Cosine(query, related) > Cosine(query, unrelated));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void AnEmptyTextStillGivesAUsableUnitVector(string text)
    {
        var vector = DeterministicEmbedding.Embed(text);

        Assert.Equal(1.0, Math.Sqrt(vector.Sum(x => (double)x * x)), precision: 5);
    }
}
