using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Quorum.Modules.Tenancy.Domain;
using Quorum.Shared.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Tenancy.Persistence;

internal sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, ITenantContext tenantContext)
    : IdentityUserContext<QuorumUser, Guid>(options)
{
    public const string Schema = "tenancy";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<SetupState> SetupStates => Set<SetupState>();

    public DbSet<OnboardingChecklist> OnboardingChecklists => Set<OnboardingChecklist>();

    public DbSet<ServiceToken> ServiceTokens => Set<ServiceToken>();

    private Guid? CurrentTenantId => tenantContext.TenantId;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenants");
            tenant.Property(t => t.Name).HasMaxLength(200);
        });

        builder.Entity<SetupState>(setup =>
        {
            setup.ToTable("setup_state");
            setup.Property(s => s.Id).ValueGeneratedNever();
        });

        builder.Entity<OnboardingChecklist>(checklist =>
        {
            checklist.ToTable("onboarding_checklist");
            checklist.Property(c => c.Id).ValueGeneratedNever();
        });

        builder.Entity<ServiceToken>(token =>
        {
            token.ToTable("service_tokens");
            token.Property(t => t.Id).ValueGeneratedNever();
            token.Property(t => t.TokenHash).HasMaxLength(64);
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.HasIndex(t => t.TenantId);
            token.HasQueryFilter(t => CurrentTenantId == null || t.TenantId == CurrentTenantId);
        });

        builder.Entity<QuorumUser>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.DisplayName).HasMaxLength(100);
            user.Property(u => u.Role).HasMaxLength(50);
            user.HasIndex(u => u.TenantId);
            user.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId);

            // Sign-in looks users up before any tenant is known, so the filter only narrows once a tenant is resolved.
            user.HasQueryFilter(u => CurrentTenantId == null || u.TenantId == CurrentTenantId);
        });

        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
    }
}

internal sealed class TenancyDbContextDesignFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<TenancyDbContext>(TenancyDbContext.Schema), NoTenantContext.Instance);
}
