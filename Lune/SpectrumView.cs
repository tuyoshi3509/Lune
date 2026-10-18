using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lune;

public sealed class SpectrumView : FrameworkElement
{
    public static readonly DependencyProperty PlayerProperty = DependencyProperty.Register(nameof(Player), typeof(PlayerService), typeof(SpectrumView), new PropertyMetadata(null, Changed));
    public PlayerService? Player { get => (PlayerService?)GetValue(PlayerProperty); set => SetValue(PlayerProperty, value); }
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(67) };
    private readonly float[] displayed = new float[SpectrumAnalyzer.BandCount], snapshot = new float[SpectrumAnalyzer.BandCount];
    private Window? owner;
    internal bool IsAnimating => timer.IsEnabled;
    public SpectrumView()
    {
        IsHitTestVisible = false;
        timer.Tick += (_, _) => { Player?.Spectrum.CopyLevels(snapshot); for (var i = 0; i < displayed.Length; i++) displayed[i] = Math.Max(snapshot[i], displayed[i] * .78f); InvalidateVisual(); };
        Loaded += (_, _) => { owner = Window.GetWindow(this); if (owner != null) owner.StateChanged += OwnerChanged; Update(); };
        Unloaded += (_, _) => { if (owner != null) owner.StateChanged -= OwnerChanged; owner = null; timer.Stop(); Player?.SetSpectrumVisible(this, false); };
        IsVisibleChanged += (_, _) => Update();
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SpectrumView)d;
        if (e.OldValue is PlayerService old) { old.PropertyChanged -= view.PlayerChanged; old.SetSpectrumVisible(view, false); }
        if (e.NewValue is PlayerService current) current.PropertyChanged += view.PlayerChanged;
        view.Update();
    }
    private void PlayerChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(PlayerService.IsPlaying) or nameof(PlayerService.SpectrumEnabled)) Update(); }
    private void OwnerChanged(object? sender, EventArgs e) => Update();
    private void Update()
    {
        var active = IsLoaded && IsVisible && owner?.WindowState != WindowState.Minimized && Player?.SpectrumEnabled == true && Player.IsPlaying;
        Player?.SetSpectrumVisible(this, active);
        if (active) timer.Start(); else { timer.Stop(); Array.Clear(displayed); InvalidateVisual(); }
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (!timer.IsEnabled || ActualWidth <= 0) return;
        var brush = TryFindResource("Accent") as Brush ?? Brushes.Gray; var slot = ActualWidth / displayed.Length;
        for (var i = 0; i < displayed.Length; i++)
        {
            var height = displayed[i] * ActualHeight * .92; if (height < 1) continue;
            dc.DrawRoundedRectangle(brush, null, new Rect(i * slot + 1, ActualHeight - height, Math.Max(1, slot - 3), height), 2, 2);
        }
    }
}
