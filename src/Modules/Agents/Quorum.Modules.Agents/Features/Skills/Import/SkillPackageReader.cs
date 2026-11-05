using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.AspNetCore.Http;

namespace Quorum.Modules.Agents.Features.Skills.Import;

/// <summary>A rejection of the whole upload, before any preview exists (spec 017 D-23): the status says whether it was too large or malformed.</summary>
internal sealed class SkillPackageException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>A file of the upload as it was found: its path inside the package, with <c>/</c> separators, and its bytes.</summary>
internal sealed record PackageFile(string Path, byte[] Content);

/// <summary>
/// Reads an upload of skills (spec 017 D-19, D-23): one <c>SKILL.md</c>, the files of a folder with their relative paths, or one
/// <c>.zip</c> / <c>.tar.gz</c>. Everything is read in memory under a byte counter; entries are never written to disk, so the only path
/// questions are names (rejected when absolute or with <c>..</c>) and links (rejected). Only the files are returned: deciding which are
/// skills is the job of <see cref="SkillFileDiscovery"/>.
/// </summary>
internal static class SkillPackageReader
{
    /// <summary>The limit of the upload itself and of the total once extracted (D-23).</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    public const string SkillFileName = "SKILL.md";

    public static IReadOnlyList<PackageFile> Read(IReadOnlyList<(string Name, Stream Content, long Length)> uploads)
    {
        if (uploads.Sum(upload => upload.Length) > MaxBytes)
        {
            throw new SkillPackageException(StatusCodes.Status413PayloadTooLarge, $"The upload is larger than {MaxBytes / 1024 / 1024} MB.");
        }

        var budget = new ExtractionBudget();
        var files = new List<PackageFile>();
        foreach (var (name, content, _) in uploads)
        {
            var normalized = NormalizePath(name);
            if (IsArchive(normalized, out var kind))
            {
                files.AddRange(kind == ArchiveKind.Zip ? ReadZip(content, budget) : ReadTarGz(content, budget));
            }
            else
            {
                files.Add(new PackageFile(normalized, budget.Read(content)));
            }
        }

        return files;
    }

    private enum ArchiveKind
    {
        Zip,
        TarGz,
    }

    private static bool IsArchive(string name, out ArchiveKind kind)
    {
        var lower = name.ToLowerInvariant();
        kind = lower.EndsWith(".zip", StringComparison.Ordinal) ? ArchiveKind.Zip : ArchiveKind.TarGz;
        return lower.EndsWith(".zip", StringComparison.Ordinal) || lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal);
    }

    private static List<PackageFile> ReadZip(Stream content, ExtractionBudget budget)
    {
        var files = new List<PackageFile>();
        using var archive = OpenZip(content);
        foreach (var entry in archive.Entries)
        {
            // A symbolic link in a zip is a regular entry whose Unix mode says so; it is refused like a link in a tar (D-23).
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw Malformed("The archive contains a link, which is not allowed.");
            }

            var path = NormalizePath(entry.FullName);
            if (path.EndsWith('/'))
            {
                continue;
            }

            using var stream = entry.Open();
            files.Add(new PackageFile(path, budget.Read(stream)));
        }

        return files;
    }

    private static ZipArchive OpenZip(Stream content)
    {
        try
        {
            return new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException)
        {
            throw Malformed("The archive is not a readable zip file.");
        }
    }

    private static List<PackageFile> ReadTarGz(Stream content, ExtractionBudget budget)
    {
        var files = new List<PackageFile>();
        try
        {
            using var gzip = new GZipStream(content, CompressionMode.Decompress, leaveOpen: false);
            using var tar = new TarReader(gzip, leaveOpen: false);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    throw Malformed("The archive contains a link, which is not allowed.");
                }

                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    continue;
                }

                var path = NormalizePath(entry.Name);
                using var stream = entry.DataStream ?? Stream.Null;
                files.Add(new PackageFile(path, budget.Read(stream)));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw Malformed("The archive is not a readable tar.gz file.");
        }

        return files;
    }

    /// <summary>Separators are <c>/</c>; absolute paths, drive letters and <c>..</c> segments are refused (D-23).</summary>
    private static string NormalizePath(string name)
    {
        var path = name.Replace('\\', '/');
        if (path.StartsWith('/') || (path.Length > 1 && path[1] == ':'))
        {
            throw Malformed($"The path '{name}' is absolute, which is not allowed.");
        }

        if (path.Split('/').Any(segment => segment == ".."))
        {
            throw Malformed($"The path '{name}' goes up a folder, which is not allowed.");
        }

        return path;
    }

    private static SkillPackageException Malformed(string message) => new(StatusCodes.Status400BadRequest, message);

    /// <summary>The bytes read so far across the whole upload, extracted ones included: the 20 MB limit covers both (D-23).</summary>
    private sealed class ExtractionBudget
    {
        private long _spent;

        public byte[] Read(Stream source)
        {
            using var target = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                _spent += read;
                if (_spent > MaxBytes)
                {
                    throw new SkillPackageException(StatusCodes.Status413PayloadTooLarge, $"The package is larger than {MaxBytes / 1024 / 1024} MB once extracted.");
                }

                target.Write(buffer, 0, read);
            }

            return target.ToArray();
        }
    }
}
