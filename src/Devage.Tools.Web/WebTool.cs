using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Devage.Tools.Abstractions;

namespace Devage.Tools.Web;

/// <summary>
/// Read-only HTTP fetch tool for documentation / reference pages. No external writes.
/// </summary>
public sealed class WebTool : IDevageTool
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IHttpClientFactory? _httpClientFactory;

    public WebTool(IHttpClientFactory? httpClientFactory = null)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "web";

    public string Description =>
        "Fetch HTTP documentation and reference pages (Stack Overflow–like). Returns citable text. No external writes.";

    public IReadOnlyList<ToolConfigField> ConfigFields { get; } =
    [
        new("userAgent", "User-Agent", "DevageWebTool/1.0", false),
        new("maxBytes", "Max response bytes", "200000", false),
        new("timeoutSeconds", "Timeout seconds", "30", false)
    ];

    public string DescribeSchema() =>
        """
        {
          "type": "object",
          "required": ["url"],
          "properties": {
            "url": { "type": "string", "description": "HTTP(S) URL to fetch" },
            "query": { "type": "string", "description": "Optional focus query for excerpting" }
          }
        }
        """;

    public Task ValidateConfigAsync(JsonElement config, CancellationToken cancellationToken = default)
    {
        if (config.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Task.CompletedTask;
        }

        if (config.TryGetProperty("maxBytes", out var maxBytes) &&
            maxBytes.ValueKind == JsonValueKind.Number &&
            maxBytes.GetInt32() <= 0)
        {
            throw new ArgumentException("maxBytes must be positive.");
        }

        return Task.CompletedTask;
    }

    public async Task<ToolResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!context.Input.TryGetProperty("url", out var urlProp) ||
            urlProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(urlProp.GetString()))
        {
            return ToolResult.Fail("Missing required input: url");
        }

        var urlText = urlProp.GetString()!;
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ToolResult.Fail("url must be an absolute http(s) URL.");
        }

        var userAgent = GetConfigString(context.Config, "userAgent", "DevageWebTool/1.0");
        var maxBytes = GetConfigInt(context.Config, "maxBytes", 200_000);
        var timeoutSeconds = GetConfigInt(context.Config, "timeoutSeconds", 30);
        var query = context.Input.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString()
            : null;

        using var client = _httpClientFactory?.CreateClient("devage-web") ?? new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/json,text/plain,*/*");

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ToolResult.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var limited = new LimitedReadStream(stream, maxBytes);
            using var reader = new StreamReader(limited, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var raw = await reader.ReadToEndAsync(cancellationToken);
            var text = HtmlToText(raw);
            if (!string.IsNullOrWhiteSpace(query))
            {
                text = ExcerptAroundQuery(text, query!, 1200);
            }
            else if (text.Length > 6000)
            {
                text = text[..6000] + "\n…[truncated]";
            }

            var citation = $"Source: {uri}";
            var output = $"{citation}\nFetched: {DateTimeOffset.UtcNow:O}\n\n{text}";
            return ToolResult.Ok(output, new Dictionary<string, object?>
            {
                ["url"] = uri.ToString(),
                ["status"] = (int)response.StatusCode,
                ["contentType"] = response.Content.Headers.ContentType?.ToString()
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return ToolResult.Fail($"Fetch failed: {ex.Message}");
        }
    }

    private static string GetConfigString(JsonElement config, string key, string fallback)
    {
        if (config.ValueKind == JsonValueKind.Object &&
            config.TryGetProperty(key, out var prop) &&
            prop.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(prop.GetString()))
        {
            return prop.GetString()!;
        }

        return fallback;
    }

    private static int GetConfigInt(JsonElement config, string key, int fallback)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(key, out var prop))
        {
            return fallback;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.GetInt32(),
            JsonValueKind.String when int.TryParse(prop.GetString(), out var n) => n,
            _ => fallback
        };
    }

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var withoutScripts = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        var withoutTags = Regex.Replace(withoutScripts, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string ExcerptAroundQuery(string text, string query, int window)
    {
        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return text.Length <= window ? text : text[..window] + "\n…[truncated]";
        }

        var start = Math.Max(0, idx - window / 3);
        var length = Math.Min(window, text.Length - start);
        var excerpt = text.Substring(start, length);
        if (start > 0)
        {
            excerpt = "…" + excerpt;
        }

        if (start + length < text.Length)
        {
            excerpt += "…";
        }

        return excerpt;
    }

    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _max;
        private long _read;

        public LimitedReadStream(Stream inner, long max)
        {
            _inner = inner;
            _max = max;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= _max)
            {
                return 0;
            }

            var remaining = (int)Math.Min(count, _max - _read);
            var n = _inner.Read(buffer, offset, remaining);
            _read += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
