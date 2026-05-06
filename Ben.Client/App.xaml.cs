// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#if WINDOWS
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.UI;
using WindowsColor = Windows.UI.Color;
using MauiColor = Microsoft.Maui.Graphics.Color;
#endif

using Ben.Services;
using Ben.ViewModels;

namespace Ben;

public partial class App : Application
{

#if WINDOWS
    private static AppWindow? _appWindow;
#endif

    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Light;

        Microsoft.Maui.Handlers.WindowHandler.Mapper.AppendToMapping(nameof(IWindow), (handler, view) =>
        {
#if WINDOWS
            handler.PlatformView.Activate();

            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(handler.PlatformView);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(windowHandle));
            _appWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));

            // Set title bar color to Ink color from resources
            MainThread.BeginInvokeOnMainThread(() => UpdateTitleBarColor("Ink"));
#endif
        });

    }

    private const int WindowWidth = 1280;
    private const int WindowHeight = 720;

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var themeService = IPlatformApplication.Current?.Services.GetService<ThemeService>();
        if (themeService != null)
        {
            themeService.InitializeTheme();
            themeService.ThemeChanged -= OnThemeChanged;
            themeService.ThemeChanged += OnThemeChanged;
            System.Diagnostics.Debug.WriteLine("Theme service initialized");
        }

        var userFontService = IPlatformApplication.Current?.Services.GetService<UserFontService>();
        userFontService?.InitializeUserFont();

        return new Window(new AppShell());
    }

    /// <summary>
    /// Updates the Windows title bar color from a resource key.
    /// Call this when theme colors change to update the title bar dynamically.
    /// </summary>
    public static void UpdateTitleBarColor(string colorResourceKey)
    {
#if WINDOWS
        if (_appWindow == null || !AppWindowTitleBar.IsCustomizationSupported())
            return;

        if (Application.Current?.Resources.TryGetValue(colorResourceKey, out var colorResource) == true
            && colorResource is MauiColor mauiColor)
        {
            var windowsColor = WindowsColor.FromArgb(
                (byte)(mauiColor.Alpha * 255),
                (byte)(mauiColor.Red * 255),
                (byte)(mauiColor.Green * 255),
                (byte)(mauiColor.Blue * 255)
            );

            var titleBar = _appWindow.TitleBar;
            titleBar.BackgroundColor = windowsColor;
            titleBar.InactiveBackgroundColor = windowsColor;
            // Text color stays white for contrast
        }
#endif
    }

    private void OnThemeChanged(object? sender, string themeName)
    {
        UpdateTitleBarColor("Ink");
    }

}