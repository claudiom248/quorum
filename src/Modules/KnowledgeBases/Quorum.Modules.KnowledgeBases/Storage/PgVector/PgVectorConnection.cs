using System.Globalization;
using System.Text;
using Npgsql;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

/// <summary>
/// The metric of the query, which pgvector does not keep in the column: it is the operator of the query and the operator class of the
/// index (spec 008 D-7).
/// </summary>
internal enum PgVectorDistance
{
    Cosine,
    L2,
    InnerProduct,
}

/// <summary>
/// Where the administrator says a PgVector knowledge base lives. Either separate fields or a connection string, never both; a request
/// with neither leaves the secret to the one already stored (spec 008 D-16). <see cref="Table"/> is only absent when sources are being listed.
/// </summary>
internal sealed record PgVectorConnectionRequest(
    string? Host,
    int? Port,
    string? Database,
    string? Username,
    string? Password,
    string? SslMode,
    string? ConnectionString,
    string? Schema,
    string? Table,
    string? VectorColumn,
    string? Distance);

/// <summary>Where a PgVector knowledge base lives, as it is stored: the secret is the encrypted one, and only one of the two is set.</summary>
internal sealed record PgVectorConnectionSettings(
    string? Host,
    int? Port,
    string? Database,
    string? Username,
    string? SslMode,
    string? ProtectedPassword,
    string? ProtectedConnectionString,
    string Schema,
    string Table,
    string? VectorColumn,
    string Distance);

/// <summary>
/// A connection to a PostgreSQL database as the adapter understands it, secrets in the clear and only for the length of a call.
/// </summary>
/// <param name="ConnectionString">The string the administrator typed, when they chose that way; <see langword="null"/> with fields.</param>
/// <param name="Password">With fields: the plain password, if the database asks for one.</param>
internal sealed record PgVectorConnection(
    string? Host,
    int? Port,
    string? Database,
    string? Username,
    string? Password,
    string? SslMode,
    string? ConnectionString,
    string Schema,
    string? Table,
    string? VectorColumn,
    PgVectorDistance Distance) : IStoreConnection
{
    public const int DefaultPort = 5432;

    /// <summary>Few connections per registered knowledge base: a small pool is plenty for a read-only assistant (spec 008 D-4).</summary>
    private const int DefaultMaxPoolSize = 5;

    public bool UsesConnectionString => ConnectionString is not null;

    public bool UsesFields => Host is not null;

    /// <summary>Whether the request says how to reach the database at all; without it the secret is the stored one.</summary>
    public bool HasTarget => UsesConnectionString || UsesFields;

    /// <summary>The source the connection names, quoted, for messages; never a secret.</summary>
    public string SourceName => $"{Schema}.{Table}";

    /// <summary>
    /// What Npgsql connects with: the administrator's string as written, or the fields; the only additions are the defaults they did not
    /// set (a short connect timeout, a small pool, a name for the server's logs). A string that does not parse is rejected when it is read.
    /// </summary>
    public string ToNpgsqlConnectionString()
    {
        var builder = UsesConnectionString
            ? new NpgsqlConnectionStringBuilder(ConnectionString)
            : new NpgsqlConnectionStringBuilder
            {
                Host = Host,
                Database = Database,
                Username = Username,
                Password = Password,
                Port = Port ?? DefaultPort,
                SslMode = Enum.Parse<SslMode>(SslMode ?? nameof(Npgsql.SslMode.Prefer), ignoreCase: true),
            };

        if (!builder.ShouldSerialize("Maximum Pool Size"))
        {
            builder.MaxPoolSize = DefaultMaxPoolSize;
        }

        if (!builder.ShouldSerialize("Timeout"))
        {
            builder.Timeout = (int)StoreLimits.RequestTimeout.TotalSeconds;
        }

        // Npgsql abandons a command after 30 seconds by default, which would cut a lookup that is allowed ten minutes: the limits are the
        // statement timeout the connector sets in each transaction and the cancellation token of the caller.
        if (!builder.ShouldSerialize("Command Timeout"))
        {
            builder.CommandTimeout = 0;
        }

        if (!builder.ShouldSerialize("Application Name"))
        {
            builder.ApplicationName = "Quorum";
        }

        return builder.ConnectionString;
    }

    // The generated ToString prints every member: a password or a connection string must never reach a log or an exception message that way.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Source = {SourceName}");
        return true;
    }
}
