using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Features.Skills.Import;
using Quorum.Modules.Agents.Persistence;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;
using YamlDotNet.Serialization;

namespace Quorum.Modules.Agents.Features.Skills;

/// <summary>
/// Downloads skills as <c>SKILL.md</c> files (spec 017 D-25): one skill as a file, several or all as a zip with one <c>&lt;slug&gt;/SKILL.md</c>
/// each. The display name and the scope are not written, so the files stay valid for other Agent Skills tools and come back through the import.
/// </summary>
internal sealed class SkillExportEndpoints : IEndpointGroup
{
    private static readonly ISerializer Yaml = new SerializerBuilder().Build();

    public void Map(IEndpointRouteBuilder app)
    {
        var skills = app.MapGroup("/api/skills")
            .WithTags("Skills")
            .RequireAuthorization(QuorumPolicies.SkillsManage);

        skills.MapGet("/{id:guid}/export", ExportOneAsync).WithName("ExportSkill");
        skills.MapGet("/export", ExportManyAsync).WithName("ExportSkills");
    }

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> ExportOneAsync(Guid id, AgentsDbContext db, CancellationToken cancellationToken)
    {
        if (await SkillVisibility.For(db).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken) is not { } skill)
        {
            return Error.NotFound("skill.not_found", "The skill does not exist.").ToProblem();
        }

        return TypedResults.File(Encoding.UTF8.GetBytes(SkillMarkdown(skill)), "text/markdown", "SKILL.md");
    }

    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> ExportManyAsync(Guid[]? ids, AgentsDbContext db, CancellationToken cancellationToken)
    {
        var visible = SkillVisibility.For(db).AsNoTracking();
        var selected = ids is { Length: > 0 }
            ? await visible.Where(s => ids.Contains(s.Id)).OrderBy(s => s.Slug).ToListAsync(cancellationToken)
            : await visible.OrderBy(s => s.Slug).ToListAsync(cancellationToken);
        if (ids is { Length: > 0 } && selected.Count != ids.Distinct().Count())
        {
            return Error.NotFound("skill.not_found", "One of the skills does not exist.").ToProblem();
        }

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var skill in selected)
            {
                using var writer = new StreamWriter(archive.CreateEntry($"{skill.Slug}/{SkillPackageReader.SkillFileName}").Open(), new UTF8Encoding(false));
                writer.Write(SkillMarkdown(skill));
            }
        }

        return TypedResults.File(stream.ToArray(), "application/zip", "skills.zip");
    }

    /// <summary>The file a skill is exported as: frontmatter with <c>name</c> and <c>description</c>, then the body as it is stored.</summary>
    internal static string SkillMarkdown(Skill skill)
    {
        // The line endings of the file are LF on every platform: the YAML writer uses the ones of the host.
        var frontmatter = Yaml.Serialize(new Dictionary<string, string> { ["name"] = skill.Slug, ["description"] = skill.Description })
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        return "---\n" + frontmatter + "---\n" + skill.Body;
    }
}
