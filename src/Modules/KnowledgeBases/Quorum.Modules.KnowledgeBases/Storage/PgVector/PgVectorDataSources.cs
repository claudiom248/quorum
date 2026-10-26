using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

/// <summary>
/// The pools of connections to the databases of the registered knowledge bases (spec 008 non-functional requirements): one small pool per
/// connection, kept while it is used and disposed when it has been idle, so an edited connection leaves no pool behind and a deleted
/// knowledge base's pool goes away by itself. The pool is found by the connection string it was built from, so an edit of the connection
/// simply asks for another one.
/// </summary>
internal sealed class PgVectorDataSources : IDisposable
{
    /// <summary>How long a pool lives without being asked for.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(5);

    private const int MaxPools = 64;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxPools });

    private readonly Lock _gate = new();

    public NpgsqlDataSource For(PgVectorConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var connectionString = connection.ToNpgsqlConnectionString();
        lock (_gate)
        {
            if (_cache.TryGetValue(connectionString, out NpgsqlDataSource? existing) && existing is not null)
            {
                return existing;
            }

            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseVector();
            var created = builder.Build();
            _cache.Set(
                connectionString,
                created,
                new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = IdleLifetime }.RegisterPostEvictionCallback(static (_, value, _, _) => (value as IDisposable)?.Dispose()));
            return created;
        }
    }

    public void Dispose() => _cache.Dispose();
}
