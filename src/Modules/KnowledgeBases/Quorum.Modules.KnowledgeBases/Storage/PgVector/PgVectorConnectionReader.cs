using System.Text.Json;
using Npgsql;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Storage.PgVector;

/// <summary>
/// Reads and checks the <c>connection</c> object of a request (spec 008 D-4, D-15). Quorum does not restrict the address or filter the
/// parameters of a connection string: what the administrator enters is their responsibility. A string that cannot be parsed is rejected
/// without being echoed, because it carries a password.
/// </summary>
internal static class PgVectorConnectionReader
{
    public const string DefaultSchema = "public";

    private const int MaxNameLength = 255;

    private const int MaxConnectionStringLength = 4096;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static Result<PgVectorConnection> Read(JsonElement? connection, bool requireTable)
    {
        PgVectorConnectionRequest? request;
        try
        {
            request = connection is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element
                ? element.Deserialize<PgVectorConnectionRequest>(Web)
                : null;
        }
        catch (JsonException)
        {
            return Invalid("The connection must be an object whose members are text, except port, which is a number.");
        }

        if (request is null)
        {
            return Invalid("The connection is required: host, database and username, or a connectionString, and the relation.");
        }

        var host = Trim(request.Host);
        var database = Trim(request.Database);
        var username = Trim(request.Username);
        var sslMode = Trim(request.SslMode);
        var connectionString = string.IsNullOrWhiteSpace(request.ConnectionString) ? null : request.ConnectionString;
        var password = string.IsNullOrEmpty(request.Password) ? null : request.Password;
        var fields = host is not null || database is not null || username is not null || request.Port is not null || password is not null || sslMode is not null;

        if (fields && connectionString is not null)
        {
            return Invalid("Use either host, database and username (with the password), or a connectionString, not both.");
        }

        if (connectionString is not null && Check(connectionString) is { } stringProblem)
        {
            return stringProblem;
        }

        if (fields && Check(host, database, username, request.Port, sslMode) is { } fieldsProblem)
        {
            return fieldsProblem;
        }

        var schema = Trim(request.Schema) ?? DefaultSchema;
        var table = Trim(request.Table);
        var vectorColumn = Trim(request.VectorColumn);
        if (schema.Length > MaxNameLength || table?.Length > MaxNameLength || vectorColumn?.Length > MaxNameLength)
        {
            return Invalid($"The schema, the relation and the vector column can have up to {MaxNameLength} characters.");
        }

        if (requireTable && table is null)
        {
            return Invalid("The relation (table or view) is required.");
        }

        var distance = PgVectorDistance.Cosine;
        if (Trim(request.Distance) is { } named && !DictionaryFieldRules.TryParseMember(named, out distance))
        {
            return Invalid("The distance must be Cosine, L2 or InnerProduct.");
        }

        return new PgVectorConnection(host, request.Port, database, username, password, sslMode, connectionString, schema, table, vectorColumn, distance);
    }

    private static Error? Check(string connectionString)
    {
        if (connectionString.Length > MaxConnectionStringLength)
        {
            return Invalid($"The connection string can have up to {MaxConnectionStringLength} characters.");
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or FormatException)
        {
            return Invalid("The connection string is not valid: it must be in the Npgsql format, for example Host=db;Database=knowledge;Username=quorum;Password=...");
        }
    }

    private static Error? Check(string? host, string? database, string? username, int? port, string? sslMode)
    {
        if (host is null || database is null || username is null)
        {
            return Invalid("The host, the database and the username are required.");
        }

        if (host.Length > MaxNameLength || database.Length > MaxNameLength || username.Length > MaxNameLength)
        {
            return Invalid($"The host, the database and the username can have up to {MaxNameLength} characters.");
        }

        if (port is < 1 or > 65535)
        {
            return Invalid("The port must be between 1 and 65535.");
        }

        // Enum.TryParse would also take a number.
        return sslMode is not null && !Enum.GetNames<SslMode>().Any(name => string.Equals(name, sslMode, StringComparison.OrdinalIgnoreCase))
            ? Invalid($"The SSL mode must be one of: {string.Join(", ", Enum.GetNames<SslMode>())}.")
            : null;
    }

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static Error Invalid(string description) => Error.Validation("knowledge_base.invalid_connection", description);
}
