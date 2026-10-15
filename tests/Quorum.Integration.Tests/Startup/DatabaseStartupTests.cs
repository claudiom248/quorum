using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Quorum.Integration.Tests.Startup;

public sealed class DatabaseStartupTests
{
    [Fact]
    public void StartupFailsWithAClearErrorWhenThePlatformDatabaseIsUnreachable()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Platform", "Host=127.0.0.1;Port=1;Database=quorum;Username=quorum;Password=x;Timeout=1");
            builder.UseSetting("Database:StartupTimeout", "00:00:01");
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(Path.GetTempPath(), "quorum-tests", Guid.NewGuid().ToString("N")));
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var databaseError = FindInChain<InvalidOperationException>(exception);
        Assert.NotNull(databaseError);
        Assert.Contains("platform database", databaseError.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static T? FindInChain<T>(Exception? exception) where T : Exception
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is T match)
            {
                return match;
            }

            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (FindInChain<T>(inner) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }
}
