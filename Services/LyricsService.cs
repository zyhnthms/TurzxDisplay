using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TurzxDisplay.Services;

public sealed class Lrc
{
    public List<(TimeSpan Time, string Text)> Lines = new();

    public int IndexAt(TimeSpan pos)
    {
        int i = Lines.Count - 1;
        while (i >= 0 && Lines[i].Time > pos) i--;
        return i;   // -1 before the first line
    }
}

/// <summary>
/// Local LRC lyrics: parse (multi-timestamp lines, UTF-8/GB18030) and match to a track
/// by artist/title against the configured lyrics folder.
/// </summary>
public static partial class LyricsService
{
    private static readonly Dictionary<string, Lrc?> Cache = new();

    static LyricsService() =>
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public static Lrc? Find(string folder, string title, string artist)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(title)) return null;
        string key = $"{folder}|{artist}|{title}";
        if (Cache.TryGetValue(key, out var cached))
        {
            if (cached != null && Cache.Count > 200) Cache.Clear();
            return cached;
        }

        Lrc? result = null;
        try
        {
            if (Directory.Exists(folder))
            {
                var files = Directory.GetFiles(folder, "*.lrc");
                string t = Norm(title), a = Norm(artist);
                var byName = files.ToDictionary(f => Norm(Path.GetFileNameWithoutExtension(f)));

                // exact candidates first: "artist - title", "title - artist", "title"
                string[] wanted = string.IsNullOrEmpty(a)
                    ? new[] { t }
                    : new[] { $"{a}-{t}", $"{t}-{a}", t };
                foreach (var w in wanted)
                {
                    if (byName.TryGetValue(w, out var path)) { result = Parse(path); break; }
                }

                // fuzzy: file name contains the title (and artist when possible)
                if (result is null)
                {
                    var best = byName
                        .Where(kv => kv.Key.Contains(t) && (a.Length == 0 || !kv.Key.Contains("feat")))
                        .OrderByDescending(kv => a.Length > 0 && kv.Key.Contains(a))
                        .ThenBy(kv => kv.Key.Length)
                        .FirstOrDefault();
                    if (!best.Equals(default(KeyValuePair<string, string>)) && best.Key is not null)
                        result = Parse(best.Value);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"lyrics find failed: {ex.Message}");
        }

        if (Cache.Count > 200) Cache.Clear();
        Cache[key] = result;
        return result;
    }

    /// <summary>Lowercase, keep word chars + CJK — punctuation/format-insensitive.</summary>
    private static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch) || ch >= 0x4E00 && ch <= 0x9FFF)
                sb.Append(ch);
        return sb.ToString();
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    public static Lrc? Parse(string path)
    {
        try
        {
            // LRC files are commonly UTF-8 (with/without BOM) or GB18030
            string text;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            else
            {
                try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
                catch (DecoderFallbackException) { text = Encoding.GetEncoding("GB18030").GetString(bytes); }
            }

            var lrc = new Lrc();
            TimeSpan offset = TimeSpan.Zero;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r', ' ').Trim();
                if (line.Length == 0) continue;

                // [offset:+/-ms] shifts all timestamps (must run before the time-tag filter)
                if ((line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("[偏移:", StringComparison.OrdinalIgnoreCase)) && line.EndsWith(']'))
                {
                    if (int.TryParse(line[(line.IndexOf(':') + 1)..^1], out var ms))
                        offset = TimeSpan.FromMilliseconds(ms);
                    continue;
                }

                var matches = TimeTag().Matches(line);
                if (matches.Count == 0) continue;   // metadata tags ([ti:] [ar:] …) drop out here

                string body = line[(matches[^1].Index + matches[^1].Length)..].Trim();
                if (body.Length == 0) continue;
                foreach (Match m in matches)
                {
                    int mins = int.Parse(m.Groups[1].Value);
                    int secs = int.Parse(m.Groups[2].Value);
                    int fracRaw = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                    int frac = m.Groups[3].Success
                        ? fracRaw * (int)Math.Pow(10, 3 - m.Groups[3].Value.Length)
                        : 0;
                    lrc.Lines.Add((TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(secs)
                        + TimeSpan.FromMilliseconds(frac) + offset, body));
                }
            }

            lrc.Lines.Sort((x, y) => x.Time.CompareTo(y.Time));
            return lrc.Lines.Count > 0 ? lrc : null;
        }
        catch (Exception ex)
        {
            Log.Write($"lyrics parse failed ({Path.GetFileName(path)}): {ex.Message}");
            return null;
        }
    }

    public static (string Prev, string Cur, string Next) Window(Lrc? lrc, TimeSpan pos)
    {
        if (lrc is null || lrc.Lines.Count == 0) return ("", "", "");
        int i = lrc.IndexAt(pos);
        string prev = i - 1 >= 0 ? lrc.Lines[i - 1].Text : "";
        string cur = i >= 0 ? lrc.Lines[i].Text : "";
        string next = i + 1 < lrc.Lines.Count ? lrc.Lines[i + 1].Text : "";
        return (prev, cur, next);
    }
}
