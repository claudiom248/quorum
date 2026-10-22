using System.Text.RegularExpressions;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// The base <c>docker-compose.yml</c>, which pins the images the tests run against so they exercise what operators run.
/// </summary>
public static partial class ComposeFile
{
    /// <summary>The pinned image of the first service that uses <paramref name="repository"/>, for example <c>qdrant/qdrant</c>.</summary>
    public static string Image(string repository)
    {
        var match = ImageLine().Matches(ReadText()).FirstOrDefault(candidate => candidate.Groups["repository"].Value == repository);
        return match is null
            ? throw new InvalidOperationException($"No {repository} image in docker-compose.yml.")
            : match.Groups["image"].Value;
    }

    private static string ReadText()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var compose = Path.Combine(directory.FullName, "docker-compose.yml");
            if (File.Exists(compose))
            {
                return File.ReadAllText(compose);
            }
        }

        throw new InvalidOperationException("docker-compose.yml not found above the test output directory.");
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>(?<repository>[^:\s]+):\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ImageLine();
}
