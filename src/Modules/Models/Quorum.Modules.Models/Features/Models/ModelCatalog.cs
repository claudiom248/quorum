using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Models.Contracts;
using Quorum.Modules.Models.Domain;
using Quorum.Modules.Models.Persistence;
using Quorum.Modules.Models.Secrets;

namespace Quorum.Modules.Models.Features.Models;

/// <summary>The catalog other modules ask: usability of a model for a consumer, and its connection (spec 019 D-9, D-18).</summary>
internal sealed class ModelCatalog(ModelsDbContext db, ApiKeyProtector apiKeyProtector) : IModelCatalog
{
    public async Task<ModelUsability> CheckUsableAsync(Guid modelId, ModelCapabilities required, ModelUseScope consumer, CancellationToken cancellationToken)
    {
        var model = await db.Models.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(m => m.Id == modelId, cancellationToken);
        if (model is null)
        {
            return new ModelUsability("The model does not exist.");
        }

        if (!ModelVisibility.Covers(model, consumer))
        {
            return new ModelUsability("The model is not shared as widely as this consumer: choose a global model, or one shared with its tenants.");
        }

        if ((model.Capabilities & required) != required)
        {
            return new ModelUsability("The model does not have the capability this use needs.");
        }

        return new ModelUsability(null);
    }

    public async Task<ModelConnection?> ResolveAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var model = await db.Models.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(m => m.Id == modelId, cancellationToken);
        return model is null
            ? null
            : new ModelConnection(
                model.Id,
                model.BaseUrl,
                model.ModelName,
                apiKeyProtector.Unprotect(model.ProtectedApiKey),
                model.Capabilities,
                model.ContextTokenBudget,
                model.QueryPrefix,
                model.EmbeddingDimensions);
    }
}
