namespace HwScope.App.Theming;

public sealed record ThemeLoadResult(
    ThemeDefinition Theme,
    bool UsedFallback,
    string? Message);
