using Ben.Services;
using Ben.ViewModels;

namespace Ben.Views;

public partial class SettingsPage : ContentPage
{
    private static readonly string[] PreviewResourceKeys =
    {
        "Paper",
        "WritingPaper",
        "Ink",
        "Line",
        "Accent",
        "Link",
        "UserText",
        "PageBackgroundBrush",
        "CardBackgroundBrush",
        "InkBrush",
        "AccentBrush",
        "HeaderBackgroundBrush",
        "HeaderForegroundBrush",
        "BorderColor",
        "DividerColor",
        "BorderThickness",
        "DividerThickness",
        "ListCardCornerRadius",
    };

    private string _originalTheme = "Blue";
    private string _selectedTheme = "Blue";
    private string _selectedUserFont = "PatrickHand";
    private readonly ThemeService _themeService;
    private readonly UserFontService _userFontService;
    private readonly DailyViewModel _dailyViewModel;
    private readonly List<ThemeOption> _availableThemes = new()
    {
        new ThemeOption { Name = "Red",          DisplayName = "Red" },
        new ThemeOption { Name = "Orange",       DisplayName = "Orange" },
        new ThemeOption { Name = "Yellow",       DisplayName = "Yellow" },
        new ThemeOption { Name = "Green",        DisplayName = "Green" },
        new ThemeOption { Name = "Blue",         DisplayName = "Blue" },
        new ThemeOption { Name = "Purple",       DisplayName = "Purple" },
        new ThemeOption { Name = "Brown",        DisplayName = "Brown" },
        new ThemeOption { Name = "Gray",         DisplayName = "Gray" },
        new ThemeOption { Name = "Bodie",        DisplayName = "Bodie" },
        new ThemeOption { Name = "CapeLookout",  DisplayName = "Cape Lookout" },
        new ThemeOption { Name = "Currituck",    DisplayName = "Currituck" },
        new ThemeOption { Name = "Hatteras",     DisplayName = "Hatteras" },
        new ThemeOption { Name = "OakIsland",    DisplayName = "Oak Island" },
        new ThemeOption { Name = "Ocracoke",     DisplayName = "Ocracoke" },
    };

    public List<ThemeOption> AvailableThemes => _availableThemes;
    public IReadOnlyList<AppFontOption> AvailableUserFonts => _userFontService.AvailableFonts;
    public DailyViewModel AuthViewModel => _dailyViewModel;

    public SettingsPage()
    {
        InitializeComponent();
        _themeService = IPlatformApplication.Current!.Services.GetService<ThemeService>()!;
        _userFontService = IPlatformApplication.Current!.Services.GetRequiredService<UserFontService>();
        _dailyViewModel = IPlatformApplication.Current!.Services.GetRequiredService<DailyViewModel>();
        BindingContext = this;

        // Store the original theme so we can revert if user cancels
        _originalTheme = _themeService.CurrentTheme;
        _selectedTheme = _originalTheme;

        // Set the picker to the current theme
        var currentIndex = _availableThemes.FindIndex(t => t.Name == _originalTheme);
        ThemeColorPicker.SelectedIndex = currentIndex >= 0 ? currentIndex : 3; // Default to Hatteras

        // Set the font picker to the current selected user font.
        _selectedUserFont = _userFontService.CurrentUserFont;
        var currentFontIndex = AvailableUserFonts
            .Select((font, index) => new { font, index })
            .FirstOrDefault(x => x.font.Alias == _selectedUserFont)?.index ?? 0;
        UserFontPicker.SelectedIndex = currentFontIndex;
        ApplyPickerFontPreview();

        ApplyThemePreview(_selectedTheme);
    }

    private async void OnSignOutTapped(object sender, EventArgs e)
    {
        // Handles sign-out when the user is already authenticated (any provider)
        await _dailyViewModel.ToggleAuthenticationAsync();
    }

    private async void OnSignInMicrosoftTapped(object sender, TappedEventArgs e)
    {
        // Launches the existing Microsoft MSAL sign-in flow
        await _dailyViewModel.ToggleAuthenticationAsync();
    }

    private async void OnSignInAppleTapped(object sender, TappedEventArgs e)
    {
        // Launches the External ID sign-in flow for Apple via WebAuthenticator
        await _dailyViewModel.SignInWithAppleAsync();
    }

    private async void OnHelpTapped(object sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(HelpPage));
    }

    private void OnThemeColorSelected(object sender, EventArgs e)
    {
        if (ThemeColorPicker.SelectedItem is ThemeOption selectedTheme)
        {
            _selectedTheme = selectedTheme.Name;

            ApplyThemePreview(_selectedTheme);
        }
    }

    private void OnCancelClicked(object sender, EventArgs e)
    {
        // Just go back without changing anything
        Navigation.PopAsync();
    }

    private void OnSaveClicked(object sender, EventArgs e)
    {
        _themeService.SetTheme(_selectedTheme);
        _userFontService.ApplyUserFont(_selectedUserFont);
        Navigation.PopAsync();
    }

    private void OnUserFontSelected(object sender, EventArgs e)
    {
        if (UserFontPicker.SelectedItem is AppFontOption selectedFont)
        {
            _selectedUserFont = selectedFont.Alias;
            ApplyPickerFontPreview();
        }
    }

    private void ApplyPickerFontPreview()
    {
        ThemeColorPicker.FontFamily = _selectedUserFont;
        UserFontPicker.FontFamily = _selectedUserFont;
    }

    private void ApplyThemePreview(string themeName)
    {
        foreach (var resourceKey in PreviewResourceKeys)
        {
            var themeResource = _themeService.GetThemeResource(themeName, resourceKey);
            if (themeResource != null)
            {
                Resources[resourceKey] = themeResource;
            }
        }
    }
}

public class ThemeOption
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

