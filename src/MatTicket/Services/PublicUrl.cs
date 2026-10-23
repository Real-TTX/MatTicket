using MatTicket.Configuration;

namespace MatTicket.Services;

/// <summary>
/// Absolute addresses for links in mails and for robots.txt: the public address of the configuration, else the address of the request, else the
/// address of the last request the app saw (mails are written in the background, without a request).
/// </summary>
public sealed class PublicUrl(ConfigStore config, IHttpContextAccessor accessor)
{
    private static volatile string? _lastSeen;

    /// <summary>Called for every request: remembers how the app is reached.</summary>
    public static void Remember(HttpContext http) => _lastSeen = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";

    public static string Base(ConfigStore config, HttpContext? http = null)
    {
        var configured = config.Current.Server.PublicUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return http is not null ? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}" : _lastSeen ?? "http://localhost:2740";
    }

    public string Absolute(string path) => Base(config, accessor.HttpContext) + (path.StartsWith('/') ? path : "/" + path);
}
