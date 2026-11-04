using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfTextEditor;

/// <summary>첫 실행·도움말용 단계별 튜토리얼.</summary>
public sealed class TutorialWindow : Window
{
    readonly TextBlock _title;
    readonly TextBlock _body;
    readonly TextBlock _stepLabel;
    readonly Button _prev;
    readonly Button _next;
    readonly CheckBox _dontShow;
    int _index;

    static readonly (string Title, string Body)[] Steps =
    {
        ("PDF 편집기에 오신 것을 환영합니다",
         "이 프로그램은 Adobe Acrobat처럼 PDF 안의 글자를 직접 고치고, 페이지를 정리하고, 주석을 달 수 있는 편집기입니다.\n\n" +
         "흰 상자로 덮지 않고, PDF 내용 스트림의 글자를 실제로 바꿉니다.\n\n" +
         "오른쪽 아래 「다음」을 눌러 주요 기능을 살펴보세요."),

        ("1. 파일 열기 · 새로 만들기 · 저장",
         "• 파일 → 열기 (Ctrl+O) : 기존 PDF를 엽니다.\n" +
         "• 파일 → 새로 만들기 : 빈 페이지로 새 문서를 만듭니다.\n" +
         "• 파일 → 저장 (Ctrl+S) : 원래 파일에 덮어씁니다.\n" +
         "• 파일 → 다른 이름으로 저장 : 새 경로에 저장합니다.\n\n" +
         "수정 후 창을 닫으면 「수정 사항이 있습니다. 저장하시겠습니까?」 확인이 나타납니다."),

        ("2. 텍스트 수정 · 삭제",
         "왼쪽 도구에서 「텍스트 수정」을 선택한 뒤, 페이지 위 글자를 클릭하세요.\n\n" +
         "• 같은 줄의 가까운 조각은 자동으로 묶여 한 번에 편집됩니다.\n" +
         "• Enter로 확정, Esc로 취소합니다.\n" +
         "• 「텍스트 삭제」 도구로 묶음을 통째로 지울 수 있습니다.\n\n" +
         "상단에서 글꼴·굵게·기울임·색을 바꾼 뒤 수정하면 그 스타일이 적용됩니다."),

        ("3. 글꼴 관리",
         "상단의 빨간 「글꼴 관리」 버튼 또는 메뉴 글꼴 → 글꼴 관리…\n\n" +
         "• 언어(한국어, 일본어, 중국어 등)별로 Google Fonts를 고릅니다.\n" +
         "• 「선택 글꼴 다운로드」로 오프라인에서도 쓰게 저장합니다.\n" +
         "• 「내 컴퓨터에서 글꼴 추가」로 TTF/OTF 파일을 등록할 수 있습니다."),

        ("4. 그리기 · 주석 · 페이지",
         "왼쪽 패널\n" +
         "• 그리기: 사각형, 타원, 선, 화살표, 펜/서명\n" +
         "• 주석: 형광펜, 밑줄, 취소선, 메모, 링크\n\n" +
         "메뉴 페이지\n" +
         "• 회전, 삭제, 순서 이동, 빈 페이지 삽입, 다른 PDF 병합, 자르기\n\n" +
         "메뉴 문서\n" +
         "• 워터마크, 머리글/바닥글/쪽 번호, 배경색"),

        ("5. 보기 · 되돌리기 · 팁",
         "• ＋ / － / 너비 / 전체 로 확대·축소합니다.\n" +
         "• Ctrl+Z 또는 되돌리기 버튼으로 최대 30단계까지 취소합니다.\n" +
         "• 회전된 페이지는 먼저 원래 방향으로 돌린 뒤 텍스트를 편집하세요.\n" +
         "• 암호화·스캔 이미지·Form 안 글자는 텍스트 편집이 안 됩니다.\n\n" +
         "도움이 필요하면 메뉴 도움말 → 튜토리얼 다시 보기를 누르세요.\n" +
         "이제 시작해 보세요!"),
    };

    public TutorialWindow(Window? owner, bool showDontShowAgain = true)
    {
        Owner = owner;
        Title = "시작 가이드";
        Width = 560;
        Height = 420;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White;

        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app-icon.png");
            Icon = BitmapFrame.Create(uri);
        }
        catch { /* 아이콘 없어도 동작 */ }

        var root = new DockPanel { Margin = new Thickness(0) };

        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2C)),
            Padding = new Thickness(20, 14, 20, 14),
        };
        DockPanel.SetDock(header, Dock.Top);
        header.Child = new TextBlock
        {
            Text = "PDF 편집기 시작 가이드",
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        };
        root.Children.Add(header);

        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            Padding = new Thickness(16, 10, 16, 10),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        var foot = new DockPanel();
        _dontShow = new CheckBox
        {
            Content = "다시 보지 않기",
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = showDontShowAgain ? Visibility.Visible : Visibility.Collapsed,
        };
        DockPanel.SetDock(_dontShow, Dock.Left);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _prev = new Button { Content = "이전", Width = 80, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        _next = new Button
        {
            Content = "다음",
            Width = 90,
            Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(0xE3, 0x48, 0x2B)),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
        };
        _prev.Click += (_, _) => { _index--; ShowStep(); };
        _next.Click += (_, _) =>
        {
            if (_index >= Steps.Length - 1)
            {
                if (_dontShow.IsChecked == true)
                    AppSettings.SetTutorialSeen();
                DialogResult = true;
                Close();
            }
            else
            {
                _index++;
                ShowStep();
            }
        };
        btns.Children.Add(_prev);
        btns.Children.Add(_next);
        DockPanel.SetDock(btns, Dock.Right);
        foot.Children.Add(btns);
        foot.Children.Add(_dontShow);
        footer.Child = foot;
        root.Children.Add(footer);

        var body = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        _stepLabel = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xE3, 0x48, 0x2B)), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        _title = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        _body = new TextBlock { FontSize = 13.5, LineHeight = 22, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) };
        body.Children.Add(_stepLabel);
        body.Children.Add(_title);
        body.Children.Add(_body);
        root.Children.Add(body);

        Content = root;
        ShowStep();
    }

    void ShowStep()
    {
        _index = Math.Clamp(_index, 0, Steps.Length - 1);
        var (t, b) = Steps[_index];
        _stepLabel.Text = $"단계 {_index + 1} / {Steps.Length}";
        _title.Text = t;
        _body.Text = b;
        _prev.IsEnabled = _index > 0;
        _next.Content = _index >= Steps.Length - 1 ? "시작하기" : "다음";
    }
}
