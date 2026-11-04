using System.Text;
using System.Text.RegularExpressions;
using iText.IO.Font;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using PdfTextEditor.Fonts;

namespace PdfTextEditor.Pdf;

/// <summary>콘텐츠 스트림 안의 텍스트 표시 연산자 하나의 바이트 범위.</summary>
public sealed record ShowOp(int Start, int End, string? FontName, string? FontSize, bool Replaceable);

public sealed class PdfEngine
{
    // ───────────────────────── 분석: 화면용 텍스트 조각 ─────────────────────────
    public PageAnalysis Analyze(byte[] bytes, int pageNo)
    {
        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        var page = pdf.GetPage(pageNo);
        var box = page.GetCropBox() ?? page.GetMediaBox();

        var col = new Collector();
        var proc = new PdfCanvasProcessor(col);
        foreach (var kind in new[] { "Tj", "TJ", "'", "\"", "Do" })
        {
            var tag = new Tag(col, kind);
            tag.Inner = proc.RegisterContentOperator(kind, tag);
        }
        proc.ProcessPageContent(page);

        var ops = new List<OpInfo>();
        for (int i = 0; i < col.Count; i++)
        {
            if (!col.Items.TryGetValue(i, out var a))
            {
                ops.Add(new OpInfo(i, "", 0, 0, 0, 0, false, "", false, false));
                continue;
            }
            var text = a.Text.ToString();
            bool editable = (col.Kinds[i] == "Tj" || col.Kinds[i] == "TJ") && !a.Invisible && text.Trim().Length > 0;
            ops.Add(new OpInfo(i, text, a.L, a.R, a.B, a.T, editable, a.Font, a.Bold, a.Italic));
        }

        var runs = BuildRuns(ops);
        return new PageAnalysis(ops, runs, col.Count, box.GetLeft(), box.GetBottom(), box.GetWidth(), box.GetHeight(),
                                page.GetRotation(), pdf.GetNumberOfPages());
    }

    /// <summary>
    /// 같은 줄·가까운 간격의 연산자만 TextRun으로 묶는다.
    /// 세로로 멀리 떨어진 줄은 절대 합치지 않는다(제목·본문이 한 박스로 잡히는 문제 방지).
    /// 바로 아래 줄만, 줄 간격이 글자 높이의 1.5배 이내이고 문장이 아직 안 끝났을 때만 이어 붙인다.
    /// </summary>
    public static List<TextRun> BuildRuns(IReadOnlyList<OpInfo> ops)
    {
        var lineRuns = new List<List<OpInfo>>();
        var editable = ops.Where(o => o.Editable).OrderBy(o => o.Index).ToList();
        if (editable.Count == 0) return new List<TextRun>();

        var cur = new List<OpInfo> { editable[0] };
        for (int i = 1; i < editable.Count; i++)
        {
            var prev = cur[^1];
            var next = editable[i];
            bool consecutive = next.Index == prev.Index + 1;
            double h = Math.Max(1, Math.Max(prev.Top - prev.Bottom, next.Top - next.Bottom));
            bool sameLine = Math.Abs(prev.Bottom - next.Bottom) < h * 0.35
                         && Math.Abs(prev.Top - next.Top) < h * 0.5;
            double gap = next.Left - prev.Right;
            bool close = gap >= -h * 0.15 && gap < h * 2.2;
            bool similarFont = SimilarFont(prev.FontName, next.FontName);

            if (consecutive && sameLine && close && similarFont)
                cur.Add(next);
            else
            {
                lineRuns.Add(cur);
                cur = new List<OpInfo> { next };
            }
        }
        if (cur.Count > 0) lineRuns.Add(cur);

        // 인접한 줄만 조심스럽게 문장 연결 (큰 공백/제목 간격은 분리)
        var groups = new List<List<OpInfo>>();
        var sent = new List<OpInfo>();
        foreach (var line in lineRuns)
        {
            if (sent.Count == 0)
            {
                sent.AddRange(line);
                continue;
            }

            var last = sent[^1];
            var first = line[0];
            double hLast = Math.Max(1, last.Top - last.Bottom);
            double hFirst = Math.Max(1, first.Top - first.Bottom);
            double h = Math.Max(hLast, hFirst);

            // 세로 거리: 윗줄 바닥 → 아랫줄 천장
            double vGap = last.Bottom - first.Top; // PDF y: 위가 큼. 아랫줄 Top이 더 작음
            // 같은 줄이 아닌 경우 first.Top < last.Bottom
            if (first.Top > last.Bottom) vGap = first.Bottom - last.Top; // 예외적 순서

            bool sequential = first.Index > last.Index && first.Index <= last.Index + 3;
            // 줄 간격이 글자 높이의 1.4배 이내만 "다음 줄"
            bool tightLine = vGap >= -h * 0.2 && vGap <= h * 1.4;
            bool similarSize = Math.Abs(hLast - hFirst) < h * 0.45;
            bool similarFont = SimilarFont(last.FontName, first.FontName);
            bool ends = EndsSentence(string.Concat(sent.Select(o => o.Text)));

            if (!ends && sequential && tightLine && similarSize && similarFont)
                sent.AddRange(line);
            else
            {
                groups.Add(sent);
                sent = new List<OpInfo>(line);
            }

            if (EndsSentence(string.Concat(sent.Select(o => o.Text))))
            {
                groups.Add(sent);
                sent = new List<OpInfo>();
            }
        }
        if (sent.Count > 0) groups.Add(sent);

        return groups.Select(MakeRun).ToList();
    }

    static bool EndsSentence(string text)
    {
        var t = text.TrimEnd();
        if (t.Length == 0) return false;
        char c = t[^1];
        return c is '.' or '!' or '?' or '。' or '！' or '？' or '…';
    }

    static bool SimilarFont(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return true;
        var ka = FontMapper.NormalizeKey(a);
        var kb = FontMapper.NormalizeKey(b);
        return ka == kb || ka.Contains(kb) || kb.Contains(ka);
    }

    static TextRun MakeRun(List<OpInfo> list)
    {
        var text = string.Concat(list.Select((o, i) =>
        {
            if (i == 0) return o.Text;
            // 이전과 간격이 있으면 공백 보충 (이미 Collector가 넣은 경우도 있음)
            return o.Text.StartsWith(' ') || list[i - 1].Text.EndsWith(' ') ? o.Text : " " + o.Text;
        }));
        // 이중 공백 정리
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return new TextRun(
            list,
            text.TrimEnd(),
            list.Min(o => o.Left), list.Max(o => o.Right),
            list.Min(o => o.Bottom), list.Max(o => o.Top),
            list[0].FontName, list[0].Bold, list[0].Italic);
    }

    sealed class Acc
    {
        public StringBuilder Text = new();
        public double L = double.MaxValue, R = double.MinValue, B = double.MaxValue, T = double.MinValue;
        public double EndX = double.NaN, EndY;
        public string Font = "";
        public bool Bold, Italic, Invisible;
    }

    sealed class Collector : IEventListener
    {
        public int Current = -1, Count, ShowNest, FormDepth;
        public readonly List<string> Kinds = new();
        public readonly Dictionary<int, Acc> Items = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || Current < 0) return;
            var ri = (TextRenderInfo)data;
            var txt = ri.GetText();
            if (string.IsNullOrEmpty(txt)) return;

            var s0 = ri.GetBaseline().GetStartPoint();
            var e0 = ri.GetBaseline().GetEndPoint();
            double top = ri.GetAscentLine().GetStartPoint().Get(1);
            double bottom = ri.GetDescentLine().GetStartPoint().Get(1);
            double h = Math.Max(1, top - bottom);

            if (!Items.TryGetValue(Current, out var a))
            {
                a = Items[Current] = new Acc();
                var names = ri.GetFont()?.GetFontProgram()?.GetFontNames();
                a.Font = names?.GetFontName() ?? "";
                a.Bold = names != null && (names.IsBold() || names.GetFontWeight() >= 600);
                a.Italic = names != null && names.IsItalic();
                a.Invisible = ri.GetTextRenderMode() == 3;
            }
            // TJ 안의 큰 간격(단어 사이)은 문자열에 공백이 없으므로 보충
            if (a.Text.Length > 0 && !double.IsNaN(a.EndX))
            {
                double gap = Math.Abs(s0.Get(1) - a.EndY) > h * 0.5 ? double.MaxValue : s0.Get(0) - a.EndX;
                if (gap > h * 0.2 && a.Text[^1] != ' ' && txt[0] != ' ') a.Text.Append(' ');
            }
            a.Text.Append(txt);
            a.L = Math.Min(a.L, Math.Min(s0.Get(0), e0.Get(0)));
            a.R = Math.Max(a.R, Math.Max(s0.Get(0), e0.Get(0)));
            a.B = Math.Min(a.B, bottom);
            a.T = Math.Max(a.T, top);
            a.EndX = e0.Get(0); a.EndY = e0.Get(1);
        }

        public ICollection<EventType> GetSupportedEvents() => new HashSet<EventType> { EventType.RENDER_TEXT };
    }

    /// <summary>Tj/TJ/'/" 연산자에 번호를 매긴다(페이지 수준만, Form XObject 내부 제외). ' 와 "가 내부에서 호출하는 Tj는 중복 집계하지 않는다.</summary>
    sealed class Tag : IContentOperator
    {
        readonly Collector _c; readonly string _kind;
        public IContentOperator? Inner;
        public Tag(Collector c, string kind) { _c = c; _kind = kind; }

        public void Invoke(PdfCanvasProcessor processor, PdfLiteral op, IList<PdfObject> operands)
        {
            if (_kind == "Do")
            {
                _c.FormDepth++;
                try { Inner?.Invoke(processor, op, operands); } finally { _c.FormDepth--; }
                return;
            }
            if (_c.ShowNest == 0)
            {
                if (_c.FormDepth == 0) { _c.Current = _c.Count++; _c.Kinds.Add(_kind); }
                else _c.Current = -1;
            }
            _c.ShowNest++;
            try { Inner?.Invoke(processor, op, operands); }
            finally { _c.ShowNest--; if (_c.ShowNest == 0) _c.Current = -1; }
        }
    }

    // ───────────────────────── 수정: 콘텐츠 스트림 직접 교체 ─────────────────────────
    /// <summary>텍스트 분석이 실패해도 좌표 변환에 필요한 페이지 정보만 반환.</summary>
    public PageAnalysis Geometry(byte[] bytes, int pageNo)
    {
        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        var page = pdf.GetPage(pageNo);
        var box = page.GetCropBox() ?? page.GetMediaBox();
        return new PageAnalysis(Array.Empty<OpInfo>(), Array.Empty<TextRun>(), 0,
                                box.GetLeft(), box.GetBottom(), box.GetWidth(), box.GetHeight(),
                                page.GetRotation(), pdf.GetNumberOfPages());
    }

    public async Task<(byte[] Data, string Note)> EditTextAsync(byte[] bytes, int page, int expected, TextRun run, string newText,
        string? family, bool bold, bool italic, FontResolver resolver, Action<string>? log = null)
    {
        if (newText.Length == 0)
            return (await DeleteTextAsync(bytes, page, expected, run.Indices), "텍스트를 삭제했습니다.");

        log?.Invoke("폰트 준비 중…");
        var choice = string.IsNullOrWhiteSpace(family)
            ? await resolver.ResolveAsync(run.FontName, run.Bold || bold, run.Italic || italic, newText)
            : await resolver.ResolveFamilyAsync(family, bold, italic, newText);
        log?.Invoke("PDF 내용을 수정하는 중…");
        var data = await Task.Run(() => RewriteRange(bytes, page, expected, run.Indices, newText, choice));
        return (data, $"{FontMapper.Display(run.FontName)} → {choice.Family} ({choice.Variant})");
    }

    public Task<byte[]> DeleteTextAsync(byte[] bytes, int page, int expected, IReadOnlyList<int> indices) =>
        Task.Run(() => RewriteRange(bytes, page, expected, indices, null, null));

    /// <summary>
    /// 여러 텍스트 연산자 범위를 한 번에 처리한다.
    /// newText != null 이면 첫 연산자 자리에 새 텍스트를 넣고 나머지 연산자는 제거.
    /// newText == null 이면 해당 연산자들을 모두 제거.
    /// 인덱스가 높은 쪽부터 처리해 오프셋 밀림을 방지한다.
    /// </summary>
    static byte[] RewriteRange(byte[] src, int pageNo, int expected, IReadOnlyList<int> indices, string? newText, FontChoice? choice)
    {
        if (indices.Count == 0) return src;

        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.SetCloseStream(false);

        using (var pdf = new PdfDocument(new PdfReader(new MemoryStream(src)), writer))
        {
            var page = pdf.GetPage(pageNo);
            var bytes = page.GetContentBytes();
            var shows = Scan(bytes);
            if (shows.Count != expected)
                throw new InvalidOperationException("이 페이지의 구조를 정확히 분석하지 못해 안전하게 편집할 수 없습니다.");

            // 유효성 검사
            var ordered = indices.Distinct().OrderBy(i => i).ToList();
            foreach (var idx in ordered)
            {
                if (idx < 0 || idx >= shows.Count || !shows[idx].Replaceable)
                    throw new InvalidOperationException("이 텍스트는 편집할 수 없는 형태입니다.");
            }

            // 한 번에 재구성: 구간을 앞에서부터 모은 뒤 합친다
            var work = ApplyEdits(bytes, shows, ordered, newText, choice, page, pdf);
            var stream = new PdfStream(work);
            stream.MakeIndirect(pdf);
            page.GetPdfObject().Put(PdfName.Contents, stream);
        }
        return ms.ToArray();
    }

    static byte[] ApplyEdits(byte[] bytes, List<ShowOp> shows, List<int> orderedAsc,
                             string? newText, FontChoice? choice, iText.Kernel.Pdf.PdfPage page, PdfDocument pdf)
    {
        // orderedAsc: 오름차순 인덱스
        // 전략: 첫 연산자 자리에 새 텍스트(또는 빈 문자열)를 넣고, 나머지 연산자 구간은 제거
        var parts = new List<(int Start, int End, byte[]? Insert)>();
        int cursor = 0;

        for (int i = 0; i < orderedAsc.Count; i++)
        {
            int opIndex = orderedAsc[i];
            var sh = shows[opIndex];
            if (sh.Start < 0) continue;

            // 이전 cursor ~ 이 연산자 시작까지 그대로 복사
            if (sh.Start > cursor)
                parts.Add((cursor, sh.Start, null));

            if (i == 0 && newText != null && choice != null)
            {
                // 첫 연산자만 교체
                var font = PdfFontFactory.CreateFont(choice.Program, PdfEncodings.IDENTITY_H,
                                                     PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
                var name = page.GetResources().AddFont(pdf, font);
                var hex = "<" + Convert.ToHexString(font.ConvertToBytes(newText)) + ">";
                var rep = Encoding.ASCII.GetBytes(
                    $"\n/{name.GetValue()} {sh.FontSize} Tf {hex} Tj {sh.FontName} {sh.FontSize} Tf\n");
                parts.Add((sh.Start, sh.End, rep));
            }
            // else: 삭제 → insert 없음

            cursor = sh.End;
        }
        if (cursor < bytes.Length)
            parts.Add((cursor, bytes.Length, null));

        using var outp = new MemoryStream();
        foreach (var (start, end, insert) in parts)
        {
            if (insert != null)
                outp.Write(insert);
            else if (end > start)
                outp.Write(bytes, start, end - start);
        }
        return outp.ToArray();
    }

    // ───────────────────────── 콘텐츠 스트림 토크나이저 ─────────────────────────
    static readonly Regex IdRe = new(@"\sID\s", RegexOptions.Compiled);
    static readonly Regex EiRe = new(@"\sEI(?=\s|$)", RegexOptions.Compiled);

    public static List<ShowOp> Scan(byte[] data)
    {
        var s = Encoding.Latin1.GetString(data);          // 1바이트 = 1문자 → 오프셋이 그대로 바이트 위치
        int n = s.Length, i = 0;
        var res = new List<ShowOp>();
        var fontStack = new Stack<(string?, string?)>();
        var args = new List<(char T, int A, int B)>();
        string? fn = null, fs = null;
        int nest = 0; // q/Q 깊이 (안전용)

        static bool Ws(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\0';
        static bool Dl(char c) => "()<>[]{}/%".IndexOf(c) >= 0;

        while (i < n)
        {
            char c = s[i]; int a = i;
            if (Ws(c)) { i++; continue; }
            if (c == '%') { while (i < n && s[i] != '\n' && s[i] != '\r') i++; continue; }
            if (c == '(')
            {
                // 리터럴 문자열: 이스케이프·중첩 괄호 처리
                int d = 0;
                do
                {
                    if (i >= n) break;
                    if (s[i] == '\\') { i += 2; continue; } // 이스케이프된 문자 건너뛰기
                    if (s[i] == '(') d++;
                    else if (s[i] == ')') d--;
                    i++;
                } while (d > 0 && i < n);
                args.Add(('s', a, i)); continue;
            }
            if (c == '<')
            {
                if (i + 1 < n && s[i + 1] == '<') { i += 2; args.Add(('d', a, i)); continue; }
                // hex 문자열: \> 는 없으므로 단순 탐색
                int k = s.IndexOf('>', i + 1);
                i = k < 0 ? n : k + 1;
                args.Add(('h', a, i)); continue;
            }
            if (c == '>') { i += (i + 1 < n && s[i + 1] == '>') ? 2 : 1; continue; }
            if (c == '[') { i++; args.Add(('[', a, i)); continue; }
            if (c == ']')
            {
                i++;
                int j = args.Count - 1; while (j >= 0 && args[j].T != '[') j--;
                int st = j >= 0 ? args[j].A : a;
                if (j >= 0) args.RemoveRange(j, args.Count - j);
                args.Add(('a', st, i)); continue;
            }
            if (c == '/') { i++; while (i < n && !Ws(s[i]) && !Dl(s[i])) i++; args.Add(('n', a, i)); continue; }

            while (i < n && !Ws(s[i]) && !Dl(s[i])) i++;
            if (i == a) { i++; continue; }
            var w = s.Substring(a, i - a);

            if (char.IsDigit(w[0]) || w[0] is '+' or '-' or '.') { args.Add(('#', a, i)); continue; }

            if (w == "BI")      // 인라인 이미지: 바이너리 데이터 건너뛰기
            {
                var m1 = IdRe.Match(s, i);
                if (m1.Success)
                {
                    var m2 = EiRe.Match(s, m1.Index + m1.Length);
                    i = m2.Success ? m2.Index + m2.Length : n;
                }
                else i = n;
                args.Clear(); continue;
            }

            if (w == "q") { fontStack.Push((fn, fs)); nest++; }
            else if (w == "Q")
            {
                if (fontStack.Count > 0) (fn, fs) = fontStack.Pop();
                if (nest > 0) nest--;
            }
            else if (w == "Tf" && args.Count >= 2)
            {
                var nm = args[^2]; var sz = args[^1];
                fn = s.Substring(nm.A, nm.B - nm.A);
                fs = s.Substring(sz.A, sz.B - sz.A);
            }
            else if (w == "Tj" || w == "TJ")
            {
                if (args.Count > 0) res.Add(new ShowOp(args[^1].A, i, fn, fs, fn != null && fs != null));
                else res.Add(new ShowOp(-1, i, fn, fs, false));
            }
            else if (w == "'" || w == "\"") res.Add(new ShowOp(-1, i, fn, fs, false));

            args.Clear();
        }
        return res;
    }
}
