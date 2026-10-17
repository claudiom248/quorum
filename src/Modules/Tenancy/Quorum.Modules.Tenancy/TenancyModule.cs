using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Tenancy.Domain;
using Quorum.Modules.Tenancy.Identity;
using Quorum.Modules.Tenancy.Persistence;
using Quorum.Modules.Tenancy.Setup;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Tenancy;

public static class TenancyModule
{
    public const string AuthCookieName = "quorum.auth";

    /// <summary>Authorization policy for administrator-only endpoints (same name as <c>QuorumPolicies.Admin</c>).</summary>
    public const string AdminPolicy = QuorumPolicies.Admin;

    public static Assembly Assembly => typeof(TenancyModule).Assembly;

    public static IServiceCollection AddTenancyModule(this IServiceCollection services)
    {
        // Registration order matters: setup mode is decided after the tenancy migrations ran.
        services.AddModuleDbContext<TenancyDbContext>(TenancyDbContext.Schema);
        services.AddSingleton<SetupMode>();
        services.AddSingleton<IDatabaseInitializer, SetupModeInitializer>();

        services.AddHttpContextAccessor();
        services.AddScoped<ITenantContext, HttpTenantContext>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        services.AddIdentityCore<QuorumUser>(options =>
            {
                // NIST-style: length over composition rules.
                options.Password.RequiredLength = 12;
                options.Password.RequiredUniqueChars = 1;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;

                options.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<TenancyDbContext>()
            .AddSignInManager()
            .AddClaimsPrincipalFactory<QuorumUserClaimsFactory>();

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = AuthCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // Secure whenever the app is served over HTTPS (the expected production setup behind a TLS proxy).
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SlidingExpiration = true;

            // An API answers with status codes, never with redirects to a login page.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        // The one place that says which role holds which permission: only the administrator exists today, and a
        // dedicated users and roles spec will change this mapping without touching the endpoints, which name permissions.
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(QuorumPolicies.ToolsManage, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(QuorumPolicies.ToolsAttach, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(QuorumPolicies.ToolsUse, policy => policy.RequireRole(Roles.Admin));

        return services;
    }
}
