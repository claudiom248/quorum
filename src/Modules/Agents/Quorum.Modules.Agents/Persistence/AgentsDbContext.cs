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

    public DbSet<Tool> Tools => Set<Tool>();

    public DbSet<AssistantTool> AssistantTools => Set<AssistantTool>();

    public DbSet<ToolSettings> ToolSettings => Set<ToolSettings>();

    public DbSet<ToolAuditEntry> ToolAuditEntries => Set<ToolAuditEntry>();

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

        modelBuilder.Entity<Tool>(tool =>
        {
            tool.ToTable("tools");
            tool.Property(t => t.Origin).HasConversion<string>().HasMaxLength(20);
            tool.Property(t => t.Name).HasMaxLength(128);
            tool.Property(t => t.State).HasConversion<string>().HasMaxLength(20);
            tool.Property(t => t.Policy).HasConversion<string>().HasMaxLength(20);
            tool.HasIndex(t => new { t.TenantId, t.Origin, t.Name }).IsUnique();
            tool.HasQueryFilter(t => t.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<AssistantTool>(activation =>
        {
            activation.ToTable("assistant_tools");
            activation.HasKey(a => new { a.AssistantId, a.ToolId });
            activation.HasIndex(a => a.TenantId);
            activation.HasOne<Assistant>().WithMany().HasForeignKey(a => a.AssistantId).OnDelete(DeleteBehavior.Cascade);
            activation.HasOne<Tool>().WithMany().HasForeignKey(a => a.ToolId).OnDelete(DeleteBehavior.Cascade);
            activation.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ToolSettings>(settings =>
        {
            settings.ToTable("tool_settings");
            settings.HasKey(s => s.TenantId);
            settings.Property(s => s.TenantId).ValueGeneratedNever();
            settings.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<ToolAuditEntry>(entry =>
        {
            entry.ToTable("tool_audit_entries");
            entry.Property(e => e.ServerName).HasMaxLength(64);
            entry.Property(e => e.ToolName).HasMaxLength(128);
            entry.Property(e => e.Decision).HasConversion<string>().HasMaxLength(20);
            entry.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(20);
            entry.Property(e => e.ErrorCode).HasMaxLength(100);
            entry.HasIndex(e => new { e.TenantId, e.StartedAt });
            entry.HasIndex(e => new { e.TenantId, e.ToolName });
            entry.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });
    }
}

internal sealed class AgentsDbContextDesignFactory : IDesignTimeDbContextFactory<AgentsDbContext>
{
    public AgentsDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<AgentsDbContext>(AgentsDbContext.Schema), NoTenantContext.Instance);
}
