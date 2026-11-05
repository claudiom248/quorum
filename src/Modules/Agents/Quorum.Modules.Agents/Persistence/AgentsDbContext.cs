using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Tools;
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

    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<AgentSubAgent> AgentSubAgents => Set<AgentSubAgent>();

    public DbSet<AgentTool> AgentTools => Set<AgentTool>();

    public DbSet<ToolSettings> ToolSettings => Set<ToolSettings>();

    public DbSet<ToolAuditEntry> ToolAuditEntries => Set<ToolAuditEntry>();

    public DbSet<McpServer> McpServers => Set<McpServer>();

    public DbSet<RagTool> RagTools => Set<RagTool>();

    public DbSet<LookupTool> LookupTools => Set<LookupTool>();

    public DbSet<WebConfiguration> WebConfigurations => Set<WebConfiguration>();

    public DbSet<WebSearchTool> WebSearchTools => Set<WebSearchTool>();

    public DbSet<WebFetchTool> WebFetchTools => Set<WebFetchTool>();

    public DbSet<Skill> Skills => Set<Skill>();

    public DbSet<AgentSkill> AgentSkills => Set<AgentSkill>();

    public DbSet<SkillSettings> SkillSettings => Set<SkillSettings>();

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
            endpoint.Property(e => e.Name).HasMaxLength(ModelEndpointRules.MaxNameLength);
            endpoint.Property(e => e.BaseUrl).HasMaxLength(2048);
            endpoint.Property(e => e.ModelName).HasMaxLength(200);
            endpoint.HasIndex(e => new { e.TenantId, e.Name }).IsUnique();
            endpoint.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Assistant>(assistant =>
        {
            assistant.ToTable("assistants");
            assistant.Property(a => a.Name).HasMaxLength(200);
            assistant.Property(a => a.MaxParallelSubAgents).HasDefaultValue(Assistant.DefaultMaxParallelSubAgents);
            assistant.HasIndex(a => a.TenantId);
            assistant.HasOne<Agent>().WithMany().HasForeignKey(a => a.RootAgentId).OnDelete(DeleteBehavior.Restrict);
            assistant.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Agent>(agent =>
        {
            agent.ToTable("agents");
            agent.Property(a => a.Slug).HasMaxLength(AgentRules.MaxSlugLength);
            agent.Property(a => a.DisplayName).HasMaxLength(AgentRules.MaxDisplayNameLength);
            agent.Property(a => a.Description).HasMaxLength(AgentRules.MaxDescriptionLength);
            agent.Property(a => a.Scope).HasConversion<string>().HasMaxLength(20);
            agent.HasIndex(a => new { a.TenantId, a.Slug }).IsUnique();
            agent.HasOne<ModelEndpoint>().WithMany().HasForeignKey(a => a.ModelEndpointId).OnDelete(DeleteBehavior.Restrict);
            agent.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<AgentSubAgent>(edge =>
        {
            edge.ToTable("agent_sub_agents");
            edge.HasKey(e => new { e.ParentAgentId, e.ChildAgentId });
            edge.Property(e => e.FailurePolicy).HasConversion<string>().HasMaxLength(20).HasDefaultValue(SubAgentFailurePolicy.ReturnError);
            edge.HasIndex(e => e.TenantId);
            edge.HasIndex(e => e.ChildAgentId);
            edge.HasOne<Agent>().WithMany().HasForeignKey(e => e.ParentAgentId).OnDelete(DeleteBehavior.Cascade);
            // An agent that is somebody's sub-agent cannot be deleted (D-16): the edge is what the 409 lists.
            edge.HasOne<Agent>().WithMany().HasForeignKey(e => e.ChildAgentId).OnDelete(DeleteBehavior.Restrict);
            edge.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Tool>(tool =>
        {
            tool.ToTable("tools");
            tool.Property(t => t.Origin).HasConversion<string>().HasMaxLength(20);
            tool.Property(t => t.Name).HasMaxLength(128);
            tool.Property(t => t.CustomName).HasMaxLength(ToolNameRules.MaxLength);
            tool.Ignore(t => t.ExposedName);
            tool.Property(t => t.State).HasConversion<string>().HasMaxLength(20);
            tool.Property(t => t.Policy).HasConversion<string>().HasMaxLength(20);
            tool.Property(t => t.DefinitionHash).HasMaxLength(64);
            // A built-in or rag tool is unique in its tenant and origin; the names the model reads are kept apart across origins when a tool is saved (spec 015 D-5). An MCP tool is unique in its server (two servers may both offer "search").
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
            lookup.Property(l => l.LookupFilters).HasColumnType("jsonb").HasDefaultValue("{}");
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

        modelBuilder.Entity<WebFetchTool>(webFetch =>
        {
            webFetch.ToTable("web_fetch_tools");
            webFetch.HasKey(w => w.ToolId);
            webFetch.Property(w => w.ToolId).ValueGeneratedNever();
            webFetch.Property(w => w.Scope).HasConversion<string>().HasMaxLength(20);
            webFetch.HasIndex(w => w.TenantId);
            webFetch.HasOne<Tool>().WithOne().HasForeignKey<WebFetchTool>(w => w.ToolId).OnDelete(DeleteBehavior.Cascade);
            webFetch.HasQueryFilter(w => w.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<AgentTool>(activation =>
        {
            activation.ToTable("agent_tools");
            activation.HasKey(a => new { a.AgentId, a.ToolId });
            activation.Property(a => a.Manifest).HasMaxLength(10).HasDefaultValue(LookupManifest.Full);
            activation.HasIndex(a => a.TenantId);
            activation.HasOne<Agent>().WithMany().HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Cascade);
            activation.HasOne<Tool>().WithMany().HasForeignKey(a => a.ToolId).OnDelete(DeleteBehavior.Cascade);
            activation.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Skill>(skill =>
        {
            skill.ToTable("skills");
            skill.Property(s => s.Slug).HasMaxLength(AgentRules.MaxSlugLength);
            skill.Property(s => s.DisplayName).HasMaxLength(SkillRules.MaxDisplayNameLength);
            skill.Property(s => s.Description).HasMaxLength(SkillRules.MaxDescriptionLength);
            skill.Property(s => s.Body).HasColumnType("text");
            skill.Property(s => s.Scope).HasConversion<string>().HasMaxLength(20);
            skill.HasIndex(s => new { s.TenantId, s.Slug }).IsUnique();
            skill.HasQueryFilter(s => s.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<AgentSkill>(assignment =>
        {
            assignment.ToTable("agent_skills");
            assignment.HasKey(a => new { a.AgentId, a.SkillId });
            assignment.HasIndex(a => a.TenantId);
            assignment.HasIndex(a => a.SkillId);
            // An agent's assignments go with it; a skill still assigned cannot be deleted (spec 017 D-14), and the database refuses it too.
            assignment.HasOne<Agent>().WithMany().HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Cascade);
            assignment.HasOne<Skill>().WithMany().HasForeignKey(a => a.SkillId).OnDelete(DeleteBehavior.Restrict);
            assignment.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<SkillSettings>(settings =>
        {
            settings.ToTable("skill_settings");
            settings.HasKey(s => s.TenantId);
            settings.Property(s => s.TenantId).ValueGeneratedNever();
            settings.Property(s => s.DisplayNameStyle).HasConversion<string>().HasMaxLength(20);
            settings.HasQueryFilter(s => s.TenantId == CurrentTenantId);
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
            entry.Property(e => e.TraceId).HasMaxLength(32);
            entry.HasIndex(e => new { e.TenantId, e.StartedAt });
            entry.HasIndex(e => new { e.TenantId, e.ToolName });
            entry.HasIndex(e => e.ConversationId);
            entry.HasQueryFilter(e => e.TenantId == CurrentTenantId);
        });
    }
}

internal sealed class AgentsDbContextDesignFactory : IDesignTimeDbContextFactory<AgentsDbContext>
{
    public AgentsDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<AgentsDbContext>(AgentsDbContext.Schema), NoTenantContext.Instance);
}
