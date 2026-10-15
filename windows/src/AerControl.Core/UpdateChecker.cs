using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AerControl.Core;

public sealed record AppRelease(string Version, Uri PageUrl, string? Notes, IReadOnlyDictionary<string, Uri> Assets)
{
    /// <summary>Installer for this architecture: the .appinstaller (enables Windows auto-update), else the zip, else the page.</summary>
    public Uri WindowsDownloadUrl(string arch) =>
        Assets.FirstOrDefault(a => a.Key.Equals($"AerControl-{arch}.appinstaller", StringComparison.OrdinalIgnoreCase)).Value
        ?? Assets.FirstOrDefault(a => a.Key.Equals($"AerControl-windows-{arch}.zip", StringComparison.OrdinalIgnoreCase)).Value
        ?? PageUrl;
}

/// <summary>
/// Checks GitHub Releases for a newer version. Mirrors UpdateChecker (Swift). The MSIX is
/// also installed through an .appinstaller file, so Windows itself can update in the
/// background; this check covers zip/sideload installs and shows the banner.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "parthh37/aercontrol";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AerControl-Windows", "1"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static async Task<AppRelease> LatestReleaseAsync(CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        return Parse(json);
    }

    private sealed record Asset([property: JsonPropertyName("name")] string Name,
                                [property: JsonPropertyName("browser_download_url")] Uri Url);
    private sealed record Release([property: JsonPropertyName("tag_name")] string Tag,
                                  [property: JsonPropertyName("html_url")] Uri Page,
                                  [property: JsonPropertyName("body")] string? Body,
                                  [property: JsonPropertyName("assets")] List<Asset> Assets);

    public static AppRelease Parse(string json)
    {
        var r = JsonSerializer.Deserialize<Release>(json) ?? throw new JsonException("empty release");
        var assets = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in r.Assets) assets.TryAdd(a.Name, a.Url);
        return new AppRelease(Normalize(r.Tag), r.Page, r.Body, assets);
    }

    /// <summary>"v1.2.3" → "1.2.3"; drops any "-suffix".</summary>
    public static string Normalize(string version)
    {
        var v = version.Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        return v.Split('-')[0];
    }

    /// <summary>Numeric dotted comparison; missing parts count as 0 ("1.2" == "1.2.0.0").</summary>
    public static bool IsNewer(string candidate, string current)
    {
        int[] Parts(string s) => Normalize(s).Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var a = Parts(candidate);
        var b = Parts(current);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}
