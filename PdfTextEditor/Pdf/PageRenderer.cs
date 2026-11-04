using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace PdfTextEditor.Pdf;

/// <summary>Windows 내장 PDF 렌더러(Windows.Data.Pdf)로 페이지를 비트맵으로 그린다.</summary>
public static class PageRenderer
{
    public static async Task<BitmapSource> RenderAsync(byte[] pdf, int pageNo, double zoom)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(pdf.AsBuffer());
        input.Seek(0);

        var doc = await PdfDocument.LoadFromStreamAsync(input);
        using var page = doc.GetPage((uint)(pageNo - 1));
        var opt = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, page.Size.Width * zoom),
            DestinationHeight = (uint)Math.Max(1, page.Size.Height * zoom),
        };

        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, opt);
        output.Seek(0);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = output.AsStreamForRead();
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
