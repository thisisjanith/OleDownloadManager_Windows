using System.Diagnostics;
using System.IO;
using System.Windows;

namespace OleDownloadManager.App.Services;

/// <summary>Windows shell actions used by view models (kept behind an interface for tests).</summary>
public interface IShellService
{
    /// <summary>Opens a file with its associated program. Returns an error message on failure.</summary>
    string? OpenFile(string path);

    string? RevealInExplorer(string path);

    string? OpenFolder(string path);

    void CopyText(string text);

    string? GetClipboardText();
}

public sealed class ShellService : IShellService
{
    public string? OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            return "The file no longer exists. It may have been moved or deleted.";
        }

        try
        {
            // Opening is always an explicit user action; Windows applies SmartScreen/Mark-of-the-Web checks.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "The file could not be opened: " + ex.Message;
        }
    }

    public string? RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add("/select," + path);
                Process.Start(start);
                return null;
            }

            var directory = Path.GetDirectoryName(path);
            return directory is not null && Directory.Exists(directory) ? OpenFolder(directory) : "The folder no longer exists.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "File Explorer could not be opened: " + ex.Message;
        }
    }

    public string? OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return "The folder does not exist.";
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "The folder could not be opened: " + ex.Message;
        }
    }

    public void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard locked by another process; ignore.
        }
    }

    public string? GetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
