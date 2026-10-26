using Npgsql;
using Quorum.SampleKnowledgeBase;
using Testcontainers.PostgreSql;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// One PostgreSQL container with the pgvector extension for the whole test assembly, started the first time a test asks for it. It is
/// a data owner's database, not the platform's (<see cref="PostgresFixture"/>): the image is the one the compose file pins for its
/// <c>pgvector</c> profile, so the tests run against what customers can run.
/// </summary>
public sealed class PgVectorFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _sharedGate = new(1, 1);
    private PostgreSqlContainer? _container;
    private string? _shared;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        _gate.Dispose();
        _sharedGate.Dispose();
    }

    /// <summary>The address a knowledge base is registered with.</summary>
    public async Task<PgVectorEndpoint> GetEndpointAsync()
    {
        var container = await ContainerAsync();
        return new PgVectorEndpoint(container.Hostname, container.GetMappedPublicPort(5432), PostgreSqlBuilder.DefaultDatabase, PostgreSqlBuilder.DefaultUsername, PostgreSqlBuilder.DefaultPassword);
    }

    /// <summary>A connection string of the owner of the database, in Npgsql format.</summary>
    public async Task<string> GetConnectionStringAsync() => (await ContainerAsync()).GetConnectionString();

    /// <summary>Creates a schema with every sample relation (D-18) and returns its name; tests that change a relation use their own.</summary>
    public async Task<string> NewSchemaAsync()
    {
        var schema = $"t{Guid.NewGuid():N}"[..9];
        await SeedAsync(schema);
        return schema;
    }

    /// <summary>The schema with the sample relations that tests which only read them share: seeded the first time it is asked for.</summary>
    public async Task<string> GetSharedSchemaAsync()
    {
        await _sharedGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            return _shared ??= await NewSchemaAsync();
        }
        finally
        {
            _sharedGate.Release();
        }
    }

    public async Task SeedAsync(string schema, IEnumerable<SampleRelation>? relations = null)
    {
        await using var dataSource = PgVectorSeeder.CreateDataSource(await GetConnectionStringAsync());
        await new PgVectorSeeder(dataSource).SeedAsync(schema, relations ?? SampleRelations.All(schema), TestContext.Current.CancellationToken);
    }

    /// <summary>Runs a statement as the owner of the database, the way a data owner prepares their database (roles, grants, indexes).</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(await GetConnectionStringAsync());
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<PostgreSqlContainer> ContainerAsync()
    {
        await _gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_container is null)
            {
                var container = new PostgreSqlBuilder(ComposeFile.Image("pgvector/pgvector")).Build();
                await container.StartAsync(TestContext.Current.CancellationToken);

                // Created once, here: seeders that run in parallel and each issue CREATE EXTENSION IF NOT EXISTS can collide on the catalog's unique index.
                await container.ExecScriptAsync("CREATE EXTENSION IF NOT EXISTS vector", TestContext.Current.CancellationToken);
                _container = container;
            }

            return _container;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Where the shared pgvector database answers, with the owner's credentials.</summary>
public sealed record PgVectorEndpoint(string Host, int Port, string Database, string Username, string Password);
