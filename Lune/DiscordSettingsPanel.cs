using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Lune;

internal sealed class DiscordSettingsPanel : StackPanel
{
    internal TextBox ApplicationIdInput { get; }
    internal CheckBox EnabledCheck { get; }
    internal Button ApplyButton { get; }
    internal DiscordSettingsPanel(MainWindow main)
    {
        var preferences = main.Preferences;
        Children.Add(Dialogs.Heading("Discord連携"));
        Children.Add(Dialogs.Note("再生中の「♪ アーティスト名」と曲名・再生時間をプロフィールに表示します。オンにすると、この情報がDiscordの共有設定に従って他の人に見えます。"));
        ApplicationIdInput = Dialogs.Field(this, "Application ID", preferences.DiscordApplicationId);
        ApplicationIdInput.MaxLength = 64;
        var error = Dialogs.Note(""); error.Visibility = Visibility.Collapsed; Children.Add(error);
        bool SaveId()
        {
            var id = ApplicationIdInput.Text.Trim();
            if (id.Length > 0 && !DiscordPresence.ValidId(id))
            {
                error.Text = "数字だけのApplication IDを入力してください。Client Secretやトークンは不要です。";
                error.Visibility = Visibility.Visible; return false;
            }
            error.Visibility = Visibility.Collapsed; preferences.DiscordApplicationId = id;
            main.Discord.Configure(preferences.DiscordEnabled, id); main.SaveSettings(); return true;
        }
        ApplicationIdInput.LostKeyboardFocus += (_, _) => SaveId();
        ApplicationIdInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveId(); e.Handled = true; } };
        EnabledCheck = Dialogs.Check(this, "Discordに再生中の曲を表示する", preferences.DiscordEnabled, enabled =>
        {
            preferences.DiscordEnabled = enabled;
            // Turning sharing off always takes effect, including while an ID edit is invalid.
            if (!enabled) { main.Discord.Configure(false, preferences.DiscordApplicationId); main.SaveSettings(); }
            else if (!SaveId()) { preferences.DiscordEnabled = false; if (EnabledCheck != null) EnabledCheck.IsChecked = false; }
        });
        ApplyButton = Dialogs.Button("IDを保存して接続設定を適用", () => SaveId()); Children.Add(ApplyButton);
        var status = Dialogs.Note(""); status.Margin = new Thickness(0, 8, 0, 8);
        status.SetBinding(TextBlock.TextProperty, new Binding("Status") { Source = main.Discord }); Children.Add(status);
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), FontSize = 13 };
        preview.SetBinding(TextBlock.TextProperty, new Binding("Preview") { Source = main.Discord });
        var border = new Border { CornerRadius = new CornerRadius(8), Child = preview }; border.SetResourceReference(Border.BackgroundProperty, "Surface");
        Children.Add(Dialogs.Note("送信内容のプレビュー")); Children.Add(border);
        Children.Add(Dialogs.Heading("初回の設定"));
        Children.Add(Dialogs.Note("1. 下のボタンからDiscord Developer Portalを開き、New Applicationで「Lune」を作成します。\n2. General InformationのAPPLICATION IDをコピーします。\n3. 上の欄に貼り付けて保存し、「Discordに再生中の曲を表示する」をオンにします。\n4. Discordデスクトップ版を起動し、ユーザー設定 → アクティビティのプライバシーでアクティビティの共有をオンにします。"));
        Children.Add(Dialogs.Button("Discordのアプリ作成ページを開く", () => Process.Start(new ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true })));
        Children.Add(Dialogs.Note("一時停止・停止・Luneの終了時は表示を消します。Discordを後から起動した場合も自動で再接続します。曲の変更は通常5秒以内を目安に反映します（Discord側の反映に時間がかかる場合があります）。\n\nDiscordのブラウザー版だけでは連携できません。Spotify専用の「一緒に聴く」やPC内のアルバムアートの共有には対応しません。実際の表示位置や見た目はDiscord側の仕様によって変わります。"));
    }
}
