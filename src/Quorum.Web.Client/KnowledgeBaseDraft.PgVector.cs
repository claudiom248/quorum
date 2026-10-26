using System.Globalization;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

// What is specific to a knowledge base in a PostgreSQL database with pgvector (spec 008): the account, the relation and the vector column.
public sealed partial class KnowledgeBaseDraft
{
    public static IReadOnlyList<string> Distances { get; } = ["Cosine", "L2", "InnerProduct"];

    public static IReadOnlyList<string> SslModes { get; } = ["Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull"];

    /// <summary>What the knowledge base is stored in. It cannot change once registered: another kind starts from nothing.</summary>
    public string Kind { get; set; } = KnowledgeBaseKinds.Qdrant;

    public bool IsPgVector => Kind == KnowledgeBaseKinds.PgVector;

    /// <summary>The way the database is reached: a connection string instead of separate fields.</summary>
    public bool UseConnectionString { get; set; }

    public string Host { get; set; } = string.Empty;

    /// <summary>Text, so that a half-typed number is a problem to show and not a value lost.</summary>
    public string Port { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string SslMode { get; set; } = string.Empty;

    public string ConnectionString { get; set; } = string.Empty;

    public string DatabaseSchema { get; set; } = string.Empty;

    public string Table { get; set; } = string.Empty;

    public string VectorColumn { get; set; } = string.Empty;

    public string Distance { get; set; } = string.Empty;

    /// <summary>The account when the draft was loaded: a stored password is only kept for the same host, port, database and user.</summary>
    public string OriginalAccount { get; init; } = string.Empty;

    public bool HadPassword { get; init; }

    public bool HadConnectionString { get; init; }

    /// <summary>The password is stored for another account: the server will ask for it again.</summary>
    public bool NeedsPasswordAgain => IsPgVector && !UseConnectionString && HadPassword && Password.Length == 0 && Account() != OriginalAccount;

    /// <summary>The way of giving the secret changed, so the new secret is needed (spec 008 D-16).</summary>
    public bool NeedsSecretForNewMode =>
        IsPgVector && !IsNew && ((UseConnectionString && !HadConnectionString && ConnectionString.Trim().Length == 0) || (!UseConnectionString && HadConnectionString && Password.Length == 0));

    private static KnowledgeBaseDraft FromPgVector(KnowledgeBaseDetail detail, ConnectionInfo connection)
    {
        var draft = new KnowledgeBaseDraft
        {
            Id = detail.Id,
            Kind = KnowledgeBaseKinds.PgVector,
            HadPassword = connection.HasPassword,
            HadConnectionString = connection.HasConnectionString,
            OriginalAccount = AccountOf(connection.Host, connection.Port, connection.Database, connection.Username),
            UseConnectionString = connection.HasConnectionString,
            Host = connection.Host ?? string.Empty,
            Port = connection.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Database = connection.Database ?? string.Empty,
            Username = connection.Username ?? string.Empty,
            SslMode = connection.SslMode ?? string.Empty,
            DatabaseSchema = connection.Schema ?? string.Empty,
            Table = connection.Table ?? string.Empty,
            VectorColumn = connection.VectorColumn ?? string.Empty,
            Distance = connection.Distance ?? string.Empty,
        };
        draft.FillCommon(detail);
        return draft;
    }

    /// <summary>Takes a relation from the list the server gave: the schema, the name and, when it has only one, the vector column.</summary>
    public void UseRelation(RelationInfo relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        DatabaseSchema = relation.Schema;
        Table = relation.Name;
        VectorColumn = relation.VectorColumns.Count == 1 ? relation.VectorColumns[0].Name : string.Empty;
        Distance = string.Empty;
    }

    /// <summary>The request that lists the relations of the database: the connection without the relation, which is what is being chosen.</summary>
    public RelationsRequest ToRelationsRequest(string? filter) => new(
        Kind,
        PgVectorConnection(includeSource: false),
        string.IsNullOrWhiteSpace(filter) ? null : filter.Trim());

    /// <summary>What blocks the connection step of a PostgreSQL knowledge base: the same limits the server applies.</summary>
    public IReadOnlyList<string> PgVectorConnectionProblems()
    {
        var problems = new List<string>(CommonProblems());
        problems.AddRange(PgVectorServerProblems());

        if (Table.Trim().Length == 0)
        {
            problems.Add("The relation (table or view) is required: list the relations of the database and choose one.");
        }

        return problems;
    }

    /// <summary>What blocks listing the relations: everything about reaching the database, nothing about the relation itself.</summary>
    public IReadOnlyList<string> PgVectorServerProblems()
    {
        var problems = new List<string>();
        if (UseConnectionString)
        {
            if (ConnectionString.Trim().Length == 0 && !(HadConnectionString && !IsNew))
            {
                problems.Add("The connection string is required.");
            }
        }
        else
        {
            if (Host.Trim().Length == 0)
            {
                problems.Add("The host of the database is required.");
            }

            if (Database.Trim().Length == 0)
            {
                problems.Add("The database is required.");
            }

            if (Username.Trim().Length == 0)
            {
                problems.Add("The user is required.");
            }

            if (Port.Trim().Length > 0 && !(int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535))
            {
                problems.Add("The port is a number from 1 to 65535.");
            }
        }

        if (NeedsSecretForNewMode)
        {
            problems.Add(UseConnectionString
                ? "The stored password does not carry over to a connection string: enter the connection string."
                : "The stored connection string does not carry over to separate fields: enter the password.");
        }

        return problems;
    }

    /// <summary>The vector column of the relation is not chosen although there are several (spec 008 AC-8).</summary>
    public bool NeedsVectorColumnChoice(InspectResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Vectors.Count > 1 && VectorColumn.Trim().Length == 0;
    }

    private void UseInspectionPgVector(InspectResult result)
    {
        var chosen = result.Vectors.Count == 1
            ? result.Vectors[0]
            : result.Vectors.FirstOrDefault(v => v.Name == VectorColumn.Trim());
        if (chosen is null)
        {
            return;
        }

        if (VectorColumn.Trim().Length == 0 && chosen.Name is { Length: > 0 } only)
        {
            VectorColumn = only;
        }

        // The distance of the index is a proposal: the administrator's choice, once made, stays.
        if (Distance.Length == 0 && Distances.Contains(chosen.Distance))
        {
            Distance = chosen.Distance;
        }
    }

    private PgVectorConnectionRequest PgVectorConnection(bool includeSource = true)
    {
        var source = includeSource;
        return new PgVectorConnectionRequest(
            UseConnectionString ? null : Clean(Host, trim: true),
            UseConnectionString || Port.Trim().Length == 0 || !int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? null : port,
            UseConnectionString ? null : Clean(Database, trim: true),
            UseConnectionString ? null : Clean(Username, trim: true),
            UseConnectionString ? null : Clean(Password, trim: false),
            UseConnectionString ? null : Clean(SslMode, trim: true),
            UseConnectionString ? Clean(ConnectionString, trim: true) : null,
            Clean(DatabaseSchema, trim: true),
            source ? Clean(Table, trim: true) : null,
            source ? Clean(VectorColumn, trim: true) : null,
            source ? Clean(Distance, trim: true) : null);
    }

    private string Account() => AccountOf(Host, Port, Database, Username);

    private static string AccountOf(string? host, int? port, string? database, string? username) =>
        AccountOf(host, port?.ToString(CultureInfo.InvariantCulture), database, username);

    // The server compares them the same way: the host without regard to case, a missing port as the default one.
    private static string AccountOf(string? host, string? port, string? database, string? username) =>
        string.Join(
            '\u001f',
            (host ?? string.Empty).Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(port) || port.Trim() == "5432" ? "5432" : port.Trim(),
            (database ?? string.Empty).Trim(),
            (username ?? string.Empty).Trim());

    private IEnumerable<string> PgVectorSummary()
    {
        var where = UseConnectionString ? "a connection string" : $"{Host.Trim()}{(Port.Trim().Length > 0 ? ":" + Port.Trim() : string.Empty)}/{Database.Trim()}";
        var schema = DatabaseSchema.Trim().Length > 0 ? DatabaseSchema.Trim() + "." : string.Empty;
        yield return $"{Name.Trim()} · PostgreSQL {where} · {schema}{Table.Trim()}";
        yield return $"Vector {(VectorColumn.Trim().Length > 0 ? VectorColumn.Trim() : "(the only one)")} · distance {(Distance.Length > 0 ? Distance : "Cosine")}";
    }
}
