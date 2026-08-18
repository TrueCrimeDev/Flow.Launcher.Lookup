using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lookup.Services;
using Xunit;

namespace Lookup.Tests;

/// <summary>
/// The plugin's only network path. Tests drive it through a stub handler — no live
/// fetching — and focus on what must not go wrong: junk never reaches the cache, and a
/// dead host is not retried on every keystroke.
/// </summary>
public sealed class FaviconCacheTests
{
    private static readonly byte[] PngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
    private static readonly byte[] IcoBytes = { 0x00, 0x00, 0x01, 0x00, 1, 0, 16, 16, 0, 0 };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int Calls;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lookup-favicon-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TryGetCached_WithNothingCached_ReturnsNull()
    {
        var cache = new FaviconCache(NewDir());

        Assert.Null(cache.TryGetCached("https://github.com"));
    }

    [Fact]
    public void TryGetCached_InvalidUrl_ReturnsNull()
    {
        var cache = new FaviconCache(NewDir());

        Assert.Null(cache.TryGetCached("not a url"));
    }

    [Fact]
    public async Task EnsureAsync_FetchesAndCachesAPng()
    {
        var dir = NewDir();
        var handler = new StubHandler(_ => Ok(PngBytes));
        var cache = new FaviconCache(dir, handler);

        await cache.EnsureAsync("https://github.com", CancellationToken.None);

        var cached = cache.TryGetCached("https://github.com");
        Assert.NotNull(cached);
        Assert.Equal(".png", Path.GetExtension(cached));
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(cached!));
    }

    [Fact]
    public async Task EnsureAsync_SniffsIcoContent()
    {
        var dir = NewDir();
        var cache = new FaviconCache(dir, new StubHandler(_ => Ok(IcoBytes)));

        await cache.EnsureAsync("https://example.com", CancellationToken.None);

        Assert.Equal(".ico", Path.GetExtension(cache.TryGetCached("https://example.com")));
    }

    [Fact]
    public async Task EnsureAsync_RequestsTheSiteItself()
    {
        Uri? requested = null;
        var handler = new StubHandler(r => { requested = r.RequestUri; return Ok(PngBytes); });

        await new FaviconCache(NewDir(), handler).EnsureAsync("https://acme.example.com/browse/", CancellationToken.None);

        // First-party only: the site the link already points at, never a third-party
        // favicon service that would learn what the user opens.
        Assert.Equal("acme.example.com", requested!.Host);
        Assert.Equal("/favicon.ico", requested.AbsolutePath);
    }

    [Fact]
    public async Task EnsureAsync_NonImageBody_CachesNothing()
    {
        var dir = NewDir();
        var cache = new FaviconCache(dir, new StubHandler(_ => Ok(new byte[] { (byte)'<', (byte)'h', (byte)'t' })));

        await cache.EnsureAsync("https://github.com", CancellationToken.None);

        Assert.Null(cache.TryGetCached("https://github.com"));
    }

    [Fact]
    public async Task EnsureAsync_EmptyBody_CachesNothing()
    {
        var cache = new FaviconCache(NewDir(), new StubHandler(_ => Ok(Array.Empty<byte>())));

        await cache.EnsureAsync("https://github.com", CancellationToken.None);

        Assert.Null(cache.TryGetCached("https://github.com"));
    }

    [Fact]
    public async Task EnsureAsync_HttpError_IsSwallowed()
    {
        var cache = new FaviconCache(NewDir(), new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        await cache.EnsureAsync("https://github.com", CancellationToken.None);

        Assert.Null(cache.TryGetCached("https://github.com"));
    }

    [Fact]
    public async Task EnsureAsync_ThrowingHandler_IsSwallowed()
    {
        var cache = new FaviconCache(NewDir(), new StubHandler(_ => throw new HttpRequestException("dns")));

        await cache.EnsureAsync("https://nope.invalid", CancellationToken.None);

        Assert.Null(cache.TryGetCached("https://nope.invalid"));
    }

    [Fact]
    public async Task EnsureAsync_AfterAFailure_DoesNotRetryTheSameHost()
    {
        // Query results re-resolve icons on every keystroke; retrying a dead host each
        // time would turn one bad link into a request storm.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var cache = new FaviconCache(NewDir(), handler);

        for (var i = 0; i < 5; i++)
            await cache.EnsureAsync("https://github.com", CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EnsureAsync_WhenAlreadyCached_DoesNotFetchAgain()
    {
        var handler = new StubHandler(_ => Ok(PngBytes));
        var cache = new FaviconCache(NewDir(), handler);

        await cache.EnsureAsync("https://github.com", CancellationToken.None);
        await cache.EnsureAsync("https://github.com", CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EnsureAsync_SameHostConcurrently_FetchesOnce()
    {
        var handler = new StubHandler(_ => Ok(PngBytes));
        var cache = new FaviconCache(NewDir(), handler);

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => cache.EnsureAsync("https://github.com", CancellationToken.None)));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EnsureAsync_InvalidUrl_DoesNotFetch()
    {
        var handler = new StubHandler(_ => Ok(PngBytes));

        await new FaviconCache(NewDir(), handler).EnsureAsync("mailto:someone@example.com", CancellationToken.None);

        Assert.Equal(0, handler.Calls);
    }
}
