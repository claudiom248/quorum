using System.Text;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// A fake embedding that needs no model: a hashed bag of words, normalised. Two texts that share words get vectors that point the
/// same way, so tests and the sample collections can assert a real ranking. The hash is FNV-1a over the word, never
/// <see cref="string.GetHashCode()"/>, so the same text gives the same vector in every process.
/// </summary>
public static class DeterministicEmbedding
{
    /// <summary>The size the sample collections use, and the size tests ask the fake model endpoint for.</summary>
    public const int DefaultDimensions = 64;

    public static float[] Embed(string text, int dimensions = DefaultDimensions)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);

        var vector = new float[dimensions];
        foreach (var word in Words(text))
        {
            var hash = Fnv1a(word);
            // A second bit of the hash picks the sign, so unrelated words tend to cancel out instead of piling up.
            vector[(int)(hash % (uint)dimensions)] += (hash & 0x8000_0000) == 0 ? 1f : -1f;
        }

        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (norm == 0)
        {
            // No words: any fixed unit vector will do, and it must not be all zeros (cosine distance rejects it).
            vector[0] = 1f;
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }

        return vector;
    }

    private static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                word.Append(char.ToLowerInvariant(character));
            }
            else if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    private static uint Fnv1a(string word)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(word))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return hash;
    }
}
