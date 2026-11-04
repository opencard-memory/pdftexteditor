using System.Text;
using System.Text.RegularExpressions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;

namespace PdfTextEditor.Pdf;

public sealed record ImageInfo(
    int Index,
    double Left, double Right, double Bottom, double Top,
    byte[]? Bytes,
    string? Name,
    /// <summary>콘텐츠 스트림에서 이 이미지를 그리는 구간의 바이트 시작(포함).</summary>
    int StreamStart = -1,
    /// <summary>콘텐츠 스트림에서 이 이미지를 그리는 구간의 바이트 끝(미포함).</summary>
    int StreamEnd = -1);

/// <summary>페이지에 그려진 이미지를 찾고, 스트림에서 완전히 제거·교체한다.</summary>
public static class PdfImages
{
    public static List<ImageInfo> List(byte[] pdf, int pageNo)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var page = doc.GetPage(pageNo);
        var content = page.GetContentBytes();

        // 1) 렌더 이벤트로 위치·바이트·이름
        var listener = new ImageListener();
        new PdfCanvasProcessor(listener).ProcessPageContent(page);

        // 2) 콘텐츠 스트림에서 /Name Do (및 감싼 q…Q) 구간 찾기
        var ranges = FindImageDoRanges(content);
        // 이름 순서가 렌더 순서와 맞도록 매칭
        var byNameQueue = new Dictionary<string, Queue<(int S, int E)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, s, e) in ranges)
        {
            if (!byNameQueue.TryGetValue(name, out var q))
                byNameQueue[name] = q = new Queue<(int, int)>();
            q.Enqueue((s, e));
        }

        var result = new List<ImageInfo>();
        for (int i = 0; i < listener.Items.Count; i++)
        {
            var it = listener.Items[i];
            int start = -1, end = -1;
            string? name = it.Name;
            if (name != null)
            {
                // "/Im0" 또는 "Im0" 형태 통일
                var key = name.StartsWith('/') ? name[1..] : name;
                if (byNameQueue.TryGetValue(key, out var q) && q.Count > 0)
                {
                    var (s, e) = q.Dequeue();
                    start = s; end = e;
                }
                else if (byNameQueue.TryGetValue("/" + key, out q) && q.Count > 0)
                {
                    var (s, e) = q.Dequeue();
                    start = s; end = e;
                }
            }
            // 이름으로 못 찾으면 등장 순서(i번째 Do)로 매칭
            if (start < 0 && i < ranges.Count)
            {
                start = ranges[i].Start;
                end = ranges[i].End;
                name ??= ranges[i].Name;
            }
            result.Add(it with { StreamStart = start, StreamEnd = end, Name = name ?? it.Name });
        }
        return result;
    }

    /// <summary>콘텐츠 스트림에서 이미지 Do 연산자를 제거해 완전히 삭제한다.</summary>
    public static byte[] Delete(byte[] pdf, int pageNo, ImageInfo img)
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.SetCloseStream(false);

        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)), writer))
        {
            var page = doc.GetPage(pageNo);
            var bytes = page.GetContentBytes();

            int start = img.StreamStart, end = img.StreamEnd;
            if (start < 0 || end <= start || end > bytes.Length)
            {
                // 스트림 위치를 다시 스캔
                var list = List(pdf, pageNo);
                var match = list.FirstOrDefault(x => x.Index == img.Index)
                         ?? list.FirstOrDefault(x =>
                                Math.Abs(x.Left - img.Left) < 1 && Math.Abs(x.Bottom - img.Bottom) < 1);
                if (match == null || match.StreamStart < 0)
                    throw new InvalidOperationException(
                        "이 이미지의 스트림 위치를 찾지 못해 완전히 삭제할 수 없습니다. (폼/중첩 XObject 안 이미지일 수 있습니다)");
                start = match.StreamStart;
                end = match.StreamEnd;
            }

            var outp = new MemoryStream();
            outp.Write(bytes, 0, start);
            // 삭제 구간을 공백 하나로 대체(주변 연산자 토큰 경계 유지)
            outp.WriteByte((byte)' ');
            outp.Write(bytes, end, bytes.Length - end);

            var stream = new PdfStream(outp.ToArray());
            stream.MakeIndirect(doc);
            page.GetPdfObject().Put(PdfName.Contents, stream);

            // 페이지 리소스에서 더 이상 안 쓰이면 XObject 항목 제거 시도
            TryRemoveImageResource(page, img.Name);
        }
        return ms.ToArray();
    }

    /// <summary>완전 삭제 후 실패 시에만 쓰는 폴백(흰 가림).</summary>
    public static byte[] Cover(byte[] pdf, int page, double left, double bottom, double width, double height)
    {
        return PdfOps.AddShape(pdf, page, ShapeKind.Rect,
            left, bottom, left + width, bottom + height,
            new Rgb(255, 255, 255), 0.1, 1.0, fill: true);
    }

    /// <summary>기존 이미지를 스트림에서 제거한 뒤 새 위치·크기로 다시 넣는다.</summary>
    public static byte[] Replace(byte[] pdf, int page, ImageInfo img,
        double newLeft, double newBottom, double newWidth, double newHeight)
    {
        if (img.Bytes == null || img.Bytes.Length == 0)
            throw new InvalidOperationException("이 이미지 데이터를 추출할 수 없어 크기를 바꿀 수 없습니다.");
        byte[] basePdf;
        try { basePdf = Delete(pdf, page, img); }
        catch { basePdf = Cover(pdf, page, img.Left, img.Bottom, img.Right - img.Left, img.Top - img.Bottom); }
        return PdfOps.AddImage(basePdf, page, img.Bytes, newLeft, newBottom, newWidth, newHeight);
    }

    // ───────── 스트림 스캔: q … /Name Do … Q 또는 /Name Do ─────────
    static readonly Regex DoRe = new(@"/(?<name>[A-Za-z][A-Za-z0-9._\-]*)\s+Do\b", RegexOptions.Compiled);

    static List<(string Name, int Start, int End)> FindImageDoRanges(byte[] data)
    {
        var s = Encoding.Latin1.GetString(data);
        var list = new List<(string, int, int)>();
        foreach (Match m in DoRe.Matches(s))
        {
            int doStart = m.Index;
            int doEnd = m.Index + m.Length;
            // 앞으로 가서 가장 가까운 q 찾기 (같은 깊이)
            int start = doStart;
            // Do 앞에서 공백 무시하고 q 블록 시작 탐색
            int qPos = FindPrecedingToken(s, doStart, "q");
            int qEnd = FindFollowingToken(s, doEnd, "Q");
            if (qPos >= 0 && qEnd > doEnd)
            {
                // q와 Q 사이에 다른 q/Q 균형 확인
                if (IsBalancedQ(s, qPos, qEnd + 1))
                {
                    start = qPos;
                    doEnd = qEnd + 1;
                }
            }
            // cm 변환만 있는 경우도 있으므로, q 없으면 /Name Do 만 제거
            list.Add((m.Groups["name"].Value, start, doEnd));
        }
        return list;
    }

    static bool IsWsOrDelim(char c) =>
        char.IsWhiteSpace(c) || "()<>[]{}/%".IndexOf(c) >= 0;

    static int FindPrecedingToken(string s, int from, string token)
    {
        // from 앞에서 token이 독립 토큰으로 나오는지
        int i = from - 1;
        while (i >= 0 && char.IsWhiteSpace(s[i])) i--;
        if (i < token.Length - 1) return -1;
        // 역으로 토큰 매칭
        for (int k = token.Length - 1; k >= 0; k--)
        {
            if (i - (token.Length - 1 - k) < 0 || s[i - (token.Length - 1 - k)] != token[k])
                return -1;
        }
        int start = i - token.Length + 1;
        if (start > 0 && !IsWsOrDelim(s[start - 1]) && !char.IsWhiteSpace(s[start - 1]))
            return -1;
        return start;
    }

    static int FindFollowingToken(string s, int from, string token)
    {
        int i = from;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i + token.Length > s.Length) return -1;
        for (int k = 0; k < token.Length; k++)
            if (s[i + k] != token[k]) return -1;
        int end = i + token.Length - 1;
        if (end + 1 < s.Length && !IsWsOrDelim(s[end + 1]) && !char.IsWhiteSpace(s[end + 1]))
            return -1;
        return end;
    }

    static bool IsBalancedQ(string s, int qPos, int afterQ)
    {
        // qPos의 q부터 afterQ 직전까지 depth
        int depth = 0;
        int i = qPos;
        while (i < afterQ && i < s.Length)
        {
            if (IsTokenAt(s, i, "q")) { depth++; i += 1; continue; }
            if (IsTokenAt(s, i, "Q")) { depth--; i += 1; if (depth < 0) return false; continue; }
            i++;
        }
        return depth == 0;
    }

    static bool IsTokenAt(string s, int i, string token)
    {
        if (i + token.Length > s.Length) return false;
        for (int k = 0; k < token.Length; k++)
            if (s[i + k] != token[k]) return false;
        if (i > 0 && !IsWsOrDelim(s[i - 1]) && !char.IsWhiteSpace(s[i - 1])) return false;
        int e = i + token.Length;
        if (e < s.Length && !IsWsOrDelim(s[e]) && !char.IsWhiteSpace(s[e])) return false;
        return true;
    }

    static void TryRemoveImageResource(iText.Kernel.Pdf.PdfPage page, string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var res = page.GetResources();
            var key = name.StartsWith('/') ? new PdfName(name[1..]) : new PdfName(name);
            // XObject 딕셔너리에서 제거
            var xobj = res.GetResource(PdfName.XObject);
            if (xobj is PdfDictionary dict && dict.ContainsKey(key))
                dict.Remove(key);
        }
        catch { /* 다른 페이지/폼에서 쓰일 수 있으므로 실패해도 무시 */ }
    }

    sealed class ImageListener : IEventListener
    {
        public readonly List<ImageInfo> Items = new();
        int _i;

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_IMAGE) return;
            var ri = (ImageRenderInfo)data;
            try
            {
                var ctm = ri.GetImageCtm();
                double e = ctm.Get(iText.Kernel.Geom.Matrix.I31);
                double f = ctm.Get(iText.Kernel.Geom.Matrix.I32);
                double a = ctm.Get(iText.Kernel.Geom.Matrix.I11);
                double b = ctm.Get(iText.Kernel.Geom.Matrix.I12);
                double c = ctm.Get(iText.Kernel.Geom.Matrix.I21);
                double d = ctm.Get(iText.Kernel.Geom.Matrix.I22);
                double x0 = e, y0 = f;
                double x1 = e + a, y1 = f + b;
                double x2 = e + a + c, y2 = f + b + d;
                double x3 = e + c, y3 = f + d;
                double left = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
                double right = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
                double bottom = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
                double top = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));

                byte[]? bytes = null;
                try
                {
                    var img = ri.GetImage();
                    if (img != null)
                    {
                        try { bytes = img.GetImageBytes(true); }
                        catch { try { bytes = img.GetImageBytes(false); } catch { bytes = null; } }
                    }
                }
                catch { }

                string? name = null;
                try
                {
                    var rn = ri.GetImageResourceName();
                    name = rn?.GetValue() ?? rn?.ToString();
                }
                catch { }

                Items.Add(new ImageInfo(_i++, left, right, bottom, top, bytes, name));
            }
            catch { }
        }

        public ICollection<EventType> GetSupportedEvents() =>
            new HashSet<EventType> { EventType.RENDER_IMAGE };
    }
}
