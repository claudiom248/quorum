using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Quorum.Modules.Models.Contracts;
using Quorum.Modules.Models.Domain;
using Quorum.Shared.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Models.Persistence;

internal sealed class ModelsDbContext(DbContextOptions<ModelsDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    public const string Schema = "models";

    public DbSet<Model> Models => Set<Model>();

    /// <summary>
    /// The tenant of the request. Queries that must also see what other tenants share with this one turn the tenant filter off and
    /// state the rule themselves (<see cref="ModelVisibility"/>), with this value.
    /// </summary>
    public Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Model>(model =>
        {
            model.ToTable("models");
            model.Property(m => m.Name).HasMaxLength(ModelRules.MaxNameLength);
            model.Property(m => m.BaseUrl).HasMaxLength(2048);
            model.Property(m => m.ModelName).HasMaxLength(ModelRules.MaxModelNameLength);
            model.Property(m => m.Capabilities).HasConversion<string>().HasMaxLength(64);
            model.Property(m => m.Scope).HasConversion<string>().HasMaxLength(16);
            model.Property(m => m.QueryPrefix).HasMaxLength(ModelRules.MaxQueryPrefixLength);
            model.Property(m => m.ScopeTenantIds).HasColumnType("uuid[]");
            model.HasIndex(m => new { m.TenantId, m.Name }).IsUnique();
            model.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });
    }
}

internal sealed class ModelsDbContextDesignFactory : IDesignTimeDbContextFactory<ModelsDbContext>
{
    public ModelsDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<ModelsDbContext>(ModelsDbContext.Schema), NoTenantContext.Instance);
}
