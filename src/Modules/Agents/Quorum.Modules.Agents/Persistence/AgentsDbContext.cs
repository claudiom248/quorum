using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Tools.Mcp;
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

    public DbSet<McpServer> McpServers => Set<McpServer>();

    public DbSet<RagTool> RagTools => Set<RagTool>();

    public DbSet<LookupTool> LookupTools => Set<LookupTool>();

    public DbSet<WebConfiguration> WebConfigurations => Set<WebConfiguration>();

    public DbSet<WebSearchTool> WebSearchTools => Set<WebSearchTool>();

    /// <summary>
    /// The tenant of the request. Queries that must also see what other tenants share with this one (a server's scope) turn the
    /// tenant filter off and state the rule themselves, with this value.
    /// </summary>
    public Guid? CurrentTenantId => tenantContext.TenantId;

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
            tool.Property(t => t.DefinitionHash).HasMaxLength(64);
            // A built-in or rag tool is unique in its tenant and origin (their exposed names have different prefixes); an MCP tool in its server (two servers may both offer "search").
            tool.HasIndex(t => new { t.TenantId, t.Origin, t.Name }).IsUnique().HasFilter("\"ServerId\" IS NULL");
            tool.HasIndex(t => new { t.ServerId, t.Name }).IsUnique().HasFilter("\"ServerId\" IS NOT NULL");
            tool.HasOne<McpServer>().WithMany().HasForeignKey(t => t.ServerId).OnDelete(DeleteBehavior.Cascade);
            tool.HasQueryFilter(t => t.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<McpServer>(server =>
        {
            server.ToTable("mcp_servers");
            server.Property(s => s.Name).HasMaxLength(McpServerRules.MaxNameLength);
            server.Property(s => s.Url).HasMaxLength(McpServerRules.MaxUrlLength);
            server.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            server.Property(s => s.Scope).HasConversion<string>().HasMaxLength(20);
            server.Property(s => s.LastError).HasMaxLength(500);
            server.HasIndex(s => new { s.TenantId, s.Name }).IsUnique();
            server.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<RagTool>(rag =>
        {
            rag.ToTable("rag_tools");
            rag.HasKey(r => r.ToolId);
            rag.Property(r => r.ToolId).ValueGeneratedNever();
            rag.Property(r => r.FixedFilters).HasColumnType("jsonb");
            rag.Property(r => r.ExposedAliases).HasColumnType("jsonb").HasDefaultValue("{}");
            rag.Property(r => r.Scope).HasConversion<string>().HasMaxLength(20);
            rag.HasIndex(r => r.TenantId);
            rag.HasOne<Tool>().WithOne().HasForeignKey<RagTool>(r => r.ToolId).OnDelete(DeleteBehavior.Cascade);
            rag.HasQueryFilter(r => r.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<LookupTool>(lookup =>
        {
            lookup.ToTable("lookup_tools");
            lookup.HasKey(l => l.ToolId);
            lookup.Property(l => l.ToolId).ValueGeneratedNever();
            lookup.Property(l => l.LookupAliases).HasColumnType("jsonb").HasDefaultValue("{}");
            lookup.Property(l => l.ExposedAliases).HasColumnType("jsonb").HasDefaultValue("{}");
            lookup.Property(l => l.FixedFilters).HasColumnType("jsonb");
            lookup.Property(l => l.Scope).HasConversion<string>().HasMaxLength(20);
            lookup.HasIndex(l => l.TenantId);
            lookup.HasOne<Tool>().WithOne().HasForeignKey<LookupTool>(l => l.ToolId).OnDelete(DeleteBehavior.Cascade);
            lookup.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });

        // Global on purpose: no tenant filter, because the platform has one web configuration and every tenant reads it (specs 005, 006).
        modelBuilder.Entity<WebConfiguration>(configuration =>
        {
            configuration.ToTable("web_configuration");
            configuration.HasKey(c => c.Id);
            configuration.Property(c => c.Id).ValueGeneratedNever();
            configuration.Property(c => c.SearchUrl).HasMaxLength(McpServerRules.MaxUrlLength);
            configuration.Property(c => c.UserAgent).HasMaxLength(WebConfiguration.MaxUserAgentLength);
        });

        modelBuilder.Entity<WebSearchTool>(webSearch =>
        {
            webSearch.ToTable("web_search_tools");
            webSearch.HasKey(w => w.ToolId);
            webSearch.Property(w => w.ToolId).ValueGeneratedNever();
            webSearch.Property(w => w.Scope).HasConversion<string>().HasMaxLength(20);
            webSearch.HasIndex(w => w.TenantId);
            webSearch.HasOne<Tool>().WithOne().HasForeignKey<WebSearchTool>(w => w.ToolId).OnDelete(DeleteBehavior.Cascade);
            webSearch.HasQueryFilter(w => w.TenantId == CurrentTenantId);
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
