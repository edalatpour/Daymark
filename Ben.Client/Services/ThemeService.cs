using Microsoft.Maui.Controls;
using Ben.Resources.Themes;

namespace Ben.Services;

public class ThemeService
{
    private const string SelectedThemePreferenceKey = "SelectedTheme";

    private static readonly HashSet<string> ThemeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Red",
        "Orange",
        "Yellow",
        "Green",
        "Blue",
        "Purple",
        "Brown",
        "Gray",
        "Currituck",
        "Hatteras",
        "Ocracoke",
        "Bodie",
        "CapeLookout",
        "Assateague",
        "HeadHarbour",
        "Herring",
        "Hocq",
        "Keskiniemi",
        "Kingswear",
        "LaMarina",
        "Laitakari",
        "StMartins",
        "Trinity",
        "TybeeIsland",
        "Ystad",
    };

    private string _currentTheme;
    private ResourceDictionary? _currentThemeDict;
    private readonly IThemeIdentityService _themeIdentityService;

    public string CurrentTheme => _currentTheme;

    public event EventHandler<string>? ThemeChanged;

    public ThemeService(IThemeIdentityService themeIdentityService)
    {
        _themeIdentityService = themeIdentityService;
        _currentTheme = Preferences.Get(SelectedThemePreferenceKey, "Hatteras");
        System.Diagnostics.Debug.WriteLine($"ThemeService initialized with theme: {_currentTheme}");
    }

    /// <summary>
    /// Initializes the theme service by loading the saved theme on app startup.
    /// Call this from App.xaml.cs after the app is initialized.
    /// </summary>
    public void InitializeTheme()
    {
        SetTheme(_currentTheme, skipPrefsSave: true);
    }

    public void SetTheme(string themeName, bool skipPrefsSave = false)
    {
        try
        {
            var normalizedThemeName = NormalizeThemeName(themeName);

            if (_currentThemeDict != null && string.Equals(_currentTheme, normalizedThemeName, StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"Theme '{normalizedThemeName}' already active. Skipping reapply.");
                return;
            }

            ResourceDictionary newThemeDict = CreateThemeDictionary(normalizedThemeName);

            var appResources = Application.Current?.Resources;
            if (appResources == null)
            {
                System.Diagnostics.Debug.WriteLine("Application.Current.Resources is null!");
                return;
            }

            var mergedDicts = appResources.MergedDictionaries;

            var existingThemeDictionaries = mergedDicts
                .Where(IsThemeDictionary)
                .ToList();

            foreach (var existingThemeDictionary in existingThemeDictionaries)
            {
                System.Diagnostics.Debug.WriteLine($"Removing previous theme: {existingThemeDictionary.GetType().Name}");
                mergedDicts.Remove(existingThemeDictionary);
            }

            mergedDicts.Add(newThemeDict);
            _currentThemeDict = newThemeDict;

            _currentTheme = normalizedThemeName;

            if (!skipPrefsSave)
            {
                Preferences.Set(SelectedThemePreferenceKey, normalizedThemeName);
            }

            System.Diagnostics.Debug.WriteLine($"Theme dict has {newThemeDict.Count} resources");

            var hasInk = newThemeDict.ContainsKey("Ink");
            var hasLine = newThemeDict.ContainsKey("Line");
            System.Diagnostics.Debug.WriteLine($"Theme dict has Ink: {hasInk}, has Line: {hasLine}");

            var canGetInk = Application.Current?.Resources.TryGetValue("Ink", out var inkColor) == true;
            System.Diagnostics.Debug.WriteLine($"Can get Ink from app resources: {canGetInk}");

            System.Diagnostics.Debug.WriteLine($"Theme changed to: {normalizedThemeName}");
            ThemeChanged?.Invoke(this, normalizedThemeName);

            _themeIdentityService.ApplyThemeIdentity(normalizedThemeName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error setting theme to {themeName}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    public Color? GetThemeColor(string themeName, string resourceKey)
    {
        var resource = GetThemeResource(themeName, resourceKey);
        if (resource is Color color)
        {
            return color;
        }

        return null;
    }

    public object? GetThemeResource(string themeName, string resourceKey)
    {
        var dictionary = CreateThemeDictionary(NormalizeThemeName(themeName));
        if (dictionary.TryGetValue(resourceKey, out var resource))
        {
            return resource;
        }

        return null;
    }

    private static string NormalizeThemeName(string themeName)
    {
        return ThemeNames.Contains(themeName) ? themeName : "Hatteras";
    }

    private static ResourceDictionary CreateThemeDictionary(string themeName)
    {
        return themeName switch
        {
            "Red" => new Red(),
            "Orange" => new Orange(),
            "Yellow" => new Yellow(),
            "Green" => new Green(),
            "Blue" => new Blue(),
            "Purple" => new Purple(),
            "Gray" => new Gray(),
            "Brown" => new Brown(),
            "Currituck" => new Currituck(),
            "Hatteras" => new Hatteras(),
            "Ocracoke" => new Ocracoke(),
            "Bodie" => new Bodie(),
            "CapeLookout" => new CapeLookout(),
            "Assateague" => new Assateague(),
            "HeadHarbour" => new HeadHarbour(),
            "Herring" => new Herring(),
            "Hocq" => new Hocq(),
            "Keskiniemi" => new Keskiniemi(),
            "Kingswear" => new Kingswear(),
            "LaMarina" => new LaMarina(),
            "Laitakari" => new Laitakari(),
            "StMartins" => new StMartins(),
            "Trinity" => new Trinity(),
            "TybeeIsland" => new TybeeIsland(),
            "Ystad" => new Ystad(),
            _ => new Hatteras()
        };
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        return dictionary.GetType().Namespace == "Ben.Resources.Themes"
            && ThemeNames.Contains(dictionary.GetType().Name);
    }
}
