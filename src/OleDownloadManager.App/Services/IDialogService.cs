using OleDownloadManager.Core.Enums;

namespace OleDownloadManager.App.Services;

/// <summary>Values used to pre-fill the Add Download dialog (from paste, browser, clipboard or command line).</summary>
public sealed record AddDownloadPrefill(string? Url, string? FileName = null, string? PageUrl = null, DownloadSource Source = DownloadSource.User);

public sealed record ScheduleChoice(bool Confirmed, DateTimeOffset? When);

/// <summary>Window and message-box interactions, abstracted so view models stay testable.</summary>
public interface IDialogService
{
    /// <summary>Shows the Add Download dialog. Returns true when a download was added.</summary>
    bool ShowAddDownload(AddDownloadPrefill? prefill);

    void ShowSettings();

    void ShowMediaDialog(string? url);

    bool Confirm(string title, string message, string confirmText = "OK");

    void ShowError(string title, string message);

    ScheduleChoice AskSchedule(DateTimeOffset? current);

    string? PickFolder(string? initialDirectory);

    void ShowAbout();
}
