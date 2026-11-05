using System.IO.Compression;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Skills;

/// <summary>Import of skills from files, folders and archives: preview, then confirmation (spec 017, PR 3, AC-18 to AC-27, AC-31).</summary>
public sealed class SkillImportTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly Uri Preview = new("/api/skills/import/preview", UriKind.Relative);
    private static readonly Uri Import = new("/api/skills/import", UriKind.Relative);
    private static readonly Uri Skills = new("/api/skills", UriKind.Relative);
    private static readonly Uri Settings = new("/api/skills/settings", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SkillMd(string name, string description = "Does the work.", string body = "# Steps\n\n1. Do it.", string extra = "") =>
        $"---\nname: {name}\ndescription: {description}\n{extra}---\n{body}\n";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static MultipartFormDataContent Upload(params (string Path, byte[] Content)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (path, content) in files)
        {
            form.Add(new ByteArrayContent(content), "files", path);
        }

        return form;
    }

    private static byte[] Zip(params (string Path, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var entry = archive.CreateEntry(path).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static async Task<JsonElement> PreviewAsync(HttpClient client, MultipartFormDataContent form)
    {
        using var response = await client.PostAsync(Preview, form, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static string StatusOf(JsonElement preview, string slug) =>
        preview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("slug").GetString() == slug).GetProperty("status").GetString()!;

    private static async Task<JsonElement> ConfirmAsync(HttpClient client, string previewId, params (string Slug, string Action)[] decisions)
    {
        using var response = await client.PostAsJsonAsync(Import, new
        {
            previewId,
            decisions = decisions.Select(d => new { slug = d.Slug, action = d.Action }).ToArray(),
        }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<JsonElement?> SkillAsync(HttpClient client, string slug)
    {
        var list = await client.GetFromJsonAsync<JsonElement>(Skills, Ct);
        var match = list.EnumerateArray().FirstOrDefault(s => s.GetProperty("slug").GetString() == slug);
        if (match.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var id = match.GetProperty("id").GetGuid();
        return await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/skills/{id}", UriKind.Relative), Ct);
    }

    private static async Task<int> CountSkillsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>(Skills, Ct)).GetArrayLength();

    [Fact] // AC-18
    public async Task ASingleSkillFileIsPreviewedAndCreatedWithItsSlugAsDisplayName()
    {
        var client = host.Admin;
        var preview = await PreviewAsync(client, Upload(("SKILL.md", Bytes(SkillMd("contract-review")))));

        Assert.Equal("new", StatusOf(preview, "contract-review"));
        var result = await ConfirmAsync(client, preview.GetProperty("previewId").GetString()!, ("contract-review", "skip"));

        Assert.Equal(["contract-review"], result.GetProperty("created").EnumerateArray().Select(e => e.GetString()));
        var skill = await SkillAsync(client, "contract-review");
        Assert.Equal("contract-review", skill!.Value.GetProperty("displayName").GetString());
        Assert.Equal("Tenant", skill.Value.GetProperty("scope").GetString());
    }

    [Fact] // AC-31
    public async Task TheHumanizedStyleNamesNewSkillsAndNeverRenamesExistingOnes()
    {
        var client = host.Admin;
        var keep = await PreviewAsync(client, Upload(("keep-me/SKILL.md", Bytes(SkillMd("keep-me")))));
        await ConfirmAsync(client, keep.GetProperty("previewId").GetString()!);
        await client.PutAsJsonAsync(new Uri("/api/skills/" + (await SkillAsync(client, "keep-me"))!.Value.GetProperty("id").GetGuid(), UriKind.Relative),
            new { slug = "keep-me", displayName = "Kept name", description = "Does the work.", body = "# Steps", scope = "Tenant", scopeTenantIds = Array.Empty<Guid>() }, Ct);
        await client.PutAsJsonAsync(Settings, new { displayNameStyle = "Humanized" }, Ct);

        var preview = await PreviewAsync(client, Upload(("code-review/SKILL.md", Bytes(SkillMd("code-review")))));
        await ConfirmAsync(client, preview.GetProperty("previewId").GetString()!, ("code-review", "skip"));

        Assert.Equal("Code review", (await SkillAsync(client, "code-review"))!.Value.GetProperty("displayName").GetString());
        Assert.Equal("Kept name", (await SkillAsync(client, "keep-me"))!.Value.GetProperty("displayName").GetString());
    }

    [Fact] // AC-19
    public async Task AFolderAndArchivesAreSearchedTwoLevelsDeepRegardlessOfFolderNames()
    {
        var client = host.Admin;
        var folder = await PreviewAsync(client, Upload(
            ("first/SKILL.md", Bytes(SkillMd("first-skill"))),
            ("outer/inner/SKILL.md", Bytes(SkillMd("second-skill"))),
            ("a/b/c/SKILL.md", Bytes(SkillMd("too-deep")))));

        var zipped = await PreviewAsync(client, Upload(("bundle.zip", Zip(
            ("x/SKILL.md", Bytes(SkillMd("zip-one"))),
            ("y/z/SKILL.md", Bytes(SkillMd("zip-two")))))));

        Assert.Equal("new", StatusOf(folder, "first-skill"));
        Assert.Equal("new", StatusOf(folder, "second-skill"));
        Assert.DoesNotContain(folder.GetProperty("items").EnumerateArray(), i => i.GetProperty("slug").GetString() == "too-deep");
        Assert.Equal("new", StatusOf(zipped, "zip-one"));
        Assert.Equal("new", StatusOf(zipped, "zip-two"));
    }

    [Fact] // AC-20
    public async Task HundredsOfSkillsInOneArchiveAreAllPreviewedAndConfirmed()
    {
        var client = host.Admin;
        var entries = Enumerable.Range(1, 300).Select(i => ($"skill-{i:000}/SKILL.md", Bytes(SkillMd($"skill-{i:000}")))).ToArray();
        var preview = await PreviewAsync(client, Upload(("many.zip", Zip(entries))));

        Assert.Equal(300, preview.GetProperty("items").GetArrayLength());
        var result = await ConfirmAsync(client, preview.GetProperty("previewId").GetString()!);
        Assert.Equal(300, result.GetProperty("created").GetArrayLength());
    }

    [Fact] // AC-21
    public async Task AnUploadOverTheLimitIsRejectedAndNothingIsWritten()
    {
        var client = host.Admin;
        var big = new byte[21 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        using var response = await client.PostAsync(Preview, Upload(("huge.zip", big)), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        // A bomb that is small on the wire but extracts to more than the limit.
        var bomb = Zip(("bomb/SKILL.md", new byte[25 * 1024 * 1024]));
        using var bombResponse = await client.PostAsync(Preview, Upload(("bomb.zip", bomb)), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, bombResponse.StatusCode);
        Assert.Equal(0, await CountSkillsAsync(client));
    }

    [Fact] // AC-22
    public async Task AnArchiveWithTraversalOrLinksIsRejectedAndNothingIsWritten()
    {
        var client = host.Admin;
        var traversal = Zip(("../escape/SKILL.md", Bytes(SkillMd("escaped"))));
        using var traversalResponse = await client.PostAsync(Preview, Upload(("evil.zip", traversal)), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, traversalResponse.StatusCode);

        using var tarStream = new MemoryStream();
        using (var gzip = new GZipStream(tarStream, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "linked/SKILL.md") { LinkName = "/etc/passwd" });
        }

        using var linkResponse = await client.PostAsync(Preview, Upload(("links.tar.gz", tarStream.ToArray())), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, linkResponse.StatusCode);
        Assert.Equal(0, await CountSkillsAsync(client));
    }

    [Fact] // AC-23
    public async Task AnInvalidSkillIsReportedAndTheOthersProceed()
    {
        var client = host.Admin;
        var preview = await PreviewAsync(client, Upload(
            ("bad-name/SKILL.md", Bytes(SkillMd("Bad Name"))),
            ("no-description/SKILL.md", Bytes("---\nname: no-description\n---\nBody.\n")),
            ("empty-body/SKILL.md", Bytes(SkillMd("empty-body", body: "   "))),
            ("good/SKILL.md", Bytes(SkillMd("good")))));

        Assert.Equal("invalid", StatusOf(preview, "Bad Name"));
        Assert.Equal("invalid", StatusOf(preview, "no-description"));
        Assert.Equal("invalid", StatusOf(preview, "empty-body"));
        Assert.Equal("new", StatusOf(preview, "good"));
        var result = await ConfirmAsync(client, preview.GetProperty("previewId").GetString()!);
        Assert.Equal(["good"], result.GetProperty("created").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact] // AC-24
    public async Task ExtraFilesAndFieldsAreReportedAndOnlyNameDescriptionAndBodyAreStored()
    {
        var client = host.Admin;
        var extra = "license: MIT\nmetadata: {owner: team}\nallowed-tools: [shell]\n";
        var preview = await PreviewAsync(client, Upload(
            ("pack/SKILL.md", Bytes(SkillMd("pack", extra: extra))),
            ("pack/references/guide.md", Bytes("guide")),
            ("pack/scripts/run.sh", Bytes("echo hi"))));

        var item = preview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("slug").GetString() == "pack");
        var files = item.GetProperty("ignoredFiles").EnumerateArray().Select(e => e.GetString()).ToList();
        var fields = item.GetProperty("ignoredFields").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("references/guide.md", files);
        Assert.Contains("scripts/run.sh", files);
        Assert.Contains("license", fields);
        Assert.Contains("metadata", fields);
        Assert.Contains("allowed-tools", fields);

        await ConfirmAsync(client, preview.GetProperty("previewId").GetString()!);
        Assert.Equal("Does the work.", (await SkillAsync(client, "pack"))!.Value.GetProperty("description").GetString());
    }

    [Fact] // AC-25
    public async Task AConflictIsSkippedByDefaultAndOverwriteKeepsTheNameAndScope()
    {
        var client = host.Admin;
        var first = await PreviewAsync(client, Upload(("dup/SKILL.md", Bytes(SkillMd("dup", "Old description.", "OLD BODY")))));
        await ConfirmAsync(client, first.GetProperty("previewId").GetString()!);

        var second = await PreviewAsync(client, Upload(("dup/SKILL.md", Bytes(SkillMd("dup", "New description.", "NEW BODY")))));
        Assert.Equal("conflict", StatusOf(second, "dup"));
        var skipped = await ConfirmAsync(client, second.GetProperty("previewId").GetString()!);
        Assert.Equal(["dup"], skipped.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Old description.", (await SkillAsync(client, "dup"))!.Value.GetProperty("description").GetString());

        var third = await PreviewAsync(client, Upload(("dup/SKILL.md", Bytes(SkillMd("dup", "New description.", "NEW BODY")))));
        await ConfirmAsync(client, third.GetProperty("previewId").GetString()!, ("dup", "overwrite"));
        var replaced = (await SkillAsync(client, "dup"))!.Value;
        Assert.Equal("New description.", replaced.GetProperty("description").GetString());
        Assert.Equal("NEW BODY", replaced.GetProperty("body").GetString()?.Trim().Split('\n').Last());
        Assert.Equal("dup", replaced.GetProperty("displayName").GetString());
    }

    [Fact] // AC-26
    public async Task APreviewWritesNothingUntilItIsConfirmedAndIsConsumedOnce()
    {
        var client = host.Admin;
        var preview = await PreviewAsync(client, Upload(("pending/SKILL.md", Bytes(SkillMd("pending")))));
        Assert.Equal(0, await CountSkillsAsync(client));

        var previewId = preview.GetProperty("previewId").GetString()!;
        await ConfirmAsync(client, previewId);
        using var again = await client.PostAsJsonAsync(Import, new { previewId, decisions = Array.Empty<object>() }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact] // AC-27
    public async Task ASlugTakenAfterThePreviewFailsAloneAndTheOthersAreApplied()
    {
        var client = host.Admin;
        var preview = await PreviewAsync(client, Upload(
            ("race/SKILL.md", Bytes(SkillMd("race"))),
            ("calm/SKILL.md", Bytes(SkillMd("calm")))));
        Assert.Equal("new", StatusOf(preview, "race"));

        using var created = await client.PostAsJsonAsync(Skills, new
        {
            slug = "race",
            displayName = "race",
            description = "Taken meanwhile.",
            body = "body",
            scope = "Tenant",
            scopeTenantIds = Array.Empty<Guid>(),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var result = await ConfirmAsync(client, preview.GetProperty("previewId").GetString()!);
        Assert.Equal(["calm"], result.GetProperty("created").EnumerateArray().Select(e => e.GetString()));
        var failed = result.GetProperty("failed").EnumerateArray().Single();
        Assert.Equal("race", failed.GetProperty("slug").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failed.GetProperty("reason").GetString()));
    }
}
