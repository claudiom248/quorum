using System.Text;

namespace Quorum.Modules.Conversations.Extraction;

/// <summary>Reads a CSV (RFC 4180 quoting, comma, semicolon or tab separated) and writes it as a compact Markdown table.</summary>
internal static class CsvTable
{
    private static readonly char[] Delimiters = [',', ';', '\t'];

    public static string ToMarkdown(string csv, CancellationToken cancellationToken) =>
        Table(Parse(csv, DetectDelimiter(csv), cancellationToken));

    /// <summary>Rows of cells as a compact Markdown table whose first row is the header; short rows are padded.</summary>
    public static string Table(IReadOnlyList<List<string>> rows)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var columns = rows.Max(row => row.Count);
        var table = new StringBuilder();

        for (var r = 0; r < rows.Count; r++)
        {
            table.Append('|');
            for (var c = 0; c < columns; c++)
            {
                table.Append(' ').Append(Cell(c < rows[r].Count ? rows[r][c] : string.Empty)).Append(" |");
            }

            if (r == 0)
            {
                table.Append("\n|").Append(string.Concat(Enumerable.Repeat(" --- |", columns)));
            }

            if (r < rows.Count - 1)
            {
                table.Append('\n');
            }
        }

        return table.ToString();
    }

    /// <summary>The most frequent candidate on the first line, outside quotes; a comma when there is none.</summary>
    private static char DetectDelimiter(string csv)
    {
        var counts = new int[Delimiters.Length];
        var inQuotes = false;

        foreach (var character in csv)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (character == '\n' && !inQuotes)
            {
                break;
            }
            else if (!inQuotes)
            {
                var index = Array.IndexOf(Delimiters, character);
                if (index >= 0)
                {
                    counts[index]++;
                }
            }
        }

        var best = Array.IndexOf(counts, counts.Max());
        return counts[best] == 0 ? ',' : Delimiters[best];
    }

    private static List<List<string>> Parse(string csv, char delimiter, CancellationToken cancellationToken)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var rowHadQuotes = false;

        void EndRow()
        {
            row.Add(cell.ToString());
            cell.Clear();

            // Blank lines are not rows; a row holding only an empty quoted cell ("") is data.
            if (row.Count > 1 || row[0].Length > 0 || rowHadQuotes)
            {
                rows.Add(row);
            }

            row = [];
            rowHadQuotes = false;
            cancellationToken.ThrowIfCancellationRequested();
        }

        for (var i = 0; i < csv.Length; i++)
        {
            var character = csv[i];

            if (inQuotes)
            {
                if (character == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (character == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    cell.Append(character);
                }
            }
            else if (character == '"' && cell.Length == 0)
            {
                inQuotes = true;
                rowHadQuotes = true;
            }
            else if (character == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (character == '\n')
            {
                EndRow();
            }
            else
            {
                cell.Append(character);
            }
        }

        if (cell.Length > 0 || row.Count > 0 || rowHadQuotes)
        {
            EndRow();
        }

        return rows;
    }

    /// <summary>One line of plain text that cannot break the table.</summary>
    private static string Cell(string value) =>
        value.Replace("\n", " ", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);
}
