using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PdfTextEditor.Fonts;
using PdfTextEditor.Pdf;
using Shapes = System.Windows.Shapes;

namespace PdfTextEditor;

public enum Tool { Edit, Delete, TextBox, Image, ImageEdit, Table, Chart, Rect, Ellipse, Line, Arrow, Pen, Highlight, Underline, Strike, Note, Link }

public partial class MainWindow : Window
{
    static readonly (string Name, Rgb C)[] Palette =
    {
        ("검정", new Rgb(0, 0, 0)), ("빨강", new Rgb(220, 38, 38)), ("주황", new Rgb(245, 124, 0)), ("노랑", new Rgb(255, 235, 0)),
        ("초록", new Rgb(22, 163, 74)), ("파랑", new Rgb(37, 99, 235)), ("보라", new Rgb(124, 58, 237)),
        ("회색", new Rgb(120, 120, 120)), ("흰색", new Rgb(255, 255, 255)),
    };

    string HintOf(Tool tool) => tool switch
    {
        Tool.Edit => Loc.T("hint.edit"),
        Tool.Delete => Loc.T("hint.delete"),
        Tool.TextBox => Loc.T("hint.textbox"),
        Tool.Image => Loc.T("hint.image"),
        Tool.ImageEdit => Loc.T("hint.imageedit"),
        Tool.Table => Loc.T("hint.table"),
        Tool.Chart => Loc.T("hint.chart"),
        Tool.Rect => Loc.T("hint.rect"),
        Tool.Ellipse => Loc.T("hint.ellipse"),
        Tool.Line => Loc.T("hint.line"),
        Tool.Arrow => Loc.T("hint.arrow"),
        Tool.Pen => Loc.T("hint.pen"),
        Tool.Highlight => Loc.T("hint.highlight"),
        Tool.Underline => Loc.T("hint.underline"),
        Tool.Strike => Loc.T("hint.strike"),
        Tool.Note => Loc.T("hint.note"),
        Tool.Link => Loc.T("hint.link"),
        _ => "",
    };


    readonly AppSettings _settings = AppSettings.Load();
    readonly GoogleFontsClient _gf;
    readonly CustomFontStore _custom;
    readonly FontResolver _resolver;
    readonly PdfEngine _engine = new();

    byte[]? _work;
    string _path = "";
    bool _dirty;
    readonly List<byte[]> _undo = new();
    int _page = 1, _pageCount;
    double _zoom = 1.5;
    bool _busy;
    PageAnalysis? _an;
    BitmapSource? _bmp;
    Action<bool>? _finishEditor;
    Tool _tool = Tool.Edit;

    Point _p0; bool _drag;
    Shapes.Shape? _rubber;
    Shapes.Polyline? _penLine;
    readonly List<Point> _pen = new();

    // 찾기
    List<SearchHit> _findHits = new();
    int _findIndex = -1;
    string _findQuery = "";
    List<ImageInfo> _images = new();
    List<string> _allFontFamilies = new(); // 검색용 전체 목록
    bool _fontBoxFiltering;
    int _selectedImage = -1;
    bool _imgResize;
    int _imgHandle; // 0=none, 1=BR corner
    

    public MainWindow()
    {
        InitializeComponent();
        _gf = new GoogleFontsClient(_settings);
        _custom = new CustomFontStore(_settings.ResolveCacheDir());
        _resolver = new FontResolver(_gf, _custom);
        ColorBox.ItemsSource = Palette.Select(p => p.Name).ToList();
        ColorBox.SelectedIndex = 0;
        FontBox.Items.Add("자동"); FontBox.SelectedIndex = 0;
        UpdateTitle();
        Loaded += async (_, _) =>
        {
            ApplyUiLanguage();
            await LoadFamiliesAsync();
            // 파일 연결로 전달된 PDF 열기: PdfTextEditor.exe "파일.pdf"
            try
            {
                var args = Environment.GetCommandLineArgs();
                var pdfArg = args.Skip(1).FirstOrDefault(a =>
                    a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
                if (pdfArg != null)
                    await OpenPathAsync(pdfArg, showFontNotice: true);
            }
            catch { /* ignore */ }

            if (!AppSettings.IsTutorialSeen())
            {
                var tut = new TutorialWindow(this, showDontShowAgain: true);
                tut.ShowDialog();
            }
        };
    }

    // ───────────── 공통 ─────────────
    void Status(string msg) { if (StatusText != null) StatusText.Text = msg; }

    void UpdateTitle()
    {
        string name = string.IsNullOrEmpty(_path) ? Loc.T("title.untitled") : Path.GetFileName(_path);
        string star = _dirty ? " *" : "";
        Title = $"{Loc.T("app.name")} - {name}{star}";
    }

    void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
    }

    void MarkClean()
    {
        _dirty = false;
        UpdateTitle();
    }
    Rgb CurColor => Palette[Math.Max(0, ColorBox.SelectedIndex)].C;
    string? CurFamily
    {
        get
        {
            string? s = FontBox.SelectedItem as string;
            if (s == null || s == "자동")
            {
                s = FontBox.Text?.Trim();
                if (string.IsNullOrEmpty(s) || s == "자동") return null;
            }
            if (s.StartsWith("──")) return null; // 헤더
            const string prefix = "[내 글꼴] ";
            if (s.StartsWith(prefix)) return s[prefix.Length..];
            return s;
        }
    }

    /// <summary>글꼴을 실제로 적용할 때 최근 목록에 넣는다.</summary>
    void RememberFont(string? family)
    {
        if (!string.IsNullOrWhiteSpace(family))
            AppSettings.AddRecentFont(family);
    }
    bool Bold => BoldChk.IsChecked == true;
    bool Italic => ItalicChk.IsChecked == true;
    static double Num(TextBox t, double d) =>
        double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
    double Alpha => Math.Clamp(Num(OpacityBox, 100) / 100.0, 0.05, 1.0);

    void UpdateButtons()
    {
        UndoBtn.IsEnabled = _undo.Count > 0;
        PrevBtn.IsEnabled = _work != null && _page > 1;
        NextBtn.IsEnabled = _work != null && _page < _pageCount;
    }

    async Task LoadFamiliesAsync()
    {
        Status("Google Fonts 목록 확인 중…");
        bool ok = await _gf.EnsureCatalogAsync();
        var fams = ok ? _gf.Families() : FontMapper.PrefetchSet.Select(f => f.Family).Distinct().ToList();
        _allFontFamilies = new List<string>();
        foreach (var c in _custom.Families)
            _allFontFamilies.Add("[내 글꼴] " + c);
        _allFontFamilies.AddRange(fams);
        RebuildFontBox(filter: null);
        Status(ok
            ? $"준비됨 · Google Fonts {fams.Count}개 · 내 글꼴 {_custom.All.Count}개"
            : "오프라인: 저장된 폰트 목록/폰트로 동작합니다. PDF 파일을 여세요.");
    }

    /// <summary>최근 사용 + 모든 글꼴 구조로 FontBox를 채운다. filter가 있으면 이름 검색.</summary>
    void RebuildFontBox(string? filter)
    {
        _fontBoxFiltering = true;
        string? keep = FontBox.Text;
        FontBox.Items.Clear();

        string? q = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        bool Match(string name) =>
            q == null || name.Contains(q, StringComparison.OrdinalIgnoreCase);

        // 1) 자동
        if (q == null || "자동".Contains(q, StringComparison.OrdinalIgnoreCase))
            FontBox.Items.Add("자동");

        // 2) 최근 사용한 글꼴
        var recent = AppSettings.GetRecentFonts()
            .Where(r => _allFontFamilies.Any(f =>
                string.Equals(StripFontPrefix(f), r, StringComparison.OrdinalIgnoreCase)
                || string.Equals(f, r, StringComparison.OrdinalIgnoreCase)))
            .Select(r =>
            {
                // 목록에 있는 표시 이름 사용
                var hit = _allFontFamilies.FirstOrDefault(f =>
                    string.Equals(StripFontPrefix(f), r, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f, r, StringComparison.OrdinalIgnoreCase));
                return hit ?? r;
            })
            .Where(Match)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recent.Count > 0)
        {
            FontBox.Items.Add(MakeHeader("── 최근 사용한 글꼴 ──"));
            foreach (var r in recent) FontBox.Items.Add(r);
        }

        // 3) 모든 글꼴
        var all = _allFontFamilies.Where(Match).ToList();
        if (all.Count > 0)
        {
            FontBox.Items.Add(MakeHeader("── 모든 글꼴 ──"));
            foreach (var f in all) FontBox.Items.Add(f);
        }

        // 선택 복원
        if (!string.IsNullOrEmpty(keep))
        {
            for (int i = 0; i < FontBox.Items.Count; i++)
            {
                if (FontBox.Items[i] is string s && s == keep) { FontBox.SelectedIndex = i; break; }
            }
            if (FontBox.SelectedIndex < 0) FontBox.Text = keep;
        }
        else FontBox.SelectedIndex = 0;

        _fontBoxFiltering = false;
    }

    static ComboBoxItem MakeHeader(string text) => new()
    {
        Content = text,
        IsEnabled = false,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
        HorizontalContentAlignment = HorizontalAlignment.Left,
    };

    static string StripFontPrefix(string s)
    {
        const string prefix = "[내 글꼴] ";
        return s.StartsWith(prefix) ? s[prefix.Length..] : s;
    }

    void FontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fontBoxFiltering) return;
        if (FontBox.SelectedItem is string name && name.Length > 0 && name != "자동")
            AppSettings.AddRecentFont(name);
        else if (FontBox.SelectedItem is ComboBoxItem) // 헤더 클릭 방지
            FontBox.SelectedIndex = -1;
    }

    void FontBox_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape or Key.Up or Key.Down or Key.Left or Key.Right or Key.Tab)
            return;
        // 입력 중 필터 + 드롭다운 열기
        string text = FontBox.Text ?? "";
        RebuildFontBox(string.IsNullOrWhiteSpace(text) || text == "자동" ? null : text);
        if (!FontBox.IsDropDownOpen) FontBox.IsDropDownOpen = true;
        // 커서 위치 유지 위해 텍스트 재설정
        FontBox.Text = text;
        if (FontBox.Template.FindName("PART_EditableTextBox", FontBox) is TextBox etb)
        {
            etb.CaretIndex = text.Length;
        }
    }

    void FontBox_DropDownOpened(object sender, EventArgs e)
    {
        // 목록 열 때 필터 없이 전체(최근+전체) 표시, 입력 중이면 유지
        if (string.IsNullOrWhiteSpace(FontBox.Text) || FontBox.Text == "자동")
            RebuildFontBox(null);
    }

    void FontManager_Click(object sender, RoutedEventArgs e)
    {
        var w = new FontManagerWindow(this, _gf, _custom);
        w.ShowDialog();
        _ = LoadFamiliesAsync(); // 추가/다운로드 반영
    }

    /// <summary>현재 문서에 작업을 적용(스냅샷을 남겨 되돌리기 가능)하고 화면을 갱신한다.</summary>
    async Task RunAsync(Func<byte[], Task<byte[]>> op, string ok = "완료", int? goPage = null)
    {
        if (_busy || _work == null) return;
        _busy = true;
        try
        {
            Status("처리 중…");
            var res = await op(_work);
            _undo.Add(_work); if (_undo.Count > 30) _undo.RemoveAt(0);
            _work = res;
            _pageCount = await Task.Run(() => PdfOps.PageCount(res));
            if (goPage != null) _page = goPage.Value;
            _page = Math.Clamp(_page, 1, _pageCount);
            MarkDirty();
            Status(ok);
        }
        catch (Exception ex)
        {
            Status("실패: " + ex.Message);
            MessageBox.Show(this, ex.Message, "작업할 수 없습니다", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; }
        await LoadPageAsync();
    }

    static Func<byte[], Task<byte[]>> Sync(Func<byte[], byte[]> f) => b => Task.Run(() => f(b));

    // ───────────── 파일 ─────────────
    async void New_Click(object sender, RoutedEventArgs e) => await NewAsync();
    async void Open_Click(object sender, RoutedEventArgs e) => await OpenAsync();
    void Save_Click(object sender, RoutedEventArgs e) => Save(saveAs: false);
    void SaveAs_Click(object sender, RoutedEventArgs e) => Save(saveAs: true);
    void Exit_Click(object sender, RoutedEventArgs e) => Close();
    void Tutorial_Click(object sender, RoutedEventArgs e)
    {
        var w = new TutorialWindow(this, showDontShowAgain: false);
        w.ShowDialog();
    }
    void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "PDF 편집기\n\n콘텐츠 스트림 기반 텍스트 편집 · 페이지 정리 · 주석\nGoogle Fonts / 사용자 글꼴 지원\n\niText 9 (AGPL) · .NET / WPF",
            "정보", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    
    void Lang_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag)
            Loc.SetLanguage(tag);
    }

    void ApplyUiLanguage()
    {
        try
        {
            if (MenuFile != null) MenuFile.Header = Loc.T("menu.file");
            if (MenuEdit != null) MenuEdit.Header = Loc.T("menu.edit");
            if (MenuPage != null) MenuPage.Header = Loc.T("menu.page");
            if (MenuDoc != null) MenuDoc.Header = Loc.T("menu.doc");
            if (MenuFont != null) MenuFont.Header = Loc.T("menu.font");
            if (MenuLang != null) MenuLang.Header = Loc.T("menu.lang");
            if (MenuHelp != null) MenuHelp.Header = Loc.T("menu.help");

            SetMenu(MenuFile, 0, "menu.new");
            SetMenu(MenuFile, 1, "menu.open");
            SetMenu(MenuFile, 3, "menu.save");
            SetMenu(MenuFile, 4, "menu.saveas");
            SetMenu(MenuFile, 6, "menu.print");
            SetMenu(MenuFile, 8, "menu.export.word");
            SetMenu(MenuFile, 9, "menu.extract");
            SetMenu(MenuFile, 10, "menu.split");
            SetMenu(MenuFile, 12, "menu.exit");

            SetMenu(MenuEdit, 0, "menu.undo");
            SetMenu(MenuEdit, 1, "menu.find");

            SetMenu(MenuPage, 0, "menu.rotate.cw");
            SetMenu(MenuPage, 1, "menu.rotate.ccw");
            SetMenu(MenuPage, 3, "menu.page.delete");
            SetMenu(MenuPage, 4, "menu.page.up");
            SetMenu(MenuPage, 5, "menu.page.down");
            SetMenu(MenuPage, 7, "menu.page.insert");
            SetMenu(MenuPage, 8, "menu.page.merge");
            SetMenu(MenuPage, 9, "menu.page.crop");

            SetMenu(MenuDoc, 0, "menu.watermark");
            SetMenu(MenuDoc, 1, "menu.header");
            SetMenu(MenuDoc, 2, "menu.bg");
            SetMenu(MenuDoc, 3, "menu.sign");

            SetMenu(MenuFont, 0, "menu.font.mgr");
            SetMenu(MenuFont, 1, "menu.font.refresh");
            SetMenu(MenuFont, 2, "menu.font.prefetch");

            if (MiTutorial != null) MiTutorial.Header = Loc.T("menu.tutorial");
            if (MiRegister != null) MiRegister.Header = Loc.T("menu.register");
            if (MiUnregister != null) MiUnregister.Header = Loc.T("menu.unregister");
            if (MiDefaults != null) MiDefaults.Header = Loc.T("menu.defaults");
            if (MiAbout != null) MiAbout.Header = Loc.T("menu.about");

            if (BtnNew != null) BtnNew.Content = Loc.T("btn.new");
            if (BtnOpen != null) BtnOpen.Content = Loc.T("btn.open");
            if (BtnSave != null) BtnSave.Content = Loc.T("btn.save");
            if (BtnSaveAs != null) BtnSaveAs.Content = Loc.T("btn.saveas");
            if (BtnPrint != null) BtnPrint.Content = Loc.T("btn.print");
            if (BtnUndo != null) BtnUndo.Content = Loc.T("btn.undo");
            if (BtnFind != null) BtnFind.Content = Loc.T("btn.find");
            if (BtnFitW != null) BtnFitW.Content = Loc.T("btn.fit.w");
            if (BtnFitPage != null) BtnFitPage.Content = Loc.T("btn.fit.page");
            if (BtnFontMgr != null) BtnFontMgr.Content = Loc.T("btn.font");

            // 도구 패널: Tag로 찾기
            ApplyToolLabels();
            UpdateTitle();
            if (_work == null) Status(Loc.T("status.open"));
            else Status(HintOf(_tool));

            // 언어 메뉴 체크
            foreach (var mi in new[] { LangKo, LangEn, LangEs, LangFr })
            {
                if (mi == null) continue;
                mi.IsChecked = (mi.Tag as string) == Loc.Lang;
            }
        }
        catch { /* 디자인 타임 등 */ }
    }

    static void SetMenu(MenuItem? parent, int index, string key)
    {
        if (parent == null || index < 0 || index >= parent.Items.Count) return;
        if (parent.Items[index] is MenuItem mi)
            mi.Header = Loc.T(key);
    }

    void ApplyToolLabels()
    {
        foreach (var rb in FindVisualChildren<RadioButton>(this))
        {
            if (rb.Tag is not string tag) continue;
            string key = tag switch
            {
                "Edit" => "tool.edit",
                "Delete" => "tool.delete",
                "TextBox" => "tool.textbox",
                "Image" => "tool.image",
                "ImageEdit" => "tool.imageedit",
                "Table" => "tool.table",
                "Chart" => "tool.chart",
                "Rect" => "tool.rect",
                "Ellipse" => "tool.ellipse",
                "Line" => "tool.line",
                "Arrow" => "tool.arrow",
                "Pen" => "tool.pen",
                "Highlight" => "tool.highlight",
                "Underline" => "tool.underline",
                "Strike" => "tool.strike",
                "Note" => "tool.note",
                "Link" => "tool.link",
                _ => ""
            };
            if (key.Length > 0) rb.Content = Loc.T(key);
        }
        // 섹션 라벨
        foreach (var tb in FindVisualChildren<TextBlock>(this))
        {
            if (tb.Text is "그리기" or "Draw" or "Dibujar" or "Dessin") tb.Text = Loc.T("tool.draw");
            if (tb.Text is "주석" or "Annotate" or "Anotar" or "Annoter") tb.Text = Loc.T("tool.annot");
        }
    }

    static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var c in FindVisualChildren<T>(child)) yield return c;
        }
    }

void RegisterDefault_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociation.Register();
            var r = MessageBox.Show(this,
                "등록했습니다.\n\n" +
                "Windows 설정 > 앱 > 기본 앱 에서\n" +
                "「PDF」 또는 「.pdf」를 검색한 뒤\n" +
                "「PDF 편집기」를 선택하세요.\n\n" +
                "지금 기본 앱 설정을 열까요?",
                "기본 프로그램 등록",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes)
                FileAssociation.OpenWindowsDefaultApps();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "등록에 실패했습니다.\n" + ex.Message +
                "\n\nPublish 로 만든 .exe 로 실행한 뒤 다시 시도해 주세요.\n" +
                "(dotnet run 개발 실행 시에는 경로가 달라질 수 있습니다.)\n\n" +
                "현재 경로: " + FileAssociation.ExePath,
                "기본 프로그램", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void UnregisterDefault_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociation.Unregister();
            MessageBox.Show(this, "기본 프로그램 등록을 해제했습니다.", "기본 프로그램",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "기본 프로그램", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OpenDefaultApps_Click(object sender, RoutedEventArgs e) =>
        FileAssociation.OpenWindowsDefaultApps();


    bool ConfirmFontNotice()
    {
        var w = new Window
        {
            Title = Loc.T("dlg.open.title"),
            Width = 440, Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White,
        };
        var sp = new StackPanel { Margin = new Thickness(18) };
        sp.Children.Add(new TextBlock
        {
            Text = "이 프로그램은 어도비의 유료 글꼴은 사용할 수 없습니다.\n" +
                   "따라서 없는 글꼴은 다른 글꼴로 대체될 수 있습니다.\n\n" +
                   "불러오시겠습니까?\n(글꼴을 추가하면 원본에 가깝게 불러올 수 있습니다.)",
            TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 0, 0, 16),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = Loc.T("dlg.open.ok"), Width = 90, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(0xE3, 0x48, 0x2B)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        var cancel = new Button { Content = Loc.T("dlg.open.cancel"), Width = 80, Height = 30, IsCancel = true };
        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        sp.Children.Add(buttons);
        w.Content = sp;
        w.ShowDialog();
        return accepted;
    }

    /// <summary>저장되지 않은 변경이 있으면 확인. true면 계속 진행 가능.</summary>
    bool ConfirmDiscardOrSave()
    {
        if (!_dirty || _work == null) return true;
        var r = MessageBox.Show(this,
            Loc.T("dlg.unsaved"),
            Loc.T("app.name"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return Save(saveAs: false);
        return true; // No → discard
    }

    void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardOrSave()) e.Cancel = true;
    }

    async Task NewAsync()
    {
        if (!ConfirmDiscardOrSave()) return;
        var r = FormDialog.Ask(this, "새 PDF 만들기",
            ("페이지 수", "1", false),
            ("용지 (A4 / Letter / Legal / A3 / A5)", "A4", false),
            ("가로 방향 (예 / 아니오)", "아니오", false));
        if (r == null) return;
        int n = int.TryParse(r[0].Trim(), out var nn) ? nn : 1;
        bool land = r[2].Trim().StartsWith("예");
        try
        {
            _work = PdfOps.CreateBlank(n, r[1], land);
            _path = "";
            _undo.Clear();
            _page = 1;
            _pageCount = PdfOps.PageCount(_work);
            MarkDirty(); // 아직 파일로 저장 안 됨
            await LoadPageAsync();
            Status($"빈 문서 {n}페이지를 만들었습니다. 저장하면 파일이 됩니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "새 문서", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateButtons();
    }

    async Task OpenAsync()
    {
        if (!ConfirmDiscardOrSave()) return;
        var dlg = new OpenFileDialog { Filter = "PDF 파일 (*.pdf)|*.pdf" };
        if (dlg.ShowDialog(this) != true) return;
        await OpenPathAsync(dlg.FileName, showFontNotice: true);
    }

    /// <summary>경로로 PDF를 연다. 파일 연결 / 열기 공용.</summary>
    async Task OpenPathAsync(string path, bool showFontNotice)
    {
        if (showFontNotice && !ConfirmFontNotice()) return;
        try
        {
            Status("파일 읽는 중…");
            _work = await File.ReadAllBytesAsync(path);
            _pageCount = PdfOps.PageCount(_work);
            _path = path;
            _undo.Clear();
            _page = 1;
            MarkClean();
            _selectedImage = -1;
            Status("문서 글꼴을 확인하고 대체 글꼴을 준비하는 중…");
            await PrefetchDocumentFontsAsync(_work);
            await LoadPageAsync();
            Status(HintOf(_tool));
        }
        catch (Exception ex)
        {
            _work = null;
            MessageBox.Show(this, "PDF를 열 수 없습니다(암호화/손상 파일은 지원하지 않습니다).\n" + ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateButtons();
    }

    /// <summary>문서에 쓰인 폰트 이름을 모아 대체 Google Fonts를 미리 받는다.</summary>
    async Task PrefetchDocumentFontsAsync(byte[] pdf)
    {
        try
        {
            await _gf.EnsureCatalogAsync();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int pages = Math.Min(PdfOps.PageCount(pdf), 8); // 앞 몇 페이지만 스캔
            for (int p = 1; p <= pages; p++)
            {
                try
                {
                    var an = _engine.Analyze(pdf, p);
                    foreach (var op in an.Ops.Where(o => o.Editable && o.FontName.Length > 0))
                        names.Add(FontMapper.Display(op.FontName));
                }
                catch { }
            }
            int got = 0;
            foreach (var name in names)
            {
                var cands = FontMapper.Candidates(name, "가A", _gf);
                foreach (var fam in cands.Take(2))
                {
                    Status($"글꼴 준비: {fam}…");
                    var (ok, _) = await _gf.DownloadFamilyAsync(fam);
                    if (ok > 0) { got++; break; }
                }
            }
            if (got > 0) Status($"대체 글꼴 {got}종을 준비했습니다.");
        }
        catch (Exception ex)
        {
            Status("글꼴 미리 받기 일부 실패: " + ex.Message);
        }
    }

    /// <summary>저장 성공 시 true.</summary>
    bool Save(bool saveAs)
    {
        if (_work == null) return false;
        string? target = _path;
        if (saveAs || string.IsNullOrEmpty(target))
        {
            var dlg = new SaveFileDialog
            {
                Filter = "PDF 파일 (*.pdf)|*.pdf",
                FileName = string.IsNullOrEmpty(_path)
                    ? "제목 없음.pdf"
                    : Path.GetFileName(_path),
            };
            if (dlg.ShowDialog(this) != true) return false;
            target = dlg.FileName;
        }
        try
        {
            byte[] data;
            try { data = PdfOps.Optimize(_work); } catch { data = _work; }
            File.WriteAllBytes(target, data);
            _path = target;
            MarkClean();
            Status($"저장했습니다: {target} ({data.Length / 1024:N0} KB)");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "저장에 실패했습니다.\n" + ex.Message, "저장", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_work == null) return;
        var r = FormDialog.Ask(this, "페이지 추출", ("추출할 페이지 (예: 1-3,5)", _page.ToString(), false));
        if (r == null) return;
        var pages = ParsePages(r[0], _pageCount);
        if (pages.Count == 0) { Status("올바른 페이지 범위가 아닙니다."); return; }
        var dlg = new SaveFileDialog { Filter = "PDF 파일 (*.pdf)|*.pdf", FileName = Path.GetFileNameWithoutExtension(_path) + "-추출.pdf" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllBytes(dlg.FileName, PdfOps.Optimize(PdfOps.Pick(_work, pages)));
        Status($"{pages.Count}페이지를 추출해 저장했습니다.");
    }

    void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_work == null) return;
        var dlg = new OpenFolderDialog { Title = "분할한 파일을 저장할 폴더" };
        if (dlg.ShowDialog(this) != true) return;
        int n = PdfOps.SplitPages(_work, dlg.FolderName, Path.GetFileNameWithoutExtension(_path));
        Status($"{n}개 파일로 분할했습니다: {dlg.FolderName}");
    }

    static List<int> ParsePages(string s, int n)
    {
        var r = new List<int>();
        foreach (var part in s.Split(',', ';', ' '))
        {
            var t = part.Trim(); if (t.Length == 0) continue;
            var ab = t.Split('-');
            if (!int.TryParse(ab[0], out var a)) continue;
            int b = ab.Length > 1 && int.TryParse(ab[1], out var bb) ? bb : a;
            for (int k = Math.Min(a, b); k <= Math.Max(a, b); k++) if (k >= 1 && k <= n) r.Add(k);
        }
        return r;
    }

    // ───────────── 페이지 표시 ─────────────
    async Task LoadPageAsync()
    {
        if (_work == null) return;
        try
        {
            var bytes = _work; int page = _page;
            try { _an = await Task.Run(() => _engine.Analyze(bytes, page)); }
            catch (Exception ex)
            {
                _an = await Task.Run(() => _engine.Geometry(bytes, page));
                Status("이 페이지의 텍스트 분석에 실패해 텍스트 편집은 쓸 수 없습니다(다른 기능은 가능): " + ex.Message);
            }
            _pageCount = _an.PageCount;
            _bmp = await PageRenderer.RenderAsync(_work, _page, _zoom);
            PageImage.Source = _bmp;
            PageImage.Width = Overlay.Width = _bmp.PixelWidth;
            PageImage.Height = Overlay.Height = _bmp.PixelHeight;
            PageText.Text = $"{_page} / {_pageCount}";
            ZoomText.Text = $"{_zoom:P0}";
            try { _images = await Task.Run(() => PdfImages.List(_work, _page)); }
            catch { _images = new List<ImageInfo>(); }
            if (_selectedImage >= _images.Count) _selectedImage = -1;
            DrawOverlay();
        }
        catch (Exception ex) { Status("페이지를 불러오지 못했습니다: " + ex.Message); }
        UpdateButtons();
    }

    (double X, double Y) ToPdf(Point p)
    {
        double sx = _bmp!.PixelWidth / _an!.BoxWidth, sy = _bmp.PixelHeight / _an.BoxHeight;
        return (_an.BoxLeft + p.X / sx, _an.BoxBottom + _an.BoxHeight - p.Y / sy);
    }

    void DrawOverlay()
    {
        Overlay.Children.Clear();
        _finishEditor = null;
        if (_an == null || _bmp == null) return;

        double sx = _bmp.PixelWidth / _an.BoxWidth, sy = _bmp.PixelHeight / _an.BoxHeight;

        // 찾기 결과 하이라이트
        if (_findHits.Count > 0)
        {
            for (int i = 0; i < _findHits.Count; i++)
            {
                var hit = _findHits[i];
                if (hit.Page != _page) continue;
                double x = (hit.Left - _an.BoxLeft) * sx;
                double y = (_an.BoxBottom + _an.BoxHeight - hit.Top) * sy;
                double w = Math.Max(4, (hit.Right - hit.Left) * sx);
                double h = Math.Max(6, (hit.Top - hit.Bottom) * sy);
                bool current = i == _findIndex;
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = w, Height = h,
                    Fill = new SolidColorBrush(current ? Color.FromArgb(120, 255, 200, 0) : Color.FromArgb(70, 255, 255, 0)),
                    Stroke = current ? Brushes.DarkOrange : Brushes.Goldenrod,
                    StrokeThickness = current ? 2 : 1,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
                Overlay.Children.Add(rect);
            }
        }

        // 이미지 편집 오버레이
        if (_tool == Tool.ImageEdit && _an.Rotation == 0)
        {
            for (int i = 0; i < _images.Count; i++)
            {
                var img = _images[i];
                double x = (img.Left - _an.BoxLeft) * sx;
                double y = (_an.BoxBottom + _an.BoxHeight - img.Top) * sy;
                double w = Math.Max(8, (img.Right - img.Left) * sx);
                double h = Math.Max(8, (img.Top - img.Bottom) * sy);
                bool sel = i == _selectedImage;
                var border = new Border
                {
                    Width = w, Height = h,
                    BorderBrush = sel ? Brushes.DeepSkyBlue : Brushes.CornflowerBlue,
                    BorderThickness = new Thickness(sel ? 2 : 1),
                    Background = new SolidColorBrush(Color.FromArgb(sel ? (byte)40 : (byte)20, 30, 144, 255)),
                    Cursor = Cursors.Hand,
                    Tag = i,
                };
                int captured = i;
                border.MouseLeftButtonDown += (s, ev) =>
                {
                    ev.Handled = true;
                    _selectedImage = captured;
                    DrawOverlay();
                    Status($"이미지 #{captured + 1} 선택됨 — Delete 삭제, 오른쪽 아래 핸들로 크기 조절");
                };
                Canvas.SetLeft(border, x); Canvas.SetTop(border, y);
                Overlay.Children.Add(border);
                if (sel)
                {
                    // 우하단 리사이즈 핸들
                    var handle = new System.Windows.Shapes.Rectangle
                    {
                        Width = 12, Height = 12, Fill = Brushes.DeepSkyBlue, Cursor = Cursors.SizeNWSE, Tag = "resize"
                    };
                    handle.MouseLeftButtonDown += (s, ev) =>
                    {
                        ev.Handled = true;
                        _imgResize = true; _imgHandle = 1;
                        _p0 = ev.GetPosition(Overlay);
                        Overlay.CaptureMouse();
                    };
                    Canvas.SetLeft(handle, x + w - 6); Canvas.SetTop(handle, y + h - 6);
                    Overlay.Children.Add(handle);
                }
            }
            if (_images.Count == 0)
                Status("이 페이지에서 편집 가능한 이미지를 찾지 못했습니다.");
        }

        if (_an.Rotation != 0)
        {
            if (_tool is Tool.Edit or Tool.Delete)
                Status("회전된 페이지에서는 페이지 기능만 쓸 수 있습니다. 원래 방향으로 돌린 뒤 편집하세요.");
            return;
        }
        if (_tool != Tool.Edit && _tool != Tool.Delete) return;

        bool del = _tool == Tool.Delete;
        // TextRun 단위로 하이라이트 (같은 줄 연속 조각을 하나로 묶음)
        foreach (var run in _an.Runs)
        {
            double x = (run.Left - _an.BoxLeft) * sx;
            double y = (_an.BoxBottom + _an.BoxHeight - run.Top) * sy;
            double w = Math.Max(8, (run.Right - run.Left) * sx);
            double h = Math.Max(10, (run.Top - run.Bottom) * sy);
            // 비정상적으로 큰 높이(잘못된 묶음)는 평균 줄 높이로 제한해 히트박스 표시
            if (run.Ops.Count > 0)
            {
                double avg = run.Ops.Average(o => Math.Max(1, o.Top - o.Bottom)) * sy;
                int lines = Math.Max(1, (int)Math.Round((run.Top - run.Bottom) / Math.Max(1, run.Ops.Average(o => Math.Max(1, o.Top - o.Bottom)))));
                double maxH = avg * Math.Min(lines, 6) + 4;
                if (h > maxH * 1.3) h = Math.Max(avg + 4, maxH);
            }

            var hover = del ? Color.FromArgb(80, 220, 38, 38) : Color.FromArgb(55, 47, 93, 80);
            var tip = run.Ops.Count > 1
                ? $"{run.Text}\n({run.Ops.Count}개 조각 묶음)"
                : run.Text;
            var b = new Border
            {
                Width = w, Height = h, Background = Brushes.Transparent,
                Cursor = del ? Cursors.Hand : Cursors.IBeam, ToolTip = tip,
                BorderThickness = new Thickness(0),
            };
            b.MouseEnter += (_, _) =>
            {
                b.Background = new SolidColorBrush(hover);
                b.BorderBrush = del ? Brushes.IndianRed : Brushes.SeaGreen;
                b.BorderThickness = new Thickness(1);
            };
            b.MouseLeave += (_, _) =>
            {
                b.Background = Brushes.Transparent;
                b.BorderThickness = new Thickness(0);
            };
            var captured = run;
            b.MouseLeftButtonDown += (_, ev) =>
            {
                ev.Handled = true;
                if (del) _ = DeleteTextAsync(captured);
                else StartEdit(captured, x, y, w, h);
            };
            Canvas.SetLeft(b, x); Canvas.SetTop(b, y);
            Overlay.Children.Add(b);
        }
    }

    // ───────────── 텍스트 수정 / 삭제 ─────────────
    void StartEdit(TextRun run, double x, double y, double w, double h)
    {
        if (_busy) return;
        if (_finishEditor != null) { _finishEditor(true); return; }

        // 조각 평균 높이(PDF pt) → 화면 px 로 환산. 비정상적으로 큰 바운딩 박스는 한 줄로 취급.
        double pdfRunH = Math.Max(1, run.Top - run.Bottom);
        double avgPdfLine = run.Ops.Count > 0
            ? run.Ops.Average(o => Math.Max(1, o.Top - o.Bottom))
            : pdfRunH;
        double screenLineH = avgPdfLine / pdfRunH * h;          // 화면 기준 한 줄 높이
        screenLineH = Math.Clamp(screenLineH, 10, 36);

        double fontPx = Math.Clamp(screenLineH * 0.78, 10, 22);

        // 실제 줄 수 추정: 바운딩이 한 줄 높이의 1.7배 넘고 텍스트에 개행이 있을 때만 여러 줄
        int estLines = Math.Max(1, (int)Math.Round(pdfRunH / Math.Max(1, avgPdfLine)));
        bool multi = (estLines >= 2 && pdfRunH > avgPdfLine * 1.7)
                  || run.Text.Contains("\n");

        double pageW = _bmp?.PixelWidth ?? 800;
        double pageH = _bmp?.PixelHeight ?? 600;
        double maxW = Math.Max(100, Math.Min(pageW - x - 8, Math.Max(w + 20, pageW * 0.9)));
        double boxW = Math.Clamp(w + 12, 60, maxW);

        // 한 줄이면 글자 높이에 맞춘 얇은 박스, 여러 줄만 원본 높이 사용
        double boxH = multi
            ? Math.Clamp(Math.Min(h + 8, screenLineH * estLines + 12), fontPx + 12, Math.Min(140, pageH - y))
            : Math.Clamp(screenLineH + 8, fontPx + 8, fontPx + 22);

        var tb = new TextBox
        {
            Text = run.Text,
            FontSize = fontPx,
            Width = boxW,
            Height = boxH,
            MinWidth = 60,
            MaxWidth = maxW,
            MinHeight = fontPx + 8,
            MaxHeight = Math.Min(180, (_bmp?.PixelHeight ?? 600) - Math.Max(0, y) + 4),
            Padding = new Thickness(3, 2, 3, 2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(34, 139, 100)),
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Color.FromArgb(250, 255, 255, 245)),
            VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center,
            AcceptsReturn = multi,
            TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ToolTip = multi ? "Ctrl+Enter 확정 · Esc 취소" : "Enter 확정 · Esc 취소",
        };

        // 입력 시 폭은 maxW까지만 살짝 늘
        tb.TextChanged += (_, _) =>
        {
            tb.Measure(new Size(maxW, boxH * 3));
            double need = tb.DesiredSize.Width + 6;
            tb.Width = Math.Clamp(Math.Max(boxW, need), 60, maxW);
        };

        Canvas.SetLeft(tb, Math.Max(0, x - 2));
        Canvas.SetTop(tb, Math.Max(0, y - 2));

        bool done = false;
        async void Finish(bool ok)
        {
            if (done) return;
            done = true; _finishEditor = null;
            var value = tb.Text;
            Overlay.Children.Remove(tb);
            if (ok && value != run.Text) await CommitEditAsync(run, value);
        }
        _finishEditor = Finish;
        tb.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter)
            {
                // 한 줄: Enter 확정 / 여러 줄: Ctrl+Enter 확정
                if (!multi || (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                { Finish(true); ev.Handled = true; }
            }
            else if (ev.Key == Key.Escape) { Finish(false); ev.Handled = true; }
        };
        tb.LostFocus += (_, _) => Finish(true);
        Overlay.Children.Add(tb);
        tb.Focus(); tb.SelectAll();
    }

    async Task CommitEditAsync(TextRun run, string text)
    {
        string note = ""; int page = _page, count = _an!.ShowCount;
        string? fam = CurFamily; bool b = Bold, i = Italic;
        await RunAsync(async cur =>
        {
            var r = await _engine.EditTextAsync(cur, page, count, run, text, fam, b, i, _resolver, Status);
            note = r.Note; return r.Data;
        }, "적용했습니다.");
        if (note.Length > 0) Status("적용했습니다. 폰트: " + note);
        RememberFont(CurFamily);
    }

    async Task DeleteTextAsync(TextRun run)
    {
        int page = _page, count = _an!.ShowCount;
        string msg = run.Ops.Count > 1
            ? $"{run.Ops.Count}개 조각(\"{Truncate(run.Text, 24)}\")을 삭제했습니다."
            : "텍스트를 삭제했습니다.";
        await RunAsync(cur => _engine.DeleteTextAsync(cur, page, count, run.Indices), msg);
    }

    static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    // ───────────── 그리기 도구 ─────────────
    void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || sender is not RadioButton rb || rb.Tag is not string tag || !Enum.TryParse<Tool>(tag, out var t)) return;
        _finishEditor?.Invoke(true);
        _tool = t;
        OpacityBox.Text = t == Tool.Highlight ? "40" : "100";
        if (t == Tool.Highlight) ColorBox.SelectedIndex = 3;
        Overlay.Cursor = t is Tool.Edit or Tool.Delete or Tool.ImageEdit ? null : Cursors.Cross;
        Status(HintOf(t));
        DrawOverlay();
    }

    void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _finishEditor?.Invoke(true);
        if (_tool is Tool.Edit or Tool.Delete || _busy || _an == null || _bmp == null) return;
        if (_tool == Tool.ImageEdit) return; // 이미지 선택은 오버레이 Border에서 처리
        if (_an.Rotation != 0) return;

        _p0 = e.GetPosition(Overlay); _drag = true; Overlay.CaptureMouse();
        var c = CurColor; var brush = new SolidColorBrush(Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B));
        switch (_tool)
        {
            case Tool.Pen:
                _pen.Clear(); _pen.Add(_p0);
                _penLine = new Shapes.Polyline { Stroke = brush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
                _penLine.Points.Add(_p0); Overlay.Children.Add(_penLine);
                break;
            case Tool.Line or Tool.Arrow:
                _rubber = new Shapes.Line { X1 = _p0.X, Y1 = _p0.Y, X2 = _p0.X, Y2 = _p0.Y, Stroke = brush, StrokeThickness = 2 };
                Overlay.Children.Add(_rubber);
                break;
            case Tool.Note or Tool.TextBox:
                break;
            case Tool.Ellipse:
                _rubber = new Shapes.Ellipse { Stroke = brush, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 } };
                Overlay.Children.Add(_rubber);
                break;
            default:
                _rubber = new Shapes.Rectangle { Stroke = Brushes.DodgerBlue, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4, 3 } };
                Overlay.Children.Add(_rubber);
                break;
        }
    }

    void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_imgResize && _imgHandle == 1 && _selectedImage >= 0 && _selectedImage < _images.Count && _an != null && _bmp != null)
        {
            // 미리보기만: 선택 박스 갱신은 mouse up 시 적용
            return;
        }
        if (!_drag) return;
        var p = e.GetPosition(Overlay);
        if (_penLine != null) { _pen.Add(p); _penLine.Points.Add(p); return; }
        switch (_rubber)
        {
            case Shapes.Line ln: ln.X2 = p.X; ln.Y2 = p.Y; break;
            case Shapes.Shape s:
                Canvas.SetLeft(s, Math.Min(_p0.X, p.X)); Canvas.SetTop(s, Math.Min(_p0.Y, p.Y));
                s.Width = Math.Abs(p.X - _p0.X); s.Height = Math.Abs(p.Y - _p0.Y);
                break;
        }
    }

    async void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_imgResize && _selectedImage >= 0 && _selectedImage < _images.Count && _an != null && _bmp != null && _work != null)
        {
            _imgResize = false; Overlay.ReleaseMouseCapture();
            var p1 = e.GetPosition(Overlay);
            var img = _images[_selectedImage];
            double sx = _bmp.PixelWidth / _an.BoxWidth, sy = _bmp.PixelHeight / _an.BoxHeight;
            // 우하단 핸들: 새 right/top in screen → pdf
            double newRight = img.Left + (p1.X - (img.Left - _an.BoxLeft) * sx) / sx;
            double newTop = img.Top - (p1.Y - (_an.BoxBottom + _an.BoxHeight - img.Top) * sy) / sy;
            double newW = Math.Max(10, newRight - img.Left);
            double newH = Math.Max(10, newTop - img.Bottom);
            int idx = _selectedImage;
            try
            {
                await RunAsync(Sync(cur => PdfImages.Replace(cur, _page, img, img.Left, img.Bottom, newW, newH)), "이미지 크기를 바꿨습니다.");
                _selectedImage = -1;
            }
            catch (Exception ex)
            {
                Status("이미지 크기 변경 실패: " + ex.Message);
                MessageBox.Show(this, ex.Message, "이미지", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }
        if (!_drag) return;
        _drag = false; Overlay.ReleaseMouseCapture();
        var pUp = e.GetPosition(Overlay);
        if (_rubber != null) { Overlay.Children.Remove(_rubber); _rubber = null; }
        if (_penLine != null) { Overlay.Children.Remove(_penLine); _penLine = null; }
        await ApplyToolAsync(_p0, pUp);
    }

    async Task ApplyToolAsync(Point a, Point b)
    {
        if (_an == null || _bmp == null || _work == null) return;
        var (ax, ay) = ToPdf(a); var (bx, by) = ToPdf(b);
        double x1 = Math.Min(ax, bx), x2 = Math.Max(ax, bx), y1 = Math.Min(ay, by), y2 = Math.Max(ay, by);
        bool click = Math.Abs(a.X - b.X) < 4 && Math.Abs(a.Y - b.Y) < 4;
        int pg = _page; var color = CurColor; double width = Num(WidthBox, 2), alpha = Alpha, size = Math.Max(4, Num(SizeBox, 14));
        string? fam = CurFamily; bool bold = Bold, italic = Italic, fill = FillChk.IsChecked == true;

        switch (_tool)
        {
            case Tool.TextBox:
            {
                var r = FormDialog.Ask(this, "텍스트 상자", ("내용 (여러 줄 가능)", "", true));
                if (r == null || r[0].Trim().Length == 0) return;
                string text = r[0]; double tx = click ? ax : x1, ty = click ? ay : y2; string used = "";
                await RunAsync(async cur =>
                {
                    var fc = await _resolver.ResolveFamilyAsync(fam, bold, italic, text);
                    used = $"{fc.Family} ({fc.Variant})";
                    return await Task.Run(() => PdfOps.AddText(cur, pg, tx, ty, text, fc, size, color, alpha));
                }, "텍스트 상자를 추가했습니다.");
                if (used.Length > 0) Status("텍스트 상자를 추가했습니다. 폰트: " + used);
                break;
            }
            case Tool.Image:
            {
                var ofd = new OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff" };
                if (ofd.ShowDialog(this) != true) return;
                var img = await File.ReadAllBytesAsync(ofd.FileName);
                var fr = BitmapFrame.Create(new MemoryStream(img), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                double ratio = (double)fr.PixelHeight / fr.PixelWidth, w, h, x, y;
                if (click) { w = _an.BoxWidth * 0.35; h = w * ratio; x = ax; y = ay - h; }
                else { w = x2 - x1; h = y2 - y1; x = x1; y = y1; }
                await RunAsync(Sync(cur => PdfOps.AddImage(cur, pg, img, x, y, w, h)), "이미지를 추가했습니다.");
                break;
            }
            case Tool.Table:
            {
                if (click) return;
                var r = FormDialog.Ask(this, "표 추가",
                    ("열 수", "3", false),
                    ("행 수", "3", false),
                    ("셀 내용 (쉼표로 구분, 행 우선)", "A,B,C,1,2,3,4,5,6", true));
                if (r == null) return;
                int cols = int.TryParse(r[0].Trim(), out var cc) ? Math.Max(1, cc) : 3;
                int rows = int.TryParse(r[1].Trim(), out var rr) ? Math.Max(1, rr) : 3;
                var cells = r[2].Split(',').Select(s => s.Trim()).ToArray();
                if (cells.Length < cols * rows)
                    cells = cells.Concat(Enumerable.Repeat("", cols * rows - cells.Length)).ToArray();
                double colW = (x2 - x1) / cols, rowH = (y2 - y1) / rows;
                if (colW < 10 || rowH < 10) { Status("표 영역이 너무 작습니다."); return; }
                await RunAsync(async cur =>
                {
                    var fc = await _resolver.ResolveFamilyAsync(fam, bold, italic, string.Concat(cells) + "가A");
                    return await Task.Run(() => PdfOps.AddTable(cur, pg, x1, y2, cols, cells, fc, size, color, colW, rowH, width));
                }, "표를 추가했습니다.");
                break;
            }
            case Tool.Chart:
            {
                if (click) return;
                var r = FormDialog.Ask(this, "막대 그래프",
                    ("값 (쉼표로 구분)", "10,25,18,30", false),
                    ("라벨 (쉼표로 구분, 선택)", "A,B,C,D", false));
                if (r == null) return;
                var vals = r[0].Split(',').Select(s => double.TryParse(s.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
                var labs = r[1].Split(',').Select(s => s.Trim()).ToArray();
                if (vals.Length == 0) return;
                await RunAsync(async cur =>
                {
                    var fc = await _resolver.ResolveFamilyAsync(fam, false, false, "0123456789가A");
                    return await Task.Run(() => PdfOps.AddBarChart(cur, pg, x1, y1, x2 - x1, y2 - y1, vals, labs, fc, color, new Rgb(60, 60, 60)));
                }, "그래프를 추가했습니다.");
                break;
            }
            case Tool.Rect or Tool.Ellipse:
                if (click) return;
                await RunAsync(Sync(cur => PdfOps.AddShape(cur, pg, _tool == Tool.Rect ? ShapeKind.Rect : ShapeKind.Ellipse, ax, ay, bx, by, color, width, alpha, fill)), "도형을 추가했습니다.");
                break;
            case Tool.Line or Tool.Arrow:
                if (click) return;
                await RunAsync(Sync(cur => PdfOps.AddShape(cur, pg, _tool == Tool.Line ? ShapeKind.Line : ShapeKind.Arrow, ax, ay, bx, by, color, width, alpha, false)), "선을 추가했습니다.");
                break;
            case Tool.Pen:
            {
                var pts = _pen.Select(ToPdf).ToList();
                if (pts.Count < 2) return;
                await RunAsync(Sync(cur => PdfOps.AddInk(cur, pg, pts, color, width, alpha)), "그림을 추가했습니다.");
                break;
            }
            case Tool.Highlight or Tool.Underline or Tool.Strike:
                if (click) return;
                var kind = _tool == Tool.Highlight ? MarkKind.Highlight : _tool == Tool.Underline ? MarkKind.Underline : MarkKind.Strike;
                await RunAsync(Sync(cur => PdfOps.AddMarkup(cur, pg, kind, x1, y1, x2, y2, color, alpha)), "표시를 추가했습니다.");
                break;
            case Tool.Note:
            {
                var r = FormDialog.Ask(this, "메모", ("메모 내용", "", true));
                if (r == null || r[0].Trim().Length == 0) return;
                await RunAsync(Sync(cur => PdfOps.AddNote(cur, pg, ax, ay, r[0])), "메모를 추가했습니다.");
                break;
            }
            case Tool.Link:
            {
                if (click) return;
                var r = FormDialog.Ask(this, "링크", ("웹 주소(URL) 또는 이동할 페이지 번호", "https://", false));
                if (r == null || r[0].Trim().Length == 0) return;
                await RunAsync(Sync(cur => PdfOps.AddLink(cur, pg, x1, y1, x2, y2, r[0])), "링크를 추가했습니다.");
                break;
            }
        }
    }

    // ───────────── 페이지 메뉴 ─────────────
    async void RotateCw_Click(object s, RoutedEventArgs e) { int p = _page; await RunAsync(Sync(b => PdfOps.Rotate(b, p, 90)), "회전했습니다."); }
    async void RotateCcw_Click(object s, RoutedEventArgs e) { int p = _page; await RunAsync(Sync(b => PdfOps.Rotate(b, p, -90)), "회전했습니다."); }

    async void DeletePage_Click(object s, RoutedEventArgs e)
    {
        if (_work == null) return;
        if (_pageCount <= 1) { Status("마지막 한 페이지는 삭제할 수 없습니다."); return; }
        int p = _page;
        await RunAsync(Sync(b => PdfOps.DeletePage(b, p)), $"{p}페이지를 삭제했습니다.");
    }

    async void MoveUp_Click(object s, RoutedEventArgs e) => await MoveAsync(-1);
    async void MoveDown_Click(object s, RoutedEventArgs e) => await MoveAsync(1);

    async Task MoveAsync(int d)
    {
        if (_work == null) return;
        int p = _page, q = p + d;
        if (q < 1 || q > _pageCount) { Status("더 이동할 수 없습니다."); return; }
        var order = Enumerable.Range(1, _pageCount).ToList();
        (order[p - 1], order[q - 1]) = (order[q - 1], order[p - 1]);
        await RunAsync(Sync(b => PdfOps.Pick(b, order)), "페이지를 이동했습니다.", q);
    }

    async void InsertBlank_Click(object s, RoutedEventArgs e)
    {
        int p = _page;
        await RunAsync(Sync(b => PdfOps.InsertBlank(b, p)), "빈 페이지를 삽입했습니다.", p + 1);
    }

    async void InsertPdf_Click(object s, RoutedEventArgs e)
    {
        if (_work == null) return;
        var dlg = new OpenFileDialog { Filter = "PDF 파일 (*.pdf)|*.pdf" };
        if (dlg.ShowDialog(this) != true) return;
        var other = await File.ReadAllBytesAsync(dlg.FileName);
        int p = _page;
        await RunAsync(Sync(b => PdfOps.InsertPdf(b, other, p)), "PDF를 삽입했습니다.", p + 1);
    }

    async void Crop_Click(object s, RoutedEventArgs e)
    {
        if (_work == null) return;
        var r = FormDialog.Ask(this, "페이지 자르기 (mm)", ("위", "10", false), ("아래", "10", false), ("왼쪽", "10", false), ("오른쪽", "10", false), ("적용 범위 (현재 / 전체)", "현재", false));
        if (r == null) return;
        double k = 72.0 / 25.4;
        double t = Num(r[0]) * k, bt = Num(r[1]) * k, l = Num(r[2]) * k, rt = Num(r[3]) * k;
        int target = r[4].Trim() == "전체" ? 0 : _page;
        await RunAsync(Sync(b => PdfOps.Crop(b, target, l, t, rt, bt)), "페이지를 잘랐습니다.");
    }

    static double Num(string s) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // ───────────── 문서 메뉴 ─────────────
    async void Watermark_Click(object s, RoutedEventArgs e)
    {
        if (_work == null) return;
        var r = FormDialog.Ask(this, "워터마크 (글자 색상/글꼴/굵게는 상단 옵션 사용)", ("텍스트", "CONFIDENTIAL", false), ("크기(pt)", "64", false), ("각도(°)", "45", false), ("투명도(%)", "25", false));
        if (r == null || r[0].Trim().Length == 0) return;
        string text = r[0]; double size = Num(r[1]), angle = Num(r[2]), alpha = Math.Clamp(Num(r[3]) / 100, 0.05, 1);
        var color = CurColor; var fam = CurFamily; bool b = Bold, i = Italic;
        await RunAsync(async cur =>
        {
            var fc = await _resolver.ResolveFamilyAsync(fam, b, i, text);
            return await Task.Run(() => PdfOps.Watermark(cur, text, fc, size, angle, alpha, color));
        }, "워터마크를 추가했습니다.");
    }

    async void HeaderFooter_Click(object s, RoutedEventArgs e)
    {
        if (_work == null) return;
        var r = FormDialog.Ask(this, "머리글/바닥글  ({n}=쪽 번호, {N}=전체 쪽 수)",
            ("머리글 왼쪽", "", false), ("머리글 가운데", "", false), ("머리글 오른쪽", "", false),
            ("바닥글 왼쪽", "", false), ("바닥글 가운데", "{n} / {N}", false), ("바닥글 오른쪽", "", false),
            ("글자 크기(pt)", "10", false), ("여백(pt)", "24", false));
        if (r == null) return;
        var texts = r.Take(6).ToArray(); double size = Num(r[6]), margin = Num(r[7]);
        var color = CurColor; var fam = CurFamily; bool bold = Bold, italic = Italic;
        string all = string.Concat(texts);
        await RunAsync(async cur =>
        {
            var fc = await _resolver.ResolveFamilyAsync(fam, bold, italic, all.Length > 0 ? all : "0");
            return await Task.Run(() => PdfOps.HeaderFooter(cur, texts, fc, size <= 0 ? 10 : size, color, margin));
        }, "머리글/바닥글을 추가했습니다.");
    }

    async void Background_Click(object s, RoutedEventArgs e)
    {
        var c = CurColor;
        await RunAsync(Sync(b => PdfOps.Background(b, c)), "배경색을 적용했습니다.");
    }

    // ───────────── 되돌리기 / 탐색 / 확대 ─────────────
    async void Undo_Click(object s, RoutedEventArgs e) => await UndoAsync();

    async Task UndoAsync()
    {
        if (_busy || _undo.Count == 0) return;
        _finishEditor?.Invoke(false);
        _work = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        _pageCount = PdfOps.PageCount(_work);
        _page = Math.Clamp(_page, 1, _pageCount);
        MarkDirty();
        Status("되돌렸습니다.");
        await LoadPageAsync();
    }

    async void Prev_Click(object s, RoutedEventArgs e) { if (_page > 1) { _page--; await LoadPageAsync(); } }
    async void Next_Click(object s, RoutedEventArgs e) { if (_page < _pageCount) { _page++; await LoadPageAsync(); } }
    async void ZoomIn_Click(object s, RoutedEventArgs e) { _zoom = Math.Min(4, _zoom * 1.2); await LoadPageAsync(); }
    async void ZoomOut_Click(object s, RoutedEventArgs e) { _zoom = Math.Max(0.4, _zoom / 1.2); await LoadPageAsync(); }

    async void ZoomFitWidth_Click(object s, RoutedEventArgs e)
    {
        if (_an == null) return;
        // ScrollViewer 실제 폭에서 여백을 뺀 값에 맞춤
        var sv = FindScrollViewer();
        double avail = sv != null ? Math.Max(200, sv.ViewportWidth - 48) : 900;
        _zoom = Math.Clamp(avail / _an.BoxWidth, 0.4, 4);
        await LoadPageAsync();
    }

    async void ZoomFitPage_Click(object s, RoutedEventArgs e)
    {
        if (_an == null) return;
        var sv = FindScrollViewer();
        double aw = sv != null ? Math.Max(200, sv.ViewportWidth - 48) : 900;
        double ah = sv != null ? Math.Max(200, sv.ViewportHeight - 48) : 700;
        double zx = aw / _an.BoxWidth, zy = ah / _an.BoxHeight;
        _zoom = Math.Clamp(Math.Min(zx, zy), 0.4, 4);
        await LoadPageAsync();
    }

    ScrollViewer? FindScrollViewer()
    {
        DependencyObject? d = PageHost;
        while (d != null)
        {
            if (d is ScrollViewer sv) return sv;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        // 선택된 이미지 삭제
        if (e.Key == Key.Delete && _tool == Tool.ImageEdit && _selectedImage >= 0 && _selectedImage < _images.Count && _work != null)
        {
            e.Handled = true;
            var img = _images[_selectedImage];
            try
            {
                await RunAsync(Sync(cur => PdfImages.Delete(cur, _page, img)), "이미지를 삭제했습니다.");
            }
            catch (Exception ex)
            {
                // 스트림 위치를 못 찾으면 가림으로 폴백
                var r = MessageBox.Show(this,
                    "스트림에서 이미지를 완전히 제거하지 못했습니다.\n" + ex.Message +
                    "\n\n대신 흰 사각형으로 가리겠습니까?",
                    "이미지 삭제", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r == MessageBoxResult.Yes)
                    await RunAsync(Sync(cur => PdfImages.Cover(cur, _page, img.Left, img.Bottom, img.Right - img.Left, img.Top - img.Bottom)),
                        "이미지를 가렸습니다(완전 삭제 아님).");
            }
            _selectedImage = -1;
            return;
        }
        // F3: 다음/이전 찾기
        if (e.Key == Key.F3)
        {
            e.Handled = true;
            if (_findHits.Count == 0) Find_Click(this, e);
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) FindPrev();
            else FindNext();
            return;
        }
        var mod = Keyboard.Modifiers;
        if (!mod.HasFlag(ModifierKeys.Control)) return;
        if (e.Key == Key.O) { e.Handled = true; await OpenAsync(); }
        else if (e.Key == Key.N) { e.Handled = true; await NewAsync(); }
        else if (e.Key == Key.S)
        {
            e.Handled = true;
            Save(saveAs: mod.HasFlag(ModifierKeys.Shift));
        }
        else if (e.Key == Key.Z) { e.Handled = true; await UndoAsync(); }
        else if (e.Key == Key.F) { e.Handled = true; Find_Click(this, e); }
        else if (e.Key == Key.P) { e.Handled = true; Print_Click(this, e); }
    }

    // ───────────── 폰트 ─────────────
    async void RefreshFonts_Click(object sender, RoutedEventArgs e)
    {
        Status("Google Fonts 목록을 갱신하는 중…");
        bool ok = await _gf.EnsureCatalogAsync(force: true);
        if (ok) await LoadFamiliesAsync();
        Status(ok ? "폰트 목록을 갱신했습니다." : "목록을 가져오지 못했습니다(오프라인이거나 API 키 오류). 저장된 목록/폰트로 계속 동작합니다.");
    }

    async void Prefetch_Click(object sender, RoutedEventArgs e)
    {
        Status("폰트 목록 확인 중…");
        if (!await _gf.EnsureCatalogAsync())
        {
            MessageBox.Show(this, "폰트 목록을 가져오지 못했습니다. 인터넷 연결과 API 키를 확인해 주세요.", "오프라인용 폰트", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await _gf.PrefetchAsync(FontMapper.PrefetchSet, new Progress<string>(Status));
        Status("오프라인용 폰트를 저장했습니다. 이제 인터넷 없이도 편집할 수 있습니다.");
    }

    // ───────────── 찾기 ─────────────
    void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_work == null) { Status("먼저 PDF를 여세요."); return; }
        var r = FormDialog.Ask(this, "찾기",
            ("찾을 내용", _findQuery, false),
            ("대소문자 구분 (예/아니오)", "아니오", false));
        if (r == null) return;
        _findQuery = r[0];
        bool matchCase = r[1].Trim().StartsWith("예");
        if (string.IsNullOrWhiteSpace(_findQuery)) return;

        Status("검색 중…");
        try
        {
            _findHits = PdfSearch.Find(_work, _findQuery, matchCase);
            _findIndex = _findHits.Count > 0 ? 0 : -1;
            if (_findHits.Count == 0)
            {
                Status($"\"{_findQuery}\"을(를) 찾지 못했습니다.");
                DrawOverlay();
                return;
            }
            Status($"{_findHits.Count}개 찾음 — Enter/F3 다음, Shift+F3 이전");
            _ = GoToFindHitAsync(0);
        }
        catch (Exception ex)
        {
            Status("검색 실패: " + ex.Message);
        }
    }

    async Task GoToFindHitAsync(int index)
    {
        if (_findHits.Count == 0) return;
        _findIndex = ((index % _findHits.Count) + _findHits.Count) % _findHits.Count;
        var hit = _findHits[_findIndex];
        if (hit.Page != _page)
        {
            _page = hit.Page;
            await LoadPageAsync();
        }
        else
            DrawOverlay();
        Status($"찾기 {_findIndex + 1}/{_findHits.Count}: \"{hit.Text}\" (페이지 {hit.Page})");
    }

    async void FindNext() => await GoToFindHitAsync(_findIndex + 1);
    async void FindPrev() => await GoToFindHitAsync(_findIndex - 1);

    // ───────────── 인쇄 ─────────────
    async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_work == null) { Status("먼저 PDF를 여세요."); return; }
        try
        {
            var dlg = new System.Windows.Controls.PrintDialog();
            if (dlg.ShowDialog() != true) return;

            Status("인쇄 준비 중…");
            int n = _pageCount;
            // 너무 많은 페이지면 확인
            if (n > 30)
            {
                var conf = MessageBox.Show(this,
                    $"문서가 {n}페이지입니다. 전부 인쇄할까요?\n(시간이 걸릴 수 있습니다)",
                    "인쇄", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (conf != MessageBoxResult.Yes) return;
            }

            var doc = new System.Windows.Documents.FixedDocument();
            doc.DocumentPaginator.PageSize = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);

            for (int i = 1; i <= n; i++)
            {
                Status($"인쇄 렌더링 {i}/{n}…");
                // 인쇄용 해상도 (약 150dpi 상당)
                double zoom = 2.0;
                var bmp = await PageRenderer.RenderAsync(_work, i, zoom);
                var img = new System.Windows.Controls.Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    Width = dlg.PrintableAreaWidth - 20,
                    Height = dlg.PrintableAreaHeight - 20,
                };
                var container = new System.Windows.Controls.Border
                {
                    Width = dlg.PrintableAreaWidth,
                    Height = dlg.PrintableAreaHeight,
                    Child = img,
                    Padding = new Thickness(10),
                };
                container.Measure(new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight));
                container.Arrange(new Rect(0, 0, dlg.PrintableAreaWidth, dlg.PrintableAreaHeight));
                container.UpdateLayout();

                var pageContent = new System.Windows.Documents.PageContent();
                var fixedPage = new System.Windows.Documents.FixedPage
                {
                    Width = dlg.PrintableAreaWidth,
                    Height = dlg.PrintableAreaHeight,
                };
                fixedPage.Children.Add(container);
                ((System.Windows.Markup.IAddChild)pageContent).AddChild(fixedPage);
                doc.Pages.Add(pageContent);
            }

            dlg.PrintDocument(doc.DocumentPaginator,
                string.IsNullOrEmpty(_path) ? "PDF 편집기" : Path.GetFileName(_path));
            Status("인쇄 작업을 보냈습니다.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "인쇄에 실패했습니다.\n" + ex.Message, "인쇄",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Status("인쇄 실패");
        }
    }

    // ───────────── Word 내보내기 ─────────────
    void ExportWord_Click(object sender, RoutedEventArgs e)
    {
        if (_work == null) { Status("먼저 PDF를 여세요."); return; }
        var dlg = new SaveFileDialog
        {
            Filter = "Word 문서 (*.docx)|*.docx",
            FileName = (string.IsNullOrEmpty(_path) ? "제목 없음" : Path.GetFileNameWithoutExtension(_path)) + ".docx",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Status("Word로 내보내는 중…");
            PdfExport.ToWord(_work, dlg.FileName);
            Status($"Word로 저장했습니다: {dlg.FileName}");
            MessageBox.Show(this,
                "텍스트를 Word 문서로 내보냈습니다.\n\n레이아웃·이미지는 포함되지 않으며, 추출 가능한 글자만 들어갑니다.",
                "Word 내보내기", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "내보내기에 실패했습니다.\n" + ex.Message, "Word 내보내기",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ───────────── 인증서 전자서명 ─────────────
    async void Sign_Click(object sender, RoutedEventArgs e)
    {
        if (_work == null) { Status("먼저 PDF를 여세요."); return; }

        MessageBox.Show(this, PdfSign.HelpText, "인증서 서명 안내", MessageBoxButton.OK, MessageBoxImage.Information);

        var pfxDlg = new OpenFileDialog
        {
            Filter = "PKCS#12 인증서 (*.pfx;*.p12)|*.pfx;*.p12|모든 파일|*.*",
            Title = "서명에 사용할 인증서 선택",
        };
        if (pfxDlg.ShowDialog(this) != true) return;

        var r = FormDialog.Ask(this, "전자서명",
            ("인증서 비밀번호", "", false),
            ("서명 사유", "문서 승인", false),
            ("위치", "", false),
            ("연락처", "", false));
        if (r == null) return;

        try
        {
            Status("서명 중…");
            var signed = await Task.Run(() =>
                PdfSign.Sign(_work!, pfxDlg.FileName, r[0], r[1], r[2], r[3], _page));
            _undo.Add(_work!);
            if (_undo.Count > 30) _undo.RemoveAt(0);
            _work = signed;
            MarkDirty();
            await LoadPageAsync();
            Status("전자서명을 추가했습니다. 저장하세요.");
            MessageBox.Show(this,
                "서명이 문서에 추가되었습니다.\n반드시 저장(Ctrl+S)하세요.\n\n서명 유효성은 Adobe Reader 등에서 확인할 수 있습니다.",
                "전자서명", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "서명에 실패했습니다.\n" + ex.Message +
                "\n\n비밀번호가 맞는지, 파일에 개인키가 포함돼 있는지 확인하세요.",
                "전자서명", MessageBoxButton.OK, MessageBoxImage.Warning);
            Status("서명 실패");
        }
    }
}

