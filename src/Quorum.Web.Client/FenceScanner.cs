namespace Quorum.Web.Client;

/// <summary>
/// Tells whether a markdown source ends inside a fenced code block whose closing fence has not arrived yet (spec
/// 014, D-2). Markdig parses an unclosed fence into a code block like any other and does not record the difference,
/// so the source is scanned here: a fence opens with three or more backticks or tildes (indented at most three
/// spaces, optionally followed by an info string) and closes with a line of the same character, at least as long,
/// carrying nothing else. While an answer streams, the block that is still growing must not offer a Preview button.
/// </summary>
public static class FenceScanner
{
    public static bool LastFenceIsOpen(string markdown)
    {
        var fenceChar = '\0';
        var fenceLength = 0;
        var position = 0;

        while (position < markdown.Length)
        {
            var lineEnd = markdown.IndexOf('\n', position);
            var line = lineEnd < 0
                ? markdown.AsSpan()[position..]
                : markdown.AsSpan()[position..lineEnd];
            position = lineEnd < 0 ? markdown.Length : lineEnd + 1;

            if (line.EndsWith("\r"))
            {
                line = line[..^1];
            }

            var indent = 0;
            while (indent < line.Length && indent < 3 && line[indent] == ' ')
            {
                indent++;
            }

            var content = line[indent..];
            if (content.IsEmpty || (content[0] != '`' && content[0] != '~'))
            {
                continue;
            }

            var runChar = content[0];
            var run = 0;
            while (run < content.Length && content[run] == runChar)
            {
                run++;
            }

            if (run < 3)
            {
                continue;
            }

            if (fenceChar == '\0')
            {
                fenceChar = runChar;
                fenceLength = run;
                continue;
            }

            if (runChar == fenceChar && run >= fenceLength && content[run..].IsWhiteSpace())
            {
                fenceChar = '\0';
                fenceLength = 0;
            }
        }

        return fenceChar != '\0';
    }
}
