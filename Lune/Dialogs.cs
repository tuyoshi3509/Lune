using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lune;
public static class Dialogs
{
    public static StackPanel Shell(Window window, string title, string subtitle = "")
    {
        window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize;
        var border = new Border { BorderThickness = new Thickness(1) }; border.SetResourceReference(Border.BorderBrushProperty, "Line"); border.SetResourceReference(Border.BackgroundProperty, "Bg");
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); grid.RowDefinitions.Add(new RowDefinition()); border.Child = grid;
        var header = new Grid { Background = Brushes.Transparent, Margin = new Thickness(18, 0, 6, 0) }; header.Children.Add(new TextBlock { Text = "◒   L U N E", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        var close = Button("×", window.Close); close.Width = 34; close.Height = 34; close.Padding = new Thickness(0); close.Margin = new Thickness(0); close.HorizontalAlignment = HorizontalAlignment.Right; header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Grid || e.OriginalSource is TextBlock) try { window.DragMove(); } catch { } }; grid.Children.Add(header);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(26, 10, 18, 24), Padding = new Thickness(0, 0, 8, 0) }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var content = new StackPanel(); scroll.Content = content;
        content.Children.Add(new TextBlock { Text = title, FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        if (subtitle.Length > 0) content.Children.Add(Note(subtitle));
        window.Content = border; window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        return content;
    }
    public static TextBlock Note(string text) { var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 20 }; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return t; }
    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 10) };
    public static Button Button(string text, Action action, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 7, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (primary) b.SetResourceReference(FrameworkElement.StyleProperty, "Primary"); else b.SetResourceReference(Control.BackgroundProperty, "Surface");
        b.Click += (_, _) => action(); return b;
    }
    public static TextBox Field(Panel panel, string title, string value = "")
    {
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 12, 0, 6), FontSize = 12 });
        var text = new TextBox { Text = value }; panel.Children.Add(text); return text;
    }
    public static CheckBox Check(Panel panel, string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Content = text, IsChecked = value }; c.Checked += (_, _) => changed(true); c.Unchecked += (_, _) => changed(false); panel.Children.Add(c); return c;
    }
    public static string? Prompt(Window owner, string title, string label, string initial)
    {
        var window = new Window { Owner = owner, Title = title, Width = 430, Height = 278, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var stack = Shell(window, title); var input = Field(stack, label, initial); string? result = null;
        void Save() { if (string.IsNullOrWhiteSpace(input.Text)) { input.Focus(); return; } result = input.Text.Trim(); window.Close(); }
        stack.Children.Add(Button("保存", Save, true)); input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; window.ShowDialog(); return result;
    }
}
