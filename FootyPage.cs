using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FootyScores;

internal class FootyPage(
    ILogger<FootyPage> logger,
    IFootyDataService dataService,
    IMemoryCache cache)
{
    private readonly ILogger<FootyPage> _logger = logger;
    private readonly IFootyDataService _dataService = dataService;
    private readonly IMemoryCache _cache = cache;
    private static readonly string AssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
    private static readonly string AssetsFullPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets"));
    private record AssetCache(byte[] Raw, byte[]? Gzip);

    [Function("FootyScores")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{*path}")] HttpRequestData req,
        string? path = null)
    {
        // Handle warmup request
        if (path == "warmup")
        {
            _logger.LogInformation("Warmup endpoint called");
            var warmupResponse = req.CreateResponse(HttpStatusCode.OK);
            warmupResponse.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            await warmupResponse.WriteStringAsync("OK");
            return warmupResponse;
        }

        // Handle static asset requests (with or without version query string)
        if (!string.IsNullOrEmpty(path))
        {
            // Strip version query parameter if present
            var assetPath = path.Split('?')[0];
            return await ServeStaticAsset(req, assetPath);
        }

        // Handle main page request
        int? requestedRoundId = null;
        var roundStr = req.Query["round"];
        if (!string.IsNullOrEmpty(roundStr) && int.TryParse(roundStr, out var roundId))
        {
            requestedRoundId = roundId;
        }

        bool forceRefresh = req.Query["refresh"] == "true";

        try
        {
            var cacheKey = $"page_{requestedRoundId ?? 0}";

            if (forceRefresh)
            {
                _logger.LogInformation("Force refresh requested, invalidating cache");
                _cache.Remove(cacheKey);
                _cache.Remove("footy_data");
            }

            // Try to get cached gzip-compressed HTML first
            var gzipHtml = await _cache.GetOrCreateAsync(
                cacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(FootyConfiguration.ServerCacheSeconds);
                    _logger.LogInformation("Cache miss, generating new page");

                    // Fetch data with caching
                    var rounds = await _cache.GetOrCreateAsync(
                        "footy_data",
                        async entry =>
                        {
                            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(FootyConfiguration.ServerCacheSeconds);
                            _logger.LogInformation("Fetching fresh data from API");
                            return await _dataService.FetchDataAsync();
                        });

                    if (rounds == null || rounds.Count == 0)
                    {
                        throw new InvalidOperationException("Failed to fetch data from API");
                    }

                    var round = _dataService.FindAndParseRound(rounds, requestedRoundId, FootyConfiguration.MelbourneNow);
                    var html = HtmlGenerator.GenerateCompletePage(round, FootyConfiguration.AssetVersion);
                    return Compress(Encoding.UTF8.GetBytes(html));
                });

            if (gzipHtml == null)
            {
                throw new InvalidOperationException("Failed to generate page HTML");
            }

            return await CreateHtmlResponse(req, gzipHtml);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing footy scores request");
            var errorHtml = HtmlGenerator.GenerateErrorPage($"Unable to load footy scores: {ex.Message}");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(errorHtml);
            return errorResponse;
        }
    }

    private static async Task<HttpResponseData> CreateHtmlResponse(HttpRequestData req, byte[] gzipHtml)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Cache-Control", $"private, max-age={FootyConfiguration.ClientCacheSeconds}");
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        response.Headers.Add("Referrer-Policy", "no-referrer");
        response.Headers.Add("Vary", "Accept-Encoding");
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");

        var acceptEncoding = req.Headers.TryGetValues("Accept-Encoding", out var values)
            ? string.Join(",", values)
            : "";

        if (acceptEncoding.Contains("gzip"))
        {
            response.Headers.Add("Content-Encoding", "gzip");
            await response.WriteBytesAsync(gzipHtml);
        }
        else
        {
            // Rare: decompress for clients that don't support gzip
            using var compressed = new MemoryStream(gzipHtml);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            await response.WriteStringAsync(await reader.ReadToEndAsync());
        }

        return response;
    }

    private async Task<HttpResponseData> ServeStaticAsset(HttpRequestData req, string assetPath)
    {
        try
        {
            var asset = await GetOrBuildAssetAsync(assetPath);
            if (asset == null)
                return req.CreateResponse(HttpStatusCode.NotFound);

            var contentType = Path.GetExtension(assetPath).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".ico" => "image/x-icon",
                ".css" => "text/css",
                ".svg" => "image/svg+xml",
                ".webmanifest" => "application/manifest+json",
                _ => "application/octet-stream"
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Cache-Control", "public, max-age=31536000, immutable");
            response.Headers.Add("X-Content-Type-Options", "nosniff");
            response.Headers.Add("Referrer-Policy", "no-referrer");
            response.Headers.Add("Vary", "Accept-Encoding");
            response.Headers.Add("Content-Type", contentType);

            var acceptEncoding = req.Headers.TryGetValues("Accept-Encoding", out var encValues)
                ? string.Join(",", encValues)
                : "";

            if (asset.Gzip != null && acceptEncoding.Contains("gzip"))
            {
                response.Headers.Add("Content-Encoding", "gzip");
                await response.WriteBytesAsync(asset.Gzip);
            }
            else
            {
                await response.WriteBytesAsync(asset.Raw);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving static asset: {AssetPath}", assetPath);
            return req.CreateResponse(HttpStatusCode.InternalServerError);
        }
    }

    private async Task<AssetCache?> GetOrBuildAssetAsync(string filename)
    {
        // Sanitize before using as cache key
        filename = filename.Replace("..", "").Replace("~", "").TrimStart('/');

        return await _cache.GetOrCreateAsync($"asset_{filename}", async entry =>
        {
            entry.Priority = CacheItemPriority.NeverRemove;

            var fullPath = Path.GetFullPath(Path.Combine(AssetsPath, filename));
            if (!fullPath.StartsWith(AssetsFullPath) || !File.Exists(fullPath))
                return null;

            var rawBytes = await File.ReadAllBytesAsync(fullPath);
            var ext = Path.GetExtension(filename).ToLowerInvariant();

            if (ext == ".css")
            {
                rawBytes = Encoding.UTF8.GetBytes(MinifyCss(Encoding.UTF8.GetString(rawBytes)));
            }

            byte[]? gzipBytes = ext is ".css" or ".svg" or ".webmanifest"
                ? Compress(rawBytes)
                : null;

            return new AssetCache(rawBytes, gzipBytes);
        });
    }

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Optimal))
            gzip.Write(data);
        return ms.ToArray();
    }

    private static string MinifyCss(string css)
    {
        if (string.IsNullOrWhiteSpace(css))
            return css;

        // Remove comments
        css = Regex.Replace(css, @"/\*[\s\S]*?\*/", "");

        // Remove unnecessary whitespace
        css = Regex.Replace(css, @"\s+", " ");

        // Remove spaces around specific characters
        css = Regex.Replace(css, @"\s*([{}:;,>+~])\s*", "$1");

        // Remove trailing semicolon before closing brace
        css = css.Replace(";}", "}");

        // Remove quotes from url() when possible
        css = Regex.Replace(css, @"url\([""']([^""']+)[""']\)", "url($1)");

        // Trim
        return css.Trim();
    }
}