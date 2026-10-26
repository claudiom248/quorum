using System.Net;
using Quorum.Integration.Tests.Fixtures;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>Spec 008 D-9, D-10: looking at a relation before registering it.</summary>
public sealed class PgVectorInspectTests(PostgresFixture postgres, PgVectorFixture pgvector)
{
    private async Task<InspectApi.Report> InspectAsync(string relation, string? schema = null, string? vectorColumn = null, object? pastedSchema = null, int dimensions = DeterministicEmbedding.DefaultDimensions, Dictionary<string, object?>? connection = null)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app, dimensions: dimensions);
        schema ??= await pgvector.GetSharedSchemaAsync();
        return await PgVectorApi.InspectOkAsync(
            admin,
            connection ?? PgVectorApi.Fields(await pgvector.GetEndpointAsync(), schema, relation, vectorColumn),
            pastedSchema);
    }

    [Fact] // AC-2
    public async Task APgVectorKnowledgeBaseIsAcceptedAndAnUnknownKindIsNot()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        InspectApi.ServeEmbeddings(app);

        using var unknown = await PgVectorApi.PostInspectAsync(admin, new { }, kind: "Pinecone");

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("knowledge_base.unsupported_kind", await PgVectorApi.ProblemCodeAsync(unknown));
        Assert.True((await InspectAsync(SampleRelations.Flat)).Reachable);
    }

    [Fact] // AC-3
    public async Task AConnectionStringInspectsTheSameRelation()
    {
        var schema = await pgvector.GetSharedSchemaAsync();
        var connection = PgVectorApi.ConnectionString(await pgvector.GetEndpointAsync(), schema, SampleRelations.Flat);

        var report = await InspectAsync(SampleRelations.Flat, connection: connection);

        Assert.True(report.CollectionExists);
        Assert.Equal("text", report.DraftMapping.Roles.Text);
    }

    [Fact] // AC-9, AC-10, AC-11
    public async Task TheRolesAndTheTypedFieldsOfAFlatRelationAreProposed()
    {
        var report = await InspectAsync(SampleRelations.Flat);

        Assert.True(report.Reachable);
        Assert.True(report.CollectionExists);
        Assert.Equal(new InspectApi.RolesBody("text", "document_id", "title", "page", "source_url"), report.DraftMapping.Roles);
        var fields = report.DraftMapping.Fields.ToDictionary(f => f.Path, f => f.Type);
        Assert.Equal("Text", fields["department"]);
        Assert.Equal("Number", fields["year"]);
        Assert.Equal("Date", fields["published_on"]);
        Assert.Equal("Boolean", fields["confidential"]);
        Assert.DoesNotContain("embedding", fields.Keys);
        Assert.Equal(64, report.EmbeddingSize);
        Assert.True(report.DimensionMatch);
        Assert.Equal([new InspectApi.VectorBody("embedding", 64, "Cosine")], report.Vectors);
        Assert.Empty(report.UnindexedFields);
        Assert.All(report.Warnings, w => Assert.Equal("knowledge_base.pgvector_writable", w.Code)); // the test database is reached as its owner
    }

    [Fact] // AC-10, AC-11
    public async Task TheKeysOfAJsonbColumnAreFoundWithTheirOwnPathsAndTypedFromTheirValues()
    {
        var report = await InspectAsync(SampleRelations.LangChain);

        var fields = report.DraftMapping.Fields.ToDictionary(f => f.Path, f => (f.Name, f.Type));
        Assert.Equal(("year", "Number"), fields["cmetadata.year"]);
        Assert.Equal(("department", "Text"), fields["cmetadata.department"]);
        Assert.Equal(("published_on", "Date"), fields["cmetadata.published_on"]);
        Assert.Equal(("confidential", "Boolean"), fields["cmetadata.confidential"]);
        Assert.DoesNotContain("cmetadata", fields.Keys);
        Assert.DoesNotContain("embedding", fields.Keys);
        Assert.Equal("cmetadata.document_id", report.DraftMapping.Roles.DocumentId);
    }

    [Fact] // AC-11
    public async Task AListOfTextsIsAListField()
    {
        var report = await InspectAsync(SampleRelations.Tags);

        Assert.Equal("TextList", report.DraftMapping.Fields.Single(f => f.Path == "tags").Type);
        Assert.Equal("content", report.DraftMapping.Roles.Text);
    }

    [Fact] // AC-11
    public async Task ADeclaredTypeWinsOverTheValuesOfTheSampleAndAnEmptyColumnStillHasAType()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $"""
            CREATE TYPE {schema}.mood AS ENUM ('calm', 'busy');
            CREATE DOMAIN {schema}.score AS integer;
            CREATE TABLE {schema}.typed (
                id bigint PRIMARY KEY, text text NOT NULL, code text, mood {schema}.mood, rating {schema}.score, ratio double precision,
                price numeric(10, 2), seen timestamp, seen_at timestamptz, ref uuid, never_set integer, embedding vector(64));
            INSERT INTO {schema}.typed (id, text, code, mood, rating, ratio, price, seen, seen_at, ref)
            VALUES (1, 'a', '2024-01-15', 'calm', 3, 0.5, 12.50, '2024-01-15 10:00:00', '2024-01-15 10:00:00+02', gen_random_uuid());
            """);

        var report = await InspectAsync("typed", schema);

        var fields = report.DraftMapping.Fields.ToDictionary(f => f.Path, f => f.Type);
        Assert.Equal("Text", fields["code"]);
        Assert.Equal("Text", fields["mood"]);
        Assert.Equal("Number", fields["rating"]);
        Assert.Equal("Number", fields["ratio"]);
        Assert.Equal("Number", fields["price"]);
        Assert.Equal("Date", fields["seen"]);
        Assert.Equal("Date", fields["seen_at"]);
        Assert.Equal("Text", fields["ref"]);
        Assert.Equal("Number", fields["never_set"]);
        Assert.DoesNotContain("knowledge_base.no_values", report.WarningCodes);
    }

    [Fact] // AC-12
    public async Task ColumnsThatCannotBeMappedAreReportedNotDropped()
    {
        var report = await InspectAsync(SampleRelations.Unmappable);

        var unmapped = report.Warnings.Where(w => w.Code == "knowledge_base.pgvector_column_not_mapped").Select(w => w.Path).Order(StringComparer.Ordinal);
        Assert.Equal(["duration", "raw", "scores", "years"], unmapped);
        Assert.DoesNotContain(report.DraftMapping.Fields, f => f.Path is "raw" or "years" or "duration" or "scores");
        Assert.Equal("title", report.DraftMapping.Roles.Title);
    }

    [Fact] // AC-12
    public async Task AColumnWhoseNameHasADotIsReported()
    {
        var report = await InspectAsync(SampleRelations.DottedColumn);

        var warning = Assert.Single(report.Warnings, w => w.Code == "knowledge_base.pgvector_column_name_unsupported");
        Assert.Equal("meta.year", warning.Path);
        Assert.DoesNotContain(report.DraftMapping.Fields, f => f.Path.StartsWith("meta", StringComparison.Ordinal));
    }

    [Fact] // AC-13
    public async Task APastedSchemaGivesTheDraftEvenOnAnEmptyRelation()
    {
        var report = await InspectAsync(SampleRelations.Empty, pastedSchema: SampleSchemas.RelationFlat);

        Assert.Equal("text", report.DraftMapping.Roles.Text);
        Assert.Equal("document_id", report.DraftMapping.Roles.DocumentId);
        Assert.Equal("Number", report.DraftMapping.Fields.Single(f => f.Path == "year").Type);
    }

    [Fact] // AC-9
    public async Task AnEmptyRelationIsSaidToBeEmpty()
    {
        var report = await InspectAsync(SampleRelations.Empty);

        Assert.True(report.CollectionExists);
        Assert.Contains("knowledge_base.pgvector_empty_relation", report.WarningCodes);
        Assert.True(report.DimensionMatch);
    }

    [Fact] // AC-14
    public async Task TheDimensionsOfAColumnThatDeclaresNoneAreMeasuredOnARow()
    {
        var report = await InspectAsync(SampleRelations.UnspecifiedDimensions);

        Assert.Equal([new InspectApi.VectorBody("embedding", 64, "Cosine")], report.Vectors);
        Assert.True(report.DimensionMatch);
    }

    [Fact] // AC-14
    public async Task ColumnsThatDeclareNoDimensionsAndHaveNoRowAreSaidToBeUnknown()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync($"CREATE TABLE {schema}.undeclared (id bigint PRIMARY KEY, text text, embedding vector)");

        var report = await InspectAsync("undeclared", schema);

        Assert.Null(report.DimensionMatch);
        Assert.Contains("knowledge_base.pgvector_dimensions_unknown", report.WarningCodes);
    }

    [Fact] // AC-17
    public async Task VectorsOfAnotherSizeThanTheModelsAreAMismatch()
    {
        var report = await InspectAsync(SampleRelations.OtherVectorSize);

        Assert.False(report.DimensionMatch);
        Assert.Contains("knowledge_base.pgvector_dimension_mismatch", report.WarningCodes);
    }

    [Theory] // AC-15
    [InlineData(SampleRelations.Flat, "Cosine", false)]
    [InlineData(SampleRelations.IvfFlatL2, "L2", false)]
    [InlineData(SampleRelations.InnerProduct, "InnerProduct", false)]
    [InlineData(SampleRelations.HalfVector, "Cosine", false)]
    [InlineData(SampleRelations.Unindexed, "Cosine", true)]
    public async Task TheDistanceIsTakenFromTheOperatorClassOfTheIndex(string relation, string distance, bool warns)
    {
        var report = await InspectAsync(relation);

        Assert.Equal(distance, Assert.Single(report.Vectors).Distance);
        Assert.Equal(warns, report.WarningCodes.Contains("knowledge_base.pgvector_distance_defaulted"));
    }

    [Fact] // AC-15
    public async Task IndexesWithDifferentOperatorClassesLeaveTheChoiceToTheAdministrator()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $"""
            CREATE TABLE {schema}.both (id bigint PRIMARY KEY, text text, embedding vector(64));
            CREATE INDEX ON {schema}.both USING hnsw (embedding vector_cosine_ops);
            CREATE INDEX ON {schema}.both USING hnsw (embedding vector_l2_ops);
            """);

        var report = await InspectAsync("both", schema);

        Assert.Equal("Cosine", Assert.Single(report.Vectors).Distance);
        Assert.Contains("knowledge_base.pgvector_distance_defaulted", report.WarningCodes);
    }

    [Fact] // AC-8
    public async Task SeveralVectorColumnsAskForAChoiceAndAChosenOneIsChecked()
    {
        var withoutChoice = await InspectAsync(SampleRelations.TwoVectors);
        var summary = await InspectAsync(SampleRelations.TwoVectors, vectorColumn: SampleRelations.SummaryColumn);
        var unknown = await InspectAsync(SampleRelations.TwoVectors, vectorColumn: "nope");

        Assert.Equal(["embedding", SampleRelations.SummaryColumn], withoutChoice.Vectors.Select(v => v.Name));
        Assert.Contains("knowledge_base.vector_column_required", withoutChoice.WarningCodes);
        Assert.Null(withoutChoice.DimensionMatch);
        Assert.False(summary.DimensionMatch);
        Assert.Contains("knowledge_base.pgvector_dimension_mismatch", summary.WarningCodes);
        Assert.Contains("knowledge_base.vector_column_not_found", unknown.WarningCodes);
    }

    [Fact] // AC-9
    public async Task ColumnsWithoutAnIndexAreListedAndAViewIsNotAccused()
    {
        var flat = await InspectAsync(SampleRelations.Unindexed);
        var view = await InspectAsync(SampleRelations.View);

        Assert.Equal(["confidential", "department", "published_on", "year"], flat.UnindexedFields.Order(StringComparer.Ordinal));
        Assert.All(flat.Warnings.Where(w => w.Code == "knowledge_base.pgvector_field_unindexed"), w => Assert.NotNull(w.Path));
        Assert.Empty(view.UnindexedFields);
    }

    [Fact] // AC-9
    public async Task TheKeysOfAJsonbColumnAreNotCheckedForIndexes()
    {
        var report = await InspectAsync(SampleRelations.LangChain);

        Assert.DoesNotContain(report.UnindexedFields, path => path.StartsWith("cmetadata.", StringComparison.Ordinal));
    }

    [Fact] // AC-17
    public async Task ARelationThatDoesNotExistIsAWarningNotAnError()
    {
        var report = await InspectAsync("no_such_relation");

        Assert.True(report.Reachable);
        Assert.False(report.CollectionExists);
        Assert.Contains("knowledge_base.pgvector_relation_not_found", report.WarningCodes);
    }

    [Fact] // AC-12
    public async Task ARelationWithoutAVectorColumnIsSaidToHaveNone()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync($"CREATE TABLE {schema}.plain (id bigint PRIMARY KEY, text text)");

        var report = await InspectAsync("plain", schema);

        Assert.Empty(report.Vectors);
        Assert.Contains("knowledge_base.vector_column_not_found", report.WarningCodes);
    }

    [Fact] // AC-25
    public async Task NamesWithCapitalsSpacesAndReservedWordsWork()
    {
        var report = await InspectAsync(SampleRelations.MixedCase);

        Assert.True(report.CollectionExists);
        Assert.Equal([new InspectApi.VectorBody("Embedding Vector", 64, "Cosine")], report.Vectors);
        Assert.Contains(report.DraftMapping.Fields, f => f.Path == "Select");
    }

    [Fact] // AC-17
    public async Task WrongCredentialsAreAWarningThatSaysNothingAboutThem()
    {
        var schema = await pgvector.GetSharedSchemaAsync();
        var endpoint = await pgvector.GetEndpointAsync();
        var connection = PgVectorApi.Fields(endpoint, schema, SampleRelations.Flat, password: "not-the-password");

        var report = await InspectAsync(SampleRelations.Flat, connection: connection);

        Assert.False(report.Reachable);
        var warning = Assert.Single(report.Warnings, w => w.Code == "knowledge_base.pgvector_unauthorized");
        Assert.DoesNotContain("not-the-password", warning.Message, StringComparison.Ordinal);
    }

    [Fact] // AC-17
    public async Task ARelationTheUserCannotReadIsAnUnauthorizedWarning()
    {
        var schema = await pgvector.NewSchemaAsync();
        var role = $"limited_{Guid.NewGuid():N}"[..16];
        await pgvector.ExecuteAsync($"CREATE ROLE {role} LOGIN PASSWORD 'limited-pw'");
        await pgvector.ExecuteAsync($"GRANT USAGE ON SCHEMA {schema} TO {role}");
        var endpoint = await pgvector.GetEndpointAsync();

        var report = await InspectAsync(SampleRelations.Flat, connection: PgVectorApi.Fields(endpoint, schema, SampleRelations.Flat, username: role, password: "limited-pw"));

        Assert.Contains("knowledge_base.pgvector_unauthorized", report.WarningCodes);
    }

    [Fact] // AC-9
    public async Task AtMostAHundredRowsAreSampled()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $$"""
            CREATE TABLE {{schema}}.wide (id bigint PRIMARY KEY, text text, meta jsonb, embedding vector(64));
            INSERT INTO {{schema}}.wide SELECT i, 'early', '{"early": 1}', array_fill(0.1::real, ARRAY[64])::vector FROM generate_series(1, 3000) i;
            INSERT INTO {{schema}}.wide SELECT i, 'late', '{"early": 1, "only_late": "x"}', array_fill(0.1::real, ARRAY[64])::vector FROM generate_series(3001, 3010) i;
            """);

        var report = await InspectAsync("wide", schema);

        Assert.Contains(report.DraftMapping.Fields, f => f.Path == "meta.early");
        Assert.DoesNotContain(report.DraftMapping.Fields, f => f.Path == "meta.only_late");
        Assert.True(report.DimensionMatch);
    }

    [Fact] // AC-9
    public async Task ALongValueDoesNotWeighOnTheInspection()
    {
        var schema = await pgvector.NewSchemaAsync();
        await pgvector.ExecuteAsync(
            $$"""
            CREATE TABLE {{schema}}.long (id bigint PRIMARY KEY, text text, meta jsonb, embedding vector(64));
            INSERT INTO {{schema}}.long SELECT i, (SELECT string_agg(md5(random()::text), '') FROM generate_series(1, 20000)), jsonb_build_object('note', (SELECT string_agg(md5(random()::text), '') FROM generate_series(1, 2000))), array_fill(0.1::real, ARRAY[64])::vector FROM generate_series(1, 20) i;
            """);

        var report = await InspectAsync("long", schema);

        Assert.Equal("text", report.DraftMapping.Roles.Text);
        Assert.Contains(report.DraftMapping.Fields, f => f.Path == "meta.note");
    }

    [Fact] // AC-6
    public async Task NoAddressPolicyRefusesTheDatabase()
    {
        var schema = await pgvector.GetSharedSchemaAsync();
        var endpoint = await pgvector.GetEndpointAsync();
        var connection = PgVectorApi.Fields(endpoint, schema, SampleRelations.Flat);
        connection["host"] = "127.0.0.1";

        var report = await InspectAsync(SampleRelations.Flat, connection: connection);

        Assert.True(report.Reachable);
        Assert.True(report.CollectionExists);
    }
}
