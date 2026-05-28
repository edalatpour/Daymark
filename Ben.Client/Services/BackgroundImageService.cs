namespace Ben.Services;

public class BackgroundImageService
{
    private const string UseCustomBackgroundImagePreferenceKey = "UseCustomBackgroundImage";
    private const string CustomBackgroundImagePathPreferenceKey = "CustomBackgroundImagePath";
    private readonly ThemeService _themeService;

    public bool UseCustomBackgroundImage => Preferences.Get(UseCustomBackgroundImagePreferenceKey, false);
    public string? CustomBackgroundImagePath => Preferences.Get(CustomBackgroundImagePathPreferenceKey, string.Empty);

    public BackgroundImageService(ThemeService themeService)
    {
        _themeService = themeService;
        _themeService.ThemeChanged += OnThemeChanged;
    }

    public void InitializeBackgroundImage()
    {
        ApplyBackgroundImageResourceOverride();
    }

    public void SetBackgroundImageOverride(bool useCustomBackgroundImage, string? customBackgroundImagePath)
    {
        var existingCustomBackgroundImagePath = Preferences.Get(CustomBackgroundImagePathPreferenceKey, string.Empty);

        if (useCustomBackgroundImage)
        {
            if (string.IsNullOrWhiteSpace(customBackgroundImagePath))
            {
                throw new ArgumentException("A custom background image path is required when custom background image is enabled.", nameof(customBackgroundImagePath));
            }

            Preferences.Set(UseCustomBackgroundImagePreferenceKey, true);
            Preferences.Set(CustomBackgroundImagePathPreferenceKey, customBackgroundImagePath);

            if (!string.IsNullOrWhiteSpace(existingCustomBackgroundImagePath)
                && !string.Equals(existingCustomBackgroundImagePath, customBackgroundImagePath, StringComparison.OrdinalIgnoreCase)
                && IsAppManagedBackgroundFile(existingCustomBackgroundImagePath))
            {
                try
                {
                    File.Delete(existingCustomBackgroundImagePath);
                }
                catch
                {
                    // Best effort cleanup only; file may still be in use.
                }
            }
        }
        else
        {
            Preferences.Set(UseCustomBackgroundImagePreferenceKey, false);
        }

        ApplyBackgroundImageResourceOverride();
    }

    public static async Task<string> PersistCustomBackgroundImageAsync(string sourcePath)
    {
        var appDataDirectory = Path.GetFullPath(FileSystem.AppDataDirectory);
        var fullSourcePath = Path.GetFullPath(sourcePath);

        if (fullSourcePath.StartsWith(appDataDirectory, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(fullSourcePath).StartsWith("custom_background", StringComparison.OrdinalIgnoreCase)
            && File.Exists(fullSourcePath))
        {
            // The selected file is already the app-managed background image; avoid rewriting in place.
            return fullSourcePath;
        }

        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".jpg";
        }

        var destinationPath = Path.Combine(
            FileSystem.AppDataDirectory,
            $"custom_background_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{extension}");

        await using var sourceStream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var destinationStream = File.Open(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await sourceStream.CopyToAsync(destinationStream);

        return destinationPath;
    }

    private void OnThemeChanged(object? sender, string themeName)
    {
        ApplyBackgroundImageResourceOverride();
    }

    private void ApplyBackgroundImageResourceOverride()
    {
        var appResources = Application.Current?.Resources;
        if (appResources == null)
        {
            return;
        }

        appResources["RootGridBackground"] = ResolveEffectiveRootBackground();
    }

    private string ResolveEffectiveRootBackground()
    {
        var useCustomBackgroundImage = Preferences.Get(UseCustomBackgroundImagePreferenceKey, false);
        var customBackgroundImagePath = Preferences.Get(CustomBackgroundImagePathPreferenceKey, string.Empty);

        if (useCustomBackgroundImage && !string.IsNullOrWhiteSpace(customBackgroundImagePath))
        {
            if (File.Exists(customBackgroundImagePath))
            {
                return customBackgroundImagePath;
            }

            Preferences.Set(UseCustomBackgroundImagePreferenceKey, false);
        }

        return _themeService.GetThemeResource(_themeService.CurrentTheme, "RootGridBackground") as string ?? string.Empty;
    }

    private static bool IsAppManagedBackgroundFile(string filePath)
    {
        var appDataDirectory = Path.GetFullPath(FileSystem.AppDataDirectory);
        var fullPath = Path.GetFullPath(filePath);

        return fullPath.StartsWith(appDataDirectory, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(fullPath).StartsWith("custom_background", StringComparison.OrdinalIgnoreCase)
            && File.Exists(fullPath);
    }
}