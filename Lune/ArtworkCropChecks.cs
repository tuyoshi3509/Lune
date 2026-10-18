using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lune;

internal static class ArtworkCropChecks
{
    internal static async Task Run(MainWindow main)
    {
        var results = new List<string>(); var output = Path.Combine(Paths.Root, "test-output"); Directory.CreateDirectory(output);
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("FAIL: " + name); results.Add("PASS: " + name); }
        try
        {
            var original = Fixture(1600, 900); var sourcePath = ArtworkCrop.CacheSource(original); var source = ArtworkCrop.Load(sourcePath);
            var originalHash = SHA256.HashData(File.ReadAllBytes(sourcePath));
            Check(source.PixelWidth == 1600 && source.PixelHeight == 900, "thumbnail source retains resolution and uncropped aspect ratio");
            var selection = new CropSelection(1600, 900);
            Check(selection.Bounds == new Int32Rect(350, 0, 900, 900), "landscape default is centered square");
            selection.MoveTo(new Point(-1000, -1000)); Check(selection.Bounds == new Int32Rect(0, 0, 900, 900), "dragging beyond top-left stops at image edge");
            var left = ArtworkCrop.Save(source, selection.Bounds);
            Check(CenterColor(ArtworkCrop.Load(left)).R > 150 && CenterColor(ArtworkCrop.Load(left)).G < 120, "left crop saves pixels from selected region");
            selection.MoveTo(new Point(9999, 9999)); var right = ArtworkCrop.Save(source, selection.Bounds);
            Check(selection.Bounds == new Int32Rect(700, 0, 900, 900) && CenterColor(ArtworkCrop.Load(right)).B > 150, "right edge crop saves the other side of thumbnail");
            selection.SetZoom(2); Check(selection.Bounds.Width == 450 && selection.Bounds.Width == selection.Bounds.Height, "zoom reduces square selection without stretching");
            var savedZoom = ArtworkCrop.Load(ArtworkCrop.Save(source, selection.Bounds));
            Check(savedZoom.PixelWidth == 450 && savedZoom.PixelHeight == 450, "saved zoomed artwork has exact square dimensions");
            var portrait = new CropSelection(360, 720); portrait.MoveTo(new Point(9999, 9999));
            Check(portrait.Bounds == new Int32Rect(0, 360, 360, 360), "portrait thumbnails support vertical positioning");
            portrait.SetZoom(3); portrait.MoveTo(new Point(-9999, 9999)); Check(portrait.Bounds == new Int32Rect(0, 600, 120, 120), "portrait zoom clamps all edges");
            var tiny = new CropSelection(1, 3); tiny.SetZoom(999); tiny.MoveTo(new Point(9, 9)); Check(tiny.Bounds == new Int32Rect(0, 2, 1, 1), "tiny images retain valid square bounds");
            var before = selection.Bounds; selection.SetZoom(double.NaN); selection.MoveTo(new Point(double.PositiveInfinity, 0)); Check(selection.Bounds == before, "invalid numeric input cannot corrupt crop selection");
            try { ArtworkCrop.Save(source, new Int32Rect(0, 0, 100, 90)); Check(false, "non-square rejection"); } catch (ArgumentException) { results.Add("PASS: non-square output is rejected"); }
            var large = ArtworkCrop.Load(ArtworkCrop.CacheSource(Fixture(3200, 1800)));
            Check(large.PixelWidth == 2048 && large.PixelHeight == 1152, "large sources are bounded for memory use");
            var largeCrop = ArtworkCrop.Load(ArtworkCrop.Save(large, new CropSelection(large.PixelWidth, large.PixelHeight).Bounds));
            Check(largeCrop.PixelWidth == 900 && largeCrop.PixelHeight == 900, "large cropped covers are capped at 900 pixels");
            var importer = new ImportWindow(main); importer.Show(); importer.SetArtworkSource(sourcePath); await Task.Delay(120);
            Check(importer.CropButton.IsEnabled && importer.ArtworkBounds == new Int32Rect(350, 0, 900, 900), "importer prepares square cover and enables adjustment");
            Exception? interactionError = null;
            _ = main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                var editor = Application.Current.Windows.OfType<ArtworkCropWindow>().Single();
                try
                {
                    editor.UpdateLayout(); editor.ZoomSlider.Value = 2; editor.UpdateLayout();
                    Check(editor.Selection.Bounds.Width == 450, "zoom slider updates editor selection");
                    editor.BeginSelectionDrag(new Point(editor.Stage.ActualWidth / 2, editor.Stage.ActualHeight / 2));
                    if (Environment.GetCommandLineArgs().Contains("--require-mouse-capture"))
                    {
                        Check(editor.Stage.IsMouseCaptured, "crop editor captures pointer on interactive desktop");
                        editor.ContinueSelectionDrag(new Point(10000, -10000));
                    }
                    else editor.MoveSelectionToViewPoint(new Point(10000, -10000));
                    editor.Stage.ReleaseMouseCapture();
                    Check(editor.Selection.Bounds == new Int32Rect(1150, 0, 450, 450), "editor maps screen coordinates and clamps at top-right");
                    SelfTests.Capture(editor, "lune-crop-selection.png");
                    editor.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { interactionError = ex; editor.Close(); }
            }));
            importer.CropButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); if (interactionError != null) throw interactionError;
            Check(importer.ArtworkBounds == new Int32Rect(1150, 0, 450, 450) && importer.OriginalArtworkPath == sourcePath, "confirm applies chosen region while retaining original source");
            var confirmedPath = importer.ArtworkPath; Check(CenterColor(ArtworkCrop.Load(confirmedPath)).B > 150, "confirmed import artwork contains dragged-to image region");
            SelfTests.Capture(importer, "lune-import-crop.png");
            _ = main.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                var editor = Application.Current.Windows.OfType<ArtworkCropWindow>().Single();
                try
                {
                    Check(editor.Selection.Bounds == importer.ArtworkBounds, "reopening starts at previously confirmed crop");
                    editor.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(editor.Selection.Bounds == new Int32Rect(350, 0, 900, 900), "reset recovers full-size centered region from original");
                    editor.Selection.MoveTo(new Point(0, 0)); editor.Draw(); editor.Close();
                }
                catch (Exception ex) { interactionError = ex; editor.Close(); }
            }));
            importer.CropButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); if (interactionError != null) throw interactionError;
            Check(importer.ArtworkPath == confirmedPath && importer.ArtworkBounds == new Int32Rect(1150, 0, 450, 450), "cancel preserves last confirmed cover and coordinates");
            Check(SHA256.HashData(File.ReadAllBytes(sourcePath)).SequenceEqual(originalHash), "editing never overwrites uncropped thumbnail source");
            foreach (var extension in new[] { "m4a", "mp3" })
            {
                var audio = Path.Combine(Paths.Data, "cropped-cover." + extension);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await DownloadService.Run(Path.Combine(Paths.Tools, "ffmpeg.exe"), new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", "anullsrc=r=22050:cl=mono", "-t", "0.3", audio }, null, timeout.Token);
                DownloadService.WriteImportTags(audio, "切り抜きテスト", "Lune", "Cropped cover", confirmedPath, "https://www.youtube.com/watch?v=aqz-KE-bpKQ");
                using var tagged = TagLib.File.Create(audio); var art = tagged.Tag.Pictures.Single().Data.Data;
                Check(art.SequenceEqual(File.ReadAllBytes(confirmedPath)), extension + " embeds exactly the selected square cover");
                var track = LibraryService.ReadTrack(audio); Check(ArtworkCrop.Load(track.Artwork).PixelWidth == ArtworkCrop.Load(track.Artwork).PixelHeight && track.HasSourceUrl, extension + " library reads square cover and retains source URL");
            }
            importer.Close();
            var tallSource = ArtworkCrop.CacheSource(Fixture(480, 960)); var tall = new ArtworkCropWindow(main, tallSource); tall.Show(); await Task.Delay(100);
            tall.Selection.MoveTo(new Point(240, 960)); tall.Draw(); SelfTests.Capture(tall, "lune-crop-portrait.png"); tall.Close();
            File.WriteAllLines(Path.Combine(output, "crop-tests.txt"), results); main.ExitApp();
        }
        catch (Exception ex) { results.Add(ex.ToString()); File.WriteAllLines(Path.Combine(output, "crop-tests.txt"), results); Application.Current.Shutdown(1); }
    }
    private static byte[] Fixture(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var colors = new[] { Color.FromRgb(210, 75, 60), Color.FromRgb(60, 155, 85), Color.FromRgb(50, 100, 210) };
            for (var i = 0; i < 3; i++)
            {
                drawing.DrawRectangle(new SolidColorBrush(colors[i]), null, new Rect(width * i / 3d, 0, width / 3d + 1, height));
                var text = new FormattedText(new[] { "LEFT", "CENTER", "RIGHT" }[i], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Min(width, height) / 18d, Brushes.White, 1);
                drawing.DrawText(text, new Point(width * (i + .5) / 3d - text.Width / 2, height * .2));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static Color CenterColor(BitmapSource bitmap)
    {
        var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); var bytes = new byte[4];
        pixels.CopyPixels(new Int32Rect(pixels.PixelWidth / 2, pixels.PixelHeight / 2, 1, 1), bytes, 4, 0);
        return Color.FromRgb(bytes[2], bytes[1], bytes[0]);
    }
}
