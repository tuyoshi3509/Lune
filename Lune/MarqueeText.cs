using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Lune;

public sealed class MarqueeText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarqueeText), new FrameworkPropertyMetadata("", Changed));
    public static readonly DependencyProperty OnHoverProperty = DependencyProperty.Register(nameof(OnHover), typeof(bool), typeof(MarqueeText), new PropertyMetadata(false, Changed));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool OnHover { get => (bool)GetValue(OnHoverProperty); set => SetValue(OnHoverProperty, value); }
    private readonly TextBlock label = new() { TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TranslateTransform translate = new();
    private readonly MarqueeViewport viewport;
    private Window? owner;
    private bool updatePending;
    internal AnimationClock? ScrollClock { get; private set; }
    internal bool IsScrolling { get; private set; }
    public MarqueeText()
    {
        ClipToBounds = true; Focusable = false;
        viewport = new MarqueeViewport { ClipToBounds = true, Child = label }; label.RenderTransform = translate; Content = viewport;
        Loaded += (_, _) => { owner = Window.GetWindow(this); if (owner != null) owner.StateChanged += OwnerChanged; Update(); };
        Unloaded += (_, _) => { if (owner != null) owner.StateChanged -= OwnerChanged; owner = null; Stop(); };
        SizeChanged += (_, _) => Update(); IsVisibleChanged += (_, _) => Update();
        MouseEnter += (_, _) => Update(); MouseLeave += (_, _) => Update();
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MarqueeText)d).Update();
    private void OwnerChanged(object? sender, EventArgs e) => Update();
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontFamilyProperty || e.Property == FontSizeProperty || e.Property == FontWeightProperty || e.Property == FontStyleProperty || e.Property == FontStretchProperty || e.Property == FlowDirectionProperty) ScheduleUpdate();
    }
    private void ScheduleUpdate()
    {
        if (!IsLoaded || updatePending) return;
        updatePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { updatePending = false; Update(); }));
    }
    private void Stop() { ScrollClock?.Controller?.Remove(); ScrollClock = null; translate.BeginAnimation(TranslateTransform.XProperty, null); translate.X = 0; IsScrolling = false; }
    private void Update()
    {
        Stop(); label.Text = Text ?? ""; ToolTip = Text;
        viewport.ExpandText = false;
        label.HorizontalAlignment = HorizontalAlignment.Left; label.TextTrimming = TextTrimming.CharacterEllipsis;
        if (!IsLoaded || !IsVisible || owner?.WindowState == WindowState.Minimized || ActualWidth <= 0 || (OnHover && !IsMouseOver)) return;
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var overflow = Math.Ceiling(label.DesiredSize.Width) + 1 - ActualWidth; if (overflow <= 2) return;
        label.TextTrimming = TextTrimming.None; viewport.ExpandText = true;
        var travel = Math.Max(2, overflow / 22); var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2 + travel))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4 + travel))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4 + 2 * travel))));
        Timeline.SetDesiredFrameRate(animation, 20); ScrollClock = (AnimationClock)animation.CreateClock(true); translate.ApplyAnimationClock(TranslateTransform.XProperty, ScrollClock); IsScrolling = true;
    }

    // Give scrolling text its full layout slot. A normal Grid constrains the
    // TextBlock and moves its layout clip with it, permanently hiding the tail.
    // Only this stationary viewport should clip the moving text.
    private sealed class MarqueeViewport : Decorator
    {
        private bool expandText;
        internal bool ExpandText { get => expandText; set { if (expandText == value) return; expandText = value; InvalidateMeasure(); } }
        protected override Size MeasureOverride(Size available)
        {
            if (Child == null) return new Size();
            Child.Measure(new Size(expandText ? double.PositiveInfinity : available.Width, available.Height));
            return new Size(Math.Min(available.Width, Child.DesiredSize.Width), Child.DesiredSize.Height);
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Child != null) Child.Arrange(new Rect(0, 0, expandText ? Math.Max(finalSize.Width, Math.Ceiling(Child.DesiredSize.Width) + 1) : finalSize.Width, finalSize.Height));
            return finalSize;
        }
    }
}
