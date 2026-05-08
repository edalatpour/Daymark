namespace Ben.Views;

using Ben.Models;
using Microsoft.Maui.Devices;
using Ben.ViewModels;

public partial class DailyHostPage : ContentPage
{
    const double LandscapeSeamBaseOpacity = 0.2;
    const double LandscapeSeamPeakOpacity = 0.38;

    private readonly DailyViewModel _viewModel;
    readonly TaskPageView _tasksView;
    readonly NotesPageView _notesView;
    bool _isNavigating;
    bool _isLandscape;

    public DailyHostPage(DailyViewModel vm)
    {
        InitializeComponent();
        DesktopNavigationButtons.IsVisible = ShowDesktopArrows;
        _viewModel = vm;
        BindingContext = vm;
        _tasksView = new TaskPageView(_viewModel);
        _notesView = new NotesPageView(_viewModel);
        ApplyLayout();
    }

    DailyViewModel ViewModel => _viewModel;

    public bool ShowDesktopArrows { get; } = DeviceInfo.Idiom == DeviceIdiom.Desktop;

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        bool isLandscape = width > height;
        if (_isLandscape == isLandscape)
        {
            return;
        }

        _isLandscape = isLandscape;
        ApplyLayout();
    }

    void UpdatePortraitPage()
    {
        if (ViewModel.SubPage == 0)
        {
            AttachView(SinglePageHost, _tasksView);
        }
        else
        {
            AttachView(SinglePageHost, _notesView);
        }
    }

    void ApplyLayout()
    {
        LandscapeGrid.IsVisible = _isLandscape;
        PortraitGrid.IsVisible = !_isLandscape;
        LandscapeSeamShadow.IsVisible = _isLandscape;

        if (_isLandscape)
        {
            SinglePageHost.Content = null;
            AttachView(LandscapeTasksHost, _tasksView);
            AttachView(LandscapeNotesHost, _notesView);
            return;
        }

        LandscapeTasksHost.Content = null;
        LandscapeNotesHost.Content = null;
        UpdatePortraitPage();
    }

    static void AttachView(ContentView host, View view)
    {
        if (view.Parent is ContentView parentHost && !ReferenceEquals(parentHost, host))
        {
            parentHost.Content = null;
        }

        host.Content = view;
    }

    async Task PreviousPage()
    {
        if (_isNavigating)
        {
            return;
        }

        _isNavigating = true;
        try
        {
            if (_isLandscape)
            {
                if (ViewModel.CurrentDay != null)
                {
                    await AnimateLandscapeTurnAsync(-1, () => ViewModel.NavigatePageAsync(-1));
                }
            }
            else
            {
                await AnimatePortraitNavigationAsync(-1, ViewModel.GoBackwardAsync);
            }
        }
        finally
        {
            _isNavigating = false;
        }
    }

    async Task NextPage()
    {
        if (_isNavigating)
        {
            return;
        }

        _isNavigating = true;
        try
        {
            if (_isLandscape)
            {
                if (ViewModel.CurrentDay != null)
                {
                    await AnimateLandscapeTurnAsync(1, () => ViewModel.NavigatePageAsync(1));
                }
            }
            else
            {
                await AnimatePortraitNavigationAsync(1, ViewModel.GoForwardAsync);
            }
        }
        finally
        {
            _isNavigating = false;
        }
    }

    Task AnimatePortraitNavigationAsync(int direction, Func<Task> navigateAsync)
    {
        return AnimatePortraitPanAsync(direction, navigateAsync);
    }

    async Task AnimatePortraitPanAsync(int direction, Func<Task> navigateAsync)
    {
        double width = Math.Max(SinglePageHost.Width, RootGrid.Width);
        if (width <= 0)
        {
            await navigateAsync();
            UpdatePortraitPage();
            return;
        }

        double offset = Math.Clamp(width * 0.22, 48d, 140d);

        SinglePageHost.CancelAnimations();
        await Task.WhenAll(
            SinglePageHost.TranslateToAsync(-direction * offset * 0.45, 0, 95, Easing.CubicIn),
            SinglePageHost.FadeToAsync(0.9, 95, Easing.CubicIn));

        await navigateAsync();
        UpdatePortraitPage();

        SinglePageHost.TranslationX = direction * offset;
        SinglePageHost.Opacity = 0.9;

        await Task.WhenAll(
            SinglePageHost.TranslateToAsync(0, 0, 180, Easing.CubicOut),
            SinglePageHost.FadeToAsync(1, 180, Easing.CubicOut));
    }

    async Task AnimatePortraitFlipAsync(int direction, Func<Task> navigateAsync)
    {
        double width = Math.Max(SinglePageHost.Width, RootGrid.Width);
        if (width <= 0)
        {
            await navigateAsync();
            UpdatePortraitPage();
            return;
        }

        double closeShift = Math.Clamp(width * 0.035, 14d, 40d);
        double openShift = Math.Clamp(width * 0.14, 48d, 120d);
        // In portrait, forward day navigation should visually lift the right edge toward the viewer.
        double closeTilt = direction > 0 ? 42d : -42d;
        double openTilt = -closeTilt * 0.34;

        SinglePageHost.AnchorX = direction > 0 ? 1 : 0;
        SinglePageHost.CancelAnimations();

        await Task.WhenAll(
            SinglePageHost.RotateYToAsync(closeTilt, 150, Easing.CubicIn),
            SinglePageHost.TranslateToAsync(-direction * closeShift, 0, 150, Easing.CubicIn),
            SinglePageHost.FadeToAsync(0.62, 150, Easing.CubicIn));

        await navigateAsync();
        UpdatePortraitPage();

        SinglePageHost.RotationY = openTilt;
        SinglePageHost.TranslationX = direction > 0 ? -openShift : openShift;
        SinglePageHost.Opacity = 0.62;

        await Task.WhenAll(
            SinglePageHost.RotateYToAsync(0, 190, Easing.CubicOut),
            SinglePageHost.TranslateToAsync(0, 0, 190, Easing.CubicOut),
            SinglePageHost.FadeToAsync(1, 190, Easing.CubicOut));
    }

    async Task AnimateLandscapeTurnAsync(int direction, Func<Task> navigateAsync)
    {
        double width = Math.Max(LandscapeGrid.Width, RootGrid.Width);
        if (width <= 0)
        {
            await navigateAsync();
            return;
        }

        bool turningRightPage = direction > 0;
        Border turningSheet = turningRightPage ? RightTurnSheet : LeftTurnSheet;
        Border otherSheet = turningRightPage ? LeftTurnSheet : RightTurnSheet;

        double halfWidth = width * 0.5;
        double crossDistance = Math.Clamp(halfWidth * 0.96, 110d, 420d);
        double seamNudge = Math.Clamp(width * 0.008, 4d, 10d);
        double closingTilt = turningRightPage ? -86d : 86d;
        double openingTilt = -closingTilt * 0.9;

        turningSheet.AnchorX = turningRightPage ? 0 : 1;
        turningSheet.AnchorY = 0.5;

        turningSheet.CancelAnimations();
        otherSheet.CancelAnimations();
        LandscapeSeamShadow.CancelAnimations();

        otherSheet.IsVisible = false;
        otherSheet.Opacity = 0;
        otherSheet.TranslationX = 0;
        otherSheet.RotationY = 0;

        turningSheet.IsVisible = true;
        turningSheet.Opacity = 1;
        turningSheet.TranslationX = 0;
        turningSheet.RotationY = 0;
        turningSheet.ScaleX = 1;

        double seamShift = turningRightPage ? -seamNudge : seamNudge;

        await Task.WhenAll(
            turningSheet.RotateYToAsync(closingTilt, 180, Easing.CubicIn),
            turningSheet.FadeToAsync(0.32, 180, Easing.CubicIn),
            LandscapeSeamShadow.TranslateToAsync(seamShift, 0, 180, Easing.CubicIn),
            LandscapeSeamShadow.FadeToAsync(LandscapeSeamPeakOpacity, 180, Easing.CubicIn));

        await navigateAsync();

        turningSheet.TranslationX = turningRightPage ? -crossDistance : crossDistance;
        turningSheet.RotationY = openingTilt;
        turningSheet.Opacity = 0.32;
        LandscapeSeamShadow.TranslationX = -seamShift * 0.4;

        await Task.WhenAll(
            turningSheet.TranslateToAsync(0, 0, 240, Easing.CubicOut),
            turningSheet.RotateYToAsync(0, 240, Easing.CubicOut),
            turningSheet.FadeToAsync(0, 240, Easing.CubicOut),
            LandscapeSeamShadow.TranslateToAsync(0, 0, 240, Easing.CubicOut),
            LandscapeSeamShadow.FadeToAsync(LandscapeSeamBaseOpacity, 240, Easing.CubicOut));

        turningSheet.IsVisible = false;
        turningSheet.TranslationX = 0;
        turningSheet.RotationY = 0;
        turningSheet.Opacity = 0;
    }

    async void OnSwipeLeft(object sender, SwipedEventArgs e)
    {
        await NextPage();
    }

    async void OnSwipeRight(object sender, SwipedEventArgs e)
    {
        await PreviousPage();
    }

    async void OnPreviousClicked(object sender, EventArgs e)
    {
        await PreviousPage();
    }

    async void OnNextClicked(object sender, EventArgs e)
    {
        await NextPage();
    }

    async void OnSyncStatusTapped(object sender, EventArgs e)
    {
        if (await ViewModel.TryNavigateToLatestSyncIssueAsync())
        {
            return;
        }

        await ViewModel.ForceSyncAsync();
    }

    async void OnSettingsClicked(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(SettingsPage));
    }

}