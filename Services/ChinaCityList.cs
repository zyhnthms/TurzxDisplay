using System.IO;

namespace TurzxDisplay.Services;

/// <summary>One China city/district row from the bundled 和风天气城市表 (Data/china-cities.tsv).</summary>
public sealed record CityEntry(string Name, string Adm1, string Adm2, double Lat, double Lon)
{
    /// <summary>Prefecture-level row (its Adm2 siblings are districts)?</summary>
    public bool IsCityRow => Adm2 == Name + "市" || Adm2 == Name || Adm1 == Name + "市";

    /// <summary>北京市·海淀 / 江苏省·无锡市·梁溪 — collapses redundant levels.</summary>
    public string DisplayName
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Adm1)) parts.Add(Adm1);
            if (!string.IsNullOrEmpty(Adm2) && Adm2 != Adm1 && Adm2 != Name + "市") parts.Add(Adm2);
            if (Name != Adm1 && Name + "市" != Adm1) parts.Add(Name);
            return parts.Count > 0 ? string.Join("·", parts) : Name;
        }
    }
}

/// <summary>
/// Offline China city/district search over the bundled QWeather China-City-List
/// (github.com/qwd/LocationList, Adm2/区县级, ~3.6k rows). No API calls, no quota.
/// </summary>
public static class ChinaCityList
{
    private static List<CityEntry>? _cities;

    public static bool IsLoaded => _cities is not null;

    private static List<CityEntry> All()
    {
        if (_cities is not null) return _cities;
        var list = new List<CityEntry>();
        try
        {
            string? path = FindFile();
            if (path is not null)
            {
                foreach (var line in File.ReadLines(path))
                {
                    var f = line.Split('\t');
                    if (f.Length < 5) continue;
                    if (double.TryParse(f[3], out double lat) && double.TryParse(f[4], out double lon))
                        list.Add(new CityEntry(f[2], f[0], f[1], lat, lon));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"china city list load failed: {ex.Message}");
        }
        _cities = list;
        return list;
    }

    private static string? FindFile()
    {
        string[] roots =
        {
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")),
            Directory.GetCurrentDirectory(),
        };
        foreach (var root in roots)
        {
            string p = Path.Combine(root, "Data", "china-cities.tsv");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Search by name (city or district). A prefecture match expands to its districts,
    /// so "北京" lists all Beijing districts. Trailing 市/区/县/旗 in the query is ignored.
    /// </summary>
    public static List<CityEntry> Search(string query)
    {
        string q = query.Trim();
        if (q.Length == 0) return new List<CityEntry>();
        string qs = q.TrimEnd('市', '区', '县', '旗');
        if (qs.Length < 2) qs = q;   // e.g. "沙区" still searches the full form
        var all = All();

        var results = new List<CityEntry>();
        var seen = new HashSet<string>();

        void Add(CityEntry c)
        {
            if (seen.Add(c.Adm1 + "|" + c.Adm2 + "|" + c.Name))
                results.Add(c);
        }

        // 1. exact + prefix name matches, expanding prefecture rows to their districts
        foreach (var c in all)
        {
            if (c.Name == q || c.Name == qs || c.Name.StartsWith(q) || c.Name.StartsWith(qs))
            {
                Add(c);
                if (c.IsCityRow)
                {
                    foreach (var d in all)
                        if (d.Adm2 == c.Adm2 && d.Name != c.Name)
                            Add(d);
                }
            }
            if (results.Count >= 60) return results;
        }

        // 2. substring name matches
        foreach (var c in all)
        {
            if (results.Count >= 60) break;
            if (c.Name.Contains(q) || c.Name.Contains(qs)) Add(c);
        }

        // 3. administrative-region matches (typing a prefecture name finds its city row too)
        foreach (var c in all)
        {
            if (results.Count >= 60) break;
            if (c.Adm1.Contains(q) || c.Adm2.Contains(q) || c.Adm1.Contains(qs) || c.Adm2.Contains(qs)) Add(c);
        }

        return results;
    }

    /// <summary>Nearest district to a coordinate (coarse planar distance — fine at China's scale).</summary>
    public static CityEntry? Nearest(double lat, double lon)
    {
        CityEntry? best = null;
        double bestD = double.MaxValue;
        foreach (var c in All())
        {
            double d = (c.Lat - lat) * (c.Lat - lat) + (c.Lon - lon) * (c.Lon - lon);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }
}
