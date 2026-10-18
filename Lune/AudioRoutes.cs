using System;
using System.Collections.Generic;
using System.Linq;

namespace Lune;

public sealed record OutputDevice(string Id, string Name, string Group = "再生デバイス", bool CanSelect = true, string RenderId = "", string Note = "")
{
    public bool IsRecording => Group == "録音デバイス";
    public string TransportId => IsRecording ? RenderId : Id;
}
internal sealed record AudioEndpoint(string Id, string Name, string Description, string Driver);
internal static class AudioRoutes
{
    internal const string Off = "none";
    internal static List<OutputDevice> Build(IReadOnlyList<AudioEndpoint> render, IReadOnlyList<AudioEndpoint> capture)
    {
        var result = render.OrderBy(d => d.Name).Select(d => new OutputDevice(d.Id, d.Name)).ToList();
        foreach (var mic in capture.OrderBy(d => d.Name))
        {
            AudioEndpoint? target = null;
            var note = "この録音デバイスに直接出力することはできません。VoicemeeterやVB-CABLEなどの仮想録音デバイスを選んでください。";
            // Use driver identity and endpoint descriptions, not user-editable display names.
            if (mic.Driver.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
            {
                if (mic.Description.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase) && mic.Description.EndsWith("Output", StringComparison.OrdinalIgnoreCase))
                {
                    var input = mic.Description[..^6] + "Input";
                    target = render.FirstOrDefault(d => d.Driver == mic.Driver && d.Description.Equals(input, StringComparison.OrdinalIgnoreCase));
                    note = target == null ? "対応するCABLE Inputが接続されていません。" : $"{target.Name}経由で送信します。録音するアプリで「{mic.Name}」を入力に選んでください。";
                }
                else if (mic.Description.StartsWith("Voicemeeter", StringComparison.OrdinalIgnoreCase))
                {
                    var bus = mic.Description switch
                    {
                        "Voicemeeter Out B1" or "Voicemeeter Output" => "B1",
                        "Voicemeeter Out B2" or "Voicemeeter Aux Output" => "B2",
                        "Voicemeeter Out B3" or "Voicemeeter VAIO3 Output" => "B3",
                        _ => ""
                    };
                    var input = bus switch { "B1" => "Voicemeeter Input", "B2" => "Voicemeeter AUX Input", "B3" => "Voicemeeter VAIO3 Input", _ => "" };
                    target = render.FirstOrDefault(d => input.Length > 0 && d.Driver == mic.Driver && d.Description.Equals(input, StringComparison.OrdinalIgnoreCase));
                    note = bus.Length == 0 ? "Voicemeeterの録音用バス B1・B2・B3を選んでください。" : target == null ? "対応するVoicemeeter入力が接続されていません。" :
                        $"{target.Name}へ送信します。Voicemeeterを起動し、この入力列の「{bus}」をオンにしてください。録音するアプリの入力は「{mic.Name}」を選びます。LuneからVoicemeeterの設定は変更しません。";
                }
            }
            result.Add(new(mic.Id, mic.Name, "録音デバイス", target != null, target?.Id ?? "", note));
        }
        return result;
    }
}
