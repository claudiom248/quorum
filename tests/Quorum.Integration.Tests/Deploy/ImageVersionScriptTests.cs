using System.ComponentModel;
using System.Diagnostics;

namespace Quorum.Integration.Tests.Deploy;

/// <summary>The version stamped on the container image is derived from git describe by <c>deploy/image-version.sh</c>.</summary>
public sealed class ImageVersionScriptTests
{
    [Theory]
    [InlineData("v0.6.0-0-gce392e3", "0.6.0")]
    [InlineData("0.6.0-0-gce392e3", "0.6.0")]
    [InlineData("v0.6.0-3-gabc1234", "0.6.1-alpha.0.3+abc1234")]
    [InlineData("v1.2.3-10-gdeadbee", "1.2.4-alpha.0.10+deadbee")]
    [InlineData("v0.6.0-0-gce392e3-dirty", "0.6.1-alpha.0.0+ce392e3.dirty")]
    [InlineData("v1.2.3-10-gdeadbee-dirty", "1.2.4-alpha.0.10+deadbee.dirty")]
    [InlineData("abc1234", "0.0.0-local+abc1234")]
    [InlineData("abc1234-dirty", "0.0.0-local+abc1234.dirty")]
    [InlineData("", "0.0.0-local")]
    public async Task TheVersionComesFromWhatGitDescribeSays(string describe, string expected)
    {
        Assert.Equal(expected, await RunAsync(describe));
    }

    [Fact]
    public async Task EveryVersionItPrintsIsValidSemanticVersioning()
    {
        // MinVerVersionOverride refuses anything else, which would fail the image build.
        var semVer = new global::System.Text.RegularExpressions.Regex(
            @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$");

        foreach (var describe in new[] { "v0.6.0-0-gce392e3", "v0.6.0-3-gabc1234-dirty", "abc1234", "abc1234-dirty", string.Empty })
        {
            var version = await RunAsync(describe);
            Assert.Matches(semVer, version);
        }
    }

    private static async Task<string> RunAsync(string describe)
    {
        var script = ScriptPath();
        var start = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(describe);

        Process? process = null;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            Assert.Skip("A POSIX shell (sh) is not available on this machine.");
        }

        using (process)
        {
            var output = await process!.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            return output.Trim();
        }
    }

    private static string ScriptPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "deploy", "image-version.sh");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("deploy/image-version.sh was not found above the test output directory.");
    }
}
