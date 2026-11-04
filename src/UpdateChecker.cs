using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace FseMultiMonitor;

internal sealed record UpdateInfo(Version Version, string Tag, string SetupUrl, string ChecksumUrl);

internal static class UpdateChecker
{
    private const string Repository = "dizzi1222/fse-multimonitor";
    private static readonly HttpClient Client = CreateClient();

    internal static Version CurrentVersion
    {
        get
        {
            var version = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"FseMultiMonitor/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    internal static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            throw new InvalidOperationException($"Release tag '{tag}' is not a valid version.");

        version = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        if (version <= CurrentVersion) return null;

        string? setupName = null;
        string? checksumName = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name.StartsWith("FseMultiMonitor-Setup-", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                setupName = name;
            else if (name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase))
                checksumName = name;
        }

        if (setupName is null || checksumName is null)
            throw new InvalidOperationException("The latest release does not include the installer and its SHA256 file.");

        string releaseUrl = $"https://github.com/{Repository}/releases/download/{tag}";
        return new UpdateInfo(version, tag, $"{releaseUrl}/{setupName}", $"{releaseUrl}/{checksumName}");
    }

    internal static async Task<string> DownloadAndVerifyAsync(UpdateInfo update, CancellationToken cancellationToken = default)
    {
        string folder = Path.Combine(Path.GetTempPath(), "FseMultiMonitor-update", update.Version.ToString(3));
        Directory.CreateDirectory(folder);
        string setupPath = Path.Combine(folder, $"FseMultiMonitor-Setup-{update.Version.ToString(3)}.exe");

        await using (var source = await Client.GetStreamAsync(update.SetupUrl, cancellationToken))
        await using (var target = File.Create(setupPath))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        string checksumText = await Client.GetStringAsync(update.ChecksumUrl, cancellationToken);
        string? expected = checksumText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (expected is null || expected.Length != 64)
            throw new InvalidOperationException("The published SHA256 file is not valid.");

        string actual;
        await using (var stream = File.OpenRead(setupPath))
        {
            using var sha256 = SHA256.Create();
            actual = Convert.ToHexString(await sha256.ComputeHashAsync(stream, cancellationToken));
        }

        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(setupPath);
            throw new InvalidOperationException("The downloaded installer failed SHA256 verification.");
        }

        return setupPath;
    }

    internal static void LaunchInstaller(string setupPath)
    {
        var start = new ProcessStartInfo(setupPath)
        {
            UseShellExecute = true,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS"
        };
        _ = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the installer.");
    }
}
