namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// Plain JSON Schemas of the sample payloads, the kind a data owner hands to the administrator: no Quorum keywords, written by
/// someone who knows nothing about Quorum.
/// </summary>
public static class SampleSchemas
{
    /// <summary>The payload of <see cref="SampleCollections.Flat"/>.</summary>
    public const string Flat = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "Policy chunk",
          "type": "object",
          "properties": {
            "text": { "type": "string" },
            "document_id": { "type": "string" },
            "title": { "type": "string" },
            "page": { "type": "integer" },
            "source_url": { "type": "string", "format": "uri" },
            "department": { "type": "string" },
            "year": { "type": "integer" },
            "published_on": { "type": "string", "format": "date" },
            "confidential": { "type": "boolean" }
          },
          "required": ["text"]
        }
        """;

    /// <summary>The payload of <see cref="SampleCollections.Lookups"/>.</summary>
    public const string Lookups = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "Contract chunk",
          "type": "object",
          "properties": {
            "text": { "type": "string" },
            "document_id": { "type": "string" },
            "title": { "type": "string" },
            "source_url": { "type": "string", "format": "uri" },
            "supplier": { "type": "string" },
            "department": { "type": "string" },
            "nationality": { "type": "string" },
            "audience": { "type": "array", "items": { "type": "string" } },
            "is_first_chunk": { "type": "boolean" },
            "chunk_index": { "type": "integer" }
          },
          "required": ["text"]
        }
        """;

    /// <summary>The payload of <see cref="SampleCollections.Suppliers"/>.</summary>
    public const string Suppliers = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "Supplier",
          "type": "object",
          "properties": {
            "text": { "type": "string" },
            "name": { "type": "string" },
            "country": { "type": "string" },
            "contract_count": { "type": "integer" },
            "supplier_since": { "type": "string", "format": "date" }
          },
          "required": ["text", "name"]
        }
        """;

    /// <summary>The payload of <see cref="SampleCollections.Nested"/>.</summary>
    public const string Nested = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "Policy chunk",
          "type": "object",
          "properties": {
            "chunk_text": { "type": "string" },
            "doc_id": { "type": "string" },
            "file_name": { "type": "string" },
            "page": { "type": "integer" },
            "source_url": { "type": "string", "format": "uri" },
            "meta": {
              "type": "object",
              "properties": {
                "department": { "type": "string" },
                "year": { "type": "integer" },
                "published_on": { "type": "string", "format": "date" }
              }
            }
          },
          "required": ["chunk_text"]
        }
        """;
}
