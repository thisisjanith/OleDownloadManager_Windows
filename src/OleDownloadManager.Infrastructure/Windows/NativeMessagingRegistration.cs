using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace OleDownloadManager.Infrastructure.Windows;

public sealed record BrowserRegistrationStatus(string Browser, bool Registered, string? ManifestPath, bool HostExists);

/// <summary>
/// Per-user (HKCU) registration of the native messaging host for Chrome, Edge and Firefox.
/// Only the specific extension IDs are allowed; wildcard origins are never written.
/// </summary>
public sealed class NativeMessagingRegistration
{
    public const string HostName = "com.oledm.downloadmanager";
    public const string HostExecutableName = "OleDownloadManager.BrowserHost.exe";

    /// <summary>ID of the unpacked development extension, fixed by the "key" in its manifest.</summary>
    public const string DefaultChromeExtensionId = "fjlipfmbkmopdjkjnfmpocodmcecbacf";
    public const string DefaultFirefoxExtensionId = "oledownloadmanager@oledm.local";

    private static readonly (string Browser, string KeyPath, bool Firefox)[] Browsers =
    [
        ("Chrome", @"Software\Google\Chrome\NativeMessagingHosts\" + HostName, false),
        ("Edge", @"Software\Microsoft\Edge\NativeMessagingHosts\" + HostName, false),
        ("Firefox", @"Software\Mozilla\NativeMessagingHosts\" + HostName, true),
    ];

    private readonly string _manifestDirectory;

    public NativeMessagingRegistration(string manifestDirectory)
    {
        _manifestDirectory = manifestDirectory;
    }

    public static string BuildChromeManifest(string hostPath, IEnumerable<string> extensionIds)
    {
        var origins = new JsonArray(extensionIds.Select(ValidateChromeId).Select(id => (JsonNode)$"chrome-extension://{id}/").ToArray());
        var manifest = new JsonObject
        {
            ["name"] = HostName,
            ["description"] = "OleDownloadManager browser integration",
            ["path"] = hostPath,
            ["type"] = "stdio",
            ["allowed_origins"] = origins,
        };
        return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string BuildFirefoxManifest(string hostPath, IEnumerable<string> extensionIds)
    {
        var manifest = new JsonObject
        {
            ["name"] = HostName,
            ["description"] = "OleDownloadManager browser integration",
            ["path"] = hostPath,
            ["type"] = "stdio",
            ["allowed_extensions"] = new JsonArray(extensionIds.Select(ValidateFirefoxId).Select(id => (JsonNode)id).ToArray()),
        };
        return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Writes manifests and HKCU registry entries pointing at <paramref name="hostPath"/>.</summary>
    public void Register(string hostPath, IReadOnlyList<string> chromeExtensionIds, IReadOnlyList<string> firefoxExtensionIds)
    {
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException("The native messaging host executable was not found.", hostPath);
        }

        Directory.CreateDirectory(_manifestDirectory);
        var chromePath = Path.Combine(_manifestDirectory, HostName + ".chrome.json");
        var firefoxPath = Path.Combine(_manifestDirectory, HostName + ".firefox.json");
        File.WriteAllText(chromePath, BuildChromeManifest(hostPath, chromeExtensionIds));
        File.WriteAllText(firefoxPath, BuildFirefoxManifest(hostPath, firefoxExtensionIds));

        foreach (var (_, keyPath, firefox) in Browsers)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key.SetValue(null, firefox ? firefoxPath : chromePath, RegistryValueKind.String);
        }
    }

    public void Unregister()
    {
        foreach (var (_, keyPath, _) in Browsers)
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }

        foreach (var file in new[] { HostName + ".chrome.json", HostName + ".firefox.json" })
        {
            var path = Path.Combine(_manifestDirectory, file);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    public static IReadOnlyList<BrowserRegistrationStatus> GetStatus()
    {
        var result = new List<BrowserRegistrationStatus>();
        foreach (var (browser, keyPath, _) in Browsers)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            var manifestPath = key?.GetValue(null) as string;
            var hostExists = false;
            if (manifestPath is not null && File.Exists(manifestPath))
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(manifestPath));
                    var path = node?["path"]?.GetValue<string>();
                    if (path is not null)
                    {
                        var resolved = Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(manifestPath)!, path);
                        hostExists = File.Exists(resolved);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
                {
                    hostExists = false;
                }
            }

            result.Add(new BrowserRegistrationStatus(browser, manifestPath is not null, manifestPath, hostExists));
        }

        return result;
    }

    private static string ValidateChromeId(string id)
    {
        if (id.Length != 32 || !id.All(c => c is >= 'a' and <= 'p'))
        {
            throw new ArgumentException($"\"{id}\" is not a valid Chrome extension ID.", nameof(id));
        }

        return id;
    }

    private static string ValidateFirefoxId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains('*', StringComparison.Ordinal) || id.Length > 200)
        {
            throw new ArgumentException($"\"{id}\" is not a valid Firefox extension ID.", nameof(id));
        }

        return id;
    }
}
