using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBase.Domain;
using Quorum.Modules.KnowledgeBase.Persistence;

namespace Quorum.Integration.Tests.KnowledgeBase;

internal static class DictionaryFixtures
{
    /// <summary>Inserts a field owned by a tenant that does not exist in the app: the API has no way to create a second tenant yet.</summary>
    public static async Task<Guid> AddFieldOfAnotherTenantAsync(QuorumApp app, string name, DictionaryFieldScope scope)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBaseDbContext>();
        var field = DictionaryField.Create(Guid.NewGuid(), name, DictionaryFieldType.Text, "Another tenant's field", scope, DateTimeOffset.UtcNow);
        db.DictionaryFields.Add(field);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return field.Id;
    }
}
