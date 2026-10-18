using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lune;

internal static class PlaylistImportChecks
{
    internal static async Task Run(MainWindow main)
    {
        var results = new List<string>(); var output = Path.Combine(Paths.Root, "test-output", "playlist-import-tests.txt");
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("FAIL: " + name); results.Add("PASS: " + name); }
        try
        {
            main.Library.Tracks.Clear(); main.Library.Playlists.Clear(); main.Library.ExtraFiles.Clear(); main.Preferences.Folders.Clear();
            const string url = "https://www.youtube.com/playlist?list=PL_fixture";
            const string json = """
            {"title":"週末のマイリスト","entries":[
              {"id":"aaaaaaaaaaa","title":"以前取り込んだ曲","uploader":"配信元","duration":123},
              {"id":"bbbbbbbbbbb","title":"新しく取り込む曲 1","uploader":"配信元","duration":null},
              {"id":"ccccccccccc","title":"新しく取り込む曲 2","channel":"配信元"},
              {"id":"ddddddddddd","title":"新しく取り込む曲 3","uploader":"配信元"},
              {"id":"bbbbbbbbbbb","title":"重複する曲","uploader":"配信元"},
              {"id":"eeeeeeeeeee","title":"[Private video]","availability":"private"},null]}
            """;
            var parsed = YouTubePlaylist.Parse(json, url);
            Check(parsed.Items.Count == 7 && parsed.Items[2].Index == 3 && parsed.Items[2].Info.Artist == "配信元", "playlist parsing preserves order and channel artist fallback");
            Check(parsed.Items.Count(i => i.CanSelect) == 4 && parsed.Items[1].Info.Seconds == 0, "private, duplicate and missing entries are skipped and null duration is accepted");
            Check(!YouTubePlaylist.Parse("{\"entries\":[{\"id\":\"aaaaaaaaaaa\",\"title\":\"Live\",\"live_status\":\"is_live\"}]}", url).Items[0].CanSelect, "ongoing streams cannot hold up the batch indefinitely");
            Check(YouTubePlaylist.PlaylistUrl("https://youtu.be/aaaaaaaaaaa?list=PL_fixture&index=3") == url, "video-with-playlist URL resolves to whole playlist");
            Check(YouTubePlaylist.VideoId("https://youtu.be/aaaaaaaaaaa?t=2") == "aaaaaaaaaaa" && YouTubePlaylist.VideoId("https://music.youtube.com/watch?v=aaaaaaaaaaa&list=PL_fixture") == "aaaaaaaaaaa", "equivalent source URLs use same video identity");
            Check(YouTubePlaylist.PlaylistId("https://youtube.com.evil.test/playlist?list=PL_fake").Length == 0, "non-YouTube playlist URL is rejected");
            bool emptyRejected = false; try { YouTubePlaylist.Parse("{\"entries\":[]}", url); } catch (InvalidDataException) { emptyRejected = true; } Check(emptyRejected, "empty playlist has actionable error");
            var fixtures = Path.Combine(Paths.Data, "fixtures"); Directory.CreateDirectory(fixtures);
            var oldPath = Path.Combine(fixtures, "existing.mp3"); File.WriteAllText(oldPath, "test fixture");
            main.Library.Tracks.Add(new Track { Id = "existing", Path = oldPath, Title = "以前取り込んだ曲", Artist = "以前変更した名前", Album = "既存アルバム", ImportedFromYouTube = true, SourceUrl = "https://youtu.be/aaaaaaaaaaa?si=test" });
            Check(main.ImportArtists.Suggestions(main.Library.Tracks, "").Contains("以前変更した名前"), "legacy imported artists appear without new history metadata");
            main.ImportArtists.Remember("配信元", "修正アーティスト"); main.ImportArtists.Remember("別の配信元", "別の修正名");
            Check(new ImportArtistHistory().Suggestions(main.Library.Tracks, "配信元").First() == "修正アーティスト", "artist corrections persist and matching source is ranked first");
            var single = new ImportWindow(main); single.Show(); await Task.Delay(90);
            single.ApplyMetadata(new VideoInfo("https://www.youtube.com/watch?v=bbbbbbbbbbb", "取得した曲", "配信元", "取得したアルバム", "", 10));
            Check(single.ArtistInput.Items.Contains("修正アーティスト") && single.ArtistInput.Text == "配信元", "single import offers corrected names while preserving fetched artist");
            single.ArtistInput.SelectedItem = "以前変更した名前";
            Check(single.ArtistInput.Text == "以前変更した名前" && single.AlbumInput.Items.Contains("既存アルバム"), "artist selection updates dependent album suggestions");
            var singleEditor = (TextBox)single.ArtistInput.Template.FindName("PART_EditableTextBox", single.ArtistInput); singleEditor.Text = "自由に入力した名前";
            single.ArtistInput.IsDropDownOpen = true; await Task.Delay(60); single.ArtistInput.IsDropDownOpen = false;
            Check(single.ArtistInput.Text == "自由に入力した名前", "artist dropdown does not overwrite manual text");
            single.ArtistInput.BringIntoView(); SelfTests.Capture(single, "lune-import-artist-history.png"); single.Close();

            var window = new PlaylistImportWindow(main, url); window.Show(); window.SetPlaylist(parsed); await Task.Delay(100);
            Check(!parsed.Items[0].CanSelect && !parsed.Items[0].Selected && parsed.Items[0].ExistingTrackId == "existing", "registered video is skipped even when stored URL uses short form");
            window.SkipExisting.IsChecked = false; Check(parsed.Items[0].CanSelect && !parsed.Items[0].Selected, "reimport can be explicitly enabled without automatically selecting duplicates"); window.SkipExisting.IsChecked = true;
            window.TrackList.SelectedItem = parsed.Items[1]; window.ArtistInput.SelectedItem = "以前変更した名前";
            Check(parsed.Items[1].Artist == "以前変更した名前" && window.AlbumInput.Items.Contains("既存アルバム"), "batch artist selector updates row and album candidates");
            window.ApplyArtistToSelected("修正アーティスト"); window.ApplyAlbumToSelected("週末アルバム"); window.TitleInput.Text = "手入力の曲名";
            Check(parsed.Items.Skip(1).Take(3).All(i => i.Artist == "修正アーティスト" && i.Album == "週末アルバム") && parsed.Items[0].Artist == "配信元", "batch edits apply only to checked importable rows");
            parsed.Items[1].ApplyMetadata(new VideoInfo(parsed.Items[1].Info.Url, "自動取得の曲名", "配信元", "自動アルバム", "", 20));
            Check(parsed.Items[1].Title == "手入力の曲名" && parsed.Items[1].Artist == "修正アーティスト" && parsed.Items[1].Album == "週末アルバム", "later detailed metadata preserves all manual overrides");
            var auto = new PlaylistImportItem(1, new VideoInfo("", "flat title", "channel", "", "", 0)); auto.ApplyMetadata(new VideoInfo("", "track title", "actual artist", "actual album", "", 30));
            Check(auto.Title == "track title" && auto.Artist == "actual artist" && auto.Album == "actual album", "unmodified rows adopt full song metadata");
            var bitmap = new RenderTargetBitmap(240, 120, 96, 96, PixelFormats.Pbgra32); var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) { drawing.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 120, 120)); drawing.DrawRectangle(Brushes.Coral, null, new Rect(120, 0, 120, 120)); } bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var bytes = new MemoryStream(); encoder.Save(bytes);
            PlaylistImportWindow.SetArtwork(parsed.Items[1], ArtworkCrop.CacheSource(bytes.ToArray()));
            Check(ArtworkCrop.Load(parsed.Items[1].Artwork).PixelWidth == ArtworkCrop.Load(parsed.Items[1].Artwork).PixelHeight && File.Exists(parsed.Items[1].OriginalArtwork), "batch artwork keeps original and prepares square cover");
            window.TrackList.SelectedItem = parsed.Items[2]; window.TrackList.SelectedItem = parsed.Items[1]; await Task.Delay(100);
            SelfTests.Capture(window, "lune-playlist-import.png");
            window.ArtistInput.BringIntoView(); window.ArtistInput.IsDropDownOpen = true; await Task.Delay(70);
            SelfTests.Capture((FrameworkElement)((Popup)window.ArtistInput.Template.FindName("PART_Popup", window.ArtistInput)).Child, "lune-artist-history-dropdown.png"); window.ArtistInput.IsDropDownOpen = false;

            var template = Path.Combine(fixtures, "silence.m4a");
            await DownloadService.Run(Path.Combine(Paths.Tools, "ffmpeg.exe"), new[] { "-hide_banner", "-y", "-f", "lavfi", "-i", "anullsrc=r=22050:cl=mono", "-t", "0.3", template }, null, CancellationToken.None);
            async Task<Track> SaveFixture(PlaylistImportItem item, CancellationToken ct) {
                ct.ThrowIfCancellationRequested(); var path = Path.Combine(fixtures, "saved-" + item.Index + ".m4a"); File.Copy(template, path, true);
                DownloadService.WriteImportTags(path, item.Title, item.Artist, item.Album, item.Artwork, item.Info.Url); var track = await main.RegisterDownload(path); main.ImportArtists.Remember(item.Info.Artist, item.Artist); return track;
            }
            using var cancel = new CancellationTokenSource(); var order = new List<int>();
            var first = await BatchImportRunner.RunAsync(parsed.Items, async (item, ct) => {
                order.Add(item.Index); if (item.Index == 3) throw new IOException("test failure"); if (item.Index == 4) { cancel.Cancel(); ct.ThrowIfCancellationRequested(); } return await SaveFixture(item, ct);
            }, _ => window.SavePlaylist(), cancel.Token);
            Check(first.Imported == 1 && first.Failed == 1 && first.Cancelled && order.SequenceEqual(new[] { 2, 3, 4 }), "batch processes sequentially, continues after failure and stops on cancel");
            Check(!parsed.Items[1].Selected && !parsed.Items[1].CanSelect && parsed.Items[2].Selected && parsed.Items[3].Selected, "saved rows are excluded from retry; failed and pending rows remain selected");
            var local = main.Library.Playlists.Single(); Check(local.TrackIds.SequenceEqual(new[] { "existing", parsed.Items[1].ImportedTrackId }), "partial import persists playlist including existing tracks in source order");
            var second = await BatchImportRunner.RunAsync(parsed.Items, SaveFixture, _ => window.SavePlaylist(), CancellationToken.None);
            Check(second.Imported == 2 && second.Failed == 0 && !second.Cancelled && main.Library.Tracks.Count == 4, "retry imports only remaining songs without duplicate library tracks");
            Check(local.TrackIds.SequenceEqual(new[] { "existing" }.Concat(parsed.Items.Skip(1).Take(3).Select(i => i.ImportedTrackId))), "completed playlist retains original order across retries");
            var tagged = main.Library.Tracks.First(t => t.Id == parsed.Items[1].ImportedTrackId);
            Check(tagged.Title == "手入力の曲名" && tagged.Artist == "修正アーティスト" && tagged.Album == "週末アルバム" && tagged.HasSourceUrl && tagged.Artwork.Length > 0 && tagged.IsYouTube, "saved music retains edited names, original URL, cover art and YouTube classification");
            window.RefreshExisting(); window.SavePlaylist(); Check(main.Library.Playlists.Count == 1 && main.Library.Playlists[0].TrackIds.Count == 4, "repeated playlist registration does not duplicate playlist or entries");
            var many = Enumerable.Range(1, 600).Select(i => new PlaylistImportItem(i, new VideoInfo("", "曲 " + i, "アーティスト", "アルバム", "", 30))).ToList(); window.SetPlaylist(new YouTubePlaylistInfo("PL_large", "大きな再生リスト", many)); window.TrackList.BringIntoView(); window.UpdateLayout();
            Check(Visuals<DataGridRow>(window.TrackList).Count() < 30, "large playlist virtualizes rows to limit memory and rendering");
            window.SelectAll(false); Check(many.All(i => !i.Selected), "bulk deselection updates entire list"); window.SelectAll(true); Check(many.All(i => i.Selected), "bulk selection includes off-screen rows");
            window.TrackList.ScrollIntoView(many[^1]); window.UpdateLayout(); await Task.Delay(90);
            Check(window.TrackList.ItemContainerGenerator.ContainerFromIndex(599) != null, "last row is reachable by scrolling without pagination");
            window.SetPlaylist(parsed); main.Preferences.Theme = "Snow"; main.Preferences.Accent = AppearanceThemes.Find("Snow").Accent; main.ApplyAppearance(); await Task.Delay(70); SelfTests.Capture(window, "lune-playlist-import-light.png"); window.Close();

            if (Environment.GetCommandLineArgs().Contains("--playlist-network")) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
                var live = await DownloadService.FetchPlaylistAsync("https://www.youtube.com/playlist?list=PLt5yu3-wZAlSLRHmI1qNm0wjyVNWw1pCU", timeout.Token);
                Check(live.Items.Count > 0 && live.Items.Any(i => YouTubePlaylist.VideoId(i.Info.Url).Length == 11), "real YouTube playlist metadata loads through installed yt-dlp");
                results.Add("Live playlist: " + live.Title + "; entries: " + live.Items.Count);
            }
            File.WriteAllLines(output, results); main.ExitApp();
        }
        catch (Exception ex) { results.Add(ex.ToString()); File.WriteAllLines(output, results); Application.Current.Shutdown(1); }
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Visuals<T>(child)) yield return nested; }
    }
}
