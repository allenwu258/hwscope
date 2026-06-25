using System.Windows;
using System.Windows.Media;
using HwScope.App.Configuration;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace HwScope.App.Theming;

public sealed class ThemeService
{
    private readonly JsonSettingsStore _settingsStore;
    private readonly ThemeDefinitionStore _themeDefinitionStore;
    private readonly ThemeResourceBuilder _resourceBuilder;
    private AppSettings _settings;
    private readonly List<FluentWindow> _attachedWindows = [];
    private string? _lastStatusMessage;

    public event EventHandler<string>? StatusChanged;

    public ThemeService(
        JsonSettingsStore settingsStore,
        ThemeDefinitionStore themeDefinitionStore,
        ThemeResourceBuilder resourceBuilder)
    {
        _settingsStore = settingsStore;
        _themeDefinitionStore = themeDefinitionStore;
        _resourceBuilder = resourceBuilder;
        _settings = _settingsStore.Load();

        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
    }

    public ThemeSettings ThemeSettings => _settings.Theme;

    public WindowSettings WindowSettings => _settings.Window;

    public ThemeMode EffectiveThemeMode => ResolveEffectiveThemeMode(_settings.Theme.Mode);

    public WindowBackdropType CurrentBackdropType => ToWindowBackdropType(_settings.Theme.Backdrop);

    public string? LastStatusMessage => _lastStatusMessage;

    public void ApplyCurrentTheme()
    {
        Apply(watchSystemTheme: false);
    }

    public void Attach(FluentWindow window)
    {
        if (!_attachedWindows.Contains(window))
        {
            _attachedWindows.Add(window);
            window.Loaded += AttachedWindow_Loaded;
            window.Closed += AttachedWindow_Closed;
        }

        window.WindowBackdropType = CurrentBackdropType;
        Apply(watchSystemTheme: _settings.Theme.Mode == ThemeMode.System);
    }

    public void Detach(FluentWindow window)
    {
        if (_attachedWindows.Remove(window))
        {
            TryUnWatch(window);
            window.Loaded -= AttachedWindow_Loaded;
            window.Closed -= AttachedWindow_Closed;
        }
    }

    public void SetThemeMode(ThemeMode mode)
    {
        _settings.Theme.Mode = mode;
        Apply(watchSystemTheme: mode == ThemeMode.System);
        Save();
    }

    public void SetBackdrop(BackdropMode backdrop)
    {
        _settings.Theme.Backdrop = backdrop;

        foreach (var window in _attachedWindows)
        {
            window.WindowBackdropType = CurrentBackdropType;
        }

        ApplicationThemeManager.Apply(ToApplicationTheme(EffectiveThemeMode), CurrentBackdropType, true);

        if (_settings.Theme.Mode == ThemeMode.System)
        {
            WatchAttachedWindows();
        }

        Save();
    }

    public void SetShowStatusBar(bool showStatusBar)
    {
        _settings.Window.ShowStatusBar = showStatusBar;
        Save();
    }

    public void Save()
    {
        _settingsStore.Save(_settings);
    }

    private void Apply(bool watchSystemTheme)
    {
        var effectiveMode = EffectiveThemeMode;
        ApplyHwScopeThemeResources(effectiveMode);
        ApplicationThemeManager.Apply(ToApplicationTheme(effectiveMode), CurrentBackdropType, true);

        if (watchSystemTheme)
        {
            WatchAttachedWindows();
        }
        else
        {
            UnWatchAttachedWindows();
        }
    }

    private void WatchAttachedWindows()
    {
        foreach (var window in _attachedWindows)
        {
            if (!window.IsLoaded)
            {
                continue;
            }

            SystemThemeWatcher.Watch(window, CurrentBackdropType, true);
        }
    }

    private void UnWatchAttachedWindows()
    {
        foreach (var window in _attachedWindows)
        {
            TryUnWatch(window);
        }
    }

    private void AttachedWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FluentWindow window)
        {
            return;
        }

        window.WindowBackdropType = CurrentBackdropType;

        if (_settings.Theme.Mode == ThemeMode.System)
        {
            TryWatch(window);
        }
    }

    private void AttachedWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is FluentWindow window)
        {
            Detach(window);
        }
    }

    private void TryWatch(FluentWindow window)
    {
        if (!CanUseSystemThemeWatcher(window))
        {
            return;
        }

        SystemThemeWatcher.Watch(window, CurrentBackdropType, true);
    }

    private void TryUnWatch(FluentWindow window)
    {
        if (!CanUseSystemThemeWatcher(window))
        {
            return;
        }

        SystemThemeWatcher.UnWatch(window);
    }

    private static bool CanUseSystemThemeWatcher(FluentWindow window)
    {
        return window.IsLoaded && PresentationSource.FromVisual(window) is not null;
    }

    private void OnApplicationThemeChanged(ApplicationTheme currentApplicationTheme, Color _)
    {
        if (_settings.Theme.Mode != ThemeMode.System)
        {
            return;
        }

        var mode = currentApplicationTheme == ApplicationTheme.Dark ? ThemeMode.Dark : ThemeMode.Light;
        ApplyHwScopeThemeResources(mode);
    }

    private void ApplyHwScopeThemeResources(ThemeMode mode)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var currentTheme = dictionaries.FirstOrDefault(IsHwScopeThemeDictionary);

        if (currentTheme is not null)
        {
            dictionaries.Remove(currentTheme);
        }

        var loadResult = _themeDefinitionStore.Load(mode);
        dictionaries.Add(_resourceBuilder.Build(loadResult.Theme));

        if (loadResult.UsedFallback && !string.IsNullOrWhiteSpace(loadResult.Message))
        {
            _lastStatusMessage = loadResult.Message;
            StatusChanged?.Invoke(this, loadResult.Message);
        }
    }

    private static bool IsHwScopeThemeDictionary(ResourceDictionary dictionary)
    {
        return dictionary.Contains(ThemeResourceBuilder.DictionaryMarkerKey);
    }

    private static ThemeMode ResolveEffectiveThemeMode(ThemeMode mode)
    {
        if (mode != ThemeMode.System)
        {
            return mode;
        }

        return ApplicationThemeManager.GetSystemTheme() switch
        {
            SystemTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.Light
        };
    }

    private static ApplicationTheme ToApplicationTheme(ThemeMode mode)
    {
        return mode == ThemeMode.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
    }

    private static WindowBackdropType ToWindowBackdropType(BackdropMode backdrop)
    {
        return backdrop == BackdropMode.Mica ? WindowBackdropType.Mica : WindowBackdropType.None;
    }
}
