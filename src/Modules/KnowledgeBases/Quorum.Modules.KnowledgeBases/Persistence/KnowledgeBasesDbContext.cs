using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Shared.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.KnowledgeBases.Persistence;

internal sealed class KnowledgeBasesDbContext(DbContextOptions<KnowledgeBasesDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    public const string Schema = "knowledge_base";

    public DbSet<DictionaryField> DictionaryFields => Set<DictionaryField>();

    public DbSet<KnowledgeBase> KnowledgeBases => Set<KnowledgeBase>();

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
            field.Property(f => f.Cluster).HasMaxLength(DictionaryField.MaxClusterLength);
            field.Property(f => f.Name).HasMaxLength(DictionaryField.MaxNameLength);
            field.Property(f => f.Description).HasMaxLength(DictionaryField.MaxDescriptionLength);
            field.Property(f => f.Type).HasConversion<string>().HasMaxLength(20);
            field.Property(f => f.Scope).HasConversion<string>().HasMaxLength(20);
            // No cluster counts as one value, so a field with none is unique by name like before the clusters. NULLS NOT DISTINCT needs PostgreSQL 15 or later.
            field.HasIndex(f => new { f.TenantId, f.Cluster, f.Name }).IsUnique().AreNullsDistinct(false);
            field.HasQueryFilter(f => f.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<KnowledgeBase>(knowledgeBase =>
        {
            knowledgeBase.ToTable("knowledge_bases");
            knowledgeBase.Property(k => k.Name).HasMaxLength(KnowledgeBase.MaxNameLength);
            knowledgeBase.Property(k => k.Description).HasMaxLength(KnowledgeBase.MaxDescriptionLength);
            knowledgeBase.Property(k => k.Kind).HasConversion<string>().HasMaxLength(20);
            knowledgeBase.Property(k => k.Scope).HasConversion<string>().HasMaxLength(20);
            knowledgeBase.Property(k => k.Status).HasConversion<string>().HasMaxLength(20);
            knowledgeBase.Property(k => k.EmbeddingModel).HasMaxLength(255);
            knowledgeBase.Property(k => k.QueryPrefix).HasMaxLength(KnowledgeBase.MaxQueryPrefixLength);
            knowledgeBase.Property(k => k.LastError).HasMaxLength(KnowledgeBase.MaxErrorLength);

            // Kind-specific settings and the mapping are read and written whole, never queried by a column of their own.
            knowledgeBase.OwnsOne(k => k.Connection, connection => connection.ToJson());
            knowledgeBase.OwnsOne(k => k.Mapping, mapping =>
            {
                mapping.ToJson();
                mapping.OwnsOne(m => m.Roles);
                mapping.OwnsMany(m => m.Fields);
            });

            knowledgeBase.HasIndex(k => new { k.TenantId, k.Name }).IsUnique();
            knowledgeBase.HasQueryFilter(k => k.TenantId == CurrentTenantId);
        });
    }
}

internal sealed class KnowledgeBasesDbContextDesignFactory : IDesignTimeDbContextFactory<KnowledgeBasesDbContext>
{
    public KnowledgeBasesDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<KnowledgeBasesDbContext>(KnowledgeBasesDbContext.Schema), NoTenantContext.Instance);
}
