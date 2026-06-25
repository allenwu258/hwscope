namespace HwScope.App.Configuration;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public ThemeSettings Theme { get; set; } = new();

    public WindowSettings Window { get; set; } = new();
}
