using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Skills;

/// <summary>Export of skills as SKILL.md files and zip archives, and the round trip through the import (spec 017, PR 4, AC-28).</summary>
public sealed class SkillExportTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly Uri Skills = new("/api/skills", UriKind.Relative);
    private static readonly Uri Export = new("/api/skills/export", UriKind.Relative);
    private static readonly Uri Preview = new("/api/skills/import/preview", UriKind.Relative);
    private static readonly Uri Import = new("/api/skills/import", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Uri One(Guid id) => new($"/api/skills/{id}", UriKind.Relative);

    private static Uri OneExport(Guid id) => new($"/api/skills/{id}/export", UriKind.Relative);

    private static async Task<Guid> CreateAsync(HttpClient client, string slug, string description, string body)
    {
        using var response = await client.PostAsJsonAsync(Skills, new
        {
            slug,
            displayName = slug,
            description,
            body,
            scope = "Tenant",
            scopeTenantIds = Array.Empty<Guid>(),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static Dictionary<string, string> ReadZip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            });
    }

    [Fact] // AC-28
    public async Task AnExportedZipImportsBackWithTheSameSlugsDescriptionsAndBodies()
    {
        var client = host.Admin;
        var alphaBody = "\n# Alpha\n\nLine with a colon: here.\n\n- one\n- two\n";
        var betaBody = "# Beta\n\n1. Read it.\n";
        var alpha = await CreateAsync(client, "alpha", "Use alpha: when asked, always.", alphaBody);
        var beta = await CreateAsync(client, "beta", "Beta does the other thing.", betaBody);

        var zip = await client.GetByteArrayAsync(Export, Ct);
        var entries = ReadZip(zip);
        Assert.Equal(["alpha/SKILL.md", "beta/SKILL.md"], entries.Keys.Order());

        // The same tenant with the skills gone: the archive brings them back exactly.
        await client.DeleteAsync(One(alpha), Ct);
        await client.DeleteAsync(One(beta), Ct);
        using var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(zip), "files", "skills.zip");
        using var previewResponse = await client.PostAsync(Preview, upload, Ct);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>(Ct);
        using var confirm = await client.PostAsJsonAsync(Import, new { previewId = preview.GetProperty("previewId").GetGuid(), decisions = Array.Empty<object>() }, Ct);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>(Skills, Ct);
        var restored = list.EnumerateArray().ToDictionary(s => s.GetProperty("slug").GetString()!, s => s.GetProperty("id").GetGuid());
        Assert.Equal(["alpha", "beta"], restored.Keys.Order());
        var restoredAlpha = await client.GetFromJsonAsync<JsonElement>(One(restored["alpha"]), Ct);
        var restoredBeta = await client.GetFromJsonAsync<JsonElement>(One(restored["beta"]), Ct);
        Assert.Equal("Use alpha: when asked, always.", restoredAlpha.GetProperty("description").GetString());
        Assert.Equal(alphaBody, restoredAlpha.GetProperty("body").GetString());
        Assert.Equal("Beta does the other thing.", restoredBeta.GetProperty("description").GetString());
        Assert.Equal(betaBody, restoredBeta.GetProperty("body").GetString());
    }

    [Fact]
    public async Task OneSkillIsDownloadedAsASkillMarkdownFileWithItsFrontmatter()
    {
        var client = host.Admin;
        var id = await CreateAsync(client, "gamma", "Gamma does it.", "# Gamma\n");

        using var response = await client.GetAsync(OneExport(id), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("---\n", text, StringComparison.Ordinal);
        Assert.Contains("name: gamma\n", text, StringComparison.Ordinal);
        Assert.True(text.Contains("description: Gamma does it.\n---\n", StringComparison.Ordinal), $"Actual file: [{text}]");
        Assert.EndsWith("# Gamma\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("displayName", text, StringComparison.Ordinal);
        Assert.DoesNotContain("scope", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASelectionIsExportedAndAnUnknownSkillIsNotFound()
    {
        var client = host.Admin;
        var one = await CreateAsync(client, "one", "First.", "1");
        await CreateAsync(client, "two", "Second.", "2");

        var selected = ReadZip(await client.GetByteArrayAsync(new Uri($"/api/skills/export?ids={one}", UriKind.Relative), Ct));
        Assert.Equal(["one/SKILL.md"], selected.Keys);

        using var missing = await client.GetAsync(OneExport(Guid.NewGuid()), Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ExportNeedsTheSkillsPermission()
    {
        var id = await CreateAsync(host.Admin, "private", "Private.", "secret");
        using var member = await host.App.CreateMemberClientAsync();
        using var anonymous = host.App.CreateUiClient();

        using var asMember = await member.GetAsync(OneExport(id), Ct);
        using var asAnonymous = await anonymous.GetAsync(Export, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
    }
}
