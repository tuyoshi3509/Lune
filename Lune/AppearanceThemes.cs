using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Lune;
public sealed record AppearanceTheme(string Id, string Name, string Bg, string Sidebar, string Surface, string Hover, string Line, string Accent, string Text = "#F0F1EA", string Muted = "#A5AFB4");
public static class AppearanceThemes
{
    public static AppearanceTheme[] All { get; } = {
        new("Charcoal", "チャコール", "#111619", "#0D1114", "#1B2227", "#293339", "#2B3439", "#CBEB9B"),
        new("Midnight", "ミッドナイト", "#111522", "#0C101C", "#1B2235", "#28334A", "#2C3549", "#99CFFF"),
        new("Warm", "ウォーム", "#1B1816", "#141210", "#29231F", "#39302A", "#39312B", "#F0B9A2"),
        new("Graphite", "グラファイト", "#1B1C1F", "#121316", "#27282C", "#36383D", "#414349", "#E1E4EC"),
        new("Forest", "フォレスト", "#102019", "#0A1711", "#1A2F23", "#284536", "#365243", "#A7DDB1"),
        new("Ocean", "オーシャン", "#0E2028", "#08171F", "#18313D", "#244554", "#345764", "#8BD9E5"),
        new("Aubergine", "パープル", "#21162C", "#170F21", "#30213E", "#443153", "#543C66", "#D8B4FA"),
        new("Burgundy", "バーガンディ", "#29151C", "#1D0E14", "#3B202B", "#51303B", "#633E4B", "#F1B0C4"),
        new("Rose", "ダスティローズ", "#282022", "#1C1518", "#392D31", "#4B3D42", "#5A474F", "#EDC5CD"),
        new("Olive", "オリーブ", "#202218", "#15170F", "#303323", "#424632", "#52583D", "#D4DA9A"),
        new("Slate", "スレート", "#1C252E", "#141B23", "#2A3642", "#394959", "#45586B", "#C4D8E9"),
        new("Coffee", "コーヒー", "#251B15", "#1A120D", "#392A20", "#4D3A2C", "#614936", "#E4C096"),
        new("Nord", "ノルド", "#2E3440", "#242933", "#3B4252", "#434C5E", "#596579", "#88C0D0", "#ECEFF4", "#C1C9D5"),
        new("Cobalt", "コバルト", "#101C34", "#0A1327", "#1B2D4D", "#2B4267", "#39547D", "#A5C9FF"),
        new("Monochrome", "モノクロ", "#141414", "#090909", "#242424", "#353535", "#454545", "#E6E6E6"),
        new("Paper", "ペーパー", "#F6F5EF", "#E9E8DE", "#FFFFFF", "#E1E5D8", "#C9CFC0", "#466B28", "#252B21", "#5D6857"),
        new("Snow", "スノー", "#F3F7FC", "#E4ECF5", "#FFFFFF", "#DCE8F4", "#C1CEDF", "#2166B5", "#1D2C40", "#53667C"),
        new("Sakura", "さくら", "#FFF5F7", "#F4E4EA", "#FFFFFF", "#F3DAE5", "#DFC0CE", "#963E69", "#412C36", "#765464"),
        new("Sage", "セージ", "#F2F7F0", "#E2ECE0", "#FFFFFF", "#D8E7D6", "#BACDB8", "#3C7453", "#203328", "#526C59"),
        new("Sand", "サンド", "#FBF5E9", "#EFE3CD", "#FFFCF5", "#EEE0C5", "#D9C7A6", "#995318", "#342B1E", "#736046")
    };
    public static AppearanceTheme Find(string id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
    public static void Apply(Settings preferences)
    {
        var theme = Find(preferences.Theme); var resources = Application.Current.Resources;
        var keys = new[] { "Bg", "Sidebar", "Surface", "Hover", "Line", "Text", "Muted" };
        var values = new[] { theme.Bg, theme.Sidebar, theme.Surface, theme.Hover, theme.Line, theme.Text, theme.Muted };
        for (var i = 0; i < keys.Length; i++) resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(preferences.Accent); }
        catch { preferences.Accent = theme.Accent; accent = (Color)ColorConverter.ConvertFromString(theme.Accent); }
        resources["Accent"] = new SolidColorBrush(accent);
        double Linear(byte c) { var s = c / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        var luminance = .2126 * Linear(accent.R) + .7152 * Linear(accent.G) + .0722 * Linear(accent.B);
        resources["OnAccent"] = luminance > .179 ? Brushes.Black : Brushes.White;
    }
}
