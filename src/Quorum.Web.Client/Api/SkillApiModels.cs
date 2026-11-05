namespace Quorum.Web.Client.Api;

/// <summary>A skill in the list: its slug is the name the model reads and the argument of <c>load_skill</c> (spec 017 D-12).</summary>
public sealed record SkillSummary(Guid Id, string Slug, string DisplayName, string Description, string Scope, int AgentCount);

public sealed record SkillDocument(Guid Id, string Slug, string DisplayName, string Description, string Body, string Scope, List<Guid> ScopeTenantIds);

public sealed record SkillRequest(string Slug, string DisplayName, string Description, string Body, string Scope, List<Guid> ScopeTenantIds);

/// <summary>The display name style of the tenant's imported skills: <c>Slug</c> or <c>Humanized</c> (spec 017 D-28).</summary>
public sealed record SkillSettingsInfo(string DisplayNameStyle);

public sealed record SkillImportItemInfo(string Slug, string Status, string? Reason, List<string> IgnoredFiles, List<string> IgnoredFields);

public sealed record SkillImportPreviewInfo(Guid PreviewId, DateTimeOffset ExpiresAt, List<SkillImportItemInfo> Items);

public sealed record SkillImportDecisionInfo(string Slug, string Action);

public sealed record SkillImportFailureInfo(string Slug, string Reason);

public sealed record SkillImportResultInfo(List<string> Created, List<string> Overwritten, List<string> Skipped, List<SkillImportFailureInfo> Failed);

/// <summary>A file read in the browser: its path inside the picked folder (or its name) and its bytes in base64.</summary>
public sealed record SkillFileUpload(string Path, string Base64);
