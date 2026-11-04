using System.Text.Json;
using iText.IO.Font;

namespace PdfTextEditor.Fonts;

/// <summary>사용자가 직접 추가한 TTF/OTF 폰트를 관리한다.</summary>
public sealed class CustomFontStore
{
    readonly string _dir;
    readonly string _indexPath;
    readonly Dictionary<string, CustomFontEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public CustomFontStore(string cacheRoot)
    {
        _dir = Path.Combine(cacheRoot, "custom");
        Directory.CreateDirectory(_dir);
        _indexPath = Path.Combine(_dir, "_index.json");
        Load();
    }

    public IReadOnlyList<CustomFontEntry> All =>
        _entries.Values.OrderBy(e => e.Family, StringComparer.OrdinalIgnoreCase).ToList();

    public IEnumerable<string> Families => All.Select(e => e.Family).Distinct(StringComparer.OrdinalIgnoreCase);

    public CustomFontEntry? Find(string family) =>
        _entries.TryGetValue(family, out var e) ? e : null;

    public bool Contains(string family) => _entries.ContainsKey(family);

    /// <summary>로컬 TTF/OTF 파일을 복사해 등록. 같은 이름이 있으면 덮어쓴다.</summary>
    public CustomFontEntry AddFromFile(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("폰트 파일이 없습니다.", sourcePath);
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not (".ttf" or ".otf" or ".ttc"))
            throw new InvalidOperationException("TTF, OTF, TTC 파일만 추가할 수 있습니다.");

        // 파일에서 패밀리 이름 추출 시도
        string family;
        try
        {
            var bytes = File.ReadAllBytes(sourcePath);
            var prog = FontProgramFactory.CreateFont(bytes, true);
            var names = prog.GetFontNames();
            family = names?.GetFontName() ?? Path.GetFileNameWithoutExtension(sourcePath);
            // subset 접두사(ABCDEF+) 제거
            if (family.Length > 7 && family[6] == '+') family = family[7..];
        }
        catch
        {
            family = Path.GetFileNameWithoutExtension(sourcePath);
        }

        family = family.Trim();
        if (family.Length == 0) family = Path.GetFileNameWithoutExtension(sourcePath);

        var destName = Sanitize(family) + ext;
        var dest = Path.Combine(_dir, destName);
        File.Copy(sourcePath, dest, true);

        var entry = new CustomFontEntry(family, dest, ext);
        _entries[family] = entry;
        Save();
        return entry;
    }

    public bool Remove(string family)
    {
        if (!_entries.TryGetValue(family, out var e)) return false;
        try { if (File.Exists(e.Path)) File.Delete(e.Path); } catch { /* ignore */ }
        _entries.Remove(family);
        Save();
        return true;
    }

    public byte[]? GetBytes(string family)
    {
        if (!_entries.TryGetValue(family, out var e) || !File.Exists(e.Path)) return null;
        return File.ReadAllBytes(e.Path);
    }

    public FontProgram? GetProgram(string family)
    {
        var data = GetBytes(family);
        if (data == null) return null;
        try { return FontProgramFactory.CreateFont(data, true); }
        catch { return null; }
    }

    void Load()
    {
        _entries.Clear();
        if (!File.Exists(_indexPath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<CustomFontEntry>>(File.ReadAllText(_indexPath));
            if (list == null) return;
            foreach (var e in list)
                if (File.Exists(e.Path))
                    _entries[e.Family] = e;
        }
        catch { /* corrupt index → start empty */ }
    }

    void Save()
    {
        var json = JsonSerializer.Serialize(_entries.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_indexPath, json);
    }

    static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}

public sealed class CustomFontEntry
{
    public string Family { get; set; } = "";
    public string Path { get; set; } = "";
    public string Extension { get; set; } = "";

    public CustomFontEntry() { }
    public CustomFontEntry(string family, string path, string extension)
    {
        Family = family; Path = path; Extension = extension;
    }
}
