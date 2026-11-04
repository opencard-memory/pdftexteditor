using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace PdfTextEditor.Pdf;

public sealed record SearchHit(int Page, string Text, double Left, double Right, double Bottom, double Top);

/// <summary>문서 전체에서 문자열을 찾아 위치와 함께 반환한다.</summary>
public static class PdfSearch
{
    public static List<SearchHit> Find(byte[] pdf, string query, bool matchCase = false)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query) || pdf.Length == 0) return hits;

        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        int n = doc.GetNumberOfPages();
        for (int p = 1; p <= n; p++)
        {
            var page = doc.GetPage(p);
            var listener = new ChunkListener();
            var processor = new PdfCanvasProcessor(listener);
            processor.ProcessPageContent(page);

            foreach (var c in listener.Chunks)
            {
                if (string.IsNullOrEmpty(c.Text)) continue;
                int start = 0;
                while (true)
                {
                    int idx = matchCase
                        ? c.Text.IndexOf(query, start, StringComparison.Ordinal)
                        : c.Text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;

                    // 대략적 가로 위치: 청크 폭을 글자 수로 나눔
                    double w = Math.Max(1, c.Right - c.Left);
                    double ratioL = (double)idx / Math.Max(1, c.Text.Length);
                    double ratioR = (double)(idx + query.Length) / Math.Max(1, c.Text.Length);
                    double left = c.Left + w * ratioL;
                    double right = c.Left + w * ratioR;
                    hits.Add(new SearchHit(p, c.Text.Substring(idx, Math.Min(query.Length, c.Text.Length - idx)),
                                           left, right, c.Bottom, c.Top));
                    start = idx + Math.Max(1, query.Length);
                }
            }
        }
        return hits;
    }

    /// <summary>페이지별 전체 텍스트 (Word 내보내기용).</summary>
    public static List<string> ExtractPageTexts(byte[] pdf)
    {
        var list = new List<string>();
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        for (int p = 1; p <= doc.GetNumberOfPages(); p++)
        {
            var strategy = new SimpleTextExtractionStrategy();
            var text = PdfTextExtractor.GetTextFromPage(doc.GetPage(p), strategy) ?? "";
            list.Add(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        }
        return list;
    }

    sealed class Chunk
    {
        public string Text = "";
        public double Left, Right, Bottom, Top;
    }

    sealed class ChunkListener : IEventListener
    {
        public readonly List<Chunk> Chunks = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT) return;
            var ri = (TextRenderInfo)data;
            var t = ri.GetText();
            if (string.IsNullOrEmpty(t)) return;
            var s0 = ri.GetBaseline().GetStartPoint();
            var e0 = ri.GetBaseline().GetEndPoint();
            double top = ri.GetAscentLine().GetStartPoint().Get(1);
            double bottom = ri.GetDescentLine().GetStartPoint().Get(1);
            Chunks.Add(new Chunk
            {
                Text = t,
                Left = Math.Min(s0.Get(0), e0.Get(0)),
                Right = Math.Max(s0.Get(0), e0.Get(0)),
                Bottom = bottom,
                Top = top,
            });
        }

        public ICollection<EventType> GetSupportedEvents() =>
            new HashSet<EventType> { EventType.RENDER_TEXT };
    }
}
