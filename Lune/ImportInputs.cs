using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Lune;

internal static class ImportInputs
{
    internal static ComboBox Editable(Panel panel, string label)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 6), FontSize = 12 });
        var input = new ComboBox { IsEditable = true, IsTextSearchEnabled = false, StaysOpenOnEdit = true, MinHeight = 40, MaxDropDownHeight = 220 };
        System.Windows.Automation.AutomationProperties.SetName(input, label); panel.Children.Add(input); return input;
    }
    internal static void Suggestions(ComboBox input, IReadOnlyList<string> names)
    {
        if (input.Items.Cast<string>().SequenceEqual(names)) return;
        var text = input.Text;
        // ItemsSource replacement can clear a selected item's text. Keep user input.
        input.ItemsSource = names; input.Text = text;
    }
    internal static void OnTextChanged(ComboBox input, TextChangedEventHandler handler) => input.AddHandler(TextBoxBase.TextChangedEvent, handler);
}
