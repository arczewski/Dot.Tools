using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

internal static class FetchConfiguration
{
    public static readonly string JinaApiUrl =
        Environment.GetEnvironmentVariable("JINA_API_URL") ?? "https://r.jina.ai/{url}";

    public static readonly string JinaApiToken =
        Environment.GetEnvironmentVariable("JINA_API_TOKEN") ?? "";

    public static readonly string SearxngUrl =
        Environment.GetEnvironmentVariable("SEARXNG_URL") ?? "http://127.0.0.1:8888";

    public static readonly int RequestTimeout =
        int.TryParse(Environment.GetEnvironmentVariable("REQUEST_TIMEOUT"), out var rt) ? rt : 10;

    public static readonly int MaxUrls =
        int.TryParse(Environment.GetEnvironmentVariable("MAX_URLS"), out var mu) ? mu : 3;

    public static readonly double DelayBetweenRequests =
        double.TryParse(Environment.GetEnvironmentVariable("DELAY_BETWEEN_REQUESTS"), out var d) ? d : 0.5;

    public static readonly int MaxSearchResults =
        int.TryParse(Environment.GetEnvironmentVariable("MAX_SEARCH_RESULTS"), out var msr) ? msr : 10;
}

[McpServerToolType]
public static class FetchTools
{
    private static readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(FetchConfiguration.RequestTimeout + 5)
    };

    [McpServerTool]
    [Description(
        "Fetch one or more URLs and return their content as markdown. " +
        $"Maximum {nameof(FetchConfiguration.MaxUrls)} URLs per request. " +
        "Requests are spaced out to avoid rate limiting.")]
    public static async Task<string> Fetch(string[]? urls)
    {
        if (urls == null || urls.Length == 0)
            return "No URLs provided.";

        if (urls.Length > FetchConfiguration.MaxUrls)
            return $"Too many URLs: {urls.Length}. Maximum allowed is {FetchConfiguration.MaxUrls}.";

        var results = new List<(string content, string source, bool error)>();

        for (int i = 0; i < urls.Length; i++)
        {
            if (i > 0)
                await Task.Delay(TimeSpan.FromSeconds(FetchConfiguration.DelayBetweenRequests));

            var result = await FetchSingleUrl(urls[i]);
            results.Add(result);
        }

        var sb = new StringBuilder();
        foreach (var (content, source, error) in results)
        {
            sb.AppendLine($"## URL: {source}");
            if (error)
            {
                sb.AppendLine($"*Error: {content}*");
            }
            else
            {
                sb.AppendLine(content);
            }
            sb.AppendLine("\n---\n");
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool]
    [Description(
        "Search the web using a SearXNG instance. Returns compact results with title, URL, and snippet. " +
        "Use maxResults to limit output tokens. Use snippetMaxLength to truncate long snippets.")]
    public static async Task<string> Search(
        string query,
        [Description("Optional: max number of results to return. Default 5. Lower = less tokens in response.")]
        int maxResults = 5,
        [Description("Optional: max characters per snippet. Default 150. Truncates long snippets to save tokens.")]
        int snippetMaxLength = 150,
        [Description("Optional list of engines (e.g. google, bing, duckduckgo). Leave empty for default.")]
        string[]? engines = null)
    {
        var limit = Math.Min(maxResults, FetchConfiguration.MaxSearchResults);

        var queryParams = new Dictionary<string, string>
        {
            { "q", query },
            { "format", "json" },
            { "num_results", limit.ToString() }
        };

        if (engines != null && engines.Length > 0)
            queryParams["engines"] = string.Join(",", engines);

        var uriBuilder = new UriBuilder(FetchConfiguration.SearxngUrl + "/search");
        uriBuilder.Query = string.Join("&",
            queryParams.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        try
        {
            var response = await httpClient.GetAsync(uriBuilder.Uri);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var results = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
            if (results.Count == 0)
                return $"No results found for: {query}";

            if (results.Count > limit)
                results = results.Take(limit).ToList();

            var sb = new StringBuilder();
            int i = 1;
            foreach (var r in results)
            {
                var title = r.TryGetProperty("title", out var t) ? t.GetString() : "No title";
                var url = r.TryGetProperty("url", out var u) ? u.GetString() : "";
                var snippet = r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

                if (snippet.Length > snippetMaxLength)
                    snippet = snippet[..snippetMaxLength] + "...";

                sb.AppendLine($"{i}. {title}");
                sb.AppendLine($"   {url}");
                if (!string.IsNullOrWhiteSpace(snippet))
                    sb.AppendLine($"   {snippet}");
                i++;
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Search error: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Return information about the fetch server configuration.")]
    public static string GetFetchInfo()
    {
        return $"Max URLs: {FetchConfiguration.MaxUrls}\n" +
               $"Delay between requests: {FetchConfiguration.DelayBetweenRequests * 1000} ms\n" +
               $"SearXNG URL: {FetchConfiguration.SearxngUrl}\n" +
               $"Jina token configured: {!string.IsNullOrEmpty(FetchConfiguration.JinaApiToken)}\n" +
               $"Max search results: {FetchConfiguration.MaxSearchResults}";
    }

    private static async Task<(string content, string source, bool error)> FetchSingleUrl(string url)
    {
        var jinaUrl = FetchConfiguration.JinaApiUrl.Replace("{url}", Uri.EscapeDataString(url));
        using var request = new HttpRequestMessage(HttpMethod.Get, jinaUrl);

        request.Headers.Add("X-Return-Format", "markdown");
        request.Headers.Add("X-Timeout", FetchConfiguration.RequestTimeout.ToString());

        if (!string.IsNullOrEmpty(FetchConfiguration.JinaApiToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FetchConfiguration.JinaApiToken);

        try
        {
            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return (content, url, false);
        }
        catch (Exception ex)
        {
            return ($"Error fetching {url}: {ex.Message}", url, true);
        }
    }
}