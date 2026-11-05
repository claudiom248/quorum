using System.Text;
using Microsoft.AspNetCore.Http;
using Quorum.Modules.Agents.Domain;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Quorum.Modules.Agents.Features.Skills.Import;

/// <summary>Whether a skill of the upload can be imported (spec 017 D-21).</summary>
internal enum ImportStatus
{
    New,
    Conflict,
    Invalid,
}

/// <summary>One <c>SKILL.md</c> of the upload, checked: what the preview shows and what the confirmation writes.</summary>
internal sealed record SkillCandidate(
    string Slug,
    ImportStatus Status,
    string? Reason,
    string Description,
    string Body,
    IReadOnlyList<string> IgnoredFiles,
    IReadOnlyList<string> IgnoredFields);

/// <summary>
/// Finds the <c>SKILL.md</c> files up to two folders deep (D-19) and checks each one's frontmatter (D-21, D-22). The folder name is
/// irrelevant: the slug is the <c>name</c> of the frontmatter, never normalised. Invalid skills never block the valid ones.
/// </summary>
internal static class SkillFileDiscovery
{
    private const int MaxFolderDepth = 2;

    private static readonly IDeserializer Yaml = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    public static IReadOnlyList<SkillCandidate> Discover(IReadOnlyList<PackageFile> files, Func<string, bool> slugExists)
    {
        var skillFiles = files
            .Where(file => IsSkillFile(file.Path) && FolderDepth(file.Path) <= MaxFolderDepth)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        var folders = skillFiles.Select(file => FolderOf(file.Path)).ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<SkillCandidate>(skillFiles.Count);
        foreach (var skillFile in skillFiles)
        {
            var folder = FolderOf(skillFile.Path);
            var ignoredFiles = files
                .Where(file => !IsSkillFile(file.Path) && OwnedBy(file.Path, folder, folders))
                .Select(file => RelativeTo(folder, file.Path))
                .ToList();

            var parsed = Parse(skillFile.Content, folder);
            if (parsed.Status == ImportStatus.Invalid)
            {
                candidates.Add(parsed with { IgnoredFiles = ignoredFiles });
                continue;
            }

            // A slug that appears twice in one upload makes the later ones invalid (D-21).
            if (!seen.Add(parsed.Slug))
            {
                candidates.Add(parsed with { Status = ImportStatus.Invalid, Reason = "The same slug appears more than once in this upload.", IgnoredFiles = ignoredFiles });
                continue;
            }

            var status = slugExists(parsed.Slug) ? ImportStatus.Conflict : ImportStatus.New;
            candidates.Add(parsed with { Status = status, IgnoredFiles = ignoredFiles });
        }

        return candidates;
    }

    /// <summary>The path of a file inside the folder of its skill, as the report names it.</summary>
    private static string RelativeTo(string folder, string path) =>
        folder.Length == 0 ? path : path[(folder.Length + 1)..];

    private static bool IsSkillFile(string path) =>
        path.Equals(SkillPackageReader.SkillFileName, StringComparison.Ordinal) || path.EndsWith("/" + SkillPackageReader.SkillFileName, StringComparison.Ordinal);

    private static string FolderOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static int FolderDepth(string path)
    {
        var folder = FolderOf(path);
        return folder.Length == 0 ? 0 : folder.Split('/').Length;
    }

    /// <summary>A file belongs to the skill of the deepest folder above it that holds a skill.</summary>
    private static bool OwnedBy(string path, string folder, IReadOnlyList<string> folders)
    {
        var owner = folders
            .Where(candidate => candidate.Length == 0 || path.StartsWith(candidate + "/", StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault();
        return owner is not null && owner == folder;
    }

    /// <summary>The frontmatter as YAML between two <c>---</c> lines, then the body. Unknown fields are reported, not refused (D-22).</summary>
    private static SkillCandidate Parse(byte[] content, string folder)
    {
        var fallbackSlug = folder.Length == 0 ? SkillPackageReader.SkillFileName : folder[(folder.LastIndexOf('/') + 1)..];
        var text = Encoding.UTF8.GetString(content).TrimStart('﻿');
        var (frontmatter, body) = Split(text);
        if (frontmatter is null)
        {
            return Invalid(fallbackSlug, "The file has no frontmatter between two --- lines.");
        }

        Dictionary<string, object?>? fields;
        try
        {
            fields = Yaml.Deserialize<Dictionary<string, object?>>(frontmatter);
        }
        catch (YamlException)
        {
            return Invalid(fallbackSlug, "The frontmatter is not valid YAML.");
        }

        fields ??= [];
        var name = fields.TryGetValue("name", out var rawName) ? rawName as string : null;
        var description = fields.TryGetValue("description", out var rawDescription) ? rawDescription as string : null;
        var slug = name ?? fallbackSlug;
        var ignoredFields = fields.Keys.Where(key => key is not ("name" or "description")).Order(StringComparer.Ordinal).ToList();

        if (!SkillRules.IsValidSlug(name))
        {
            return Invalid(slug, "The name must be a slug: lower-case letters, digits and '-', starting with a letter or digit, and at most 32 characters.", ignoredFields);
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return Invalid(slug, "The frontmatter has no description.", ignoredFields);
        }

        var trimmedDescription = description.Trim();
        if (!SkillRules.IsValidDescription(trimmedDescription))
        {
            return Invalid(slug, $"The description is longer than {SkillRules.MaxDescriptionLength} characters.", ignoredFields);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return Invalid(slug, "The body is empty.", ignoredFields);
        }

        return new SkillCandidate(slug, ImportStatus.New, null, trimmedDescription, body.Trim(), [], ignoredFields);
    }

    private static SkillCandidate Invalid(string slug, string reason, IReadOnlyList<string>? ignoredFields = null) =>
        new(slug, ImportStatus.Invalid, reason, string.Empty, string.Empty, [], ignoredFields ?? []);

    /// <returns>The YAML between the markers, or <see langword="null"/> when the file has no frontmatter; then the body.</returns>
    private static (string? Frontmatter, string Body) Split(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
        {
            return (null, text);
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---")
            {
                return (string.Join('\n', lines[1..i]), string.Join('\n', lines[(i + 1)..]));
            }
        }

        return (null, text);
    }
}
