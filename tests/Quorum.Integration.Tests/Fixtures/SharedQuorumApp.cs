using System.Net.Http.Json;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// One set-up app for all the tests of a class; each test calls <see cref="ResetAsync"/> first and so starts from the state right after the setup,
/// signed in as the administrator with a client of its own (a test may change its cookies and headers).
/// The fakes and the caches go back to their state after the setup too; the clock is fixed when the app starts, so tests that move it keep an app of their own.
/// </summary>
public class SharedQuorumApp(PostgresFixture postgres) : IAsyncLifetime
{
    private DatabaseSnapshot? _snapshot;

    /// <summary>Whether the app starts with the export of spans on, collected in memory (spec 016 D-23). It is a setting of the start, so a test cannot change it.</summary>
    protected virtual bool Telemetry => false;

    public QuorumApp App { get; private set; } = null!;

    public HttpClient Admin { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        App = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: Telemetry);
        using var setup = await App.SetUpAsync();
        _snapshot = await DatabaseSnapshot.TakeAsync(App.ConnectionString);
    }

    public async Task ResetAsync()
    {
        await _snapshot!.RestoreAsync();
        App.Logs.Clear();
        await App.ResetRuntimeAsync();

        Admin?.Dispose();
        Admin = App.CreateUiClient();
        using var login = await Admin.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new { email = QuorumApp.AdminEmail, password = QuorumApp.AdminPassword });
        login.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        Admin?.Dispose();
        await App.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
