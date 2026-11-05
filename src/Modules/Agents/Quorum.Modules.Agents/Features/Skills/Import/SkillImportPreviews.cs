using Microsoft.Extensions.Caching.Memory;

namespace Quorum.Modules.Agents.Features.Skills.Import;

/// <summary>An upload checked and waiting for the administrator's confirmation (spec 017 D-20). Transient state, not a definition (D-27).</summary>
internal sealed record SkillImportPreview(Guid Id, Guid TenantId, IReadOnlyList<SkillCandidate> Candidates, DateTimeOffset ExpiresAt);

/// <summary>
/// The previews of the imports, kept for 30 minutes and consumed by the confirmation (D-20, D-26). Kept in memory on purpose: one
/// instance, like the other in-memory caches of the platform (spec 007).
/// </summary>
internal sealed class SkillImportPreviews(IMemoryCache cache, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public SkillImportPreview Store(Guid tenantId, IReadOnlyList<SkillCandidate> candidates)
    {
        var expiresAt = clock.GetUtcNow().Add(Lifetime);
        var preview = new SkillImportPreview(Guid.CreateVersion7(clock.GetUtcNow()), tenantId, candidates, expiresAt);
        cache.Set(KeyOf(tenantId, preview.Id), preview, new MemoryCacheEntryOptions { AbsoluteExpiration = expiresAt });
        return preview;
    }

    /// <summary>Returns the preview and removes it: a confirmation happens once, so a repeated one finds nothing (AC-26).</summary>
    public SkillImportPreview? Take(Guid tenantId, Guid previewId)
    {
        var key = KeyOf(tenantId, previewId);
        if (!cache.TryGetValue(key, out SkillImportPreview? preview))
        {
            return null;
        }

        cache.Remove(key);
        return preview;
    }

    private static string KeyOf(Guid tenantId, Guid previewId) => $"skills:import:{tenantId:N}:{previewId:N}";
}
