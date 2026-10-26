using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>
/// What every list page of the admin shares (spec 010): the filters travel in the URL, the rows come a page at a time from the
/// server, and the drawer that edits a row is a sub-route (<c>/tools/mcp/{id}</c>) so back, reload and links all work. A page
/// owns one of these and renders from it; the page itself stays on its two routes, so the rows and the filters survive the
/// drawer opening and closing.
/// </summary>
public sealed class ListScreen<TItem> : IDisposable
{
    /// <summary>The number of rows each request asks for.</summary>
    public const int PageSize = 50;

    /// <summary>The last path segment of the route that opens the drawer on an empty form.</summary>
    public const string NewId = "new";

    private readonly NavigationManager _navigation;
    private readonly Func<ListQuery, string?, Task<ApiResult<Page<TItem>>>> _load;
    private readonly string _basePath;
    private readonly Action _changed;
    private int _generation;

    /// <param name="basePath">The route of the list without a trailing slash, for example <c>/tools/mcp</c>.</param>
    /// <param name="load">Reads one page for a query and a cursor (the cursor is <see langword="null"/> for the first page).</param>
    /// <param name="changed">Asked to render again.</param>
    public ListScreen(NavigationManager navigation, string basePath, Func<ListQuery, string?, Task<ApiResult<Page<TItem>>>> load, Action changed)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(changed);

        _navigation = navigation;
        _basePath = basePath.TrimEnd('/');
        _load = load;
        _changed = changed;
        Query = ListQuery.Parse(navigation.Uri);
        navigation.LocationChanged += OnLocationChanged;
    }

    public ListQuery Query { get; private set; }

    public List<TItem> Items { get; } = [];

    /// <summary>Set while there is a page after the rows on screen.</summary>
    public string? Cursor { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>The first page has been read (or failed): an empty list is then a fact and not a wait.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Why the list could not be read; the rows already on screen stay when a later page fails.</summary>
    public ApiProblem? Problem { get; private set; }

    public bool IsEmpty => Loaded && Problem is null && Items.Count == 0;

    /// <summary>Reads the first page again for the current filters.</summary>
    public async Task ReloadAsync()
    {
        var generation = ++_generation;
        Loading = true;
        Problem = null;
        _changed();

        var result = await LoadAsync(null);
        if (generation != _generation)
        {
            return;
        }

        Items.Clear();
        Cursor = null;
        if (result.IsSuccess)
        {
            Items.AddRange(result.Value.Items);
            Cursor = result.Value.Cursor;
        }
        else
        {
            Problem = result.Problem;
        }

        Loading = false;
        Loaded = true;
        _changed();
    }

    /// <summary>Reads the page after the rows on screen and adds it to them.</summary>
    public async Task LoadMoreAsync()
    {
        if (Cursor is null || Loading)
        {
            return;
        }

        var generation = _generation;
        Loading = true;
        _changed();

        var result = await LoadAsync(Cursor);
        if (generation != _generation)
        {
            return;
        }

        if (result.IsSuccess)
        {
            Items.AddRange(result.Value.Items);
            Cursor = result.Value.Cursor;
            Problem = null;
        }
        else
        {
            Problem = result.Problem;
        }

        Loading = false;
        _changed();
    }

    /// <summary>
    /// Applies new filters: the address changes in place (so Back does not step through every keystroke) and the first page is read.
    /// </summary>
    public Task ChangeQueryAsync(ListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        Query = query;
        _navigation.NavigateTo(query.ApplyTo(_navigation.Uri), replace: true);
        return ReloadAsync();
    }

    /// <summary>The address of the drawer for a row (<paramref name="id"/>) or an empty form (<see cref="NewId"/>), keeping the filters.</summary>
    public string DrawerUri(string id) => $"{_basePath}/{Uri.EscapeDataString(id)}{Query.ToQueryString()}";

    /// <summary>The address of the list itself, keeping the filters.</summary>
    public string ListUri => $"{_basePath}{Query.ToQueryString()}";

    public void OpenDrawer(string id) => _navigation.NavigateTo(DrawerUri(id));

    public void CloseDrawer() => _navigation.NavigateTo(ListUri);

    // An exception the client does not map (a truncated body, a timeout) must end the wait like any other failure.
    private async Task<ApiResult<Page<TItem>>> LoadAsync(string? cursor)
    {
        try
        {
            return await _load(Query, cursor);
        }
        catch (Exception exception) when (exception is JsonException or TaskCanceledException or InvalidOperationException)
        {
            return ApiResult.Fail<Page<TItem>>(ApiProblem.Unreachable);
        }
    }

    // Back and Forward can change the filters without the page asking: the rows follow the address.
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var query = ListQuery.Parse(e.Location);
        if (query.ToQueryString() == Query.ToQueryString())
        {
            return;
        }

        Query = query;
        _ = ReloadAsync();
    }

    public void Dispose() => _navigation.LocationChanged -= OnLocationChanged;
}
