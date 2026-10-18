using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Lune;

internal static class MarqueeChecks
{
    internal static async Task CheckTail(MarqueeText marquee, Action<bool, string> check, string screenshot)
    {
        await Task.Delay(120);
        await marquee.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        marquee.UpdateLayout();
        var label = (TextBlock)((Decorator)marquee.Content).Child;
        var reference = new TextBlock { Text = marquee.Text, TextWrapping = TextWrapping.NoWrap, FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight, FontStyle = label.FontStyle, FontStretch = label.FontStretch };
        TextOptions.SetTextFormattingMode(reference, TextOptions.GetTextFormattingMode(label));
        reference.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        check(marquee.IsScrolling && label.ActualWidth >= reference.DesiredSize.Width - 1, "scrolling title is laid out at full text width: " + screenshot);
        var clip = LayoutInformation.GetLayoutClip(label);
        check(clip == null || clip.Bounds.Right >= reference.DesiredSize.Width - 1, "moving text has no layout clip that removes its tail: " + screenshot);
        var clock = marquee.ScrollClock!; var animation = (DoubleAnimationUsingKeyFrames)clock.Timeline;
        clock.Controller!.Pause(); clock.Controller.SeekAlignedToLastTick(animation.KeyFrames[2].KeyTime.TimeSpan + TimeSpan.FromMilliseconds(100), TimeSeekOrigin.BeginTime);
        await Task.Delay(80); marquee.UpdateLayout();
        var offset = ((TranslateTransform)label.RenderTransform).X;
        check(offset < 0 && Math.Abs(offset - animation.KeyFrames[2].Value) < 1, $"animation reaches the full title's final position: {screenshot} (actual {offset:F1}, expected {animation.KeyFrames[2].Value:F1})");
        check(label.ActualWidth + offset <= marquee.ActualWidth + 1 && label.ActualWidth + offset >= marquee.ActualWidth - 3, "last character ends inside the visible viewport: " + screenshot);
        var width = (int)Math.Ceiling(marquee.ActualWidth); var height = (int)Math.Ceiling(marquee.ActualHeight);
        // Render the actual window, including all ancestor layout and clipping.
        // Rendering an animated child in isolation can omit its composition content.
        var window = Window.GetWindow(marquee); window.UpdateLayout();
        var frame = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); frame.Render(window);
        var origin = marquee.TransformToAncestor(window).Transform(new Point());
        var bitmap = new CroppedBitmap(frame, new Int32Rect((int)Math.Floor(origin.X), (int)Math.Floor(origin.Y), width, height));
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        var tailPixels = 0;
        var ink = ((SolidColorBrush)label.Foreground).Color;
        for (var y = 0; y < height; y++) for (var x = Math.Max(0, width - 60); x < width; x++) {
            var pixel = (y * width + x) * 4;
            if (Math.Abs(pixels[pixel] - ink.B) < 35 && Math.Abs(pixels[pixel + 1] - ink.G) < 35 && Math.Abs(pixels[pixel + 2] - ink.R) < 35) tailPixels++;
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(Paths.Root, "test-output", screenshot))) encoder.Save(file);
        check(tailPixels > 40, "actual rendering contains the trailing characters instead of blank space: " + screenshot);
    }
    internal static async Task Run(MainWindow main, Action<bool, string> check)
    {
        var window = new Window { Owner = main, Width = 760, Height = 310, WindowStartupLocation = WindowStartupLocation.CenterOwner, Title = "曲名末尾の表示確認" };
        var stack = Dialogs.Shell(window, "曲名スクロールの確認");
        var title = new MarqueeText { Width = 180, FontSize = 18, FontWeight = FontWeights.SemiBold, Text = "長い曲名の全文が見えることを確認 — Very long music title with Japanese 日本語 — 最後まで読めます END", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(title); window.Show(); await Task.Delay(140);
        await CheckTail(title, check, "lune-marquee-tail-mixed.png");
        title.Width = 110; await Task.Delay(100); await CheckTail(title, check, "lune-marquee-tail-narrow.png");
        title.FontSize = 25; title.FontStyle = FontStyles.Italic; await Task.Delay(100); await CheckTail(title, check, "lune-marquee-tail-large.png");
        title.Text = "Short"; await Task.Delay(80);
        check(!title.IsScrolling && title.ScrollClock == null, "short title stops animation and resets scroll position");
        title.Text = "幅を広げると収まる曲名 - END"; await Task.Delay(80); check(title.IsScrolling, "narrow viewport starts scrolling on title change");
        title.Width = 650; await Task.Delay(80); check(!title.IsScrolling, "widening viewport stops animation when full title fits");
        title.Width = 180; title.FontSize = 14; title.FontStyle = FontStyles.Normal; title.OnHover = true; title.Text = "曲一覧で長いタイトルの末尾まで表示する確認用です — 最後の文字 END"; await Task.Delay(80);
        check(!title.IsScrolling && ((TextBlock)((Decorator)title.Content).Child).TextTrimming == TextTrimming.CharacterEllipsis, "list title stays trimmed and idle without hover");
        title.OnHover = false; await Task.Delay(80); await CheckTail(title, check, "lune-marquee-tail-list-size.png");
        window.Hide(); await Task.Delay(60); check(!title.IsScrolling && title.ScrollClock == null, "hidden title removes animation clock");
        window.Show(); await Task.Delay(100); check(title.IsScrolling, "showing title resumes full text scrolling");
        window.WindowState = WindowState.Minimized; await Task.Delay(60); check(!title.IsScrolling, "minimized window stops title animation");
        window.Close();
    }
}
