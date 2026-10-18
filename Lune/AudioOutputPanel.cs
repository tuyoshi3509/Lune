using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Lune;

internal sealed class AudioOutputPanel : StackPanel
{
    internal ComboBox PrimarySelector { get; } = CreateSelector();
    internal ComboBox SecondarySelector { get; } = CreateSelector();
    private readonly MainWindow main;
    private bool refreshing, subscribed;
    internal AudioOutputPanel(MainWindow main)
    {
        this.main = main;
        Children.Add(Dialogs.Note("同じ曲を2つの出力先に同時に送れます。音量は共通、ミュートは個別です。"));
        AddOutput(0, PrimarySelector); AddOutput(1, SecondarySelector);
        Children.Add(Dialogs.Button("デバイス一覧を更新", main.P.RefreshOutputDevices));
        Children.Add(Dialogs.Note("録音デバイスは一覧の下側に表示します。通常のマイクには直接出力できません。仮想録音デバイスの案内に従って接続してください。"));
        Children.Add(Dialogs.Note("出力2は初期値「出力しない」です。出力2や仮想録音の接続が切れた場合、その出力は停止します。出力1の再生デバイスが未接続の場合はWindowsの既定に切り替えます。\n同じ送信先を2つ選んだ場合は音を重ねず、両方をミュートすると消音します。デバイスごとに音の遅延が異なる場合があります。"));
        main.P.RefreshOutputDevices(); Refresh();
        PrimarySelector.SelectionChanged += (_, _) => Select(0, PrimarySelector);
        SecondarySelector.SelectionChanged += (_, _) => Select(1, SecondarySelector);
        Loaded += (_, _) => { if (!subscribed) { main.P.DevicesChanged += Refresh; subscribed = true; } Refresh(); };
        Unloaded += (_, _) => { if (subscribed) { main.P.DevicesChanged -= Refresh; subscribed = false; } };
    }
    private void AddOutput(int index, ComboBox selector)
    {
        var row = new Grid { Margin = new Thickness(0, 18, 0, 6) };
        row.Children.Add(new TextBlock { Text = $"出力 {index + 1}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 6, 12, 6) };
        button.SetBinding(ContentControl.ContentProperty, new Binding(index == 0 ? "PrimaryOutputButtonLabel" : "SecondaryOutputButtonLabel") { Source = main.P });
        button.SetBinding(ToolTipProperty, new Binding(index == 0 ? "PrimaryOutputHint" : "SecondaryOutputHint") { Source = main.P });
        if (index == 1) button.SetBinding(IsEnabledProperty, new Binding("SecondaryOutputEnabled") { Source = main.P });
        button.Click += (_, _) => { if (index == 0) main.P.PrimaryOutputMuted = !main.P.PrimaryOutputMuted; else main.P.SecondaryOutputMuted = !main.P.SecondaryOutputMuted; main.SaveSettings(); };
        row.Children.Add(button); Children.Add(row); Children.Add(selector);
        var current = Dialogs.Note(""); current.Margin = new Thickness(0, 5, 0, 0);
        current.SetBinding(TextBlock.TextProperty, new Binding(index == 0 ? "OutputDeviceLabel" : "SecondaryOutputDeviceLabel") { Source = main.P, StringFormat = "選択中：{0}" }); Children.Add(current);
        var help = Dialogs.Note(""); help.Margin = new Thickness(0, 5, 0, 0);
        help.SetBinding(TextBlock.TextProperty, new Binding(index == 0 ? "OutputDeviceNote" : "SecondaryOutputDeviceNote") { Source = main.P }); Children.Add(help);
    }
    private static ComboBox CreateSelector()
    {
        var combo = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id", MaxDropDownHeight = 340 };
        var style = new Style(typeof(ComboBoxItem), (Style)Application.Current.FindResource(typeof(ComboBoxItem)));
        style.Setters.Add(new Setter(IsEnabledProperty, new Binding("CanSelect")));
        style.Setters.Add(new Setter(ToolTipProperty, new Binding("Note")));
        style.Setters.Add(new Setter(ToolTipService.ShowOnDisabledProperty, true));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .4)); style.Triggers.Add(disabled);
        combo.ItemContainerStyle = style;
        var header = new FrameworkElementFactory(typeof(TextBlock));
        header.SetBinding(TextBlock.TextProperty, new Binding("Name")); header.SetValue(TextBlock.FontSizeProperty, 11d);
        header.SetValue(MarginProperty, new Thickness(10, 12, 0, 5)); header.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        combo.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = header } });
        return combo;
    }
    private void Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            void Fill(ComboBox selector, IReadOnlyList<OutputDevice> devices, string id)
            {
                var view = new ListCollectionView(devices.ToList()); view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
                selector.ItemsSource = view; selector.SelectedValue = id;
            }
            Fill(PrimarySelector, main.P.OutputDevices, main.P.OutputDeviceId);
            Fill(SecondarySelector, main.P.SecondaryOutputDevices, main.P.SecondaryOutputDeviceId);
        }
        finally { refreshing = false; }
    }
    private void Select(int index, ComboBox selector)
    {
        if (refreshing || selector.SelectedItem is not OutputDevice device) return;
        if (!device.CanSelect) { Refresh(); return; }
        if (index == 0) main.P.OutputDeviceId = device.Id; else main.P.SecondaryOutputDeviceId = device.Id;
        main.SaveSettings();
    }
}
