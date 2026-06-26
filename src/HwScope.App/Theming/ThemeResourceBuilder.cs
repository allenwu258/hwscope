using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HwScope.App.Theming;

public sealed class ThemeResourceBuilder
{
    public const string DictionaryMarkerKey = "HwScopeThemeDictionaryMarker";

    public ResourceDictionary Build(ThemeDefinition theme)
    {
        var dictionary = new ResourceDictionary
        {
            [DictionaryMarkerKey] = true
        };

        foreach (var (key, value) in theme.Tokens)
        {
            var color = ParseColor(value);
            dictionary[key] = color;

            if (key.EndsWith("Color", StringComparison.OrdinalIgnoreCase))
            {
                dictionary[$"{key[..^"Color".Length]}Brush"] = new SolidColorBrush(color);
            }
        }

        return dictionary;
    }

    private static Color ParseColor(string value)
    {
        if (ColorConverter.ConvertFromString(value) is Color color)
        {
            return color;
        }

        throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Invalid theme color value: {value}"));
    }
}
