using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace PdfTextEditor.Pdf;

/// <summary>PDF 텍스트를 간단한 Word(.docx) 문서로 내보낸다.</summary>
public static class PdfExport
{
    public static void ToWord(byte[] pdf, string docxPath)
    {
        var pages = PdfSearch.ExtractPageTexts(pdf);
        var paragraphs = new List<string>();
        for (int i = 0; i < pages.Count; i++)
        {
            if (i > 0) paragraphs.Add(""); // 페이지 구분용 빈 줄
            paragraphs.Add($"—— 페이지 {i + 1} ——");
            foreach (var line in pages[i].Split('\n'))
            {
                var t = line.TrimEnd();
                if (t.Length > 0) paragraphs.Add(t);
            }
        }
        if (paragraphs.Count == 0) paragraphs.Add("(추출된 텍스트가 없습니다)");

        WriteDocx(docxPath, paragraphs);
    }

    static void WriteDocx(string path, List<string> paragraphs)
    {
        // 최소 OOXML docx 구성
        var ns = XNamespace.Get("http://schemas.openxmlformats.org/wordprocessingml/2006/main");
        var relNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships");
        var ctNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types");

        var body = new XElement(ns + "body");
        foreach (var p in paragraphs)
        {
            body.Add(new XElement(ns + "p",
                new XElement(ns + "r",
                    new XElement(ns + "rPr",
                        new XElement(ns + "rFonts",
                            new XAttribute(ns + "ascii", "Malgun Gothic"),
                            new XAttribute(ns + "hAnsi", "Malgun Gothic"),
                            new XAttribute(ns + "eastAsia", "Malgun Gothic")),
                        new XElement(ns + "sz", new XAttribute(ns + "val", "22"))),
                    new XElement(ns + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        p))));
        }
        body.Add(new XElement(ns + "sectPr",
            new XElement(ns + "pgSz", new XAttribute(ns + "w", "11906"), new XAttribute(ns + "h", "16838")),
            new XElement(ns + "pgMar",
                new XAttribute(ns + "top", "1440"), new XAttribute(ns + "right", "1440"),
                new XAttribute(ns + "bottom", "1440"), new XAttribute(ns + "left", "1440"))));

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(ns + "document",
                new XAttribute(XNamespace.Xmlns + "w", ns),
                body));

        var contentTypes = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(ctNs + "Types",
                new XElement(ctNs + "Default",
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ctNs + "Default",
                    new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")),
                new XElement(ctNs + "Override",
                    new XAttribute("PartName", "/word/document.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"))));

        var rels = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(relNs + "Relationships",
                new XElement(relNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "word/document.xml"))));

        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(zip, "[Content_Types].xml", contentTypes);
        WriteEntry(zip, "_rels/.rels", rels);
        WriteEntry(zip, "word/document.xml", document);
    }

    static void WriteEntry(ZipArchive zip, string name, XDocument doc)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        using var w = new StreamWriter(s, new UTF8Encoding(false));
        doc.Save(w);
    }
}
