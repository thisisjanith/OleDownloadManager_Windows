using OleDownloadManager.Core.Validation;

namespace OleDownloadManager.UnitTests;

public sealed class UrlValidatorTests
{
    [Theory]
    [InlineData("https://example.com/file.zip", "https://example.com/file.zip")]
    [InlineData("http://example.com:8080/a?b=c", "http://example.com:8080/a?b=c")]
    [InlineData("  https://example.com/x  ", "https://example.com/x")]
    [InlineData("example.com/file.zip", "https://example.com/file.zip")]
    [InlineData("localhost:8080/file", "https://localhost:8080/file")]
    public void AcceptsHttpAndHttps(string input, string expected)
    {
        Assert.True(UrlValidator.TryValidate(input, out var uri, out var error), error);
        Assert.Equal(expected, uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://exa mple.com/")]
    [InlineData("https://user:pass@example.com/file")]
    [InlineData("https://")]
    [InlineData("mailto:a@b.c")]
    public void RejectsInvalidOrUnsafe(string? input)
    {
        Assert.False(UrlValidator.TryValidate(input, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void RejectsOverlongUrls()
    {
        Assert.False(UrlValidator.IsValid("https://example.com/" + new string('a', UrlValidator.MaxUrlLength)));
    }
}

public sealed class FileNameSanitizerTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("C:\\temp\\a.txt", "a.txt")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("name. . .", "name")]
    [InlineData("trailing space   ", "trailing space")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("LPT1.log", "_LPT1.log")]
    [InlineData("COM9", "_COM9")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("日本語ファイル.zip", "日本語ファイル.zip")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    [InlineData("..", "download")]
    [InlineData("", "download")]
    [InlineData("   ", "download")]
    [InlineData("/", "download")]
    public void ProducesSafeNames(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void RemovesBidiOverrideCharacters()
    {
        // "invoice\u202Efdp.exe" displays as "invoiceexe.pdf".
        Assert.Equal("invoicefdp.exe", FileNameSanitizer.Sanitize("invoice\u202Efdp.exe"));
    }

    [Fact]
    public void TruncatesLongNamesPreservingExtension()
    {
        var result = FileNameSanitizer.Sanitize(new string('x', 400) + ".iso");
        Assert.True(result.Length <= FileNameSanitizer.MaxFileNameLength);
        Assert.EndsWith(".iso", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncationDoesNotSplitSurrogatePairs()
    {
        var name = string.Concat(Enumerable.Repeat("😀", 200)) + ".txt";
        var result = FileNameSanitizer.Truncate(name, 51);
        Assert.EndsWith(".txt", result, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(result[^5]));
    }

    [Theory]
    [InlineData("https://example.com/files/setup.exe", "setup.exe")]
    [InlineData("https://example.com/files/my%20file.zip?x=1", "my file.zip")]
    [InlineData("https://example.com/", "example_com")]
    [InlineData("https://example.com/a/..%2F..%2Fb.txt", "b.txt")]
    public void DerivesNameFromUrl(string url, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.FromUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("a.zip", 1, "a (1).zip")]
    [InlineData("archive.tar.gz", 2, "archive (2).tar.gz")]
    [InlineData("noext", 3, "noext (3)")]
    public void AddsCounters(string name, int counter, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.WithCounter(name, counter));
    }
}

public sealed class HeaderValidatorTests
{
    [Fact]
    public void ParsesValidHeaders()
    {
        Assert.True(HeaderValidator.TryParse("X-Token: abc\r\nAccept-Language: en\n\n", out var headers, out var error), error);
        Assert.Equal("abc", headers["x-token"]);
        Assert.Equal("en", headers["Accept-Language"]);
    }

    [Theory]
    [InlineData("NoColonHere")]
    [InlineData("Bad Name: value")]
    [InlineData("Host: evil.com")]
    [InlineData("Range: bytes=0-")]
    [InlineData("X-A: line\u0001break")]
    public void RejectsInvalidHeaders(string text)
    {
        Assert.False(HeaderValidator.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("Cookie", true)]
    [InlineData("X-Api-Key", true)]
    [InlineData("X-Auth-Token", true)]
    [InlineData("Accept-Language", false)]
    [InlineData("User-Agent", false)]
    public void IdentifiesSensitiveHeaders(string name, bool sensitive)
    {
        Assert.Equal(sensitive, HeaderValidator.IsSensitive(name));
    }
}

public sealed class LogRedactorTests
{
    [Fact]
    public void RemovesQueryValuesAndUserInfo()
    {
        var redacted = LogRedactor.Url(new Uri("https://user:pw@cdn.example.com:8443/f.zip?token=SECRET&sig=XYZ#frag"));
        Assert.Equal("https://cdn.example.com:8443/f.zip?token=***&sig=***", redacted);
        Assert.DoesNotContain("SECRET", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("pw", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubberRemovesSecretsFromFreeText()
    {
        var text = Infrastructure.Logging.LogScrubber.Scrub(
            "GET https://a.com/x?key=SECRET1 failed; Authorization: Bearer SECRET2; https://u:SECRET3@b.com/");
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
    }
}
