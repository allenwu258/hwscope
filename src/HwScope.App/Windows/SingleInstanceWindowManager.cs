using System.Windows;

namespace HwScope.App.Windows;

public sealed class SingleInstanceWindowManager
{
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public bool TryActivate(string key)
    {
        if (!_windows.TryGetValue(key, out var existing))
        {
            return false;
        }

        Activate(existing);
        return true;
    }

    public T ShowOrActivate<T>(string key, Func<T> factory, Window? owner = null)
        where T : Window
    {
        if (_windows.TryGetValue(key, out var existing))
        {
            Activate(existing);
            return (T)existing;
        }

        var window = factory();
        if (owner is not null && window.Owner is null && !ReferenceEquals(window, owner))
        {
            window.Owner = owner;
        }

        _windows[key] = window;
        window.Closed += (_, _) => Remove(key, window);
        window.Show();
        Activate(window);

        return window;
    }

    private void Remove(string key, Window window)
    {
        if (_windows.TryGetValue(key, out var existing) && ReferenceEquals(existing, window))
        {
            _windows.Remove(key);
        }
    }

    private static void Activate(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Focus();
    }
}
