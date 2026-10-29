namespace Quorum.Modules.KnowledgeBases.Persistence.Migrations;

/// <summary>
/// The statements that give the lookups that existed before spec 012 their names, descriptions, storage and attributes. The migration runs them
/// before the columns become required, and the tests run them against rows made through the API to keep them honest.
/// </summary>
public static class ConsolidateLookupsBackfill
{
    /// <summary>A computed lookup is named after its knowledge base and field (spec 012 D-2).</summary>
    public const string ComputedNames = """
        UPDATE knowledge_base.lookups AS l
        SET "Name" = kb."Name" || '.' || f."Name",
            "Description" = 'Distinct values of ' || f."Name" || ' in ' || kb."Name"
        FROM knowledge_base.knowledge_bases AS kb, knowledge_base.dictionary_fields AS f
        WHERE l."KnowledgeBaseId" = kb."Id" AND l."DictionaryFieldId" = f."Id" AND l."Kind" = 'Computed';
        """;

    /// <summary>An entity lookup is named after its knowledge base.</summary>
    public const string EntityNames = """
        UPDATE knowledge_base.lookups AS l
        SET "Name" = kb."Name",
            "Description" = 'Entities of ' || kb."Name"
        FROM knowledge_base.knowledge_bases AS kb
        WHERE l."KnowledgeBaseId" = kb."Id" AND l."Kind" = 'Entity';
        """;

    public const string Descriptions = """
        UPDATE knowledge_base.lookups SET "Description" = 'A list of values.' WHERE "Description" IS NULL;
        """;

    /// <summary>Every existing lookup is a snapshot.</summary>
    public const string Storage = """UPDATE knowledge_base.lookups SET "Storage" = 'Snapshot' WHERE "Storage" = '';""";

    /// <summary>An entity lookup gets the attributes its knowledge base maps as attributes; the key is the field name, as the stored values have it.</summary>
    public const string EntityAttributes = """
        UPDATE knowledge_base.lookups AS l
        SET "Attributes" = a.attributes
        FROM (
            SELECT lk."Id" AS id,
                   jsonb_agg(jsonb_build_object(
                       'Key', f."Name",
                       'Label', NULL,
                       'Type', CASE f."Type" WHEN 'Number' THEN 'Number' WHEN 'Date' THEN 'Date' WHEN 'TextList' THEN 'TextList' ELSE 'Text' END,
                       'DictionaryFieldId', f."Id")
                       ORDER BY f."Name") AS attributes
            FROM knowledge_base.lookups AS lk
            JOIN knowledge_base.knowledge_bases AS kb ON kb."Id" = lk."KnowledgeBaseId"
            CROSS JOIN LATERAL jsonb_array_elements(kb."Mapping"->'Fields') AS m
            JOIN knowledge_base.dictionary_fields AS f ON f."Id" = (m->>'DictionaryFieldId')::uuid
            WHERE lk."Kind" = 'Entity' AND (m->>'EntityAttribute')::boolean
            GROUP BY lk."Id"
        ) AS a
        WHERE l."Id" = a.id;
        """;

    public const string EmptyAttributes = """UPDATE knowledge_base.lookups SET "Attributes" = '[]'::jsonb WHERE "Attributes" IS NULL;""";
}
