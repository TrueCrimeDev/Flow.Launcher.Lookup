using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lookup.Services;

/// <summary>
/// Fetches and caches site favicons on disk. The plugin's only network path, and the
/// only reason it is not fully offline — <c>offline_icons</c> disables it entirely.
///
/// Icons are fetched first-party: the request goes to the site the link already points
/// at, never to a third-party favicon service that would learn what the user opens.
/// Bytes are stored raw under a sniffed extension, so nothing here has to decode an
/// image — Flow's ImageLoader accepts .png and .ico directly.
/// </summary>
public sealed class FaviconCache
{
    private const string IconsFolder = "icons";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly string _iconsDirectory;
    private readonly HttpClient _http;

    /// <summary>Hosts that failed this session. Results re-resolve icons on every
    /// keystroke, so without this one dead link would become a request storm.</summary>
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>In-flight fetches, so concurrent results for one host share a request.</summary>
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public FaviconCache(string settingsDirectory, HttpMessageHandler? handler = null)
    {
        _iconsDirectory = Path.Combine(settingsDirectory ?? "", IconsFolder);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout;
    }

    /// <summary>Path to the cached icon for this URL's host, or null when there is none.
    /// Pure lookup: never fetches, so it is safe on the query hot path.</summary>
    public string? TryGetCached(string url)
    {
        var host = HostOf(url);
        if (host is null || !Directory.Exists(_iconsDirectory))
            return null;

        foreach (var extension in new[] { ".png", ".ico", ".jpg", ".gif", ".bmp" })
        {
            var candidate = Path.Combine(_iconsDirectory, host + extension);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Fetches the favicon if it is not cached already. Never throws: a failed
    /// fetch leaves the link on its glyph fallback.</summary>
    public Task EnsureAsync(string url, CancellationToken cancellationToken)
    {
        var host = HostOf(url);
        if (host is null || _failed.ContainsKey(host) || TryGetCached(url) is not null)
            return Task.CompletedTask;

        return _inFlight.GetOrAdd(host, h => FetchAsync(h, cancellationToken));
    }

    private async Task FetchAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _http.GetAsync($"https://{host}/favicon.ico", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _failed.TryAdd(host, 0);
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var extension = SniffExtension(bytes);
            if (extension is null)
            {
                // An HTML error page or an empty body: caching it would show a broken
                // icon forever, so treat it as a failure.
                _failed.TryAdd(host, 0);
                return;
            }

            Directory.CreateDirectory(_iconsDirectory);

            // Write beside the target and move into place, so a torn write can never be
            // observed as a valid cache entry.
            var destination = Path.Combine(_iconsDirectory, host + extension);
            var temporary = destination + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception)
        {
            // DNS failure, timeout, TLS error, disk error — all mean the same thing here:
            // no icon this session.
            _failed.TryAdd(host, 0);
        }
        finally
        {
            _inFlight.TryRemove(host, out _);
        }
    }

    /// <summary>Host of a http/https URL, lowercased; null for anything else.</summary>
    private static string? HostOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
            return null;

        var host = uri.Host.ToLowerInvariant();
        return host.Any(c => Path.GetInvalidFileNameChars().Contains(c)) ? null : host;
    }

    /// <summary>Extension implied by the leading magic bytes, or null when the body is
    /// not an image format Flow can load.</summary>
    private static string? SniffExtension(byte[] bytes)
    {
        if (bytes.Length < 4)
            return null;

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00) return ".ico";
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) return ".jpg";
        if (bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F') return ".gif";
        if (bytes[0] == (byte)'B' && bytes[1] == (byte)'M') return ".bmp";

        return null;
    }
}
