using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

public enum SessionState
{
    Unavailable,
    SetupRequired,
    SignedOut,
    SignedIn,
}

/// <summary>Where the installation and the browser stand: setup still to do, signed out or signed in.</summary>
public sealed class Session(QuorumApi api)
{
    private SessionState? _state;

    public SessionState State => _state ?? SessionState.Unavailable;

    public SetupStatus? SetupStatus { get; private set; }

    public CurrentUser? User { get; private set; }

    public ApiProblem? Problem { get; private set; }

    /// <summary>Loads the state once; later calls reuse it until <see cref="Reset"/>.</summary>
    public async Task<SessionState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_state is { } known && known != SessionState.Unavailable)
        {
            return known;
        }

        _state = await FetchAsync(cancellationToken);
        return _state.Value;
    }

    /// <summary>Forgets the state, e.g. after signing in or out, or when the server answers 401.</summary>
    public void Reset()
    {
        _state = null;
        SetupStatus = null;
        User = null;
        Problem = null;
    }

    private async Task<SessionState> FetchAsync(CancellationToken cancellationToken)
    {
        var status = await api.GetSetupStatusAsync(cancellationToken);
        if (!status.IsSuccess)
        {
            Problem = status.Problem;
            return SessionState.Unavailable;
        }

        SetupStatus = status.Value;
        if (status.Value.IsSetupMode)
        {
            return SessionState.SetupRequired;
        }

        var user = await api.GetCurrentUserAsync(cancellationToken);
        if (user.IsSuccess)
        {
            User = user.Value;
            return SessionState.SignedIn;
        }

        if (user.Problem!.IsUnauthorized)
        {
            return SessionState.SignedOut;
        }

        Problem = user.Problem;
        return SessionState.Unavailable;
    }
}
