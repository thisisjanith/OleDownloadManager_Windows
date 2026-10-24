using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using OleDownloadManager.Core.Enums;

namespace OleDownloadManager.App.Services;

/// <summary>Applies light/dark palettes, follows the Windows app theme when set to System, and darkens title bars.</summary>
public sealed partial class ThemeService : IDisposable
{
    private const int DwmUseImmersiveDarkMode = 20;
    private static readonly Uri LightUri = new("pack://application:,,,/OleDownloadManager;component/Resources/Theme.Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/OleDownloadManager;component/Resources/Theme.Dark.xaml");

    private AppTheme _preference = AppTheme.System;

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Apply(AppTheme preference)
    {
        _preference = preference;
        var dark = preference switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        var resources = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        // App.xaml keeps the palette at index 0; identify it by content because Source may be relative.
        if (resources.Count > 0 && resources[0].Contains("AccentColor"))
        {
            resources[0] = palette;
        }
        else
        {
            resources.Insert(0, palette);
        }

        IsDark = dark;
        foreach (Window window in Application.Current.Windows)
        {
            ApplyTitleBar(window);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Matches the window's title bar to the current theme (Windows 10 20H1+ / Windows 11).</summary>
    public void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized -= OnSourceInitialized;
            window.SourceInitialized += OnSourceInitialized;
            return;
        }

        var value = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.SourceInitialized -= OnSourceInitialized;
            ApplyTitleBar(window);
        }
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_preference == AppTheme.System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
