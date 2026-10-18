using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Lune;

public sealed class DiscArtwork : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(DiscArtwork), new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(DiscArtwork), new PropertyMetadata(false, PlaybackChanged));
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    private readonly ImageBrush artwork = new() { Stretch = Stretch.UniformToFill };
    private readonly RotateTransform rotation = new();
    private Window? owner;
    internal bool IsSpinning { get; private set; }
    internal double Angle => rotation.Angle;
    public DiscArtwork()
    {
        Focusable = false; IsHitTestVisible = false;
        var disc = new Grid { Width = 100, Height = 100, RenderTransformOrigin = new Point(.5, .5), RenderTransform = rotation };
        disc.Children.Add(new Path { Data = Ring(48, 6), Fill = artwork });
        var silver = new LinearGradientBrush(new GradientStopCollection {
            new(Colors.White, 0), new(Color.FromRgb(110, 123, 139), .3), new(Colors.White, .55), new(Color.FromRgb(91, 106, 124), .8), new(Colors.White, 1) }, 35);
        silver.Freeze();
        disc.Children.Add(new Ellipse { Width = 96, Height = 96, Stroke = silver, StrokeThickness = 1.7 });
        disc.Children.Add(new Path { Data = Ring(13, 6), Fill = silver, Opacity = .85 });
        disc.Children.Add(new Ellipse { Width = 27, Height = 27, Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), StrokeThickness = .7 });
        disc.Children.Add(new Ellipse { Width = 12, Height = 12, Stroke = new SolidColorBrush(Color.FromArgb(170, 25, 30, 35)), StrokeThickness = .8 });
        Content = new Viewbox { Child = disc, Stretch = Stretch.Uniform };
        Loaded += (_, _) => { owner = Window.GetWindow(this); if (owner != null) owner.StateChanged += OwnerChanged; Update(); };
        Unloaded += (_, _) => { if (owner != null) owner.StateChanged -= OwnerChanged; owner = null; Stop(); };
        IsVisibleChanged += (_, _) => Update();
    }
    private static Geometry Ring(double outside, double inside)
    {
        var ring = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(50, 50), outside, outside), new EllipseGeometry(new Point(50, 50), inside, inside));
        ring.Freeze(); return ring;
    }
    private static void SourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var disc = (DiscArtwork)d; disc.artwork.ImageSource = (ImageSource?)e.NewValue;
        disc.Stop(); disc.rotation.Angle = 0; disc.Update();
    }
    private static void PlaybackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DiscArtwork)d).Update();
    private void OwnerChanged(object? sender, EventArgs e) => Update();
    private void Stop()
    {
        if (!IsSpinning) return;
        var angle = rotation.Angle % 360;
        rotation.BeginAnimation(RotateTransform.AngleProperty, null); rotation.Angle = angle; IsSpinning = false;
    }
    private void Update()
    {
        if (!IsLoaded || !IsVisible || owner?.WindowState == WindowState.Minimized || !IsPlaying || Source == null) { Stop(); return; }
        if (IsSpinning) return;
        var animation = new DoubleAnimation(rotation.Angle, rotation.Angle + 360, TimeSpan.FromSeconds(20)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(animation, 20);
        rotation.BeginAnimation(RotateTransform.AngleProperty, animation); IsSpinning = true;
    }
}
