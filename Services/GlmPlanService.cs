using System.IO;
using System.Text.Json;

namespace TurzxDisplay.Services;

/// <summary>One quota window (e.g. 5-hour credits, weekly credits) of the GLM Coding Plan.</summary>
public sealed record QuotaLimit(
    string Type, int Number, int Unit,
    long Total, long Used, long Remaining, double UsedPct,
    DateTimeOffset? ResetAt)
{
    /// <summary>Window name decoded from the (number, unit) pair; falls back to the raw type.</summary>
    public string WindowName
    {
        get
        {
            if (Unit == 3 && Number == 5) return "5 小时额度";
            if (Unit == 6) return Number == 1 ? "一周额度" : $"{Number} 周额度";
            return Type switch
            {
                "TOKENS_LIMIT" => "5 小时额度",
                "TIME_LIMIT" => "MCP 月度",
                _ => "额度",
            };
        }
    }

}

public sealed record PlanQuota(string Level, IReadOnlyList<QuotaLimit> Limits, DateTime FetchedAt);

public sealed record ModelBucket(DateTime Time, long Tokens);

public sealed record ModelUsage(
    IReadOnlyList<ModelBucket> Buckets, long TotalTokens,
    IReadOnlyList<(string Name, long Tokens)> Models, DateTime FetchedAt);

/// <summary>
/// GLM Coding Plan (BigModel 智谱) usage/quota API — same endpoints the official
/// glm-plan-usage plugin calls:
///   GET {base}/api/monitor/usage/quota/limit          (no params)
///   GET {base}/api/monitor/usage/model-usage?startTime&endTime
/// Auth header is the RAW ANTHROPIC_AUTH_TOKEN (no "Bearer" prefix).
/// </summary>
public static class GlmPlanService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public const string DefaultBaseUrl = "https://open.bigmodel.cn";

    public sealed record Credentials(string Token, string BaseUrl, string Source);

    // ---------------- credentials ----------------

    /// <summary>Manual token (app settings) &gt; process env &gt; ~/.claude/settings.json.</summary>
    public static Credentials? ResolveCredentials(SettingsService settings)
    {
        var manual = settings.Settings.GlmToken?.Trim();
        if (!string.IsNullOrEmpty(manual))
            return new Credentials(manual, DefaultBaseUrl, "手动配置");

        var envToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
            return new Credentials(envToken, BaseFromUrl(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL")), "环境变量");

        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("env", out var env) &&
                    env.TryGetProperty("ANTHROPIC_AUTH_TOKEN", out var tok) &&
                    tok.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(tok.GetString()))
                {
                    string? baseUrl = null;
                    if (env.TryGetProperty("ANTHROPIC_BASE_URL", out var b) && b.ValueKind == JsonValueKind.String)
                        baseUrl = b.GetString();
                    return new Credentials(tok.GetString()!, BaseFromUrl(baseUrl), "Claude Code 配置");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"read claude settings failed: {ex.Message}");
        }
        return null;
    }

    private static string BaseFromUrl(string? anthropicUrl)
    {
        if (!string.IsNullOrEmpty(anthropicUrl) && Uri.TryCreate(anthropicUrl, UriKind.Absolute, out var uri))
            return uri.GetLeftPart(UriPartial.Authority);   // scheme + host
        return DefaultBaseUrl;
    }

    // ---------------- fetch ----------------

    public static async Task<PlanQuota?> FetchQuotaAsync(Credentials c)
    {
        var root = await GetJsonAsync($"{c.BaseUrl}/api/monitor/usage/quota/limit", c.Token);
        if (root is not { } r) return null;
        if (!r.TryGetProperty("data", out var data)) return null;

        string level = Str(data, "level") ?? "";
        var limits = new List<QuotaLimit>();
        if (data.TryGetProperty("limits", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                long total = Long(item, "usage");
                long used = Long(item, "currentValue");
                long remaining = item.TryGetProperty("remaining", out var remEl) && remEl.ValueKind == JsonValueKind.Number
                    ? remEl.GetInt64() : Math.Max(0, total - used);
                double pct = item.TryGetProperty("percentage", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetDouble() : (total > 0 ? 100.0 * used / total : 0);
                DateTimeOffset? reset = item.TryGetProperty("nextResetTime", out var nr) && nr.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds(nr.GetInt64()).ToLocalTime() : null;
                limits.Add(new QuotaLimit(
                    Str(item, "type") ?? "", Int(item, "number"), Int(item, "unit"),
                    total, used, remaining, pct, reset));
            }
        }
        return new PlanQuota(level, limits, DateTime.Now);
    }

    public static async Task<ModelUsage?> FetchModelUsageAsync(Credentials c, int hours = 24)
    {
        var end = DateTime.Now;
        var start = end - TimeSpan.FromHours(hours);
        string q = $"?startTime={Url(start)}&endTime={Url(end)}";
        var root = await GetJsonAsync($"{c.BaseUrl}/api/monitor/usage/model-usage{q}", c.Token);
        if (root is not { } r2 || !r2.TryGetProperty("data", out var data)) return null;

        var buckets = new List<ModelBucket>();
        var times = data.TryGetProperty("x_time", out var xt) && xt.ValueKind == JsonValueKind.Array
            ? xt.EnumerateArray().Select(t => t.GetString() ?? "").ToList() : new();
        var tokens = data.TryGetProperty("tokensUsage", out var tu) && tu.ValueKind == JsonValueKind.Array
            ? tu.EnumerateArray().Select(Long).ToList() : new();
        int n = Math.Min(times.Count, tokens.Count);
        for (int i = 0; i < n; i++)
        {
            if (DateTime.TryParseExact(times[i], "yyyy-MM-dd HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var t))
                buckets.Add(new ModelBucket(t, tokens[i]));
        }

        long total = 0;
        var models = new List<(string, long)>();
        if (data.TryGetProperty("totalUsage", out var tot))
        {
            total = Long(tot, "totalTokensUsage");
            if (tot.TryGetProperty("modelSummaryList", out var ml) && ml.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in ml.EnumerateArray())
                    models.Add((Str(m, "modelName") ?? "?", Long(m, "totalTokens")));
            }
        }
        return new ModelUsage(buckets, total, models, DateTime.Now);
    }

    private static async Task<JsonElement?> GetJsonAsync(string url, string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", token);   // raw token, no Bearer
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en");
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                Log.Write($"glm api {res.StatusCode}: {url}");
                return null;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.Clone();   // detached from the disposable document
        }
        catch (Exception ex)
        {
            Log.Write($"glm api failed: {ex.Message}");
            return null;
        }
    }

    private static string Url(DateTime t) => Uri.EscapeDataString(t.ToString("yyyy-MM-dd HH:mm:ss"));

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static long Long(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    // ---------------- formatting ----------------

    public static string FormatTokens(long t) => t >= 100_000_000
        ? $"{t / 1e8:0.00} 亿"
        : t >= 10_000 ? $"{t / 1e4:0.0} 万" : $"{t}";
}
