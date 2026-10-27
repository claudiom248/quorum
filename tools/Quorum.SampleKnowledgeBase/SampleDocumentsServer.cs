using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Quorum.SampleKnowledgeBase;

/// <summary>
/// A small web server for development that serves, as pages, every document the sample data point to (<see cref="SampleDocuments"/>):
/// <c>/{kind}/{id}</c> is an HTML page with what the data say about the document and the text that was embedded, <c>/{kind}/{id}.txt</c>
/// the same as plain text, and <c>/</c> lists them all. Nothing in the product uses it: it is what a citation in the chat opens.
/// </summary>
public static class SampleDocumentsServer
{
    public const int DefaultPort = 5290;

    /// <summary>Builds the server; the caller starts it. <paramref name="address"/> is where it listens, such as <c>http://127.0.0.1:5290</c>.</summary>
    public static WebApplication Build(string address)
    {
        var documents = SampleDocuments.Catalog().ToDictionary(document => document.Path, StringComparer.Ordinal);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(address);
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapGet("/", () => Results.Content(Index(documents.Values), "text/html; charset=utf-8"));
        app.MapGet(
            "/{kind}/{**id}",
            (string kind, string id) =>
            {
                var text = id.EndsWith(".txt", StringComparison.Ordinal);
                var key = $"/{kind}/{Uri.EscapeDataString(text ? id[..^4] : id)}";
                return documents.TryGetValue(key, out var document)
                    ? text
                        ? Results.Text(PlainText(document), "text/plain", Encoding.UTF8)
                        : Results.Content(Page(document), "text/html; charset=utf-8")
                    : Results.Content(NotFound(kind, id), "text/html; charset=utf-8", Encoding.UTF8, statusCode: 404);
            });
        return app;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private const string Style = """
        body { font: 16px/1.55 system-ui, sans-serif; margin: 0; background: #f6f7f9; color: #1d2330; }
        main { max-width: 52rem; margin: 0 auto; padding: 1.5rem 1rem 3rem; }
        nav a { color: #3a4ed8; text-decoration: none; }
        h1 { margin: 0.5rem 0 0.25rem; font-size: 1.6rem; }
        .kind { color: #5c6578; margin: 0 0 1rem; }
        table { border-collapse: collapse; margin: 1rem 0; background: #fff; width: 100%; }
        th, td { text-align: left; padding: 0.35rem 0.7rem; border: 1px solid #dde1ea; vertical-align: top; }
        th { width: 11rem; font-weight: 600; background: #eef0f5; }
        section { background: #fff; border: 1px solid #dde1ea; border-radius: 6px; padding: 0.2rem 1rem; margin: 0.75rem 0; }
        section h2 { font-size: 0.85rem; text-transform: uppercase; letter-spacing: 0.05em; color: #5c6578; margin-bottom: 0; }
        ul { padding-left: 1.2rem; }
        """;

    private static string Layout(string title, string body) =>
        $"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{Encode(title)}</title>
        <style>{Style}</style>
        </head>
        <body>
        <main>
        <nav><a href="/">All documents</a></nav>
        {body}
        </main>
        </body>
        </html>
        """;

    private static string Page(SampleDocument document)
    {
        var body = new StringBuilder();
        body.Append("<article><h1>").Append(Encode(document.Title)).Append("</h1><p class=\"kind\">").Append(Encode(document.Kind)).Append(" · ").Append(Encode(document.Id)).Append("</p>");
        if (document.Details.Count > 0)
        {
            body.Append("<table>");
            foreach (var (name, value) in document.Details)
            {
                body.Append("<tr><th>").Append(Encode(name)).Append("</th><td>").Append(Encode(value)).Append("</td></tr>");
            }

            body.Append("</table>");
        }

        for (var i = 0; i < document.Sections.Count; i++)
        {
            body.Append("<section><h2>Section ").Append(i + 1).Append("</h2><p>").Append(Encode(document.Sections[i])).Append("</p></section>");
        }

        body.Append("<p><a href=\"").Append(Encode(document.Path)).Append(".txt\">Plain text</a></p></article>");
        return Layout(document.Title, body.ToString());
    }

    private static string PlainText(SampleDocument document)
    {
        var text = new StringBuilder();
        text.AppendLine(document.Title).AppendLine(new string('=', Math.Max(document.Title.Length, 3))).AppendLine();
        foreach (var (name, value) in document.Details)
        {
            text.Append(name).Append(": ").AppendLine(value);
        }

        foreach (var section in document.Sections)
        {
            text.AppendLine().AppendLine(section);
        }

        return text.ToString();
    }

    private static string Index(IEnumerable<SampleDocument> documents)
    {
        var body = new StringBuilder("<h1>Sample documents</h1><p class=\"kind\">What the sample knowledge bases point to: every address in a citation opens here.</p>");
        foreach (var group in documents.GroupBy(document => document.Kind, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            body.Append("<h2>").Append(Encode(group.Key)).Append(" (").Append(group.Count()).Append(")</h2><ul>");
            foreach (var document in group.OrderBy(document => document.Title, StringComparer.Ordinal))
            {
                body.Append("<li><a href=\"").Append(Encode(document.Path)).Append("\">").Append(Encode(document.Title)).Append("</a> <small>").Append(Encode(document.Id)).Append("</small></li>");
            }

            body.Append("</ul>");
        }

        return Layout("Sample documents", body.ToString());
    }

    private static string NotFound(string kind, string id) =>
        Layout("Not found", $"<h1>Not found</h1><p>There is no sample document <code>{Encode(kind)}/{Encode(id)}</code>.</p>");
}
