using System.Net.Http;
using System.Text.Json;

namespace TurzxDisplay.Services;

public sealed class HourPoint
{
    public DateTime Time { get; init; }
    public double Temp { get; init; }
    public string Text { get; init; } = "";
    public string Emoji { get; init; } = "";
}

public sealed class DayPoint
{
    public DateTime Date { get; init; }
    public double TMax { get; init; }
    public double TMin { get; init; }
    public string Text { get; init; } = "";
    public string Emoji { get; init; } = "";
}

/// <summary>
/// Unified weather payload shared by both sources (Open-Meteo / QWeather).
/// Text+Emoji are pre-rendered per source; extras (AQI, sun/moon, wind...) are
/// optional — filled when the source provides them.
/// </summary>
public sealed class WeatherData
{
    public string City = "";
    public double CurrentTemp;
    public string CurrentText = "";
    public string CurrentEmoji = "";
    public DateTime Updated;
    public List<HourPoint> Hours = new();   // next 24 h from now
    public List<DayPoint> Days = new();     // 7 days including today

    public int Humidity = -1;               // %, -1 unknown
    public string WindDir = "";
    public string WindScale = "";
    public int Aqi = -1;                    // -1 unknown
    public string AqiCategory = "";
    public DateTime? Sunrise;               // today, local
    public DateTime? Sunset;
    public string MoonName = "";            // e.g. 盈凸月
    public int MoonIllum = -1;              // %, -1 unknown
    public string MoonEmoji = "";
    public string Warning = "";             // active warning title (QWeather)
}

/// <summary>Open-Meteo (keyless) + ip-api auto-location. Current+hourly+daily+air-quality.</summary>
public static class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<WeatherData?> FetchAsync(string city, double lat, double lon)
    {
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat:0.####}&longitude={lon:0.####}" +
                  "&current=temperature_2m,weather_code" +
                  "&hourly=temperature_2m,weather_code" +
                  "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                  "&forecast_days=8&timezone=auto";
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var data = new WeatherData
        {
            City = city,
            Updated = DateTime.Now,
            CurrentTemp = root.GetProperty("current").GetProperty("temperature_2m").GetDouble(),
        };
        int curCode = root.GetProperty("current").GetProperty("weather_code").GetInt32();
        data.CurrentText = Text(curCode);
        data.CurrentEmoji = Emoji(curCode);

        // hourly: keep 24 entries from the current hour onward
        var times = root.GetProperty("hourly").GetProperty("time");
        var temps = root.GetProperty("hourly").GetProperty("temperature_2m");
        var codes = root.GetProperty("hourly").GetProperty("weather_code");
        var nowHour = DateTime.Now.ToString("yyyy-MM-ddTHH:00");
        int start = -1;
        for (int i = 0; i < times.GetArrayLength(); i++)
        {
            if (string.CompareOrdinal(times[i].GetString(), nowHour) >= 0) { start = i; break; }
        }
        if (start < 0) start = 0;
        for (int i = start; i < Math.Min(start + 24, times.GetArrayLength()); i++)
        {
            int code = codes[i].GetInt32();
            data.Hours.Add(new HourPoint
            {
                Time = DateTime.Parse(times[i].GetString()!),
                Temp = temps[i].GetDouble(),
                Text = Text(code),
                Emoji = Emoji(code),
            });
        }

        // daily: 7 entries starting today
        var dTime = root.GetProperty("daily").GetProperty("time");
        var dCode = root.GetProperty("daily").GetProperty("weather_code");
        var dMax = root.GetProperty("daily").GetProperty("temperature_2m_max");
        var dMin = root.GetProperty("daily").GetProperty("temperature_2m_min");
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        int dStart = 0;
        for (int i = 0; i < dTime.GetArrayLength(); i++)
        {
            if (dTime[i].GetString() == today) { dStart = i; break; }
        }
        for (int i = dStart; i < Math.Min(dStart + 7, dTime.GetArrayLength()); i++)
        {
            int code = dCode[i].GetInt32();
            data.Days.Add(new DayPoint
            {
                Date = DateTime.Parse(dTime[i].GetString()!),
                TMax = dMax[i].GetDouble(),
                TMin = dMin[i].GetDouble(),
                Text = Text(code),
                Emoji = Emoji(code),
            });
        }

        return data;
    }

    public static async Task<(string City, double Lat, double Lon, string CountryCode)?> LocateAsync()
    {
        try
        {
            // ip-api.com became unreachable from this network (2026-08) — ipwho.is is the keyless fallback
            using var resp = await Http.GetAsync("https://ipwho.is/");
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return null;
            if (!root.TryGetProperty("latitude", out var la) || !root.TryGetProperty("longitude", out var lo))
                return null;
            string city = root.TryGetProperty("city", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "未知" : "未知";
            string cc = root.TryGetProperty("country_code", out var ccEl) && ccEl.ValueKind == JsonValueKind.String
                ? ccEl.GetString() ?? "" : "";
            return (city, la.GetDouble(), lo.GetDouble(), cc);
        }
        catch { return null; }
    }

    // ---------------- WMO code -> Chinese text / emoji ----------------

    public static string Text(int code) => code switch
    {
        0 => "晴",
        1 => "晴间少云",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 or 67 => "冻雨",
        71 => "小雪",
        73 => "中雪",
        75 or 77 => "大雪",
        80 => "阵雨",
        81 => "强阵雨",
        82 => "暴雨",
        85 or 86 => "阵雪",
        95 => "雷暴",
        _ => "雷暴冰雹",
    };

    public static string Emoji(int code) => code switch
    {
        0 => "☀",
        1 => "🌤",
        2 => "⛅",
        3 => "☁",
        45 or 48 => "🌫",
        < 60 => "🌦",
        < 70 => "🌧",
        < 80 => "🌨",
        < 95 => "🌧",
        _ => "⛈",
    };
}
