using iText.Bouncycastle.Crypto;
using iText.Bouncycastle.X509;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms.Form.Element;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Signatures;
using Org.BouncyCastle.Pkcs;

namespace PdfTextEditor.Pdf;

/// <summary>PKCS#12(.pfx/.p12) 인증서로 PDF에 전자서명한다. (iText 8+/9 SignerProperties API)</summary>
public static class PdfSign
{
    public static byte[] Sign(byte[] pdf, string pfxPath, string password,
                              string reason, string location, string contact,
                              int? page = null, Rectangle? rect = null)
    {
        if (!File.Exists(pfxPath))
            throw new FileNotFoundException("인증서 파일을 찾을 수 없습니다.", pfxPath);

        var pkcs = new Pkcs12StoreBuilder().Build();
        using (var fs = File.OpenRead(pfxPath))
            pkcs.Load(fs, (password ?? "").ToCharArray());

        string? alias = null;
        foreach (string a in pkcs.Aliases)
        {
            if (pkcs.IsKeyEntry(a)) { alias = a; break; }
        }
        if (alias == null)
            throw new InvalidOperationException("인증서 파일에 개인키가 없습니다. .pfx/.p12 파일을 확인하세요.");

        var pkEntry = pkcs.GetKey(alias);
        var chainEntries = pkcs.GetCertificateChain(alias);
        if (pkEntry == null || chainEntries == null || chainEntries.Length == 0)
            throw new InvalidOperationException("인증서 체인을 읽을 수 없습니다.");

        var privateKey = new PrivateKeyBC(pkEntry.Key);
        var chain = new IX509Certificate[chainEntries.Length];
        for (int i = 0; i < chainEntries.Length; i++)
            chain[i] = new X509CertificateBC(chainEntries[i].Certificate);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var reader = new PdfReader(input);
        var signer = new PdfSigner(reader, output, new StampingProperties().UseAppendMode());

        int pageNum = page ?? Math.Max(1, signer.GetDocument().GetNumberOfPages());
        pageNum = Math.Clamp(pageNum, 1, signer.GetDocument().GetNumberOfPages());
        var box = signer.GetDocument().GetPage(pageNum).GetPageSize();
        var fieldRect = rect ?? new Rectangle(
            box.GetRight() - 180, box.GetBottom() + 24, 160, 50);

        string fieldName = "Sig" + DateTime.Now.ToString("yyyyMMddHHmmss");
        string layerText = BuildLayerText(chain[0], reason);

        var appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID)
            .SetContent(layerText);

        var props = new SignerProperties()
            .SetFieldName(fieldName)
            .SetReason(reason ?? "문서 승인")
            .SetLocation(location ?? "")
            .SetContact(contact ?? "")
            .SetPageNumber(pageNum)
            .SetPageRect(fieldRect)
            .SetSignatureAppearance(appearance);

        signer.SetSignerProperties(props);

        // BouncyCastleDigest 는 iText.Signatures 네임스페이스에 있음
        IExternalSignature pks = new PrivateKeySignature(privateKey, "SHA-256");
        signer.SignDetached(new BouncyCastleDigest(), pks, chain, null, null, null, 0, PdfSigner.CryptoStandard.CMS);

        return output.ToArray();
    }

    static string BuildLayerText(IX509Certificate cert, string? reason)
    {
        string subj = "Signer";
        try
        {
            subj = cert.ToString() ?? subj;
            foreach (var part in subj.Split(',', StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                {
                    subj = part[3..].Trim();
                    break;
                }
            }
            if (subj.Length > 60) subj = subj[..60];
        }
        catch { }

        return $"Digitally signed by {subj}\n{DateTime.Now:yyyy-MM-dd HH:mm}"
               + (string.IsNullOrWhiteSpace(reason) ? "" : $"\n{reason}");
    }

    public static string HelpText =>
        "PKCS#12 형식의 인증서 파일(.pfx / .p12)과 비밀번호가 필요합니다.\n" +
        "Windows 인증서 관리자에서 개인 인증서를 내보내기 한 뒤 사용할 수 있습니다.\n" +
        "서명은 문서 끝에 추가(append)되며, 기존 내용을 덮어쓰지 않습니다.";
}
