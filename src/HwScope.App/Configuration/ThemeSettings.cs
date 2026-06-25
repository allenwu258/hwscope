using HwScope.App.Theming;

namespace HwScope.App.Configuration;

public sealed class ThemeSettings
{
    public ThemeMode Mode { get; set; } = ThemeMode.System;

    public BackdropMode Backdrop { get; set; } = BackdropMode.Mica;

    public string Accent { get; set; } = "Default";

    public string Density { get; set; } = "Default";

    public string? CustomThemeId { get; set; }
}
