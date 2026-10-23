using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MatTicket.UiKit;

/// <summary>
/// The files of the toolkit and what the layout needs from them: the icon sprite (the toolkit's plus the app's own icons), placed inline at the top of
/// every page, and a hash of the toolkit files for cache busting. The files are copies (scripts/sync-matui.ps1): an app never loads them from another site.
/// </summary>
public sealed partial class IconSprite
{
    public IconSprite(IWebHostEnvironment env)
    {
        var root = env.WebRootFileProvider;
        string Read(string path)
        {
            using var stream = root.GetFileInfo(path).CreateReadStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        var toolkit = Read("ui/matui-icons.svg");
        MatUiVersion = VersionRegex().Match(Read("ui/matui.manifest.json")).Groups[1].Value;
        var own = Read("ui/mt-icons.svg");
        Names = [.. SymbolRegex().Matches(toolkit + own).Select(m => m.Groups[1].Value).Order()];

        // Both sprites are one page's worth of markup: strip the second <svg> wrapper and put its symbols into the first.
        var symbols = own[(own.IndexOf('>') + 1)..own.LastIndexOf("</svg>", StringComparison.Ordinal)];
        Html = toolkit.Replace("</svg>", symbols + "</svg>", StringComparison.Ordinal);

        var all = Read("ui/matui.css") + Read("ui/matui.js") + Read("ui/matui.boot.js") + Html;
        Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all)))[..10].ToLowerInvariant();
    }

    public string Html { get; }

    public IReadOnlyList<string> Names { get; }

    public string Hash { get; }

    /// <summary>The version of the toolkit copy (from its manifest).</summary>
    public string MatUiVersion { get; }

    [GeneratedRegex("\"version\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex VersionRegex();

    [GeneratedRegex("id=\"mat-i-([a-z0-9-]+)\"")]
    private static partial Regex SymbolRegex();
}
