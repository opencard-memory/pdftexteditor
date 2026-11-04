namespace PdfTextEditor;

/// <summary>페이지 콘텐츠 스트림의 텍스트 표시 연산자(Tj/TJ) 하나. 좌표는 PDF 사용자 공간(pt).</summary>
public sealed record OpInfo(int Index, string Text, double Left, double Right, double Bottom, double Top,
                            bool Editable, string FontName, bool Bold, bool Italic);

/// <summary>
/// 같은 줄·가까운 간격의 연속 텍스트 연산자를 묶은 편집 단위.
/// 클릭 시 이 단위 전체가 선택·수정·삭제된다.
/// </summary>
public sealed record TextRun(
    IReadOnlyList<OpInfo> Ops,
    string Text,
    double Left, double Right, double Bottom, double Top,
    string FontName, bool Bold, bool Italic)
{
    public int PrimaryIndex => Ops[0].Index;
    public IReadOnlyList<int> Indices => Ops.Select(o => o.Index).ToList();
    public double Height => Math.Max(1, Top - Bottom);
    public double Width => Math.Max(1, Right - Left);
}

public sealed record PageAnalysis(
    IReadOnlyList<OpInfo> Ops,
    IReadOnlyList<TextRun> Runs,
    int ShowCount,
    double BoxLeft, double BoxBottom, double BoxWidth, double BoxHeight,
    int Rotation, int PageCount);

public readonly record struct Rgb(int R, int G, int B);
