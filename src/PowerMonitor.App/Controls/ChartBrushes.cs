using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace PowerMonitor.App.Controls;

/// <summary>Chart colors from App.xaml's theme dictionaries for an element's actual theme.</summary>
public static class ChartBrushes
{
    public static Brush Get(string key, ElementTheme theme)
    {
        var dicts = Application.Current.Resources.ThemeDictionaries;
        if (dicts.TryGetValue(theme == ElementTheme.Dark ? "Dark" : "Light", out var d) &&
            ((ResourceDictionary)d).TryGetValue(key, out var v))
            return (Brush)v;
        return (Brush)Application.Current.Resources[key];
    }
}
