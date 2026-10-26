using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>The form of an MCP server: what the admin typed, whether it differs from what was loaded, and the request it makes.</summary>
public sealed class ServerForm
{
    private string _initial = string.Empty;

    public Guid? Id { get; private init; }

    public string OriginalName { get; private init; } = string.Empty;

    public string OriginalUrl { get; private init; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string HeadersText { get; set; } = string.Empty;

    /// <summary>The server has headers stored; their values are never sent back, so the form can only keep, replace or remove them.</summary>
    public bool HadHeaders { get; private init; }

    public bool RemoveHeaders { get; set; }

    public ScopeForm Scope { get; private init; } = new();

    /// <summary>The form has been changed since it was opened: leaving it asks first.</summary>
    public bool IsDirty => !string.Equals(_initial, Snapshot(), StringComparison.Ordinal);

    public static ServerForm New() => Opened(new ServerForm());

    public static ServerForm From(ToolServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        return Opened(new ServerForm
        {
            Id = server.Id,
            OriginalName = server.Name,
            OriginalUrl = server.Url,
            Name = server.Name,
            Url = server.Url,
            HadHeaders = server.HasHeaders,
            Scope = ScopeForm.From(server.Scope),
        });
    }

    /// <summary>The headers typed, for the connection test: it never uses the stored ones.</summary>
    public (Dictionary<string, string>? Headers, string? Error) ParseHeaders() => ToolAdminView.ParseHeaders(HeadersText);

    /// <summary>The request to send, or what is wrong with what was typed. The server checks everything again.</summary>
    public (ToolServerRequest? Request, string? Error) Build()
    {
        var (headers, headersError) = ParseHeaders();
        var (scope, scopeError) = Scope.Build();
        if ((headersError ?? scopeError) is { } error)
        {
            return (null, error);
        }

        if (RemoveHeaders && headers is not null)
        {
            return (null, "Either remove the stored headers or type new ones to replace them, not both.");
        }

        // Omitted headers keep the stored ones; an empty object removes them.
        IReadOnlyDictionary<string, string>? sent = headers;
        if (Id is not null && RemoveHeaders && headers is null)
        {
            sent = new Dictionary<string, string>();
        }

        return (new ToolServerRequest(Name.Trim(), Url.Trim(), sent, scope!), null);
    }

    private static ServerForm Opened(ServerForm form)
    {
        form._initial = form.Snapshot();
        return form;
    }

    private string Snapshot() => string.Join(
        '\u001f',
        Name,
        Url,
        HeadersText,
        RemoveHeaders,
        Scope.Kind,
        Scope.TenantsText,
        string.Join(',', Scope.AssistantIds.Order()));
}
