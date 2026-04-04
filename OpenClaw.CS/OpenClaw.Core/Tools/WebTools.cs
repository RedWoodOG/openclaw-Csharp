namespace OpenClaw.Tools;

using OpenClaw.Core;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Web fetch tool with SSRF protection.
/// </summary>
public sealed class WebFetchTool : ToolBase<WebFetchParams>
{
    private readonly HttpClient _httpClient;
    
    public override string Name => "webfetch";
    public override string Description => "Fetch content from a URL with SSRF protection";
    
    public WebFetchTool(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }
    
    protected override async Task<ToolResult> ExecuteCoreAsync(WebFetchParams p, CancellationToken ct)
    {
        // SSRF validation
        var ssrfResult = await ValidateUrlAsync(p.Url, ct);
        if (!ssrfResult.IsValid)
            return ToolResult.Fail($"SSRF protection: {ssrfResult.Reason}");
        
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(p.TimeoutMs));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            var request = new HttpRequestMessage(HttpMethod.Get, p.Url);
            request.Headers.Add("User-Agent", "OpenClaw/1.0");
            
            var response = await _httpClient.SendAsync(request, linkedCts.Token);
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync(linkedCts.Token);
            
            if (p.ExtractText && IsHtmlContent(response))
                content = ExtractTextFromHtml(content);
            
            if (content.Length > p.MaxLength)
                content = content.Substring(0, p.MaxLength) + "\n... (truncated)";
            
            return ToolResult.Ok(content);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail($"Request timed out after {p.TimeoutMs}ms");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Fetch failed: {ex.Message}", ex);
        }
    }
    
    private static async Task<SsrfResult> ValidateUrlAsync(string url, CancellationToken ct)
    {
        Uri uri;
        try { uri = new Uri(url); }
        catch { return new SsrfResult(false, "Invalid URL format"); }
        
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return new SsrfResult(false, "Only HTTP/HTTPS allowed");
        
        var blockedHosts = new[] { "localhost", "127.", "169.254.", "10.", "172.16.", "192.168.", "metadata.google.internal" };
        var host = uri.Host.ToLowerInvariant();
        
        if (blockedHosts.Any(h => host.StartsWith(h) || host == h))
            return new SsrfResult(false, "Private/internal host blocked");
        
        return new SsrfResult(true, null);
    }
    
    private static bool IsHtmlContent(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType?.Contains("html") == true;
    
    private static string ExtractTextFromHtml(string html)
    {
        html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", " ");
        html = System.Web.HttpUtility.HtmlDecode(html);
        return Regex.Replace(html, @"\s+", " ").Trim();
    }
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            url = new { type = "string", description = "URL to fetch" },
            timeout_ms = new { type = "integer", description = "Timeout in milliseconds" },
            max_length = new { type = "integer", description = "Maximum content length" },
            extract_text = new { type = "boolean", description = "Extract text from HTML" }
        },
        required = new[] { "url" }
    });
}

public sealed record SsrfResult(bool IsValid, string? Reason);

public sealed class WebFetchParams
{
    public required string Url { get; init; }
    public int TimeoutMs { get; init; } = 30000;
    public int MaxLength { get; init; } = 50000;
    public bool ExtractText { get; init; } = true;
}

/// <summary>
/// Web search tool.
/// </summary>
public sealed class WebSearchTool : ToolBase<WebSearchParams>
{
    private readonly HttpClient _httpClient;
    private readonly WebSearchConfig? _config;
    
    public override string Name => "websearch";
    public override string Description => "Search the web using DuckDuckGo";
    
    public WebSearchTool(HttpClient? httpClient = null, WebSearchConfig? config = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _config = config;
    }
    
    protected override async Task<ToolResult> ExecuteCoreAsync(WebSearchParams p, CancellationToken ct)
    {
        try
        {
            // DuckDuckGo Instant Answer API
            var url = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(p.Query)}&format=json&no_html=1";
            var response = await _httpClient.GetStringAsync(url, ct);
            
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            
            var results = new StringBuilder();
            
            // Abstract
            if (root.TryGetProperty("Abstract", out var abstractProp) && abstractProp.ValueKind == JsonValueKind.String)
            {
                var abstractText = abstractProp.GetString();
                if (!string.IsNullOrEmpty(abstractText))
                {
                    results.AppendLine($"Summary: {abstractText}");
                    if (root.TryGetProperty("AbstractURL", out var urlProp))
                        results.AppendLine($"Source: {urlProp.GetString()}");
                    results.AppendLine();
                }
            }
            
            // Related topics
            if (root.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
            {
                var count = 0;
                foreach (var topic in topics.EnumerateArray().Take(p.MaxResults))
                {
                    if (topic.TryGetProperty("Text", out var textProp) &&
                        topic.TryGetProperty("FirstURL", out var urlProp))
                    {
                        results.AppendLine($"[{++count}] {textProp.GetString()}");
                        results.AppendLine($"    URL: {urlProp.GetString()}");
                        results.AppendLine();
                    }
                }
            }
            
            return results.Length > 0
                ? ToolResult.Ok(results.ToString())
                : ToolResult.Ok("No results found");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Search failed: {ex.Message}", ex);
        }
    }
    
    public override JsonElement? GetInputSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Search query" },
            max_results = new { type = "integer", description = "Maximum number of results" }
        },
        required = new[] { "query" }
    });
}

public sealed class WebSearchConfig
{
    public string? GoogleApiKey { get; init; }
    public string? GoogleSearchEngineId { get; init; }
    public string? BingApiKey { get; init; }
}

public sealed class WebSearchParams
{
    public required string Query { get; init; }
    public int MaxResults { get; init; } = 5;
}
