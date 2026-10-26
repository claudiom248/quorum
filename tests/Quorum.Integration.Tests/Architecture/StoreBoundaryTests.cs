namespace Quorum.Integration.Tests.Architecture;

/// <summary>
/// A knowledge base store is reached through the adapter of its kind (spec 008 D-1): the registry, inspection, verification and
/// search never name the client library or the connector of one of them.
/// </summary>
public sealed class StoreBoundaryTests
{
    private static readonly string ModuleRoot = Path.Combine(FindRepositoryRoot(), "src", "Modules", "KnowledgeBases", "Quorum.Modules.KnowledgeBases");

    public static TheoryData<string, string> Forbidden() => new()
    {
        { "Qdrant", "Qdrant.Client" },
        { "Qdrant", "QdrantConnector" },
        { "PgVector", "NpgsqlDataSource" },
        { "PgVector", "NpgsqlConnection" },
        { "PgVector", "Pgvector" },
        { "PgVector", "PgVectorConnector" },
    };

    [Theory]
    [MemberData(nameof(Forbidden))]
    public void NothingOutsideAStoreFolderReferencesItsClientOrConnector(string store, string name)
    {
        var storeFolder = Path.Combine(ModuleRoot, "Storage", store) + Path.DirectorySeparatorChar;

        var offenders = Directory.EnumerateFiles(ModuleRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.StartsWith(storeFolder, StringComparison.OrdinalIgnoreCase))
            .Where(file => !IsBuildOutput(file))
            .Where(file => File.ReadAllText(file).Contains(name, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(ModuleRoot, file))
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("Qdrant")]
    [InlineData("PgVector")]
    public void TheGuardLooksWhereTheConnectorIs(string store)
    {
        // The guard above would pass vacuously if the folder moved: the connector must still be where the test looks for it.
        Assert.True(File.Exists(Path.Combine(ModuleRoot, "Storage", store, $"{store}Connector.cs")));
    }

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}obj{separator}", StringComparison.Ordinal) || file.Contains($"{separator}bin{separator}", StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Quorum.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Quorum.slnx not found above the test output directory.");
    }
}
