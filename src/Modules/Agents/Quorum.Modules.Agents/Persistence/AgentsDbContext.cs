using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Quorum.Modules.Agents.Domain;
using Quorum.Shared.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Agents.Persistence;

internal sealed class AgentsDbContext(DbContextOptions<AgentsDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    public const string Schema = "agents";

    public DbSet<ModelEndpoint> ModelEndpoints => Set<ModelEndpoint>();

    public DbSet<Assistant> Assistants => Set<Assistant>();

    private Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ModelEndpoint>(endpoint =>
        {
            endpoint.ToTable("model_endpoints");
            endpoint.Property(e => e.BaseUrl).HasMaxLength(2048);
            endpoint.Property(e => e.ModelName).HasMaxLength(200);
            endpoint.HasIndex(e => e.TenantId);
            endpoint.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Assistant>(assistant =>
        {
            assistant.ToTable("assistants");
            assistant.Property(a => a.Name).HasMaxLength(200);
            assistant.HasIndex(a => a.TenantId);
            assistant.HasOne<ModelEndpoint>().WithMany().HasForeignKey(a => a.ModelEndpointId);
            assistant.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });
    }
}

internal sealed class AgentsDbContextDesignFactory : IDesignTimeDbContextFactory<AgentsDbContext>
{
    public AgentsDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<AgentsDbContext>(AgentsDbContext.Schema), NoTenantContext.Instance);
}
