using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Lookups;

/// <summary>
/// Reads a CSV for a manual lookup and proposes the type of each attribute column from its values (spec 012 D-3). The administrator
/// confirms or corrects the types; the import itself is the create of the manual lookup, which refuses a value that does not fit.
/// </summary>
internal sealed class ManualLookupImportEndpoints : IEndpointGroup
{
    private const long MaxBytes = 1_000_000;

    private const int PreviewRows = 5;

    private static readonly string[] ReservedColumns = ["value", "label", "synonyms"];

    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/knowledge-bases/manual-lookups/import-preview", PreviewAsync)
            .WithTags("Knowledge bases")
            .WithName("PreviewManualLookupImport")
            .RequireAuthorization(QuorumPolicies.KnowledgeBaseManage)
            .DisableAntiforgery();

    private static Results<Ok<ImportPreviewDto>, ProblemHttpResult> PreviewAsync(IFormFile file)
    {
        if (file.Length == 0 || file.Length > MaxBytes)
        {
            return Invalid("The file is empty or larger than 1 MB.").ToProblem();
        }

        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { MissingFieldFound = null, TrimOptions = TrimOptions.Trim });
            if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is not { Length: > 0 } headers)
            {
                return Invalid("The file has no header row.").ToProblem();
            }

            var valueIndex = Array.FindIndex(headers, h => string.Equals(h, "value", StringComparison.OrdinalIgnoreCase));
            if (valueIndex < 0)
            {
                return Invalid("The header must have a column named value.").ToProblem();
            }

            if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
            {
                return Invalid("A column is listed twice in the header.").ToProblem();
            }

            var rows = new List<string[]>();
            while (csv.Read())
            {
                if (rows.Count == Lookup.MaxManualValues)
                {
                    return Invalid($"A manual lookup has at most {Lookup.MaxManualValues} values.").ToProblem();
                }

                rows.Add([.. Enumerable.Range(0, headers.Length).Select(i => csv.GetField(i) ?? string.Empty)]);
            }

            var attributeColumns = headers.Select((name, index) => (name, index))
                .Where(c => !ReservedColumns.Contains(c.name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (attributeColumns.Count > Lookup.MaxAttributes)
            {
                return Invalid($"A lookup has at most {Lookup.MaxAttributes} attribute columns.").ToProblem();
            }

            var columns = new List<ImportColumnDto>(headers.Length);
            foreach (var (name, index) in attributeColumns)
            {
                if (!LookupAttributeRules.IsValidKey(name))
                {
                    return Invalid($"The column '{name}' is not a valid attribute key: lower-case letters, digits and '_', starting with a letter.").ToProblem();
                }

                columns.Add(new ImportColumnDto(name, ProposeType(rows, index).ToString(), IsAttribute: true));
            }

            foreach (var reserved in headers.Where(h => ReservedColumns.Contains(h, StringComparer.OrdinalIgnoreCase)))
            {
                columns.Add(new ImportColumnDto(reserved.ToLowerInvariant(), "Text", IsAttribute: false));
            }

            var preview = rows.Take(PreviewRows)
                .Select(row => headers.Select((name, index) => (name, value: row[index])).ToDictionary(c => c.name, c => c.value, StringComparer.Ordinal))
                .ToList();
            return TypedResults.Ok(new ImportPreviewDto(columns, preview, rows.Count));
        }
        catch (CsvHelperException)
        {
            return Invalid("The file is not a valid CSV.").ToProblem();
        }
    }

    /// <summary>A number when every cell is one, a date when every cell is one, otherwise text.</summary>
    private static LookupAttributeType ProposeType(IEnumerable<string[]> rows, int index)
    {
        var cells = rows.Select(row => row[index].Trim()).Where(cell => cell.Length > 0).ToList();
        if (cells.Count == 0)
        {
            return LookupAttributeType.Text;
        }

        if (cells.All(cell => double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return LookupAttributeType.Number;
        }

        return cells.All(cell => DateTimeOffset.TryParse(cell, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            ? LookupAttributeType.Date
            : LookupAttributeType.Text;
    }

    private static Error Invalid(string description) => Error.Validation("lookup.invalid_csv", description);
}

/// <summary>A column of the file: an attribute with its proposed type, or one of the reserved columns.</summary>
internal sealed record ImportColumnDto(string Name, string Type, bool IsAttribute);

/// <summary>The columns with their proposed types, the first rows and how many rows the file has.</summary>
internal sealed record ImportPreviewDto(IReadOnlyList<ImportColumnDto> Columns, IReadOnlyList<Dictionary<string, string>> Rows, int RowCount);
