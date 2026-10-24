namespace OleDownloadManager.Infrastructure;

/// <summary>
/// Per-user data locations, kept separate from the installation directory.
/// Set OLEDM_DATA_DIR to relocate everything (used by tests and portable runs).
/// </summary>
public sealed class AppPaths
{
    public const string DataDirectoryVariable = "OLEDM_DATA_DIR";

    public AppPaths(string? dataDirectory = null)
    {
        var configured = dataDirectory ?? Environment.GetEnvironmentVariable(DataDirectoryVariable);
        DataDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OleDownloadManager")
            : Path.GetFullPath(configured);

        LogDirectory = Path.Combine(DataDirectory, "logs");
        DatabasePath = Path.Combine(DataDirectory, "oledm.db");
        NativeMessagingDirectory = Path.Combine(DataDirectory, "native-messaging");
    }

    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string DatabasePath { get; }
    public string NativeMessagingDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
