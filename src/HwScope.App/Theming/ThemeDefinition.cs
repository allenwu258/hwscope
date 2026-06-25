namespace HwScope.App.Theming;

public sealed class ThemeDefinition
{
    public string Id { get; set; } = "light";

    public string DisplayName { get; set; } = "Light";

    public ThemeMode Base { get; set; } = ThemeMode.Light;

    public Dictionary<string, string> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
