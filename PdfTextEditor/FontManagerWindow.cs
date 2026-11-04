using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PdfTextEditor.Fonts;

namespace PdfTextEditor;

/// <summary>언어별 Google Fonts 탐색·다운로드 + 사용자 글꼴 추가 창.</summary>
public sealed class FontManagerWindow : Window
{
    readonly GoogleFontsClient _gf;
    readonly CustomFontStore _custom;
    readonly ListBox _list;
    readonly TextBox _search;
    readonly ComboBox _lang;
    readonly TextBlock _status;
    readonly Button _downloadBtn;
    readonly Button _addCustomBtn;
    readonly Button _removeCustomBtn;
    bool _busy;

    static readonly (string Key, string Label)[] Languages =
    {
        ("all", "전체"),
        ("korean", "한국어"),
        ("japanese", "일본어"),
        ("chinese", "중국어"),
        ("latin", "라틴(영문 등)"),
        ("cyrillic", "키릴"),
        ("arabic", "아랍어"),
        ("thai", "태국어"),
        ("devanagari", "힌디어"),
        ("hebrew", "히브리어"),
        ("downloaded", "다운로드됨"),
        ("custom", "내 글꼴"),
    };

    public FontManagerWindow(Window owner, GoogleFontsClient gf, CustomFontStore custom)
    {
        _gf = gf;
        _custom = custom;
        Owner = owner;
        Title = "글꼴 관리";
        Width = 720;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#F5F5F5");

        var root = new DockPanel { Margin = new Thickness(0) };

        // 상단 헤더
        var header = new Border
        {
            Background = Brush("#2C2C2C"),
            Padding = new Thickness(16, 12, 16, 12),
        };
        DockPanel.SetDock(header, Dock.Top);
        header.Child = new TextBlock
        {
            Text = "글꼴 관리 — Google Fonts · 사용자 글꼴",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        };
        root.Children.Add(header);

        // 하단 상태 + 닫기
        var footer = new Border
        {
            Background = Brush("#E8E8E8"),
            Padding = new Thickness(12, 8, 12, 8),
            BorderBrush = Brush("#CCCCCC"),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        var footPanel = new DockPanel();
        _status = new TextBlock { Text = "준비", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#555") };
        DockPanel.SetDock(_status, Dock.Left);
        var closeBtn = MakeBtn("닫기", false);
        closeBtn.Click += (_, _) => Close();
        DockPanel.SetDock(closeBtn, Dock.Right);
        footPanel.Children.Add(closeBtn);
        footPanel.Children.Add(_status);
        footer.Child = footPanel;
        root.Children.Add(footer);

        // 본문
        var body = new Grid { Margin = new Thickness(12) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 필터 줄
        var filter = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _lang = new ComboBox
        {
            Width = 140,
            ItemsSource = Languages.Select(l => l.Label).ToList(),
            SelectedIndex = 1, // 한국어 기본
            VerticalContentAlignment = VerticalAlignment.Center,
            Height = 28,
        };
        _lang.SelectionChanged += (_, _) => RefreshList();
        DockPanel.SetDock(_lang, Dock.Left);

        _search = new TextBox
        {
            Margin = new Thickness(8, 0, 0, 0),
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0),
        };
        _search.TextChanged += (_, _) => RefreshList();
        var searchLabel = new TextBlock
        {
            Text = "언어",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = Brush("#444"),
        };
        DockPanel.SetDock(searchLabel, Dock.Left);
        var searchHint = new TextBlock
        {
            Text = "검색",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 6, 0),
            Foreground = Brush("#444"),
        };
        DockPanel.SetDock(searchHint, Dock.Left);

        filter.Children.Add(searchLabel);
        filter.Children.Add(_lang);
        filter.Children.Add(searchHint);
        filter.Children.Add(_search);
        Grid.SetRow(filter, 0);
        body.Children.Add(filter);

        // 리스트
        _list = new ListBox
        {
            Background = Brushes.White,
            BorderBrush = Brush("#CCC"),
            BorderThickness = new Thickness(1),
            FontSize = 13,
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        Grid.SetRow(_list, 1);
        body.Children.Add(_list);

        // 액션 버튼
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _downloadBtn = MakeBtn("선택 글꼴 다운로드", true);
        _downloadBtn.Click += async (_, _) => await DownloadSelectedAsync();
        _addCustomBtn = MakeBtn("내 컴퓨터에서 글꼴 추가…", false);
        _addCustomBtn.Click += (_, _) => AddCustom();
        _removeCustomBtn = MakeBtn("선택 글꼴 제거", false);
        _removeCustomBtn.Click += (_, _) => RemoveCustom();
        _removeCustomBtn.IsEnabled = false;

        actions.Children.Add(_downloadBtn);
        actions.Children.Add(new Border { Width = 8 });
        actions.Children.Add(_addCustomBtn);
        actions.Children.Add(new Border { Width = 8 });
        actions.Children.Add(_removeCustomBtn);
        Grid.SetRow(actions, 2);
        body.Children.Add(actions);

        root.Children.Add(body);
        Content = root;

        Loaded += async (_, _) =>
        {
            _status.Text = "Google Fonts 목록 확인 중…";
            await _gf.EnsureCatalogAsync();
            RefreshList();
            _status.Text = $"{_gf.Families().Count}개 글꼴 목록 로드됨 · 내 글꼴 {_custom.All.Count}개";
        };
    }

    string LangKey => Languages[Math.Max(0, _lang.SelectedIndex)].Key;

    void RefreshList()
    {
        _list.Items.Clear();
        var key = LangKey;
        var search = _search.Text?.Trim() ?? "";

        if (key == "custom")
        {
            foreach (var e in _custom.All)
            {
                if (search.Length > 0 && !e.Family.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                _list.Items.Add(new FontListItem($"[내 글꼴] {e.Family}", e.Family, true, true));
            }
        }
        else if (key == "downloaded")
        {
            foreach (var fam in _gf.Families())
            {
                if (!_gf.IsDownloaded(fam)) continue;
                if (search.Length > 0 && !fam.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                _list.Items.Add(new FontListItem($"✓ {fam}", fam, false, true));
            }
            foreach (var e in _custom.All)
            {
                if (search.Length > 0 && !e.Family.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                _list.Items.Add(new FontListItem($"[내 글꼴] {e.Family}", e.Family, true, true));
            }
        }
        else
        {
            var fonts = _gf.FilterByLanguage(key, search);
            foreach (var f in fonts)
            {
                bool dl = _gf.IsDownloaded(f.Family);
                var subsets = string.Join(", ", f.Subsets.Take(4));
                var label = dl ? $"✓ {f.Family}" : f.Family;
                if (subsets.Length > 0) label += $"  ·  {subsets}";
                if (!string.IsNullOrEmpty(f.Category)) label += $"  [{f.Category}]";
                _list.Items.Add(new FontListItem(label, f.Family, false, dl));
            }
        }

        _status.Text = $"{_list.Items.Count}개 표시";
        UpdateButtons();
    }

    void UpdateButtons()
    {
        if (_list.SelectedItem is not FontListItem item)
        {
            _downloadBtn.IsEnabled = false;
            _removeCustomBtn.IsEnabled = false;
            return;
        }
        _downloadBtn.IsEnabled = !item.IsCustom && !item.IsDownloaded && !_busy;
        _removeCustomBtn.IsEnabled = item.IsCustom && !_busy;
    }

    async Task DownloadSelectedAsync()
    {
        if (_busy || _list.SelectedItem is not FontListItem item || item.IsCustom) return;
        _busy = true;
        _downloadBtn.IsEnabled = false;
        try
        {
            _status.Text = $"{item.Family} 다운로드 중…";
            var (ok, fail) = await _gf.DownloadFamilyAsync(item.Family, new Progress<string>(s => _status.Text = s));
            _status.Text = ok > 0
                ? $"{item.Family}: {ok}개 파일 저장됨" + (fail > 0 ? $", {fail}개 실패" : "")
                : $"{item.Family}: 다운로드 실패 (오프라인 또는 파일 없음)";
            RefreshList();
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    void AddCustom()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "폰트 파일 (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc|모든 파일|*.*",
            Multiselect = true,
            Title = "추가할 글꼴 파일 선택",
        };
        if (dlg.ShowDialog(this) != true) return;
        int n = 0;
        foreach (var path in dlg.FileNames)
        {
            try
            {
                var e = _custom.AddFromFile(path);
                n++;
                _status.Text = $"추가됨: {e.Family}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{Path.GetFileName(path)}: {ex.Message}", "글꼴 추가 실패",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        if (n > 0)
        {
            _lang.SelectedIndex = Languages.ToList().FindIndex(l => l.Key == "custom");
            RefreshList();
            _status.Text = $"{n}개 글꼴을 추가했습니다.";
        }
    }

    void RemoveCustom()
    {
        if (_list.SelectedItem is not FontListItem item || !item.IsCustom) return;
        if (MessageBox.Show(this, $"'{item.Family}' 글꼴을 제거할까요?", "확인",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _custom.Remove(item.Family);
        RefreshList();
        _status.Text = $"제거됨: {item.Family}";
    }

    static Button MakeBtn(string text, bool primary)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 0, 0),
            MinWidth = 100,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (primary)
        {
            b.Background = Brush("#E3482B"); // Acrobat-ish red accent
            b.Foreground = Brushes.White;
            b.BorderThickness = new Thickness(0);
            b.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            b.Background = Brushes.White;
            b.BorderBrush = Brush("#AAA");
            b.BorderThickness = new Thickness(1);
        }
        return b;
    }

    static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;

    sealed class FontListItem
    {
        public string Display { get; }
        public string Family { get; }
        public bool IsCustom { get; }
        public bool IsDownloaded { get; }
        public FontListItem(string display, string family, bool custom, bool downloaded)
        {
            Display = display; Family = family; IsCustom = custom; IsDownloaded = downloaded;
        }
        public override string ToString() => Display;
    }
}
