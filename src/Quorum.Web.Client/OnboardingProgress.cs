using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>
/// The getting-started checklist as this browser knows it, read once. Pages report that they were opened through
/// <see cref="MarkAsync"/>, which only writes while the step is still open, so a finished or dismissed checklist costs no request.
/// </summary>
public sealed class OnboardingProgress(QuorumApi api, Session session)
{
    private Task<Checklist?>? _loading;

    /// <summary>The checklist, or <see langword="null"/> for a user who is not an administrator or when it cannot be read.</summary>
    public Task<Checklist?> GetAsync() => _loading ??= LoadAsync();

    /// <summary>Records that the page of a step was opened (<c>prompt</c>, <c>tools</c> or <c>knowledge</c>); failing to record it is not the page's problem.</summary>
    public async Task MarkAsync(string key)
    {
        var checklist = await GetAsync();
        if (checklist is not { IsVisible: true } || checklist.Items.Any(i => i.Key == key && i.Done))
        {
            return;
        }

        var result = await api.CompleteChecklistStepAsync(key);
        if (result.IsSuccess)
        {
            var index = checklist.Items.FindIndex(i => i.Key == key);
            if (index >= 0)
            {
                checklist.Items[index] = checklist.Items[index] with { Done = true };
            }
        }
    }

    public async Task<bool> DismissAsync()
    {
        var result = await api.DismissChecklistAsync();
        if (result.IsSuccess && await GetAsync() is { } checklist)
        {
            _loading = Task.FromResult<Checklist?>(checklist with { Dismissed = true });
        }

        return result.IsSuccess;
    }

    private async Task<Checklist?> LoadAsync()
    {
        if (!AccessRules.IsAdmin(session.User?.Role))
        {
            return null;
        }

        try
        {
            var result = await api.GetChecklistAsync();
            return result.IsSuccess ? result.Value : null;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or TaskCanceledException)
        {
            return null;
        }
    }
}
