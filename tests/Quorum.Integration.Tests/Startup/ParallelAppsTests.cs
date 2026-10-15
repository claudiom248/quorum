using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Startup;

public sealed class ParallelAppsTests(PostgresFixture postgres)
{
    // Tests start many apps in one process at once. A shared, process-wide logger would send one app's
    // events (such as its setup token) to another app's capture and make log-based tests flaky.
    [Fact]
    public async Task EachAppLogsItsSetupTokenToItsOwnCapture()
    {
        for (var round = 0; round < 2; round++)
        {
            var apps = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
            {
                var connectionString = await postgres.CreateDatabaseAsync();
                return await Task.Run(() => QuorumApp.Start(connectionString));
            }));

            try
            {
                foreach (var app in apps)
                {
                    Assert.Contains(app.Logs.Messages, message => message.Contains(app.SetupToken, StringComparison.Ordinal));
                }
            }
            finally
            {
                foreach (var app in apps)
                {
                    await app.DisposeAsync();
                }
            }
        }
    }
}
