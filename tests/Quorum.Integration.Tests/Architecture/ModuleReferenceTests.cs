using System.Xml.Linq;

namespace Quorum.Integration.Tests.Architecture;

/// <summary>
/// Modules may only see the shared kernel and other modules' Contracts projects, never their implementations.
/// </summary>
public sealed class ModuleReferenceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public static TheoryData<string> ModuleProjects()
    {
        var data = new TheoryData<string>();
        foreach (var project in Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src", "Modules"), "*.csproj", SearchOption.AllDirectories))
        {
            data.Add(Path.GetRelativePath(RepositoryRoot, project));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ModuleProjects))]
    public void ModuleReferencesOnlySharedKernelAndContracts(string project)
    {
        var references = XDocument.Load(Path.Combine(RepositoryRoot, project))
            .Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')))
            .ToList();

        var forbidden = references
            .Where(name => name != "Quorum.Shared" && !(name.StartsWith("Quorum.Modules.", StringComparison.Ordinal) && name.EndsWith(".Contracts", StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(forbidden);
    }

    [Theory]
    [MemberData(nameof(ModuleProjects))]
    public void ContractsReferenceOnlySharedKernel(string project)
    {
        if (!project.EndsWith(".Contracts.csproj", StringComparison.Ordinal))
        {
            return;
        }

        var references = XDocument.Load(Path.Combine(RepositoryRoot, project))
            .Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')));

        Assert.All(references, name => Assert.Equal("Quorum.Shared", name));
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
