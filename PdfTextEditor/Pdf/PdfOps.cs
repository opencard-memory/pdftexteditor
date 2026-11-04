using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Pdf.Navigation;
using iText.Kernel.Utils;
using iText.IO.Font;
using PdfTextEditor.Fonts;

namespace PdfTextEditor.Pdf;

public enum ShapeKind { Rect, Ellipse, Line, Arrow }
public enum MarkKind { Highlight, Underline, Strike }

/// <summary>페이지 구조/콘텐츠 추가 기능. 모두 "바이트 → 새 바이트" 형태라 되돌리기가 간단하다. 좌표는 PDF 사용자 공간(pt, 원점 좌하단).</summary>
public static class PdfOps
{
    static byte[] Stamp(byte[] src, Action<PdfDocument> act)
    {
        using var ms = new MemoryStream();
        var w = new PdfWriter(ms);
        w.SetCloseStream(false);
        using (var pdf = new PdfDocument(new PdfReader(new MemoryStream(src)), w)) act(pdf);
        return ms.ToArray();
    }

    static PdfFont MakeFont(FontChoice c) =>
        PdfFontFactory.CreateFont(c.Program, PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);

    static DeviceRgb C(Rgb c) => new DeviceRgb(c.R, c.G, c.B);

    static PdfExtGState Alpha(double a) =>
        new PdfExtGState().SetFillOpacity((float)a).SetStrokeOpacity((float)a);

    public static int PageCount(byte[] b)
    {
        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(b)));
        return pdf.GetNumberOfPages();
    }

    /// <summary>빈 PDF 문서를 만든다. sizeName: A4, Letter, Legal, A3, A5.</summary>
    public static byte[] CreateBlank(int pageCount = 1, string sizeName = "A4", bool landscape = false)
    {
        pageCount = Math.Clamp(pageCount, 1, 200);
        PageSize size = sizeName.Trim().ToUpperInvariant() switch
        {
            "LETTER" => PageSize.LETTER,
            "LEGAL" => PageSize.LEGAL,
            "A3" => PageSize.A3,
            "A5" => PageSize.A5,
            _ => PageSize.A4,
        };
        if (landscape) size = size.Rotate();

        using var ms = new MemoryStream();
        var w = new PdfWriter(ms);
        w.SetCloseStream(false);
        using (var pdf = new PdfDocument(w))
        {
            for (int i = 0; i < pageCount; i++)
                pdf.AddNewPage(new PageSize(size));
        }
        return ms.ToArray();
    }

    // ───────────── 페이지 구조 ─────────────
    public static byte[] Rotate(byte[] b, int page, int delta) => Stamp(b, p =>
    {
        var pg = p.GetPage(page);
        pg.SetRotation(((pg.GetRotation() + delta) % 360 + 360) % 360);
    });

    public static byte[] DeletePage(byte[] b, int page) => Stamp(b, p => p.RemovePage(page));

    public static byte[] InsertBlank(byte[] b, int after) => Stamp(b, p =>
    {
        int n = p.GetNumberOfPages();
        var size = p.GetPage(Math.Max(1, Math.Min(after, n))).GetPageSize();
        p.AddNewPage(after + 1, new PageSize(size));
    });

    /// <summary>pages(1부터, 순서 그대로)로 새 문서를 만든다. 이동/추출/분할에 사용.</summary>
    public static byte[] Pick(byte[] b, IList<int> pages)
    {
        using var ms = new MemoryStream();
        var w = new PdfWriter(ms);
        w.SetCloseStream(false);
        var dest = new PdfDocument(w);
        using (var src = new PdfDocument(new PdfReader(new MemoryStream(b))))
        {
            var m = new PdfMerger(dest);
            m.Merge(src, pages);
            m.Close();
        }
        return ms.ToArray();
    }

    public static byte[] InsertPdf(byte[] cur, byte[] other, int after)
    {
        using var ms = new MemoryStream();
        var w = new PdfWriter(ms);
        w.SetCloseStream(false);
        var dest = new PdfDocument(w);
        using (var a = new PdfDocument(new PdfReader(new MemoryStream(cur))))
        using (var o = new PdfDocument(new PdfReader(new MemoryStream(other))))
        {
            var m = new PdfMerger(dest);
            int n = a.GetNumberOfPages();
            if (after > 0) m.Merge(a, 1, Math.Min(after, n));
            m.Merge(o, 1, o.GetNumberOfPages());
            if (after < n) m.Merge(a, after + 1, n);
            m.Close();
        }
        return ms.ToArray();
    }

    public static int SplitPages(byte[] b, string folder, string baseName)
    {
        int n = PageCount(b);
        for (int i = 1; i <= n; i++)
            File.WriteAllBytes(System.IO.Path.Combine(folder, $"{baseName}_{i:000}.pdf"), Pick(b, new List<int> { i }));
        return n;
    }

    /// <summary>여백(pt)만큼 잘라낸다. page가 0이면 모든 페이지.</summary>
    public static byte[] Crop(byte[] b, int page, double left, double top, double right, double bottom) => Stamp(b, p =>
    {
        for (int i = 1; i <= p.GetNumberOfPages(); i++)
        {
            if (page != 0 && i != page) continue;
            var pg = p.GetPage(i);
            var box = pg.GetCropBox();
            float w = box.GetWidth() - (float)(left + right), h = box.GetHeight() - (float)(top + bottom);
            if (w < 20 || h < 20) throw new InvalidOperationException("자를 여백이 너무 큽니다.");
            pg.SetCropBox(new Rectangle(box.GetLeft() + (float)left, box.GetBottom() + (float)bottom, w, h));
        }
    });

    /// <summary>저장 시 전체 압축(PDF 1.5 객체 스트림)으로 파일 크기를 줄인다.</summary>
    public static byte[] Optimize(byte[] b)
    {
        using var ms = new MemoryStream();
        var w = new PdfWriter(ms, new WriterProperties().SetFullCompressionMode(true).SetCompressionLevel(9));
        w.SetCloseStream(false);
        using (var pdf = new PdfDocument(new PdfReader(new MemoryStream(b)), w)) { }
        return ms.ToArray();
    }

    // ───────────── 콘텐츠 추가 ─────────────
    public static byte[] AddText(byte[] b, int page, double x, double yTop, string text, FontChoice fc,
                                 double size, Rgb color, double opacity) => Stamp(b, p =>
    {
        var font = MakeFont(fc);
        var c = new PdfCanvas(p.GetPage(page));
        float fs = (float)size, lead = fs * 1.25f, yy = (float)yTop - fs;
        c.SaveState();
        if (opacity < 1) c.SetExtGState(Alpha(opacity));
        c.BeginText().SetFontAndSize(font, fs).SetFillColor(C(color));
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            c.SetTextMatrix(1f, 0f, 0f, 1f, (float)x, yy).ShowText(line);
            yy -= lead;
        }
        c.EndText().RestoreState();
    });

    public static byte[] AddImage(byte[] b, int page, byte[] image, double x, double y, double w, double h) => Stamp(b, p =>
    {
        var data = ImageDataFactory.Create(image);
        new PdfCanvas(p.GetPage(page)).AddImageFittedIntoRectangle(data, new Rectangle((float)x, (float)y, (float)w, (float)h), false);
    });

    public static byte[] AddShape(byte[] b, int page, ShapeKind kind, double x1, double y1, double x2, double y2,
                                  Rgb color, double width, double opacity, bool fill) => Stamp(b, p =>
    {
        var c = new PdfCanvas(p.GetPage(page));
        float fx1 = (float)x1, fy1 = (float)y1, fx2 = (float)x2, fy2 = (float)y2, lw = (float)Math.Max(0.25, width);
        c.SaveState().SetExtGState(Alpha(opacity)).SetStrokeColor(C(color)).SetFillColor(C(color)).SetLineWidth(lw);
        switch (kind)
        {
            case ShapeKind.Rect:
                c.Rectangle(Math.Min(fx1, fx2), Math.Min(fy1, fy2), Math.Abs(fx2 - fx1), Math.Abs(fy2 - fy1));
                if (fill) c.FillStroke(); else c.Stroke();
                break;
            case ShapeKind.Ellipse:
                c.Ellipse(Math.Min(fx1, fx2), Math.Min(fy1, fy2), Math.Max(fx1, fx2), Math.Max(fy1, fy2));
                if (fill) c.FillStroke(); else c.Stroke();
                break;
            case ShapeKind.Line:
                c.MoveTo(fx1, fy1).LineTo(fx2, fy2).Stroke();
                break;
            case ShapeKind.Arrow:
                {
                    c.MoveTo(fx1, fy1).LineTo(fx2, fy2).Stroke();
                    float dx = fx2 - fx1, dy = fy2 - fy1, len = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (len > 1)
                    {
                        float ux = dx / len, uy = dy / len, hl = Math.Max(8f, lw * 4f), hw = hl * 0.5f;
                        float bx = fx2 - ux * hl, by = fy2 - uy * hl;
                        c.MoveTo(fx2, fy2).LineTo(bx - uy * hw, by + ux * hw).LineTo(bx + uy * hw, by - ux * hw).ClosePath().Fill();
                    }
                    break;
                }
        }
        c.RestoreState();
    });

    public static byte[] AddInk(byte[] b, int page, IList<(double X, double Y)> pts, Rgb color, double width, double opacity) => Stamp(b, p =>
    {
        if (pts.Count < 2) return;
        var c = new PdfCanvas(p.GetPage(page));
        c.SaveState().SetExtGState(Alpha(opacity)).SetStrokeColor(C(color)).SetLineWidth((float)Math.Max(0.25, width))
         .SetLineCapStyle(PdfCanvasConstants.LineCapStyle.ROUND).SetLineJoinStyle(PdfCanvasConstants.LineJoinStyle.ROUND);
        c.MoveTo((float)pts[0].X, (float)pts[0].Y);
        for (int i = 1; i < pts.Count; i++) c.LineTo((float)pts[i].X, (float)pts[i].Y);
        c.Stroke().RestoreState();
    });

    public static byte[] AddMarkup(byte[] b, int page, MarkKind kind, double x1, double y1, double x2, double y2,
                                   Rgb color, double opacity) => Stamp(b, p =>
    {
        var c = new PdfCanvas(p.GetPage(page));
        float l = (float)Math.Min(x1, x2), r = (float)Math.Max(x1, x2), bt = (float)Math.Min(y1, y2), t = (float)Math.Max(y1, y2);
        c.SaveState();
        if (kind == MarkKind.Highlight)
            c.SetExtGState(new PdfExtGState().SetFillOpacity((float)opacity).SetBlendMode(PdfName.Multiply))
             .SetFillColor(C(color)).Rectangle(l, bt, r - l, t - bt).Fill();
        else
        {
            float y = kind == MarkKind.Underline ? bt + 1f : (bt + t) / 2f;
            c.SetExtGState(Alpha(opacity)).SetStrokeColor(C(color)).SetLineWidth(1.2f).MoveTo(l, y).LineTo(r, y).Stroke();
        }
        c.RestoreState();
    });

    /// <summary>스티커 메모: 보이는 노란 아이콘 + 주석(Text) 개체(내용 포함).</summary>
    public static byte[] AddNote(byte[] b, int page, double x, double yTop, string text) => Stamp(b, p =>
    {
        var pg = p.GetPage(page);
        var rect = new Rectangle((float)x, (float)yTop - 18f, 18f, 18f);
        new PdfCanvas(pg).SaveState().SetFillColor(new DeviceRgb(255, 214, 0)).SetStrokeColor(new DeviceRgb(180, 140, 0))
                         .SetLineWidth(0.8f).Rectangle(rect).FillStroke().RestoreState();
        var a = new PdfTextAnnotation(rect);
        a.SetContents(text);
        pg.AddAnnotation(a);
    });

    /// <summary>링크: target이 숫자면 해당 페이지로, 아니면 URL로 이동.</summary>
    public static byte[] AddLink(byte[] b, int page, double x1, double y1, double x2, double y2, string target) => Stamp(b, p =>
    {
        var rect = new Rectangle((float)Math.Min(x1, x2), (float)Math.Min(y1, y2), (float)Math.Abs(x2 - x1), (float)Math.Abs(y2 - y1));
        var link = new PdfLinkAnnotation(rect);
        link.SetBorder(new PdfArray(new float[] { 0f, 0f, 0f }));
        if (int.TryParse(target.Trim(), out var pn) && pn >= 1 && pn <= p.GetNumberOfPages())
            link.SetAction(PdfAction.CreateGoTo(PdfExplicitDestination.CreateFit(p.GetPage(pn))));
        else
            link.SetAction(PdfAction.CreateURI(target.Contains("://") ? target.Trim() : "https://" + target.Trim()));
        p.GetPage(page).AddAnnotation(link);
    });

    // ───────────── 문서 전체 ─────────────
    public static byte[] Watermark(byte[] b, string text, FontChoice fc, double size, double angle, double opacity, Rgb color) => Stamp(b, p =>
    {
        var font = MakeFont(fc);
        float fs = (float)size, w = font.GetWidth(text, fs);
        double rad = angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        for (int i = 1; i <= p.GetNumberOfPages(); i++)
        {
            var pg = p.GetPage(i);
            var box = pg.GetCropBox();
            double cx = box.GetLeft() + box.GetWidth() / 2, cy = box.GetBottom() + box.GetHeight() / 2;
            float x0 = (float)(cx - (w / 2) * cos + fs * 0.35 * sin), y0 = (float)(cy - (w / 2) * sin - fs * 0.35 * cos);
            new PdfCanvas(pg).SaveState().SetExtGState(Alpha(opacity)).BeginText().SetFontAndSize(font, fs).SetFillColor(C(color))
                .SetTextMatrix((float)cos, (float)sin, (float)-sin, (float)cos, x0, y0).ShowText(text).EndText().RestoreState();
        }
    });

    /// <summary>머리글/바닥글. 각 문자열의 {n}=현재 쪽, {N}=전체 쪽. texts: [머리 좌, 머리 중, 머리 우, 바닥 좌, 바닥 중, 바닥 우]</summary>
    public static byte[] HeaderFooter(byte[] b, string[] texts, FontChoice fc, double size, Rgb color, double margin) => Stamp(b, p =>
    {
        var font = MakeFont(fc);
        float fs = (float)size, m = (float)margin;
        int total = p.GetNumberOfPages();
        for (int i = 1; i <= total; i++)
        {
            var pg = p.GetPage(i);
            var box = pg.GetCropBox();
            var c = new PdfCanvas(pg);
            for (int k = 0; k < 6; k++)
            {
                var t = texts[k].Replace("{n}", i.ToString()).Replace("{N}", total.ToString());
                if (t.Length == 0) continue;
                float w = font.GetWidth(t, fs), align = k % 3;
                float x = align == 0 ? box.GetLeft() + m : align == 1 ? box.GetLeft() + (box.GetWidth() - w) / 2 : box.GetRight() - m - w;
                float y = k < 3 ? box.GetTop() - m - fs : box.GetBottom() + m;
                c.SaveState().BeginText().SetFontAndSize(font, fs).SetFillColor(C(color)).SetTextMatrix(1f, 0f, 0f, 1f, x, y)
                 .ShowText(t).EndText().RestoreState();
            }
        }
    });

    public static byte[] Background(byte[] b, Rgb color) => Stamp(b, p =>
    {
        for (int i = 1; i <= p.GetNumberOfPages(); i++)
        {
            var pg = p.GetPage(i);
            new PdfCanvas(pg.NewContentStreamBefore(), pg.GetResources(), p)
                .SaveState().SetFillColor(C(color)).Rectangle(pg.GetCropBox()).Fill().RestoreState();
        }
    });

    /// <summary>표 추가. cells는 행 우선, 열 개수는 cols. x,yTop은 표 왼쪽 위(PDF y는 위가 큼).</summary>
    public static byte[] AddTable(byte[] b, int page, double x, double yTop, int cols, string[] cells,
                                  FontChoice fc, double fontSize, Rgb color, double colWidth, double rowHeight,
                                  double borderWidth) => Stamp(b, p =>
    {
        cols = Math.Max(1, cols);
        int rows = Math.Max(1, (cells.Length + cols - 1) / cols);
        var font = MakeFont(fc);
        float fs = (float)fontSize, cw = (float)colWidth, rh = (float)rowHeight;
        float bw = (float)Math.Max(0.25, borderWidth);
        float x0 = (float)x, y0 = (float)yTop;
        var canvas = new PdfCanvas(p.GetPage(page));
        canvas.SaveState().SetStrokeColor(C(color)).SetFillColor(C(color)).SetLineWidth(bw);

        // 격자
        float totalW = cw * cols, totalH = rh * rows;
        for (int r = 0; r <= rows; r++)
        {
            float yy = y0 - r * rh;
            canvas.MoveTo(x0, yy).LineTo(x0 + totalW, yy).Stroke();
        }
        for (int c = 0; c <= cols; c++)
        {
            float xx = x0 + c * cw;
            canvas.MoveTo(xx, y0).LineTo(xx, y0 - totalH).Stroke();
        }

        // 셀 텍스트
        canvas.BeginText().SetFontAndSize(font, fs);
        for (int i = 0; i < cells.Length && i < rows * cols; i++)
        {
            int r = i / cols, c = i % cols;
            string text = cells[i] ?? "";
            if (text.Length == 0) continue;
            float tx = x0 + c * cw + 4;
            float ty = y0 - (r + 1) * rh + (rh - fs) / 2;
            canvas.SetTextMatrix(1, 0, 0, 1, tx, ty).ShowText(text.Length > 80 ? text[..80] : text);
        }
        canvas.EndText().RestoreState();
    });

    /// <summary>막대 그래프. values는 0 이상 숫자, labels는 동일한 개수(선택).</summary>
    public static byte[] AddBarChart(byte[] b, int page, double x, double yBottom, double width, double height,
                                     double[] values, string[] labels, FontChoice fc, Rgb barColor, Rgb axisColor) => Stamp(b, p =>
    {
        if (values.Length == 0) return;
        var font = MakeFont(fc);
        float x0 = (float)x, y0 = (float)yBottom, w = (float)width, h = (float)height;
        double max = values.Max();
        if (max <= 0) max = 1;
        int n = values.Length;
        float gap = w * 0.05f;
        float barW = (w - gap * (n + 1)) / n;
        var c = new PdfCanvas(p.GetPage(page));
        c.SaveState();
        // 축
        c.SetStrokeColor(C(axisColor)).SetLineWidth(1f)
         .MoveTo(x0, y0).LineTo(x0 + w, y0).Stroke()
         .MoveTo(x0, y0).LineTo(x0, y0 + h).Stroke();
        // 막대
        c.SetFillColor(C(barColor));
        for (int i = 0; i < n; i++)
        {
            float bh = (float)(values[i] / max * h * 0.9);
            float bx = x0 + gap + i * (barW + gap);
            c.Rectangle(bx, y0, barW, Math.Max(1, bh)).Fill();
        }
        // 라벨
        c.BeginText().SetFontAndSize(font, 8).SetFillColor(C(axisColor));
        for (int i = 0; i < n; i++)
        {
            string lab = i < labels.Length ? labels[i] : (i + 1).ToString();
            if (lab.Length > 8) lab = lab[..8];
            float bx = x0 + gap + i * (barW + gap) + barW / 2 - font.GetWidth(lab, 8) / 2;
            c.SetTextMatrix(1, 0, 0, 1, bx, y0 - 12).ShowText(lab);
            string val = values[i].ToString("0.##");
            float vx = x0 + gap + i * (barW + gap) + barW / 2 - font.GetWidth(val, 8) / 2;
            float bh = (float)(values[i] / max * h * 0.9);
            c.SetTextMatrix(1, 0, 0, 1, vx, y0 + Math.Max(1, bh) + 2).ShowText(val);
        }
        c.EndText().RestoreState();
    });
}
