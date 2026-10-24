using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OleDownloadManager.Core.Enums;
using OleDownloadManager.Core.Interfaces;
using OleDownloadManager.Core.Models;
using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.App.Services;

/// <summary>
/// Opt-in clipboard watcher. When the user copies a single HTTP(S) link whose file extension belongs
/// to a known category, it raises <see cref="LinkDetected"/>; nothing is downloaded automatically.
/// </summary>
public sealed partial class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly ISettingsStore _settings;
    private HwndSource? _source;
    private string? _last;

    public ClipboardMonitor(ISettingsStore settings)
    {
        _settings = settings;
    }

    public event EventHandler<string>? LinkDetected;

    public bool IsAttached => _source is not null;

    public void Attach(Window window)
    {
        if (_source is not null)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
        _ = AddClipboardFormatListener(handle);
    }

    public void Detach()
    {
        if (_source is null)
        {
            return;
        }

        _ = RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            Inspect();
        }

        return IntPtr.Zero;
    }

    private void Inspect()
    {
        string? text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null;
        }
        catch (COMException)
        {
            return;
        }

        if (string.IsNullOrEmpty(text) || text == _last || text.Length > UrlValidator.MaxUrlLength || text.Contains('\n', StringComparison.Ordinal))
        {
            return;
        }

        if (!text.Contains("://", StringComparison.Ordinal) || !UrlValidator.TryValidate(text, out var uri, out _))
        {
            return;
        }

        var name = FileNameSanitizer.FromUrl(uri!);
        if (new CategoryClassifier(_settings.Current.CategoryExtensions).Classify(name) == DownloadCategory.Other)
        {
            return;
        }

        _last = text;
        LinkDetected?.Invoke(this, uri!.AbsoluteUri);
    }

    public void Dispose() => Detach();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(IntPtr hwnd);
}
