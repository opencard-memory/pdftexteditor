using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PdfTextEditor.Fonts;

public sealed class GFont
{
    [JsonPropertyName("family")] public string Family { get; set; } = "";
    [JsonPropertyName("variants")] public List<string> Variants { get; set; } = new();
    [JsonPropertyName("subsets")] public List<string> Subsets { get; set; } = new();
    [JsonPropertyName("files")] public Dictionary<string, string> Files { get; set; } = new();
    [JsonPropertyName("category")] public string Category { get; set; } = "";
}

sealed class GFontList
{
    [JsonPropertyName("items")] public List<GFont> Items { get; set; } = new();
}

/// <summary>
/// Google Fonts 목록/폰트 파일 클라이언트.
///  - 목록(catalog)은 디스크에 캐시하고, 캐시가 신선하면 네트워크를 쓰지 않는다.
///  - 폰트 파일은 한 번 받으면 로컬 캐시에서만 읽는다(온라인이어도 재다운로드 안 함).
///  - 오프라인이면 캐시된 목록(오래됐어도) 또는 로컬 폰트 파일만으로 동작한다.
/// </summary>
public sealed class GoogleFontsClient
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    readonly AppSettings _settings;
    readonly string _dir;
    readonly string _catalogPath;
    Dictionary<string, GFont>? _byKey;
    bool _attempted;

    public GoogleFontsClient(AppSettings settings)
    {
        _settings = settings;
        _dir = settings.ResolveCacheDir();
        Directory.CreateDirectory(_dir);
        _catalogPath = Path.Combine(_dir, "_catalog.json");
    }

    public bool CatalogLoaded => _byKey != null;

    public static string Key(string s) =>
        new(s.Where(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || char.IsDigit(c) || (c >= '\uAC00' && c <= '\uD7A3'))
             .Select(char.ToLowerInvariant).ToArray());

    public async Task<bool> EnsureCatalogAsync(bool force = false)
    {
        if (_byKey != null && !force) return true;
        if (_attempted && !force) return false;       // 이번 실행에서 이미 실패했으면 다시 기다리지 않는다
        _attempted = true;

        if (!force && File.Exists(_catalogPath) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(_catalogPath) < TimeSpan.FromDays(_settings.CatalogMaxAgeDays) &&
            Load(File.ReadAllText(_catalogPath)))
            return true;

        try
        {
            var url = $"https://www.googleapis.com/webfonts/v1/webfonts?key={Uri.EscapeDataString(_settings.ApiKey)}&sort=alpha";
            var json = await _http.GetStringAsync(url);
            if (Load(json))
            {
                await File.WriteAllTextAsync(_catalogPath, json);
                return true;
            }
        }
        catch { /* 오프라인 또는 API 오류 → 캐시로 대체 */ }

        return File.Exists(_catalogPath) && Load(File.ReadAllText(_catalogPath));
    }

    bool Load(string json)
    {
        try
        {
            var list = JsonSerializer.Deserialize<GFontList>(json);
            if (list == null || list.Items.Count == 0) return false;
            var map = new Dictionary<string, GFont>();
            foreach (var f in list.Items) map.TryAdd(Key(f.Family), f);
            _byKey = map;
            return true;
        }
        catch { return false; }
    }

    public GFont? Find(string familyOrKey) =>
        _byKey != null && _byKey.TryGetValue(Key(familyOrKey), out var f) ? f : null;

    public List<string> Families() =>
        _byKey?.Values.Select(f => f.Family).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() ?? new();

    public IReadOnlyList<GFont> AllFonts() =>
        _byKey?.Values.OrderBy(f => f.Family, StringComparer.OrdinalIgnoreCase).ToList()
        ?? (IReadOnlyList<GFont>)Array.Empty<GFont>();

    /// <summary>언어(서브셋)로 필터. "all"이면 전체, "downloaded"는 별도 처리.</summary>
    public List<GFont> FilterByLanguage(string languageKey, string? search = null)
    {
        var all = AllFonts();
        IEnumerable<GFont> q = languageKey.ToLowerInvariant() switch
        {
            "korean" or "ko" => all.Where(f => f.Subsets.Contains("korean", StringComparer.OrdinalIgnoreCase)
                || f.Family.Contains("KR", StringComparison.OrdinalIgnoreCase)
                || f.Family.StartsWith("Nanum", StringComparison.OrdinalIgnoreCase)
                || f.Family.StartsWith("Gowun", StringComparison.OrdinalIgnoreCase)),
            "japanese" or "ja" => all.Where(f => f.Subsets.Contains("japanese", StringComparer.OrdinalIgnoreCase)
                || f.Family.Contains("JP", StringComparison.OrdinalIgnoreCase)),
            "chinese" or "zh" or "chinese-simplified" => all.Where(f =>
                f.Subsets.Any(s => s.Contains("chinese", StringComparison.OrdinalIgnoreCase))
                || f.Family.Contains("SC", StringComparison.OrdinalIgnoreCase)
                || f.Family.Contains("TC", StringComparison.OrdinalIgnoreCase)
                || f.Family.Contains("CN", StringComparison.OrdinalIgnoreCase)),
            "latin" => all.Where(f => f.Subsets.Contains("latin", StringComparer.OrdinalIgnoreCase)
                || f.Subsets.Count == 0),
            "cyrillic" => all.Where(f => f.Subsets.Contains("cyrillic", StringComparer.OrdinalIgnoreCase)),
            "arabic" => all.Where(f => f.Subsets.Contains("arabic", StringComparer.OrdinalIgnoreCase)),
            "thai" => all.Where(f => f.Subsets.Contains("thai", StringComparer.OrdinalIgnoreCase)),
            "devanagari" or "hindi" => all.Where(f => f.Subsets.Contains("devanagari", StringComparer.OrdinalIgnoreCase)),
            "hebrew" => all.Where(f => f.Subsets.Contains("hebrew", StringComparer.OrdinalIgnoreCase)),
            _ => all,
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(f => f.Family.Contains(s, StringComparison.OrdinalIgnoreCase)
                          || f.Category.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        return q.OrderBy(f => f.Family, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool IsDownloaded(string family, string? variant = null)
    {
        if (variant != null) return File.Exists(PathFor(family, variant));
        var safe = string.Concat(family.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return Directory.GetFiles(_dir, $"{safe}--*.ttf").Length > 0;
    }

    public async Task<(int Ok, int Fail)> DownloadFamilyAsync(string family, IProgress<string>? progress = null)
    {
        var g = Find(family);
        if (g == null) return (0, 1);
        int ok = 0, fail = 0;
        // regular + bold 우선, 너무 많으면 부담 → 주요 4종
        var prefer = new[] { "regular", "700", "italic", "700italic", "400", "500", "600", "800" };
        var targets = prefer.Where(v => g.Files.ContainsKey(v)).Distinct().ToList();
        if (targets.Count == 0) targets = g.Files.Keys.Take(4).ToList();
        foreach (var v in targets)
        {
            progress?.Report($"{family} ({v})…");
            var data = await GetFontAsync(family, v);
            if (data != null) ok++; else fail++;
        }
        return (ok, fail);
    }

    public bool SupportsKorean(string family)
    {
        var g = Find(family);
        if (g != null) return g.Subsets.Contains("korean");
        return family.Contains("KR") || family.StartsWith("Nanum") || family.StartsWith("Gowun");
    }

    public string CacheDirectory => _dir;

    string PathFor(string family, string variant)
    {
        var safe = string.Concat(family.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return Path.Combine(_dir, $"{safe}--{variant}.ttf");
    }

    public IReadOnlyCollection<string> LocalVariants(string family)
    {
        var safe = string.Concat(family.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return Directory.GetFiles(_dir, $"{safe}--*.ttf")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Select(n => n[(n.IndexOf("--", StringComparison.Ordinal) + 2)..])
            .ToList();
    }

    public IReadOnlyCollection<string> Variants(string family)
    {
        var g = Find(family);
        if (g != null && g.Variants.Count > 0) return g.Variants;
        return LocalVariants(family);
    }

    public static string? PickVariant(IReadOnlyCollection<string> v, bool bold, bool italic)
    {
        if (v.Count == 0) return null;
        string[] order = (bold, italic) switch
        {
            (true, true) => new[] { "700italic", "600italic", "italic", "700", "regular" },
            (true, false) => new[] { "700", "600", "800", "regular" },
            (false, true) => new[] { "italic", "regular" },
            _ => new[] { "regular", "400", "500" },
        };
        foreach (var o in order) if (v.Contains(o)) return o;
        return v.First();
    }

    /// <summary>로컬 캐시에 있으면 그대로 반환, 없으면 내려받아 저장. 오프라인+미보유면 null.</summary>
    public async Task<byte[]?> GetFontAsync(string family, string variant)
    {
        var path = PathFor(family, variant);
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path);

        var g = Find(family);
        if (g == null || !g.Files.TryGetValue(variant, out var url)) return null;
        try
        {
            var data = await _http.GetByteArrayAsync(url.Replace("http://", "https://"));
            var tmp = path + ".part";
            await File.WriteAllBytesAsync(tmp, data);
            File.Move(tmp, path, true);
            return data;
        }
        catch { return null; }
    }

    public async Task PrefetchAsync(IEnumerable<(string Family, string[] Variants)> set, IProgress<string> progress)
    {
        foreach (var (family, variants) in set)
            foreach (var v in variants)
            {
                if (Find(family)?.Files.ContainsKey(v) != true) continue;
                progress.Report(File.Exists(PathFor(family, v)) ? $"{family} {v}: 이미 저장됨" : $"{family} {v} 다운로드 중…");
                await GetFontAsync(family, v);
            }
    }
}
