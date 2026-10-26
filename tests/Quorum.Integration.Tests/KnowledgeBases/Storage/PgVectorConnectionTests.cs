using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Modules.KnowledgeBases.Storage.PgVector;

namespace Quorum.Integration.Tests.KnowledgeBases.Storage;

/// <summary>The connection of a PgVector knowledge base (spec 008 D-4, D-15, D-16): what is read, what is kept and what is never shown.</summary>
public sealed class PgVectorConnectionTests
{
    private const string Password = "s3cret-pw";

    private static readonly KnowledgeBaseSecretProtector Protector = new(new EphemeralDataProtectionProvider());

    private static PgVectorStoreAdapter Adapter(KnowledgeBaseSecretProtector? protector = null)
    {
        var connector = new PgVectorConnector(new PgVectorDataSources());
        return new PgVectorStoreAdapter(connector, protector ?? Protector, new PgVectorSourceListing(connector));
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static PgVectorConnection Read(object connection, PgVectorStoreAdapter? adapter = null) =>
        (PgVectorConnection)(adapter ?? Adapter()).ReadConnection(Json(connection)).Value;

    private static object Fields(string host = "db", int port = 5432, string database = "kb", string username = "reader", string? password = Password) =>
        new { host, port, database, username, password, table = "chunks" };

    [Fact]
    public void FieldsAreReadWithTheDefaultsOfTheKind()
    {
        var connection = Read(new { host = " db ", database = "kb", username = "reader", table = "chunks" });

        Assert.Equal("db", connection.Host);
        Assert.Equal("public", connection.Schema);
        Assert.Equal(PgVectorDistance.Cosine, connection.Distance);
        Assert.Null(connection.VectorColumn);
        Assert.False(connection.UsesConnectionString);
    }

    [Theory]
    [InlineData("Cosine", "Cosine")]
    [InlineData("l2", "L2")]
    [InlineData("InnerProduct", "InnerProduct")]
    public void TheDistanceIsReadIgnoringCase(string text, string expected)
    {
        Assert.Equal(expected, Read(new { host = "db", database = "kb", username = "reader", table = "chunks", distance = text }).Distance.ToString());
    }

    [Theory]
    [InlineData("""{"host":"db","database":"kb","username":"reader","table":"t","distance":"Manhattan"}""")]
    [InlineData("""{"host":"db","database":"kb","username":"reader","table":"t","distance":"1"}""")]
    [InlineData("""{"host":"db","database":"kb","username":"reader","table":"t","port":70000}""")]
    [InlineData("""{"host":"db","database":"kb","username":"reader","table":"t","port":"abc"}""")]
    [InlineData("""{"host":"db","database":"kb","table":"t"}""")]
    [InlineData("""{"host":"db","database":"kb","username":"reader","table":"t","sslMode":"7"}""")]
    [InlineData("""{"host":"db","database":"kb","username":"reader"}""")]
    [InlineData("""{"connectionString":"Host=db","host":"db","table":"t"}""")]
    [InlineData("""{"connectionString":"a=b=c;;;=","table":"t"}""")]
    [InlineData("""{"connectionString":"Host=db;Bogus=1","table":"t"}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ABadConnectionIsRefusedWithoutEchoingIt(string json)
    {
        var result = Adapter().ReadConnection(JsonDocument.Parse(json).RootElement);

        Assert.False(result.IsSuccess);
        Assert.Equal("knowledge_base.invalid_connection", result.Error.Code);
        Assert.DoesNotContain("Bogus", result.Error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompleteConnectionStringIsAcceptedAsItIs()
    {
        var connection = Read(new { connectionString = "Host=db;Database=kb;Username=reader;Password=pw;SSL Mode=Require;Search Path=x", table = "chunks" });

        Assert.True(connection.UsesConnectionString);
        Assert.Contains("Search Path=x", connection.ToNpgsqlConnectionString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADefaultIsOnlyAddedWhereTheAdministratorSaidNothing()
    {
        var fromFields = new NpgsqlConnectionStringBuilder(Read(Fields()).ToNpgsqlConnectionString());
        var fromString = new NpgsqlConnectionStringBuilder(
            Read(new { connectionString = "Host=db;Database=kb;Username=u;MaxPoolSize=20;Timeout=30;Application Name=mine", table = "t" }).ToNpgsqlConnectionString());

        Assert.Equal((5, 10, "Quorum"), (fromFields.MaxPoolSize, fromFields.Timeout, fromFields.ApplicationName));
        Assert.Equal((20, 30, "mine"), (fromString.MaxPoolSize, fromString.Timeout, fromString.ApplicationName));
    }

    [Fact]
    public void AConnectionNeverPrintsItsSecrets()
    {
        var text = Read(new { connectionString = $"Host=db;Password={Password}", table = "chunks" }).ToString() + Read(Fields()).ToString();

        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSecretsAreEncryptedInTheSettingsAndNeverDescribed()
    {
        var adapter = Adapter();
        var settings = adapter.ToSettings(Read(Fields(), adapter));

        var stored = settings.ToJson();
        var described = adapter.Describe(settings).ToJsonString();

        Assert.DoesNotContain(Password, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, described, StringComparison.Ordinal);
        Assert.True(adapter.Describe(settings)["hasPassword"]!.GetValue<bool>());
        Assert.False(adapter.Describe(settings)["hasConnectionString"]!.GetValue<bool>());
        Assert.Equal("chunks", adapter.Describe(settings)["table"]!.GetValue<string>());
        Assert.True(adapter.TryOpen(settings, out var opened));
        Assert.Equal(Password, ((PgVectorConnection)opened).Password);
    }

    [Fact]
    public void AConnectionStringIsEncryptedWholeAndOnlyReportedAsSet()
    {
        var adapter = Adapter();
        var settings = adapter.ToSettings(Read(new { connectionString = $"Host=db;Database=kb;Username=u;Password={Password}", table = "chunks" }, adapter));

        var described = adapter.Describe(settings);

        Assert.DoesNotContain(Password, settings.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("Host=db", settings.ToJson(), StringComparison.Ordinal);
        Assert.True(described["hasConnectionString"]!.GetValue<bool>());
        Assert.False(described["hasPassword"]!.GetValue<bool>());
        Assert.True(adapter.TryOpen(settings, out var opened));
        Assert.Contains(Password, ((PgVectorConnection)opened).ConnectionString, StringComparison.Ordinal);
    }

    [Fact] // AC-5
    public void AnOmittedPasswordIsKeptForTheSameHostPortDatabaseAndUsername()
    {
        var adapter = Adapter();
        var stored = adapter.ToSettings(Read(Fields(), adapter));

        var kept = adapter.KeepSecrets(Read(Fields(password: null), adapter), stored);

        Assert.True(kept.IsSuccess);
        Assert.Equal(Password, ((PgVectorConnection)kept.Value).Password);
    }

    [Theory] // AC-5
    [InlineData("other-host", 5432, "kb", "reader")]
    [InlineData("db", 5433, "kb", "reader")]
    [InlineData("db", 5432, "other", "reader")]
    [InlineData("db", 5432, "kb", "admin")]
    public void AnOmittedPasswordIsNotSentToAnotherServerOrAccount(string host, int port, string database, string username)
    {
        var adapter = Adapter();
        var stored = adapter.ToSettings(Read(Fields(), adapter));

        var kept = adapter.KeepSecrets(Read(Fields(host, port, database, username, password: null), adapter), stored);

        Assert.False(kept.IsSuccess);
        Assert.Equal("knowledge_base.password_required", kept.Error.Code);
    }

    [Fact] // AC-5
    public void ANewPasswordReplacesTheStoredOneWhateverChanged()
    {
        var adapter = Adapter();
        var stored = adapter.ToSettings(Read(Fields(), adapter));

        var kept = adapter.KeepSecrets(Read(Fields(host: "elsewhere", password: "new-pw"), adapter), stored);

        Assert.Equal("new-pw", ((PgVectorConnection)kept.Value).Password);
    }

    [Fact] // AC-5
    public void AnOmittedConnectionStringKeepsTheStoredOne()
    {
        var adapter = Adapter();
        var stored = adapter.ToSettings(Read(new { connectionString = $"Host=db;Password={Password}", table = "chunks" }, adapter));

        var kept = adapter.KeepSecrets(Read(new { table = "other_chunks" }, adapter), stored);

        Assert.True(kept.IsSuccess);
        Assert.Contains(Password, ((PgVectorConnection)kept.Value).ConnectionString, StringComparison.Ordinal);
        Assert.Equal("other_chunks", ((PgVectorConnection)kept.Value).Table);
    }

    [Fact] // AC-5
    public void SwitchingBetweenFieldsAndAConnectionStringNeedsTheNewSecret()
    {
        var adapter = Adapter();
        var storedString = adapter.ToSettings(Read(new { connectionString = $"Host=db;Password={Password}", table = "chunks" }, adapter));
        var storedFields = adapter.ToSettings(Read(Fields(), adapter));

        var toFields = adapter.KeepSecrets(Read(Fields(password: null), adapter), storedString);
        var toString = adapter.KeepSecrets(Read(new { table = "chunks" }, adapter), storedFields);

        Assert.Equal("knowledge_base.password_required", toFields.Error.Code);
        Assert.Equal("knowledge_base.invalid_connection", toString.Error.Code);
    }

    [Fact] // AC-3
    public void ANewKnowledgeBaseWithoutAnyWayToReachTheDatabaseIsRefused()
    {
        var adapter = Adapter();

        var kept = adapter.KeepSecrets(Read(new { table = "chunks" }, adapter), stored: null);

        Assert.Equal("knowledge_base.invalid_connection", kept.Error.Code);
    }

    [Fact]
    public void AStoredPasswordThatCannotBeReadAsksForAnotherAndSaysItCannotBeOpened()
    {
        var adapter = Adapter();
        var stored = adapter.ToSettings(Read(Fields(), adapter));
        var replacedKeys = Adapter(new KnowledgeBaseSecretProtector(new EphemeralDataProtectionProvider()));

        var kept = replacedKeys.KeepSecrets(Read(Fields(password: null), replacedKeys), stored);

        Assert.Equal("knowledge_base.password_required", kept.Error.Code);
        Assert.False(replacedKeys.TryOpen(stored, out _));
        Assert.Equal("knowledge_base.password_unreadable", replacedKeys.SecretUnreadable.Code);
    }

    [Fact]
    public void TheKindSupportsNoLookupsButListsItsSources()
    {
        var adapter = Adapter();

        Assert.Equal(KnowledgeBaseKind.PgVector, adapter.Kind);
        Assert.Null(adapter.Capability<ILookupsCapability>());
        Assert.NotNull(adapter.Capability<ISourceListingCapability>());
    }

    [Fact]
    public void TheFailuresOfTheDatabaseAreNamedByTheirOwnCodes()
    {
        var adapter = Adapter();

        Assert.Equal("knowledge_base.pgvector_unreachable", adapter.CodeOf(StoreFailure.Unreachable));
        Assert.Equal("knowledge_base.pgvector_unauthorized", adapter.CodeOf(StoreFailure.Unauthorized));
        Assert.Equal("knowledge_base.pgvector_relation_not_found", adapter.CodeOf(StoreFailure.NotFound));
        Assert.Equal("knowledge_base.pgvector_dimension_mismatch", adapter.CodeOf(StoreFailure.Rejected));
        Assert.Equal("knowledge_base.pgvector_unexpected_response", adapter.CodeOf(StoreFailure.Unexpected));
    }
}
