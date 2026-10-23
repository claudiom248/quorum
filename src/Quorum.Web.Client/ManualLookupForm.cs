using System.Text;
using System.Text.RegularExpressions;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>
/// What the admin types to define a manual lookup (spec 007 D-5), kept apart from the markup so it can be tested alone. The values are
/// one text, a line each: <c>value | label | synonym; synonym</c>. A pasted list and an imported CSV both end up there, so the admin
/// sees and can fix what will be sent.
/// </summary>
public sealed partial class ManualLookupForm
{
    /// <summary>The id of the lookup being edited; <see langword="null"/> for a new one.</summary>
    public Guid? Id { get; init; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary><c>Global</c> or <c>Tenant</c>.</summary>
    public string Scope { get; set; } = ScopeKinds.Tenant;

    /// <summary>Tenants the lookup is shared with besides this one, as text (scope <c>Tenant</c>).</summary>
    public string TenantsText { get; set; } = string.Empty;

    public string ValuesText { get; set; } = string.Empty;

    public bool IsNew => Id is null;

    public static ManualLookupForm From(ManualLookupInfo info, IEnumerable<LookupValueInfo> values)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(values);

        return new ManualLookupForm
        {
            Id = info.Id,
            Name = info.Name,
            Description = info.Description ?? string.Empty,
            Scope = info.Scope,
            TenantsText = string.Join(Environment.NewLine, info.ScopeTenantIds ?? []),
            ValuesText = string.Join(Environment.NewLine, values.Select(Line)),
        };
    }

    /// <summary>The values typed, with what is wrong with them. A blank line is ignored.</summary>
    public (IReadOnlyList<ManualValueRequest> Values, IReadOnlyList<string> Problems) ParseValues()
    {
        var values = new List<ManualValueRequest>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var number = 0;
        foreach (var raw in ValuesText.ReplaceLineEndings("\n").Split('\n'))
        {
            number++;
            if (raw.Trim().Length == 0)
            {
                continue;
            }

            var parts = raw.Split('|', 3).Select(p => p.Trim()).ToArray();
            var value = parts[0];
            var label = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
            var synonyms = parts.Length > 2
                ? parts[2].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : [];

            if (value.Length == 0)
            {
                problems.Add($"Line {number}: the value is empty.");
            }
            else if (value.Length > LookupLimits.MaxValueLength || label?.Length > LookupLimits.MaxValueLength || synonyms.Any(s => s.Length > LookupLimits.MaxValueLength))
            {
                problems.Add($"Line {number}: a value, a label and a synonym can have up to {LookupLimits.MaxValueLength} characters.");
            }
            else if (!seen.Add(value))
            {
                problems.Add($"Line {number}: \"{value}\" is listed twice, whatever its case.");
            }

            values.Add(new ManualValueRequest(value, label, synonyms.Count == 0 ? null : synonyms));
        }

        if (values.Count > LookupLimits.MaxManualValues)
        {
            problems.Add($"A manual lookup has at most {LookupLimits.MaxManualValues} values: this one has {values.Count}.");
        }

        return (values, problems);
    }

    /// <summary>Everything that blocks saving; the server checks the same limits again.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!ValidName().IsMatch(Name.Trim()))
        {
            problems.Add($"The name is 1 to {LookupLimits.MaxNameLength} characters: lower-case letters, digits and \"_\", starting with a letter. The model sees it.");
        }

        if (Description.Length > LookupLimits.MaxDescriptionLength)
        {
            problems.Add($"The description can have up to {LookupLimits.MaxDescriptionLength} characters.");
        }

        if (Scope == ScopeKinds.Tenant && ToolAdminView.ParseIds(TenantsText).Error is { } tenants)
        {
            problems.Add(tenants);
        }

        problems.AddRange(ParseValues().Problems);
        return problems;
    }

    /// <summary>The request to send. Call it when <see cref="Problems"/> is empty.</summary>
    public ManualLookupRequest ToRequest()
    {
        var tenants = Scope == ScopeKinds.Tenant ? ToolAdminView.ParseIds(TenantsText).Ids : [];
        return new ManualLookupRequest(
            Name.Trim(),
            Description.Trim().Length == 0 ? null : Description.Trim(),
            Scope,
            tenants,
            ParseValues().Values);
    }

    /// <summary>
    /// Adds the rows of a CSV to the values: the first column is the value, the second the label and the third the synonyms, separated
    /// by ";" or "|". A first row that reads "value" is a header and is skipped. Returns how many values were added.
    /// </summary>
    public int ImportCsv(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var lines = new List<string>();
        foreach (var row in ReadCsv(csv))
        {
            if (lines.Count == 0 && row.Count > 0 && string.Equals(row[0].Trim(), "value", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (row.All(c => c.Trim().Length == 0))
            {
                continue;
            }

            var synonyms = row.Count > 2 ? string.Join("; ", row[2].Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : string.Empty;
            var label = row.Count > 1 ? row[1].Trim() : string.Empty;
            lines.Add(synonyms.Length > 0 ? $"{row[0].Trim()} | {label} | {synonyms}" : label.Length > 0 ? $"{row[0].Trim()} | {label}" : row[0].Trim());
        }

        if (lines.Count > 0)
        {
            ValuesText = ValuesText.Trim().Length == 0
                ? string.Join(Environment.NewLine, lines)
                : ValuesText.TrimEnd() + Environment.NewLine + string.Join(Environment.NewLine, lines);
        }

        return lines.Count;
    }

    private static string Line(LookupValueInfo value)
    {
        var synonyms = value.Synonyms is { Count: > 0 } list ? string.Join("; ", list) : string.Empty;
        var label = value.Label ?? string.Empty;
        return synonyms.Length > 0 ? $"{value.Value} | {label} | {synonyms}" : label.Length > 0 ? $"{value.Value} | {label}" : value.Value;
    }

    // RFC 4180 enough for a list of values: quoted cells with doubled quotes and line breaks inside; comma or semicolon between cells.
    private static List<List<string>> ReadCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var header = text.Split('\n', 2)[0];
        var separator = header.Contains(';', StringComparison.Ordinal) && !header.Contains(',', StringComparison.Ordinal) ? ';' : ',';

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();
}
