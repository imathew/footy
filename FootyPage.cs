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

public class FootyPage(
    ILogger<FootyPage> logger,
    IFootyDataService dataService,
    IMemoryCache cache)
{
    private readonly ILogger<FootyPage> _logger = logger;
    private readonly IFootyDataService _dataService = dataService;
    private readonly IMemoryCache _cache = cache;
    private static readonly string AssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");

    [Function("FootyScores")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{*path}")] HttpRequestData req,
        string? path = null)
    {
        // Handle static asset requests
        if (!string.IsNullOrEmpty(path))
        {
            return await ServeStaticAsset(req, path);
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
            }

            // Try to get cached HTML first
            var html = await _cache.GetOrCreateAsync(
                cacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(FootyConfiguration.ServerCacheSeconds);
                    _logger.LogInformation("Cache miss, generating new page");

                    // Fetch data with caching
                    var jsonData = await _cache.GetOrCreateAsync(
                        "footy_data",
                        async entry =>
                        {
                            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(FootyConfiguration.ServerCacheSeconds);
                            _logger.LogInformation("Fetching fresh data from API");
                            return await _dataService.FetchDataAsync();
                        });

                    if (string.IsNullOrEmpty(jsonData))
                    {
                        throw new InvalidOperationException("Failed to fetch data from API");
                    }

                    var round = _dataService.FindAndParseRound(jsonData, requestedRoundId, FootyConfiguration.MelbourneNow);
                    return HtmlGenerator.GenerateCompletePage(round);
                });

            if (string.IsNullOrEmpty(html))
            {
                throw new InvalidOperationException("Failed to generate page HTML");
            }

            return await CreateHtmlResponse(req, html);
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

    private static async Task<HttpResponseData> CreateHtmlResponse(HttpRequestData req, string html)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Cache-Control", $"private, max-age={FootyConfiguration.ClientCacheSeconds}");
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        response.Headers.Add("Referrer-Policy", "no-referrer");
        response.Headers.Add("Vary", "Accept-Encoding");
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");

        // Compress if supported
        var acceptEncoding = req.Headers.TryGetValues("Accept-Encoding", out var values)
            ? string.Join(",", values)
            : "";

        if (acceptEncoding.Contains("gzip"))
        {
            response.Headers.Add("Content-Encoding", "gzip");
            using var compressedStream = new MemoryStream();
            using (var gzipStream = new GZipStream(compressedStream, CompressionLevel.Optimal))
            {
                var bytes = Encoding.UTF8.GetBytes(html);
                await gzipStream.WriteAsync(bytes);
            }
            await response.WriteBytesAsync(compressedStream.ToArray());
        }
        else
        {
            await response.WriteStringAsync(html);
        }

        return response;
    }

    private async Task<HttpResponseData> ServeStaticAsset(HttpRequestData req, string assetPath)
    {
        try
        {
            var fileBytes = await GetBinaryAssetAsync(assetPath);
            if (fileBytes == null)
            {
                return req.CreateResponse(HttpStatusCode.NotFound);
            }

            if (Path.GetExtension(assetPath).Equals(".css", StringComparison.CurrentCultureIgnoreCase))
            {
                var cssContent = Encoding.UTF8.GetString(fileBytes);
                cssContent = MinifyCss(cssContent);
                fileBytes = Encoding.UTF8.GetBytes(cssContent);
            }

            var contentType = Path.GetExtension(assetPath).ToLower() switch
            {
                ".png" => "image/png",
                ".ico" => "image/x-icon",
                ".css" => "text/css",
                ".svg" => "image/svg+xml",
                ".webmanifest" => "application/manifest+json",
                _ => "application/octet-stream"
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Cache-Control", "public, max-age=2628000, immutable");
            response.Headers.Add("X-Content-Type-Options", "nosniff");
            response.Headers.Add("Referrer-Policy", "no-referrer");
            response.Headers.Add("Vary", "Accept-Encoding");
            response.Headers.Add("Content-Type", contentType);

            // Compress text-based assets
            if (contentType.StartsWith("text/") || contentType.Contains("svg") || contentType.Contains("json"))
            {
                response.Headers.Add("Content-Encoding", "gzip");
                using var compressedStream = new MemoryStream();
                using (var gzipStream = new GZipStream(compressedStream, CompressionLevel.Optimal))
                {
                    await gzipStream.WriteAsync(fileBytes);
                }
                await response.WriteBytesAsync(compressedStream.ToArray());
            }
            else
            {
                await response.WriteBytesAsync(fileBytes);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving static asset: {AssetPath}", assetPath);
            return req.CreateResponse(HttpStatusCode.InternalServerError);
        }
    }

    private static async Task<byte[]?> GetBinaryAssetAsync(string filename)
    {
        // Sanitize the path to prevent directory traversal attacks
        filename = filename.Replace("..", "").Replace("~", "").TrimStart('/');
        var filePath = Path.Combine(AssetsPath, filename);

        // Ensure the resolved path is still under AssetsPath
        var fullPath = Path.GetFullPath(filePath);
        var assetsFullPath = Path.GetFullPath(AssetsPath);

        if (!fullPath.StartsWith(assetsFullPath))
        {
            return null; // Attempted directory traversal
        }

        if (File.Exists(fullPath))
        {
            return await File.ReadAllBytesAsync(fullPath);
        }
        return null;
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