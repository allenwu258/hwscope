using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HwScope.App.Theming;

public sealed class ThemeDefinitionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _themeDirectory;

    public ThemeDefinitionStore()
        : this(Path.Combine(AppContext.BaseDirectory, "Themes", "Json"))
    {
    }

    public ThemeDefinitionStore(string themeDirectory)
    {
        _themeDirectory = themeDirectory;
    }

    public ThemeDefinition Load(ThemeMode mode)
    {
        var id = mode == ThemeMode.Dark ? "dark" : "light";
        var path = Path.Combine(_themeDirectory, $"{id}.json");

        if (!File.Exists(path))
        {
            return CreateFallback(id);
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ThemeDefinition>(stream, SerializerOptions) ?? CreateFallback(id);
        }
        catch (JsonException)
        {
            return CreateFallback(id);
        }
        catch (IOException)
        {
            return CreateFallback(id);
        }
    }

    private static ThemeDefinition CreateFallback(string id)
    {
        return id == "dark"
            ? new ThemeDefinition
            {
                Id = "dark",
                DisplayName = "Dark",
                Base = ThemeMode.Dark,
                Tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["HwScopePanelColor"] = "#202020",
                    ["HwScopeContentColor"] = "#161616",
                    ["HwScopeCardColor"] = "#242424",
                    ["HwScopeLineColor"] = "#3A3A3A",
                    ["HwScopeTextColor"] = "#E7E7E7",
                    ["HwScopeStrongTextColor"] = "#DADADA",
                    ["HwScopeMutedTextColor"] = "#B8B8B8",
                    ["HwScopeIconColor"] = "#D6D6D6",
                    ["HwScopeIconBackplateColor"] = "#303030",
                    ["HwScopeActiveViewColor"] = "#333333"
                }
            }
            : new ThemeDefinition
            {
                Id = "light",
                DisplayName = "Light",
                Base = ThemeMode.Light,
                Tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["HwScopePanelColor"] = "#F5F5F5",
                    ["HwScopeContentColor"] = "#F5F5F5",
                    ["HwScopeCardColor"] = "#FFFFFFFF",
                    ["HwScopeLineColor"] = "#E5E5E5",
                    ["HwScopeTextColor"] = "#2F2F2F",
                    ["HwScopeStrongTextColor"] = "#4B5563",
                    ["HwScopeMutedTextColor"] = "#626262",
                    ["HwScopeIconColor"] = "#3F4752",
                    ["HwScopeIconBackplateColor"] = "#F3F4F6",
                    ["HwScopeActiveViewColor"] = "#EDEDED"
                }
            };
    }
}
