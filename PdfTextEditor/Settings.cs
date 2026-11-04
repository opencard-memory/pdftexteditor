using System.Text.Json;

namespace PdfTextEditor;

public sealed class AppSettings
{
    public string ApiKey { get; init; } = "";
    public int CatalogMaxAgeDays { get; init; } = 30;
    public string FontCacheDir { get; init; } = "";

    public static AppSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new AppSettings();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("GoogleFonts", out var g)) return new AppSettings();
        return new AppSettings
        {
            ApiKey = g.TryGetProperty("ApiKey", out var k) ? k.GetString() ?? "" : "",
            CatalogMaxAgeDays = g.TryGetProperty("CatalogMaxAgeDays", out var d) ? d.GetInt32() : 30,
            FontCacheDir = g.TryGetProperty("CacheDir", out var c) ? c.GetString() ?? "" : "",
        };
    }

    public string ResolveCacheDir() =>
        string.IsNullOrWhiteSpace(FontCacheDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfTextEditor", "fonts")
            : FontCacheDir;

    static string PrefPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfTextEditor", "prefs.json");

    static JsonDocument? ReadPrefs()
    {
        try
        {
            if (!File.Exists(PrefPath)) return null;
            return JsonDocument.Parse(File.ReadAllText(PrefPath));
        }
        catch { return null; }
    }

    static void WritePrefs(Dictionary<string, object> data)
    {
        try
        {
            var dir = Path.GetDirectoryName(PrefPath)!;
            Directory.CreateDirectory(dir);
            // merge with existing
            var merged = new Dictionary<string, object>();
            using (var existing = ReadPrefs())
            {
                if (existing != null)
                {
                    foreach (var p in existing.RootElement.EnumerateObject())
                    {
                        merged[p.Name] = p.Value.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Array => p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
                            _ => p.Value.GetRawText().Trim('"'),
                        };
                    }
                }
            }
            foreach (var kv in data) merged[kv.Key] = kv.Value;
            File.WriteAllText(PrefPath, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
    }

    public static bool IsTutorialSeen()
    {
        try
        {
            using var doc = ReadPrefs();
            return doc != null && doc.RootElement.TryGetProperty("TutorialSeen", out var v) && v.GetBoolean();
        }
        catch { return false; }
    }

    public static void SetTutorialSeen() => WritePrefs(new() { ["TutorialSeen"] = true });

    public static void ResetTutorialSeen()
    {
        try { if (File.Exists(PrefPath)) File.Delete(PrefPath); } catch { }
    }

    const int RecentMax = 8;

    public static List<string> GetRecentFonts()
    {
        try
        {
            using var doc = ReadPrefs();
            if (doc == null || !doc.RootElement.TryGetProperty("RecentFonts", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<string>();
            return arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
        }
        catch { return new List<string>(); }
    }

    public static string GetLanguage()
    {
        try
        {
            using var doc = ReadPrefs();
            if (doc != null && doc.RootElement.TryGetProperty("Language", out var v))
            {
                var s = v.GetString() ?? "ko";
                if (s is "ko" or "en" or "es" or "fr") return s;
            }
        }
        catch { }
        return "ko";
    }

    public static void SetLanguage(string lang) =>
        WritePrefs(new() { ["Language"] = lang ?? "ko" });

    public static void AddRecentFont(string family)
    {
        if (string.IsNullOrWhiteSpace(family) || family == "자동") return;
        // "[내 글꼴] X" → X
        const string prefix = "[내 글꼴] ";
        if (family.StartsWith(prefix)) family = family[prefix.Length..];
        family = family.Trim();
        if (family.Length == 0) return;

        var list = GetRecentFonts();
        list.RemoveAll(s => string.Equals(s, family, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, family);
        if (list.Count > RecentMax) list = list.Take(RecentMax).ToList();
        WritePrefs(new() { ["RecentFonts"] = list });
    }
}
