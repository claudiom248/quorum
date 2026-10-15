namespace Quorum.Web.Client;

/// <summary>The light/dark choice, shared by the theme provider and the toggle in the app bar.</summary>
public sealed class ThemeState
{
    private bool _isDark;

    public event Action? Changed;

    public bool IsDark
    {
        get => _isDark;
        set
        {
            if (_isDark == value)
            {
                return;
            }

            _isDark = value;
            Changed?.Invoke();
        }
    }
}
