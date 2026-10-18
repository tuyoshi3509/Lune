using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lune;

internal sealed class CropSelection
{
    internal int Width { get; }
    internal int Height { get; }
    internal double Zoom { get; private set; } = 1;
    internal double MaximumZoom => Math.Max(1, Math.Min(8, Math.Min(Width, Height) / 24d));
    internal Point Center { get; private set; }
    private int Side => Math.Max(1, (int)Math.Round(Math.Min(Width, Height) / Zoom));
    internal Int32Rect Bounds => new(Math.Clamp((int)Math.Round(Center.X - Side / 2d), 0, Width - Side), Math.Clamp((int)Math.Round(Center.Y - Side / 2d), 0, Height - Side), Side, Side);
    internal CropSelection(int width, int height, Int32Rect? initial = null)
    {
        if (width < 1 || height < 1) throw new ArgumentException("画像の大きさが不正です。");
        Width = width; Height = height; Reset();
        if (initial is { } rectangle)
        {
            ArtworkCrop.Validate(width, height, rectangle);
            Zoom = Math.Clamp(Math.Min(width, height) / (double)rectangle.Width, 1, MaximumZoom);
            MoveTo(new Point(rectangle.X + rectangle.Width / 2d, rectangle.Y + rectangle.Height / 2d));
        }
    }
    internal void Reset() { Zoom = 1; Center = new Point(Width / 2d, Height / 2d); }
    internal void MoveTo(Point point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
        var half = Side / 2d;
        Center = new Point(Math.Clamp(point.X, half, Width - half), Math.Clamp(point.Y, half, Height - half));
    }
    internal void SetZoom(double zoom) { if (!double.IsFinite(zoom)) return; Zoom = Math.Clamp(zoom, 1, MaximumZoom); MoveTo(Center); }
}

internal static class ArtworkCrop
{
    internal static BitmapSource Load(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 32 * 1024 * 1024) throw new InvalidDataException("画像は32MB以下にしてください。");
        return Decode(input);
    }
    private static BitmapSource Decode(Stream input)
    {
        var header = BitmapFrame.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var width = header.PixelWidth; var height = header.PixelHeight;
        input.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (Math.Max(width, height) > 2048) { if (width >= height) bitmap.DecodePixelWidth = 2048; else bitmap.DecodePixelHeight = 2048; }
        bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    internal static string CacheSource(byte[] bytes)
    {
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("画像は32MB以下にしてください。");
        using var input = new MemoryStream(bytes);
        return Cache(Decode(input), "source-");
    }
    internal static void Validate(int width, int height, Int32Rect bounds)
    {
        if (bounds.Width < 1 || bounds.Width != bounds.Height || bounds.X < 0 || bounds.Y < 0 || (long)bounds.X + bounds.Width > width || (long)bounds.Y + bounds.Height > height)
            throw new ArgumentException("切り抜き範囲を画像内の正方形にしてください。");
    }
    internal static BitmapSource Preview(BitmapSource source, Int32Rect bounds)
    {
        Validate(source.PixelWidth, source.PixelHeight, bounds);
        var cropped = new CroppedBitmap(source, bounds); cropped.Freeze(); return cropped;
    }
    internal static string Save(BitmapSource source, Int32Rect bounds)
    {
        var cropped = Preview(source, bounds);
        if (cropped.PixelWidth > 900)
        {
            var scaled = new TransformedBitmap(cropped, new ScaleTransform(900d / cropped.PixelWidth, 900d / cropped.PixelHeight)); scaled.Freeze(); cropped = scaled;
        }
        return Cache(cropped, "custom-crop-");
    }
    private static string Cache(BitmapSource bitmap, string prefix)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 94 }; encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); var bytes = output.ToArray();
        var key = prefix + Convert.ToHexString(SHA256.HashData(bytes))[..24] + ".jpg";
        var path = Path.Combine(Paths.Art, key);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return path;
    }
}
