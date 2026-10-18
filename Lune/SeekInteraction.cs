using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace Lune;

internal sealed class SeekInteraction
{
    private readonly Slider slider;
    private readonly PlayerService player;
    private readonly Action saved;
    private bool active;
    private Track? track;
    private SeekInteraction(Slider slider, PlayerService player, Action saved)
    {
        this.slider = slider; this.player = player; this.saved = saved;
        // Slider's class handler otherwise consumes track clicks before this
        // instance handler runs, moving only the thumb without seeking audio.
        slider.IsMoveToPointEnabled = false;
        slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) => { Begin(e.GetPosition(slider)); e.Handled = true; }), true);
        slider.PreviewMouseMove += (_, e) => { if (active) { Move(e.GetPosition(slider)); e.Handled = true; } };
        slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, e) => { if (active) { Move(e.GetPosition(slider)); Finish(); e.Handled = true; } }), true);
        slider.LostMouseCapture += (_, _) => Finish();
        slider.Unloaded += (_, _) => Finish(false);
        slider.PreviewKeyDown += (_, e) => {
            if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
            player.Seek(e.Key == Key.Home ? 0 : e.Key == Key.End ? player.Duration : player.Position + (e.Key == Key.Left ? -5 : 5));
            saved(); e.Handled = true;
        };
    }
    internal static SeekInteraction Attach(Slider slider, PlayerService player, Action saved) => new(slider, player, saved);
    internal void Begin(Point point)
    {
        if (player.Current == null || player.Duration <= 0) return;
        slider.Focus(); track = player.Current;
        var value = slider.Value;
        BindingOperations.ClearBinding(slider, Slider.ValueProperty); slider.Value = value;
        active = true; slider.CaptureMouse(); Move(point);
    }
    internal void Move(Point point)
    {
        if (!active) return;
        slider.ApplyTemplate();
        if (slider.Template.FindName("PART_Track", slider) is System.Windows.Controls.Primitives.Track rail)
        {
            // Derive an absolute position: ValueFromPoint uses the thumb's last
            // arranged position, which can lag behind playback/binding updates.
            var thumbWidth = rail.Thumb?.ActualWidth ?? 0;
            var width = rail.ActualWidth - thumbWidth;
            if (width <= 0) return;
            var fraction = Math.Clamp((slider.TranslatePoint(point, rail).X - thumbWidth / 2) / width, 0, 1);
            if (rail.IsDirectionReversed) fraction = 1 - fraction;
            slider.Value = slider.Minimum + fraction * (slider.Maximum - slider.Minimum);
        }
    }
    internal void Finish(bool commit = true)
    {
        if (!active) return;
        active = false;
        var value = slider.Value;
        if (slider.IsMouseCaptured) slider.ReleaseMouseCapture();
        try { if (commit && ReferenceEquals(track, player.Current)) { player.Seek(value); saved(); } }
        finally { slider.SetBinding(Slider.ValueProperty, new Binding("P.Position") { Mode = BindingMode.OneWay }); }
    }
}
