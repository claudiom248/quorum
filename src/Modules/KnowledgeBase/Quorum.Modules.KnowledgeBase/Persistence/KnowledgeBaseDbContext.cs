using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Quorum.Modules.KnowledgeBase.Domain;
using Quorum.Shared.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.KnowledgeBase.Persistence;

internal sealed class KnowledgeBaseDbContext(DbContextOptions<KnowledgeBaseDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    public const string Schema = "knowledge_base";

    public DbSet<DictionaryField> DictionaryFields => Set<DictionaryField>();

    /// <summary>
    /// The tenant of the request. Queries that must also see what other tenants share with this one (a field's scope) turn the
    /// tenant filter off and state the rule themselves, with this value.
    /// </summary>
    public Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<DictionaryField>(field =>
        {
            field.ToTable("dictionary_fields");
            field.Property(f => f.Name).HasMaxLength(DictionaryField.MaxNameLength);
            field.Property(f => f.Description).HasMaxLength(DictionaryField.MaxDescriptionLength);
            field.Property(f => f.Type).HasConversion<string>().HasMaxLength(20);
            field.Property(f => f.Scope).HasConversion<string>().HasMaxLength(20);
            field.HasIndex(f => new { f.TenantId, f.Name }).IsUnique();
            field.HasQueryFilter(f => f.TenantId == CurrentTenantId);
        });
    }
}

internal sealed class KnowledgeBaseDbContextDesignFactory : IDesignTimeDbContextFactory<KnowledgeBaseDbContext>
{
    public KnowledgeBaseDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<KnowledgeBaseDbContext>(KnowledgeBaseDbContext.Schema), NoTenantContext.Instance);
}
