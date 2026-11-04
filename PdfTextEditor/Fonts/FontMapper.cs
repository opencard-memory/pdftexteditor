using System.Text.RegularExpressions;

namespace PdfTextEditor.Fonts;

/// <summary>유료/전용 폰트 → 무료(Google Fonts) 폰트 후보 목록을 만든다.</summary>
public static class FontMapper
{
    static readonly Dictionary<string, string> Table = new()
    {
        // 메트릭 호환(글자 폭이 같아 레이아웃이 유지됨)
        ["arial"] = "Arimo", ["helvetica"] = "Arimo", ["helveticaneue"] = "Arimo", ["nimbussans"] = "Arimo",
        ["liberationsans"] = "Arimo", ["microsoftsansserif"] = "Arimo",
        ["times"] = "Tinos", ["timesroman"] = "Tinos", ["timesnewroman"] = "Tinos", ["liberationserif"] = "Tinos",
        ["courier"] = "Cousine", ["couriernew"] = "Cousine", ["lucidaconsole"] = "Cousine", ["liberationmono"] = "Cousine",
        ["calibri"] = "Carlito", ["cambria"] = "Caladea", ["georgia"] = "Gelasio",
        // 유사 폰트
        ["verdana"] = "Noto Sans", ["tahoma"] = "Noto Sans", ["segoeui"] = "Noto Sans",
        ["trebuchetms"] = "Fira Sans", ["consolas"] = "Inconsolata", ["centurygothic"] = "Questrial",
        ["gillsans"] = "Lato", ["futura"] = "Jost", ["garamond"] = "EB Garamond",
        ["palatino"] = "Crimson Text", ["palatinolinotype"] = "Crimson Text", ["bookantiqua"] = "Crimson Text",
        ["baskerville"] = "Libre Baskerville", ["bodoni"] = "Libre Bodoni", ["didot"] = "Playfair Display",
        ["optima"] = "Marcellus", ["avenir"] = "Nunito Sans", ["avenirnext"] = "Nunito Sans",
        ["frutiger"] = "Source Sans 3", ["myriad"] = "Source Sans 3", ["myriadpro"] = "Source Sans 3",
        ["univers"] = "Inter", ["franklingothic"] = "Libre Franklin", ["impact"] = "Anton",
        ["comicsansms"] = "Comic Neue", ["sfpro"] = "Inter", ["sanfrancisco"] = "Inter",
        // 한글
        ["malgungothic"] = "Noto Sans KR", ["applesdgothicneo"] = "Noto Sans KR",
        ["dotum"] = "Noto Sans KR", ["dotumche"] = "Noto Sans KR", ["gulim"] = "Noto Sans KR", ["gulimche"] = "Noto Sans KR",
        ["batang"] = "Noto Serif KR", ["batangche"] = "Noto Serif KR", ["gungsuh"] = "Noto Serif KR", ["gungsuhche"] = "Noto Serif KR",
        ["hcrbatang"] = "Noto Serif KR", ["hcrdotum"] = "Noto Sans KR",
        ["맑은고딕"] = "Noto Sans KR", ["바탕"] = "Noto Serif KR", ["바탕체"] = "Noto Serif KR",
        ["돋움"] = "Noto Sans KR", ["돋움체"] = "Noto Sans KR", ["굴림"] = "Noto Sans KR", ["굴림체"] = "Noto Sans KR",
        ["궁서"] = "Noto Serif KR", ["궁서체"] = "Noto Serif KR", ["함초롬바탕"] = "Noto Serif KR", ["함초롬돋움"] = "Noto Sans KR",
    };

    static readonly string[] StyleSuffix =
    {
        "bolditalic", "boldoblique", "semibold", "extrabold", "bold", "italic", "oblique", "regular",
        "medium", "light", "black", "heavy", "condensed", "narrow", "psmt", "mt", "ps",
    };

    /// <summary>미리 받아 두면 오프라인에서도 대부분의 문서를 편집할 수 있는 폰트들.</summary>
    public static readonly (string Family, string[] Variants)[] PrefetchSet =
    {
        ("Noto Sans KR", new[] { "regular", "700" }), ("Noto Serif KR", new[] { "regular", "700" }),
        ("Noto Sans", new[] { "regular", "700", "italic", "700italic" }),
        ("Arimo", new[] { "regular", "700", "italic", "700italic" }),
        ("Tinos", new[] { "regular", "700", "italic", "700italic" }),
        ("Cousine", new[] { "regular", "700", "italic", "700italic" }),
        ("Carlito", new[] { "regular", "700", "italic", "700italic" }),
        ("Caladea", new[] { "regular", "700", "italic", "700italic" }),
        ("Gelasio", new[] { "regular", "700", "italic", "700italic" }),
    };

    public static string Display(string baseFont) =>
        baseFont.Length > 7 && baseFont[6] == '+' ? baseFont[7..] : baseFont;

    public static string NormalizeKey(string baseFont)
    {
        var key = GoogleFontsClient.Key(Display(baseFont));
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var s in StyleSuffix)
                if (key.Length > s.Length + 2 && key.EndsWith(s)) { key = key[..^s.Length]; changed = true; }
        }
        return key;
    }

    static string Category(string key)
    {
        if (Regex.IsMatch(key, "mono|courier|consol|code|typewriter|cousine|굴림체|돋움체")) return "mono";
        if (!key.Contains("sans") &&
            Regex.IsMatch(key, "serif|times|roman|garamond|palatino|georgia|batang|myeongjo|bookman|cambria|minion|baskerville|didot|bodoni|명조|바탕|궁서"))
            return "serif";
        return "sans";
    }

    static bool HasCjk(string text) =>
        text.Any(c => (c >= '\u1100' && c <= '\u11FF') || (c >= '\u3130' && c <= '\u318F') || (c >= '\uAC00' && c <= '\uD7A3') ||
                      (c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3040' && c <= '\u30FF'));

    /// <summary>우선순위 순서의 Google Fonts 패밀리 후보.</summary>
    public static List<string> Candidates(string baseFont, string text, GoogleFontsClient gf)
    {
        var key = NormalizeKey(baseFont);
        var list = new List<string>();

        // 1) 원래부터 Google Fonts에 있는 무료 폰트면 그대로 사용
        var direct = gf.Find(key);
        if (direct != null) list.Add(direct.Family);

        // 2) 유료/전용 폰트 → 무료 대체 폰트
        if (Table.TryGetValue(key, out var mapped)) list.Add(mapped);
        else
        {
            var best = Table.Where(kv => kv.Key.Length >= 4 && key.StartsWith(kv.Key))
                            .OrderByDescending(kv => kv.Key.Length).FirstOrDefault();
            if (best.Key != null) list.Add(best.Value);
        }

        // 3) 종류별 기본값 → 마지막 안전망
        list.AddRange(Category(key) switch
        {
            "mono" => new[] { "Cousine", "Nanum Gothic Coding" },
            "serif" => new[] { "Noto Serif KR", "Tinos" },
            _ => new[] { "Noto Sans KR", "Arimo" },
        });
        list.Add("Noto Sans KR");
        list.Add("Noto Sans");

        var result = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (HasCjk(text)) result = result.OrderByDescending(gf.SupportsKorean).ToList();   // 한글 지원 폰트 우선
        return result;
    }
}
