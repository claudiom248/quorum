using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Agents.Probing;
using Quorum.Modules.Tenancy.Setup;
using Serilog;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// The whole application on its own PostgreSQL database, with a fake model server and captured logs.
/// </summary>
public sealed class QuorumApp : WebApplicationFactory<Program>
{
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

    public HttpClient CreateSetupClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Setup-Token", SetupToken);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Platform", _connectionString);
        builder.UseSetting("DataProtection:KeysPath", _dataProtectionKeysPath);

        builder.ConfigureTestServices(services =>
        {
            services.AddSerilog(logger => logger.MinimumLevel.Information().WriteTo.Sink(Logs));
            services.AddHttpClient(ModelEndpointProbe.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ModelServer);
        });
    }
}
