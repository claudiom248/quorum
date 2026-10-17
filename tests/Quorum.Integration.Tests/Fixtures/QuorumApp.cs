using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Agents.Chat;
using Quorum.Modules.Agents.Probing;
using Quorum.Modules.Tenancy.Domain;
using Quorum.Modules.Tenancy.Persistence;
using Quorum.Modules.Tenancy.Setup;
using Serilog;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// The whole application on its own PostgreSQL database, with a fake model server and captured logs.
/// </summary>
public sealed class QuorumApp : WebApplicationFactory<Program>
{
    public const string ModelBaseUrl = "http://ollama.test:11434/v1";
    public const string AdminEmail = "admin@acme.test";
    public const string AdminPassword = "correct horse battery";

    private readonly string _connectionString;
    private readonly string _dataProtectionKeysPath;

    private QuorumApp(string connectionString, string dataProtectionKeysPath)
    {
        _connectionString = connectionString;
        _dataProtectionKeysPath = dataProtectionKeysPath;
    }

    public LogCapture Logs { get; } = new();

    public FakeModelServer ModelServer { get; } = new();

    public string ConnectionString => _connectionString;

    /// <summary>The token printed to the logs while the installation is in setup mode.</summary>
    public string SetupToken => Services.GetRequiredService<SetupMode>().Token
        ?? throw new InvalidOperationException("The app is not in setup mode.");

    public static QuorumApp Start(string connectionString, string? dataProtectionKeysPath = null)
    {
        var app = new QuorumApp(
            connectionString,
            dataProtectionKeysPath ?? Path.Combine(Path.GetTempPath(), "quorum-tests", Guid.NewGuid().ToString("N")));
        _ = app.Server;
        return app;
    }

    public static async Task<QuorumApp> StartOnNewDatabaseAsync(PostgresFixture postgres) =>
        Start(await postgres.CreateDatabaseAsync());

    /// <summary>A new app instance on the same database and key ring, as after a container restart.</summary>
    public QuorumApp Restart() => Start(_connectionString, _dataProtectionKeysPath);

    /// <summary>A browser-like client in setup mode, sending the setup token.</summary>
    public HttpClient CreateSetupClient()
    {
        var client = CreateUiClient();
        client.DefaultRequestHeaders.Add("X-Setup-Token", SetupToken);
        return client;
    }

    /// <summary>A browser-like client: keeps the auth cookie and sends the anti-forgery header the web UI sends.</summary>
    public HttpClient CreateUiClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Quorum-Request", "1");
        return client;
    }

    /// <summary>Completes the setup against the fake model and returns the signed-in administrator's client.</summary>
    public async Task<HttpClient> SetUpAsync(string? systemPrompt = "You are Acme's assistant.", int contextTokenBudget = 4096)
    {
        ModelServer.Serve(ModelBaseUrl, "llama3.2");

        var client = CreateUiClient();
        client.DefaultRequestHeaders.Add("X-Setup-Token", SetupToken);
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/setup/complete", UriKind.Relative),
            new
            {
                admin = new { email = AdminEmail, displayName = "Acme Admin", password = AdminPassword },
                model = new { baseUrl = ModelBaseUrl, modelName = "llama3.2", apiKey = (string?)null, contextTokenBudget },
                systemPrompt,
            });

        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException($"Setup failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        client.DefaultRequestHeaders.Remove("X-Setup-Token");
        return client;
    }

    /// <summary>
    /// Creates a signed-in user that holds none of the administrator's permissions (the users and roles spec will define real ones).
    /// Call after <see cref="SetUpAsync"/>.
    /// </summary>
    public async Task<HttpClient> CreateMemberClientAsync(string email = "member@acme.test", string password = AdminPassword)
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            var tenantId = await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Tenants.Select(t => t.Id).SingleAsync();
            var created = await scope.ServiceProvider.GetRequiredService<UserManager<QuorumUser>>().CreateAsync(
                new QuorumUser
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    UserName = email,
                    Email = email,
                    DisplayName = "Acme Member",
                    Role = "Member",
                    CreatedAt = DateTimeOffset.UtcNow,
                    LockoutEnabled = true,
                },
                password);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException($"Cannot create the member: {string.Join("; ", created.Errors.Select(e => e.Description))}");
            }
        }

        var client = CreateUiClient();
        using var login = await client.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative), new { email, password });
        login.EnsureSuccessStatusCode();
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // The web UI is a referenced project's static asset: outside Development the host must be told to serve it.
        builder.UseStaticWebAssets();
        builder.UseSetting("ConnectionStrings:Platform", _connectionString);
        builder.UseSetting("DataProtection:KeysPath", _dataProtectionKeysPath);

        builder.ConfigureTestServices(services =>
        {
            // Many apps run in this process at once: each must log only to its own capture, so none of them
            // may take over the process-wide static logger (it would receive the others' events).
            services.AddSerilog(
                logger =>
                {
                    logger.MinimumLevel.Information().WriteTo.Sink(Logs);

                    // Set QUORUM_TEST_CONSOLE_LOGS=1 to see the server's logs while debugging a test.
                    if (Environment.GetEnvironmentVariable("QUORUM_TEST_CONSOLE_LOGS") == "1")
                    {
                        logger.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
                    }
                },
                preserveStaticLogger: true);
            services.AddHttpClient(ModelEndpointProbe.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ModelServer);
            services.AddHttpClient(OpenAiChatStream.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ModelServer);
        });
    }
}
