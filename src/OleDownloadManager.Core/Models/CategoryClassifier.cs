using OleDownloadManager.Core.Enums;

namespace OleDownloadManager.Core.Models;

/// <summary>Maps file extensions to download categories using a configurable table.</summary>
public sealed class CategoryClassifier
{
    public static IReadOnlyDictionary<DownloadCategory, string[]> DefaultMappings { get; } = new Dictionary<DownloadCategory, string[]>
    {
        [DownloadCategory.Applications] = [".exe", ".msi", ".msix", ".appx", ".msixbundle", ".appxbundle"],
        [DownloadCategory.Archives] = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".iso"],
        [DownloadCategory.Documents] = [".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".txt", ".rtf", ".odt", ".csv", ".epub"],
        [DownloadCategory.Video] = [".mp4", ".mkv", ".mov", ".webm", ".avi", ".wmv", ".m4v", ".flv"],
        [DownloadCategory.Audio] = [".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma"],
        [DownloadCategory.Images] = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".svg", ".tif", ".tiff", ".heic", ".avif"],
    };

    private readonly Dictionary<string, DownloadCategory> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    public CategoryClassifier()
        : this(DefaultMappings)
    {
    }

    public CategoryClassifier(IReadOnlyDictionary<DownloadCategory, string[]> mappings)
    {
        foreach (var (category, extensions) in mappings)
        {
            foreach (var ext in extensions)
            {
                var normalized = NormalizeExtension(ext);
                if (normalized.Length > 1)
                {
                    _byExtension[normalized] = category;
                }
            }
        }
    }

    public DownloadCategory Classify(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return DownloadCategory.Other;
        }

        var lower = fileName.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tar.xz", StringComparison.Ordinal))
        {
            return _byExtension.GetValueOrDefault(".tar", DownloadCategory.Archives);
        }

        var extension = Path.GetExtension(fileName);
        return _byExtension.GetValueOrDefault(extension, DownloadCategory.Other);
    }

    public static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim().ToLowerInvariant();
        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }
}
