using Ben.Services;
using Ben.ViewModels;
using Microsoft.Maui.Storage;

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
    private bool _originalUseCustomBackgroundImage;
    private bool _selectedUseCustomBackgroundImage;
    private string? _originalCustomBackgroundImagePath;
    private string? _selectedCustomBackgroundImagePath;
    private string _selectedUserFont = "PatrickHand";
    private readonly ThemeService _themeService;
    private readonly BackgroundImageService _backgroundImageService;
    private readonly UserFontService _userFontService;
    private readonly DailyViewModel _dailyViewModel;
    private bool _isDeleteCloudDataInProgress;
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
        new ThemeOption { Name = "Assateague",   DisplayName = "Assateague" },
        new ThemeOption { Name = "Bodie",        DisplayName = "Bodie" },
        new ThemeOption { Name = "CapeLookout",  DisplayName = "Cape Lookout" },
        new ThemeOption { Name = "Currituck",    DisplayName = "Currituck" },
        new ThemeOption { Name = "Hatteras",     DisplayName = "Hatteras" },
        new ThemeOption { Name = "HeadHarbour",  DisplayName = "Head Harbour" },
        new ThemeOption { Name = "Herring",      DisplayName = "Herring" },
        new ThemeOption { Name = "Hocq",         DisplayName = "Hocq" },
        new ThemeOption { Name = "Keskiniemi",   DisplayName = "Keskiniemi" },
        new ThemeOption { Name = "Kingswear",    DisplayName = "Kingswear" },
        new ThemeOption { Name = "LaMarina",     DisplayName = "La Marina" },
        new ThemeOption { Name = "Laitakari",    DisplayName = "Laitakari" },
        new ThemeOption { Name = "Ocracoke",     DisplayName = "Ocracoke" },
        new ThemeOption { Name = "StMartins",    DisplayName = "St. Martins" },
        new ThemeOption { Name = "Trinity",      DisplayName = "Trinity" },
        new ThemeOption { Name = "TybeeIsland",  DisplayName = "Tybee Island" },
        new ThemeOption { Name = "Ystad",        DisplayName = "Ystad" },
    };

    public List<ThemeOption> AvailableThemes => _availableThemes;
    public IReadOnlyList<AppFontOption> AvailableUserFonts => _userFontService.AvailableFonts;
    public DailyViewModel AuthViewModel => _dailyViewModel;

    public SettingsPage()
    {
        InitializeComponent();
        _themeService = IPlatformApplication.Current!.Services.GetService<ThemeService>()!;
        _backgroundImageService = IPlatformApplication.Current!.Services.GetRequiredService<BackgroundImageService>();
        _userFontService = IPlatformApplication.Current!.Services.GetRequiredService<UserFontService>();
        _dailyViewModel = IPlatformApplication.Current!.Services.GetRequiredService<DailyViewModel>();
        BindingContext = this;

        // Store the original theme so we can revert if user cancels
        _originalTheme = _themeService.CurrentTheme;
        _selectedTheme = _originalTheme;

        // Set the picker to the current theme
        var currentIndex = _availableThemes.FindIndex(t => t.Name == _originalTheme);
        ThemeColorPicker.SelectedIndex = currentIndex >= 0 ? currentIndex : 3; // Default to Hatteras

        _originalUseCustomBackgroundImage = _backgroundImageService.UseCustomBackgroundImage;
        _selectedUseCustomBackgroundImage = _originalUseCustomBackgroundImage;
        _originalCustomBackgroundImagePath = _backgroundImageService.CustomBackgroundImagePath;
        _selectedCustomBackgroundImagePath = _originalCustomBackgroundImagePath;
        UseCustomBackgroundImageCheckBox.IsChecked = _selectedUseCustomBackgroundImage;
        UpdateCustomBackgroundImageControls();

        // Set the font picker to the current selected user font.
        _selectedUserFont = _userFontService.CurrentUserFont;
        var currentFontIndex = AvailableUserFonts
            .Select((font, index) => new { font, index })
            .FirstOrDefault(x => x.font.Alias == _selectedUserFont)?.index ?? 0;
        UserFontPicker.SelectedIndex = currentFontIndex;
        ApplyPickerFontPreview();

        ApplyThemePreview(_selectedTheme);
    }

    private async void OnSignOutOnlyTapped(object sender, EventArgs e)
    {
        await _dailyViewModel.SignOutAsync();
        LocalDataNoticeLabel.Text = "Signed out. Local data remains on this device unless you choose Delete local data.";
        LocalDataNoticeLabel.IsVisible = true;
    }

    private void OnDeleteLocalDataTapped(object sender, EventArgs e)
    {
        _ = DeleteLocalDataAsync();
    }

    private async Task DeleteLocalDataAsync()
    {
        LocalDataNoticeLabel.IsVisible = false;

        bool confirmed = await DisplayAlertAsync(
            "Delete local data?",
            "This will delete and recreate the local Daymark database on this device. It will not delete your cloud data.",
            "Delete local data",
            "Cancel");

        if (!confirmed)
        {
            return;
        }

        await _dailyViewModel.DeleteLocalDataAsync();
    }

    private void OnDeleteCloudDataTapped(object sender, EventArgs e)
    {
        _ = DeleteCloudDataAsync();
    }

    private async Task DeleteCloudDataAsync()
    {
        if (_isDeleteCloudDataInProgress)
        {
            return;
        }

        _isDeleteCloudDataInProgress = true;
        SetDeleteCloudDataBusyState(true);

        try
        {
            LocalDataNoticeLabel.IsVisible = false;

            if (!_dailyViewModel.IsAuthenticated)
            {
                await DisplayAlertAsync("Sign in required", "You must be signed in to delete cloud data.", "OK");
                return;
            }

            var identity = await _dailyViewModel.ReauthenticateAsync();
            if (identity == null)
            {
                await DisplayAlertAsync(
                    "Re-authentication canceled",
                    "Cloud data was not deleted.",
                    "OK");
                return;
            }

            bool confirmed = await DisplayAlertAsync(
                "Delete cloud data?",
                "Your cloud data will be deleted. You will be signed out. Local data will remain on this device unless you choose Delete local data.",
                "Delete cloud data",
                "Cancel");

            if (!confirmed)
            {
                return;
            }

            var result = await _dailyViewModel.DeleteCloudDataAndSignOutAsync();

            LocalDataNoticeLabel.Text = "Signed out. Local data remains on this device unless you choose Delete local data.";
            LocalDataNoticeLabel.IsVisible = true;

            if (result.IsSuccess)
            {
                await DisplayAlertAsync(
                    "Cloud data deleted",
                    $"{result.Message}\n\nDeleted items:\nTasks: {result.TasksDeleted}\nNotes: {result.NotesDeleted}\nProjects: {result.ProjectsDeleted}\nAccount records: {result.UsersDeleted}\n\nYou were signed out. Local data remains on this device.",
                    "OK");
                return;
            }

            await DisplayAlertAsync(
                "Signed out",
                $"Cloud data deletion did not complete.\nStatus: {result.Status} ({result.StatusCode})\nDetails: {result.Message}\n\nYou were signed out. Local data remains on this device.",
                "OK");
        }
        finally
        {
            SetDeleteCloudDataBusyState(false);
            _isDeleteCloudDataInProgress = false;
        }
    }

    private void SetDeleteCloudDataBusyState(bool isBusy)
    {
        SignOutButton.IsEnabled = !isBusy;
        DeleteLocalDataButton.IsEnabled = !isBusy;
        DeleteCloudDataButton.IsEnabled = !isBusy;
        DeleteCloudBusyRow.IsVisible = isBusy;
    }

    private async void OnSignInMicrosoftTapped(object sender, TappedEventArgs e)
    {
        // Launches the Microsoft sign-in flow via the unified auth runtime.
        await _dailyViewModel.SignInWithMicrosoftAsync();
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

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        string? persistedCustomBackgroundImagePath = null;

        if (_selectedUseCustomBackgroundImage)
        {
            if (string.IsNullOrWhiteSpace(_selectedCustomBackgroundImagePath) || !File.Exists(_selectedCustomBackgroundImagePath))
            {
                await DisplayAlertAsync("Select an image", "Choose an image file to use as your custom background.", "OK");
                return;
            }

            persistedCustomBackgroundImagePath = await BackgroundImageService.PersistCustomBackgroundImageAsync(_selectedCustomBackgroundImagePath);
        }

        _themeService.SetTheme(_selectedTheme);
        _backgroundImageService.SetBackgroundImageOverride(_selectedUseCustomBackgroundImage, persistedCustomBackgroundImagePath);
        _userFontService.ApplyUserFont(_selectedUserFont);
        await Navigation.PopAsync();
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

    private void OnUseCustomBackgroundImageCheckedChanged(object sender, CheckedChangedEventArgs e)
    {
        _selectedUseCustomBackgroundImage = e.Value;

        UpdateCustomBackgroundImageControls();
    }

    private async void OnChooseBackgroundImageClicked(object sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Choose background image",
                FileTypes = FilePickerFileType.Images,
            });

            if (result == null)
            {
                return;
            }

            _selectedCustomBackgroundImagePath = result.FullPath;
            UpdateCustomBackgroundImageControls();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Unable to choose file", ex.Message, "OK");
        }
    }

    private void UpdateCustomBackgroundImageControls()
    {
        ChooseBackgroundImageButton.IsVisible = _selectedUseCustomBackgroundImage;

        var selectedFileName = Path.GetFileName(_selectedCustomBackgroundImagePath);
        CustomBackgroundImageFileNameLabel.Text = !_selectedUseCustomBackgroundImage
            ? "Theme background image will be used."
            : string.IsNullOrWhiteSpace(selectedFileName)
                ? "No file selected"
                : selectedFileName;
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

