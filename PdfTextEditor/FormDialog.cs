using System.Windows;
using System.Windows.Controls;

namespace PdfTextEditor;

/// <summary>여러 입력칸을 가진 간단한 대화상자.</summary>
public sealed class FormDialog : Window
{
    readonly List<TextBox> _boxes = new();

    public static string[]? Ask(Window owner, string title, params (string Label, string Default, bool Multi)[] fields)
    {
        var d = new FormDialog(owner, title, fields);
        return d.ShowDialog() == true ? d._boxes.Select(b => b.Text).ToArray() : null;
    }

    FormDialog(Window owner, string title, (string Label, string Default, bool Multi)[] fields)
    {
        Owner = owner; Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 380;

        var sp = new StackPanel { Margin = new Thickness(14) };
        foreach (var f in fields)
        {
            sp.Children.Add(new TextBlock { Text = f.Label, Margin = new Thickness(0, 6, 0, 2) });
            var tb = new TextBox
            {
                Text = f.Default, AcceptsReturn = f.Multi, TextWrapping = TextWrapping.Wrap,
                Height = f.Multi ? 90 : double.NaN, VerticalScrollBarVisibility = f.Multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            };
            _boxes.Add(tb); sp.Children.Add(tb);
        }
        var ok = new Button { Content = "확인", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = !fields.Any(x => x.Multi) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "취소", Width = 80, IsCancel = true };
        sp.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { ok, cancel } });
        Content = sp;
        Loaded += (_, _) => { _boxes[0].Focus(); _boxes[0].SelectAll(); };
    }
}
