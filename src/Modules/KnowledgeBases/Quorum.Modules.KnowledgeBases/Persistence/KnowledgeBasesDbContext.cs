using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

    public DbSet<Lookup> Lookups => Set<Lookup>();

    public DbSet<LookupSlice> LookupSlices => Set<LookupSlice>();

    public DbSet<LookupValue> LookupValues => Set<LookupValue>();

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
            knowledgeBase.Property(k => k.Connection)
                .HasColumnType("jsonb")
                .HasConversion(
                    settings => settings.ToJson(),
                    json => ConnectionSettings.Parse(json),
                    new ValueComparer<ConnectionSettings>(
                        (left, right) => left != null && right != null && left.ToJson() == right.ToJson(),
                        settings => settings.ToJson().GetHashCode(StringComparison.Ordinal),
                        settings => ConnectionSettings.Parse(settings.ToJson())));
            knowledgeBase.OwnsOne(k => k.Mapping, mapping =>
            {
                mapping.ToJson();
                mapping.OwnsOne(m => m.Roles);
                mapping.OwnsOne(m => m.CountedPoint);
                mapping.OwnsMany(m => m.Fields);
            });

            // The default is what knowledge bases registered before lookups get.
            knowledgeBase.Property(k => k.LookupRefreshHours).HasDefaultValue(KnowledgeBase.DefaultLookupRefreshHours);

            knowledgeBase.HasIndex(k => new { k.TenantId, k.Name }).IsUnique();
            knowledgeBase.HasQueryFilter(k => k.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Lookup>(lookup =>
        {
            lookup.ToTable("lookups");
            lookup.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20);
            lookup.Property(l => l.Name).HasMaxLength(Lookup.MaxNameLength);
            lookup.Property(l => l.Description).HasMaxLength(Lookup.MaxDescriptionLength);
            lookup.Property(l => l.Scope).HasConversion<string>().HasMaxLength(20);
            lookup.Property(l => l.Unit).HasConversion<string>().HasMaxLength(20);

            // The lookups of a knowledge base go with it. The dictionary field is not a foreign key: a mapped field cannot be deleted, so it outlives the lookup.
            lookup.HasOne<KnowledgeBase>().WithMany().HasForeignKey(l => l.KnowledgeBaseId).OnDelete(DeleteBehavior.Cascade);

            lookup.HasIndex(l => new { l.TenantId, l.Name }).IsUnique();
            // One lookup per listable field of a knowledge base, whatever the number of times it is saved.
            lookup.HasIndex(l => new { l.KnowledgeBaseId, l.DictionaryFieldId }).IsUnique();
            lookup.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<LookupSlice>(slice =>
        {
            slice.ToTable("lookup_slices");
            slice.Property(s => s.FilterHash).HasMaxLength(LookupSlice.FilterHashLength);
            slice.Property(s => s.Filters).HasColumnType("jsonb");
            slice.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            slice.Property(s => s.LastError).HasMaxLength(LookupSlice.MaxErrorLength);

            slice.HasOne<Lookup>().WithMany().HasForeignKey(s => s.LookupId).OnDelete(DeleteBehavior.Cascade);

            slice.HasIndex(s => new { s.LookupId, s.FilterHash }).IsUnique();
            // The refresh service looks for the slices that are due.
            slice.HasIndex(s => s.NextRefreshAt);
            slice.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<LookupValue>(value =>
        {
            value.ToTable("lookup_values");
            // Position is the rank, so a page by count is a range of the key; a page by name reads the second index.
            value.HasKey(v => new { v.SliceId, v.Generation, v.Position });
            value.Property(v => v.Value).HasMaxLength(LookupValue.MaxValueLength);
            value.Property(v => v.Label).HasMaxLength(LookupValue.MaxValueLength);
            value.Property(v => v.Attributes).HasColumnType("jsonb");

            value.HasOne<LookupSlice>().WithMany().HasForeignKey(v => v.SliceId).OnDelete(DeleteBehavior.Cascade);

            value.HasIndex(v => new { v.SliceId, v.Generation, v.DocumentCount }).IsDescending(false, false, true);
            value.HasIndex(v => new { v.SliceId, v.Generation, v.Value });
            value.HasQueryFilter(v => v.TenantId == CurrentTenantId);
        });
    }
}

internal sealed class KnowledgeBasesDbContextDesignFactory : IDesignTimeDbContextFactory<KnowledgeBasesDbContext>
{
    public KnowledgeBasesDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<KnowledgeBasesDbContext>(KnowledgeBasesDbContext.Schema), NoTenantContext.Instance);
}
