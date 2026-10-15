using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// The whole application running against a real PostgreSQL container, shared by every test in the assembly.
/// </summary>
public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private readonly string _dataProtectionKeysPath =
        Path.Combine(Path.GetTempPath(), "quorum-tests", Guid.NewGuid().ToString("N"));

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Platform", _postgres.GetConnectionString());
        builder.UseSetting("DataProtection:KeysPath", _dataProtectionKeysPath);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();

        if (Directory.Exists(_dataProtectionKeysPath))
        {
            Directory.Delete(_dataProtectionKeysPath, recursive: true);
        }
    }
}
