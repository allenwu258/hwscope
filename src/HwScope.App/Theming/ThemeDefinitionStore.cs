using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

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

    public ThemeLoadResult Load(ThemeMode mode)
    {
        var id = mode == ThemeMode.Dark ? "dark" : "light";
        var path = Path.Combine(_themeDirectory, $"{id}.json");

        if (!File.Exists(path))
        {
            return CreateFallbackResult(id, $"主题文件缺失，已使用内置{id}回退主题：{path}");
        }

        try
        {
            using var stream = File.OpenRead(path);
            var theme = JsonSerializer.Deserialize<ThemeDefinition>(stream, SerializerOptions);
            var validationMessage = Validate(theme, id);

            return validationMessage is null
                ? new ThemeLoadResult(theme!, UsedFallback: false, Message: null)
                : CreateFallbackResult(id, validationMessage);
        }
        catch (JsonException exception)
        {
            return CreateFallbackResult(id, $"主题 JSON 格式无效，已使用内置{id}回退主题：{exception.Message}");
        }
        catch (IOException exception)
        {
            return CreateFallbackResult(id, $"主题文件读取失败，已使用内置{id}回退主题：{exception.Message}");
        }
    }

    private static string? Validate(ThemeDefinition? theme, string expectedId)
    {
        if (theme is null)
        {
            return $"主题 JSON 为空，已使用内置{expectedId}回退主题。";
        }

        if (string.IsNullOrWhiteSpace(theme.Id))
        {
            return $"主题 JSON 缺少 id，已使用内置{expectedId}回退主题。";
        }

        if (!string.Equals(theme.Id, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            return $"主题 JSON id 与文件不匹配，已使用内置{expectedId}回退主题。";
        }

        foreach (var token in CreateFallback(expectedId).Tokens.Keys)
        {
            if (!theme.Tokens.TryGetValue(token, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return $"主题 JSON 缺少 token {token}，已使用内置{expectedId}回退主题。";
            }

            if (!IsColorValue(value))
            {
                return $"主题 JSON token {token} 颜色值无效，已使用内置{expectedId}回退主题。";
            }
        }

        foreach (var (token, value) in theme.Tokens)
        {
            if (!IsColorValue(value))
            {
                return $"主题 JSON token {token} 颜色值无效，已使用内置{expectedId}回退主题。";
            }
        }

        return null;
    }

    private static bool IsColorValue(string value)
    {
        try
        {
            return ColorConverter.ConvertFromString(value) is Color;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static ThemeLoadResult CreateFallbackResult(string id, string message)
    {
        return new ThemeLoadResult(CreateFallback(id), UsedFallback: true, message);
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
                    ["HwScopeActiveViewColor"] = "#333333",
                    ["HwScopeStatusGoodColor"] = "#6CCB9F",
                    ["HwScopeStatusCautionColor"] = "#F0C36A",
                    ["HwScopeStatusCriticalColor"] = "#FF8A80",
                    ["HwScopeStatusUnknownColor"] = "#B8B8B8",
                    ["HwScopeStatusInfoColor"] = "#75B9F2"
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
                    ["HwScopeActiveViewColor"] = "#EDEDED",
                    ["HwScopeStatusGoodColor"] = "#18794E",
                    ["HwScopeStatusCautionColor"] = "#8A5A00",
                    ["HwScopeStatusCriticalColor"] = "#C42B1C",
                    ["HwScopeStatusUnknownColor"] = "#626262",
                    ["HwScopeStatusInfoColor"] = "#0067C0"
                }
            };
    }
}
