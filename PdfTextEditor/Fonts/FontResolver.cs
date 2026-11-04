using iText.IO.Font;

namespace PdfTextEditor.Fonts;

public sealed record FontChoice(string Family, string Variant, FontProgram Program);

/// <summary>원본 폰트 이름/스타일 또는 사용자가 고른 패밀리를 보고 실제로 쓸 폰트를 확정한다.
/// 우선순위: 사용자 지정 커스텀 폰트 → Google Fonts → 매핑 대체.</summary>
public sealed class FontResolver
{
    readonly GoogleFontsClient _gf;
    readonly CustomFontStore _custom;
    readonly Dictionary<string, FontProgram> _programs = new();

    public FontResolver(GoogleFontsClient gf, CustomFontStore custom)
    {
        _gf = gf;
        _custom = custom;
    }

    public CustomFontStore Custom => _custom;

    /// <summary>원본 폰트(유료 포함)에 대응하는 무료 폰트 자동 선택.</summary>
    public async Task<FontChoice> ResolveAsync(string baseFont, bool bold, bool italic, string text)
    {
        await _gf.EnsureCatalogAsync();
        // 커스텀에 같은 이름이 있으면 우선
        if (_custom.Contains(baseFont) || _custom.Contains(FontMapper.Display(baseFont)))
        {
            var name = _custom.Contains(baseFont) ? baseFont : FontMapper.Display(baseFont);
            var prog = _custom.GetProgram(name);
            if (prog != null && Covers(prog, text))
                return new FontChoice(name, "custom", prog);
        }
        return await PickAsync(FontMapper.Candidates(baseFont, text, _gf), bold, italic, text);
    }

    /// <summary>사용자가 고른 패밀리 우선, 글리프가 없거나 받을 수 없으면 기본 후보로 대체.</summary>
    public async Task<FontChoice> ResolveFamilyAsync(string? family, bool bold, bool italic, string text)
    {
        await _gf.EnsureCatalogAsync();
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(family)) list.Add(family);
        // 커스텀 폰트도 후보에 포함
        list.AddRange(_custom.Families);
        list.AddRange(FontMapper.Candidates("Arial", text, _gf));
        return await PickAsync(list.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), bold, italic, text);
    }

    async Task<FontChoice> PickAsync(IEnumerable<string> families, bool bold, bool italic, string text)
    {
        foreach (var family in families)
        {
            // 1) 커스텀 폰트
            if (_custom.Contains(family))
            {
                var prog = GetOrLoadCustom(family);
                if (prog != null && Covers(prog, text))
                    return new FontChoice(family, "custom", prog);
                continue;
            }

            // 2) Google Fonts
            var variant = GoogleFontsClient.PickVariant(_gf.Variants(family), bold, italic);
            if (variant == null) continue;

            var key = family + "|" + variant;
            if (!_programs.TryGetValue(key, out var program))
            {
                var data = await _gf.GetFontAsync(family, variant);
                if (data == null) continue;
                try { program = FontProgramFactory.CreateFont(data, true); }
                catch { continue; }
                _programs[key] = program;
            }
            if (Covers(program, text)) return new FontChoice(family, variant, program);
        }

        throw new InvalidOperationException(
            "사용할 수 있는 폰트를 찾지 못했습니다. 오프라인이거나 필요한 폰트가 저장돼 있지 않을 수 있습니다. " +
            "‘글꼴 관리’에서 다운로드하거나 사용자 글꼴을 추가해 주세요.");
    }

    FontProgram? GetOrLoadCustom(string family)
    {
        var key = family + "|custom";
        if (_programs.TryGetValue(key, out var p)) return p;
        p = _custom.GetProgram(family);
        if (p != null) _programs[key] = p;
        return p;
    }

    static bool Covers(FontProgram p, string text)
    {
        foreach (var r in text.EnumerateRunes())
        {
            if (r.Value < 32 || r.Value == 0xA0) continue;
            if (p.GetGlyph(r.Value) == null) return false;
        }
        return true;
    }
}
