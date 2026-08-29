using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;

namespace TurzxDisplay.Services;

/// <summary>
/// 和风天气 (QWeather) — JWT(Ed25519) authentication against the account's dedicated API Host.
/// Endpoints: /v7/weather/{now|24h|7d}, /airquality/v1/current, /v7/astronomy/{sun|moon},
/// /v7/warning/now, geoapi /v2/city/lookup. Weather queries use lon,lat coordinates for
/// grid-level precision; the display name comes from the GeoAPI (province/city/district).
/// </summary>
public static class QWeatherService
{
    // QWeather always gzip-compresses responses — decompress transparently
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    // ---------------- JWT ----------------

    private static string? _jwt;
    private static DateTimeOffset _jwtExp;

    /// <summary>Cached Ed25519-signed JWT, minted from qweather/ed25519-private.pem.</summary>
    private static string? GetJwt(string devId, string projectId, string keyId)
    {
        if (_jwt is not null && DateTimeOffset.UtcNow < _jwtExp - TimeSpan.FromMinutes(5))
            return _jwt;

        var pemPath = FindPrivateKey();
        if (pemPath is null) return null;
        try
        {
            using var reader = new PemReader(new StringReader(File.ReadAllText(pemPath)));
            if (reader.ReadObject() is not Ed25519PrivateKeyParameters key) return null;

            long iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30;
            long exp = iat + 1800;   // 30 min, refreshed 5 min early
            string header = $$"""{"alg":"EdDSA","kid":"{{keyId}}"}""";
            string payload = $$"""{"iss":"{{devId}}","sub":"{{projectId}}","iat":{{iat}},"exp":{{exp}}}""";
            string h = B64Url(Encoding.UTF8.GetBytes(header));
            string p = B64Url(Encoding.UTF8.GetBytes(payload));
            byte[] msg = Encoding.UTF8.GetBytes($"{h}.{p}");
            var signer = new Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            signer.Init(true, key);
            signer.BlockUpdate(msg, 0, msg.Length);
            string sig = B64Url(signer.GenerateSignature());

            _jwt = $"{h}.{p}.{sig}";
            _jwtExp = DateTimeOffset.FromUnixTimeSeconds(exp);
            return _jwt;
        }
        catch (Exception ex)
        {
            Log.Write($"qweather jwt failed: {ex.Message}");
            return null;
        }
    }

    private static string? FindPrivateKey()
    {
        // exe dir (csproj copies qweather\*.pem) > project root (dev run from bin/...) > cwd
        string[] roots =
        {
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")),
            Directory.GetCurrentDirectory(),
        };
        foreach (var root in roots)
        {
            string p = Path.Combine(root, "qweather", "ed25519-private.pem");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static bool HasKeyFile => FindPrivateKey() is not null;
    public static string KeyFile => FindPrivateKey() ?? "（未找到）";

    private static string B64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---------------- weather ----------------

    public static async Task<WeatherData?> FetchAsync(double lat, double lon, string city, AppSettings s)
    {
        if (string.IsNullOrEmpty(s.QwHost)) return null;
        var jwt = GetJwt(s.QwDevId, s.QwProjectId, s.QwKeyId);
        if (jwt is null) return null;
        string host = s.QwHost.Trim().TrimEnd('/').Replace("https://", "");
        string loc = $"{lon:0.####},{lat:0.####}";

        var data = new WeatherData { City = city, Updated = DateTime.Now };

        // current
        if (await GetJsonAsync($"https://{host}/v7/weather/now?location={loc}&lang=zh", jwt) is { } nowRoot &&
            nowRoot.TryGetProperty("now", out var now))
        {
            data.CurrentTemp = Dbl(now, "temp");
            data.CurrentText = Str(now, "text") ?? "";
            data.CurrentEmoji = Emoji((int)Dbl(now, "icon"));
            data.Humidity = (int)Dbl(now, "humidity");
            data.WindDir = Str(now, "windDir") ?? "";
            data.WindScale = Str(now, "windScale") ?? "";
        }
        else return null;

        // hourly 24h
        if (await GetJsonAsync($"https://{host}/v7/weather/24h?location={loc}&lang=zh", jwt) is { } hRoot &&
            hRoot.TryGetProperty("hourly", out var hourly))
        {
            foreach (var h in hourly.EnumerateArray())
            {
                var t = Str(h, "fxTime");
                if (t is null) continue;
                data.Hours.Add(new HourPoint
                {
                    Time = DateTime.Parse(t),
                    Temp = Dbl(h, "temp"),
                    Text = Str(h, "text") ?? "",
                    Emoji = Emoji((int)Dbl(h, "icon")),
                });
            }
        }

        // daily 7d (+ today's sunrise/sunset, moon phase per day)
        if (await GetJsonAsync($"https://{host}/v7/weather/7d?location={loc}&lang=zh", jwt) is { } dRoot &&
            dRoot.TryGetProperty("daily", out var daily))
        {
            foreach (var d in daily.EnumerateArray())
            {
                var date = Str(d, "fxDate");
                if (date is null) continue;
                var dp = new DayPoint
                {
                    Date = DateTime.Parse(date),
                    TMax = Dbl(d, "tempMax"),
                    TMin = Dbl(d, "tempMin"),
                    Text = Str(d, "textDay") ?? "",
                    Emoji = Emoji((int)Dbl(d, "iconDay")),
                };
                data.Days.Add(dp);
                if (data.Days.Count == 1)
                {
                    if (DateTime.TryParse(Str(d, "sunrise"), out var sr)) data.Sunrise = DateTime.Today + sr.TimeOfDay;
                    if (DateTime.TryParse(Str(d, "sunset"), out var ss)) data.Sunset = DateTime.Today + ss.TimeOfDay;
                    data.MoonName = Str(d, "moonPhase") ?? "";
                }
            }
        }

        // astronomy (precise today's sun + moon). /v7/astronomy returns full ISO datetimes;
        // moon's "moonPhase" is an hourly array — take the first entry.
        string today = DateTime.Today.ToString("yyyyMMdd");
        if (await GetJsonAsync($"https://{host}/v7/astronomy/sun?location={loc}&date={today}&lang=zh", jwt) is { } sunRoot)
        {
            if (DateTime.TryParse(Str(sunRoot, "sunrise"), out var sr)) data.Sunrise = DateTime.Today + sr.TimeOfDay;
            if (DateTime.TryParse(Str(sunRoot, "sunset"), out var ss)) data.Sunset = DateTime.Today + ss.TimeOfDay;
        }
        if (await GetJsonAsync($"https://{host}/v7/astronomy/moon?location={loc}&date={today}&lang=zh", jwt) is { } moonRoot &&
            moonRoot.TryGetProperty("moonPhase", out var phases) && phases.ValueKind == JsonValueKind.Array &&
            phases.GetArrayLength() > 0)
        {
            var mp = phases[0];
            string name = Str(mp, "name") ?? "";
            if (name.Length > 0) data.MoonName = name;
            string? illum = Str(mp, "illumination");
            if (illum is not null && int.TryParse(illum.TrimEnd('%', ' '), out var pct)) data.MoonIllum = pct;
            data.MoonEmoji = MoonEmoji(data.MoonName);
        }

        // air quality: v1 path style — indexes[] carries national ("cn-mee") + US standards
        if (await GetJsonAsync($"https://{host}/airquality/v1/current/{lat:0.####}/{lon:0.####}?lang=zh", jwt) is { } airRoot &&
            airRoot.TryGetProperty("indexes", out var indexes))
        {
            JsonElement best = default; bool found = false;
            foreach (var ix in indexes.EnumerateArray())
            {
                string? code = Str(ix, "code");
                if (code == "cn-mee") { best = ix; found = true; break; }   // national standard
            }
            if (!found)
            {
                foreach (var ix in indexes.EnumerateArray())
                {
                    if (Str(ix, "code") != "us") { best = ix; found = true; break; }
                }
            }
            if (!found && indexes.GetArrayLength() > 0) best = indexes[0];
            if (found || !best.Equals(default(JsonElement)))
            {
                data.Aqi = (int)Dbl(best, "aqi");
                data.AqiCategory = CategoryOf(best, "category");
            }
        }

        return data;
    }

    /// <summary>category may be a plain string or a localized object ({zh:...} / {cn:...} / {en:...}).</summary>
    private static string CategoryOf(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "zh", "cn", "zh-Hans", "en" })
                if (c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
        }
        return "";
    }

    // ---------------- plumbing ----------------

    private static async Task<JsonElement?> GetJsonAsync(string url, string jwt)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {jwt}");
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                Log.Write($"qweather http {res.StatusCode}: {url}");
                return null;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            // v7 endpoints report status via a string/number "code" field
            if (root.TryGetProperty("code", out var code) && code.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                string c = code.ValueKind == JsonValueKind.String ? code.GetString()! : code.GetRawText();
                if (c != "200" && c != "0")
                {
                    Log.Write($"qweather code {c}: {url}");
                    return null;
                }
            }
            return root.Clone();
        }
        catch (Exception ex)
        {
            Log.Write($"qweather request failed: {ex.Message}");
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Dbl(JsonElement e, string name)
    {
        if (e.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d)) return d;
        }
        return 0;
    }

    // ---------------- icon -> emoji ----------------

    public static string Emoji(int icon) => icon switch
    {
        100 => "☀",
        101 => "☁",
        102 or 103 => "⛅",
        104 => "☁",
        150 => "🌙",
        151 or 152 or 153 => "🌙",
        300 or 301 => "🌦",
        302 or 303 or 304 => "⛈",
        >= 305 and <= 309 => "🌧",
        >= 310 and <= 312 => "⛈",
        >= 313 and <= 318 => "🌧",
        350 or 351 => "⛈",
        399 => "🌧",
        >= 400 and <= 406 => "🌨",
        >= 407 and <= 415 => "❄",
        499 => "❄",
        500 or 501 or 502 => "🌫",
        503 or 504 => "🌫",
        507 or 508 => "🌪",
        900 => "🔥",
        901 => "❄",
        _ => "🌡",
    };

    private static string MoonEmoji(string phase) => phase switch
    {
        "新月" => "🌑",
        "娥眉月" => "🌒",
        "上弦月" => "🌓",
        "盈凸月" => "🌔",
        "满月" => "🌕",
        "亏凸月" => "🌖",
        "下弦月" => "🌗",
        "残月" => "🌘",
        _ => "🌙",
    };
}
