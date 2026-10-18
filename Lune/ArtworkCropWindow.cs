using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Lune;

internal sealed class ArtworkCropWindow : Window
{
    private readonly BitmapSource source;
    internal CropSelection Selection { get; }
    internal Canvas Stage { get; } = new() { Height = 320, Background = new SolidColorBrush(Color.FromRgb(8, 11, 13)), ClipToBounds = true, Focusable = true, Cursor = Cursors.SizeAll };
    internal Slider ZoomSlider { get; }
    internal Button ApplyButton { get; }
    internal Button ResetButton { get; }
    internal string? ResultPath { get; private set; }
    internal Int32Rect? ResultBounds { get; private set; }
    private readonly Image fullImage, preview;
    private readonly Path shade = new() { Fill = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle frame = new() { Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Line[] guides = { new(), new(), new(), new() };
    private readonly TextBlock zoomLabel, dimensions, error;
    private double scale, offsetX, offsetY;
    private bool dragging;
    private Vector grabOffset;
    internal ArtworkCropWindow(Window owner, string path, Int32Rect? initial = null)
    {
        Owner = owner; Title = "アルバムアートを切り抜く"; Width = 690; Height = Math.Min(710, SystemParameters.WorkArea.Height - 32); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        source = ArtworkCrop.Load(path); Selection = new CropSelection(source.PixelWidth, source.PixelHeight, initial);
        var stack = Dialogs.Shell(this, "アルバムアートを切り抜く", "正方形をドラッグして位置を選びます。枠の外をクリックしても移動できます。");
        var stageBorder = new Border { Child = Stage, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 14, 0, 12) }; stack.Children.Add(stageBorder);
        fullImage = new Image { Source = source, Stretch = Stretch.Fill, IsHitTestVisible = false }; RenderOptions.SetBitmapScalingMode(fullImage, BitmapScalingMode.HighQuality);
        Stage.Children.Add(fullImage); Stage.Children.Add(shade); Stage.Children.Add(frame);
        foreach (var line in guides) { line.Stroke = Brushes.White; line.Opacity = .35; line.StrokeThickness = 1; line.IsHitTestVisible = false; Stage.Children.Add(line); }
        var controls = new Grid(); controls.ColumnDefinitions.Add(new ColumnDefinition()); controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(114) }); stack.Children.Add(controls);
        var left = new StackPanel { Margin = new Thickness(0, 0, 20, 0) }; controls.Children.Add(left);
        zoomLabel = Dialogs.Note(""); left.Children.Add(zoomLabel);
        ZoomSlider = new Slider { Minimum = 1, Maximum = Selection.MaximumZoom, Value = Selection.Zoom, SmallChange = .05, LargeChange = .25, ToolTip = "大きくすると、より狭い範囲を切り抜きます" }; left.Children.Add(ZoomSlider);
        left.Children.Add(Dialogs.Note("ホイール：拡大・縮小　矢印キー：位置調整"));
        ResetButton = Dialogs.Button("中央・最大の正方形に戻す", () => { Selection.Reset(); ZoomSlider.Value = 1; Draw(); }); left.Children.Add(ResetButton);
        var right = new StackPanel(); Grid.SetColumn(right, 1); controls.Children.Add(right);
        right.Children.Add(Dialogs.Note("仕上がり")); preview = new Image { Width = 96, Height = 96, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) }; right.Children.Add(preview);
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
        dimensions = Dialogs.Note(""); dimensions.Margin = new Thickness(0, 8, 0, 0); stack.Children.Add(dimensions);
        error = Dialogs.Note(""); error.Visibility = Visibility.Collapsed; stack.Children.Add(error);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) }); stack.Children.Add(actions);
        ApplyButton = Dialogs.Button("この範囲で切り抜く", Apply, true); actions.Children.Add(ApplyButton);
        var cancel = Dialogs.Button("キャンセル", Close); cancel.Margin = new Thickness(10, 7, 0, 0); Grid.SetColumn(cancel, 1); actions.Children.Add(cancel);
        Stage.SizeChanged += (_, _) => Draw(); ZoomSlider.ValueChanged += (_, _) => { Selection.SetZoom(ZoomSlider.Value); Draw(); };
        Stage.MouseLeftButtonDown += BeginDrag; Stage.MouseMove += ContinueDrag;
        Stage.MouseLeftButtonUp += (_, e) => { dragging = false; Stage.ReleaseMouseCapture(); e.Handled = true; };
        Stage.LostMouseCapture += (_, _) => dragging = false;
        Stage.MouseWheel += (_, e) => { ZoomSlider.Value *= Math.Pow(1.12, e.Delta / 120d); e.Handled = true; };
        Stage.KeyDown += (_, e) => {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            var delta = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), Key.Down => new Vector(0, step), _ => new Vector() };
            if (delta.LengthSquared > 0) { Selection.MoveTo(Selection.Center + delta); Draw(); e.Handled = true; }
        };
        Loaded += (_, _) => { Draw(); Stage.Focus(); };
    }
    private Point ImagePoint(Point viewPoint) => new((viewPoint.X - offsetX) / scale, (viewPoint.Y - offsetY) / scale);
    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        BeginSelectionDrag(e.GetPosition(Stage)); e.Handled = true;
    }
    internal void BeginSelectionDrag(Point viewPoint)
    {
        if (scale <= 0) return;
        Stage.Focus(); var point = ImagePoint(viewPoint); var bounds = Selection.Bounds;
        var inside = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height).Contains(point);
        grabOffset = inside ? Selection.Center - point : new Vector();
        Selection.MoveTo(point + grabOffset); dragging = Stage.CaptureMouse(); Draw();
    }
    private void ContinueDrag(object sender, MouseEventArgs e)
    {
        ContinueSelectionDrag(e.GetPosition(Stage)); e.Handled = true;
    }
    internal void ContinueSelectionDrag(Point viewPoint)
    {
        if (!dragging || scale <= 0) return;
        MoveSelectionToViewPoint(viewPoint);
    }
    internal void MoveSelectionToViewPoint(Point viewPoint)
    {
        if (scale <= 0) return;
        Selection.MoveTo(ImagePoint(viewPoint) + grabOffset); Draw();
    }
    internal void Draw()
    {
        if (Stage.ActualWidth <= 0 || preview == null) return;
        scale = Math.Min(Stage.ActualWidth / source.PixelWidth, Stage.ActualHeight / source.PixelHeight);
        var width = source.PixelWidth * scale; var height = source.PixelHeight * scale;
        offsetX = (Stage.ActualWidth - width) / 2; offsetY = (Stage.ActualHeight - height) / 2;
        fullImage.Width = width; fullImage.Height = height; Canvas.SetLeft(fullImage, offsetX); Canvas.SetTop(fullImage, offsetY);
        var bounds = Selection.Bounds; var x = offsetX + bounds.X * scale; var y = offsetY + bounds.Y * scale; var side = bounds.Width * scale;
        frame.Width = frame.Height = side; Canvas.SetLeft(frame, x); Canvas.SetTop(frame, y);
        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd }; mask.Children.Add(new RectangleGeometry(new Rect(offsetX, offsetY, width, height))); mask.Children.Add(new RectangleGeometry(new Rect(x, y, side, side))); shade.Data = mask;
        for (var i = 0; i < 2; i++)
        {
            var fraction = (i + 1) / 3d; var horizontal = guides[i]; horizontal.X1 = x; horizontal.X2 = x + side; horizontal.Y1 = horizontal.Y2 = y + side * fraction;
            var vertical = guides[i + 2]; vertical.Y1 = y; vertical.Y2 = y + side; vertical.X1 = vertical.X2 = x + side * fraction;
        }
        preview.Source = ArtworkCrop.Preview(source, bounds); zoomLabel.Text = $"拡大 {Selection.Zoom:F2}×";
        dimensions.Text = $"選択範囲 {bounds.Width} × {bounds.Height} px　·　元画像 {source.PixelWidth} × {source.PixelHeight} px";
    }
    private void Apply()
    {
        try { ResultPath = ArtworkCrop.Save(source, Selection.Bounds); ResultBounds = Selection.Bounds; Close(); }
        catch (Exception ex) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
    }
}
