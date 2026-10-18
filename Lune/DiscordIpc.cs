using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lune;

// Local Rich Presence IPC only. No Discord account token, bot, or web API is used.
internal sealed class DiscordIpc : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> pending = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int closed;
    public bool Connected => Volatile.Read(ref closed) == 0 && pipe.IsConnected;
    private DiscordIpc(NamedPipeClientStream pipe) { this.pipe = pipe; }
    internal static async Task<DiscordIpc> Connect(string id, CancellationToken cancellation, string[]? testPipes = null)
    {
        var names = testPipes ?? Enumerable.Range(0, 10).Select(n => $"discord-ipc-{n}").ToArray();
        foreach (var name in names)
        {
            cancellation.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(100, cancellation).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or IOException) { pipe.Dispose(); continue; }
            catch { pipe.Dispose(); throw; }
            var connection = new DiscordIpc(pipe);
            try
            {
                _ = connection.ReadLoop();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(3000);
                await connection.SendFrame(0, JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = id }), timeout.Token).ConfigureAwait(false);
                await connection.ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }
        throw new IOException("Discordのデスクトップ版が見つかりません。");
    }
    internal async Task SetActivity(object? activity, CancellationToken cancellation)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[nonce] = response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(3000);
            var body = JsonSerializer.SerializeToUtf8Bytes(new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity }, nonce });
            await SendFrame(1, body, timeout.Token).ConfigureAwait(false);
            await response.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { pending.TryRemove(nonce, out _); }
    }
    private async Task SendFrame(int opcode, byte[] body, CancellationToken cancellation)
    {
        await writer.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var frame = new byte[8 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, opcode); BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
            body.CopyTo(frame, 8);
            await pipe.WriteAsync(frame, cancellation).ConfigureAwait(false);
        }
        finally { writer.Release(); }
    }
    private async Task ReadLoop()
    {
        try
        {
            var header = new byte[8];
            while (!lifetime.IsCancellationRequested)
            {
                await pipe.ReadExactlyAsync(header, lifetime.Token).ConfigureAwait(false);
                var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                if (length < 0 || length > 1024 * 1024) throw new IOException("Discordからの応答サイズが不正です。");
                var body = new byte[length]; await pipe.ReadExactlyAsync(body, lifetime.Token).ConfigureAwait(false);
                if (opcode == 3) { await SendFrame(4, body, lifetime.Token).ConfigureAwait(false); continue; }
                if (opcode == 2) throw new IOException("Discordとの接続が終了しました。");
                if (opcode != 1) continue;
                using var json = JsonDocument.Parse(body); var root = json.RootElement;
                var evt = root.TryGetProperty("evt", out var eventName) && eventName.ValueKind == JsonValueKind.String ? eventName.GetString() : "";
                if (evt == "READY") ready.TrySetResult();
                var nonce = root.TryGetProperty("nonce", out var nonceValue) && nonceValue.ValueKind == JsonValueKind.String ? nonceValue.GetString() : null;
                if (evt == "ERROR")
                {
                    var code = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out var c) ? c.ToString() : "不明";
                    var error = new IOException($"Discordが表示を受け付けませんでした（{code}）。Application IDとDiscordの設定を確認してください。");
                    if (nonce != null && pending.TryGetValue(nonce, out var failed)) failed.TrySetException(error);
                    else ready.TrySetException(error);
                }
                else if (nonce != null && pending.TryGetValue(nonce, out var completed)) completed.TrySetResult();
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            foreach (var item in pending.Values) item.TrySetException(ex);
        }
        finally { Dispose(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        lifetime.Cancel(); pipe.Dispose();
        foreach (var item in pending.Values) item.TrySetCanceled();
    }
}
