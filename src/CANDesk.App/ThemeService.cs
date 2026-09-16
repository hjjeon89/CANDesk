using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace CANDesk.App;

public enum AppTheme
{
    Light,
    Dark
}

public sealed class ThemeService
{
    private const string LightThemeSource = "Themes/Colors.Light.xaml";
    private const string DarkThemeSource = "Themes/Colors.Dark.xaml";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CANDesk",
        "settings.json");

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Light;

    public void ApplySavedTheme()
    {
        ApplyTheme(ReadSavedTheme());
    }

    public void ApplyTheme(AppTheme theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var source = theme == AppTheme.Dark ? DarkThemeSource : LightThemeSource;
        var replacement = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        var existingIndex = FindColorDictionaryIndex(dictionaries);
        if (existingIndex >= 0)
        {
            dictionaries[existingIndex] = replacement;
        }
        else
        {
            dictionaries.Insert(0, replacement);
        }

        CurrentTheme = theme;
        SaveTheme(theme);
    }

    private static int FindColorDictionaryIndex(Collection<ResourceDictionary> dictionaries)
    {
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is not null && source.Contains("Colors.", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static AppTheme ReadSavedTheme()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return AppTheme.Light;
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            return string.Equals(settings?.Theme, nameof(AppTheme.Dark), StringComparison.OrdinalIgnoreCase)
                ? AppTheme.Dark
                : AppTheme.Light;
        }
        catch (IOException)
        {
            return AppTheme.Light;
        }
        catch (JsonException)
        {
            return AppTheme.Light;
        }
    }

    private static void SaveTheme(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var settings = new AppSettings(theme.ToString());
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record AppSettings(string Theme);
}
