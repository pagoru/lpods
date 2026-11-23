using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LPods.Platform;
using WPods.Core;

namespace LPods;

/// <summary>
/// The control socket ($XDG_RUNTIME_DIR/lpods.sock, this user only). A client sends one line, a JSON array of
/// arguments, and gets one JSON line back: {"ok":true,"result":...} or {"ok":false,"error":"..."}. "watch" instead
/// streams a status line on every change until the client disconnects.
/// </summary>
internal sealed class Control(Commands commands, ConnectionManager connections, Changes changes) : IDisposable
{
    private static readonly TimeSpan WatchCoalesce = TimeSpan.FromMilliseconds(150);
    private readonly CancellationTokenSource _stop = new();
    private Socket? _listener;

    public static async Task<bool> IsRunningAsync()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(LinuxPaths.ControlSocket)).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Start()
    {
        string path = LinuxPaths.ControlSocket;
        File.Delete(path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(8);
        _ = Task.Run(AcceptAsync);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Dispose();
        try
        {
            File.Delete(LinuxPaths.ControlSocket);
        }
        catch (IOException)
        {
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(Socket client)
    {
        using (client)
        await using (var stream = new NetworkStream(client, ownsSocket: false))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
        {
            try
            {
                string[] args = JsonSerializer.Deserialize<string[]>(await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) ?? "[]") ?? [];
                if (args is ["watch"])
                {
                    await WatchAsync(writer).ConfigureAwait(false);
                    return;
                }
                JsonNode? result = await commands.RunAsync(args).ConfigureAwait(false);
                await writer.WriteLineAsync(new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString()).ConfigureAwait(false);
            }
            catch (CommandException e)
            {
                await writer.WriteLineAsync(new JsonObject { ["ok"] = false, ["error"] = e.Message }.ToJsonString()).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or JsonException)
            {
                // Client gone or garbage.
            }
            catch (Exception e)
            {
                Log.Error("control", "command failed", e);
                await writer.WriteLineAsync(new JsonObject { ["ok"] = false, ["error"] = e.Message }.ToJsonString()).ConfigureAwait(false);
            }
        }
    }

    private async Task WatchAsync(StreamWriter writer)
    {
        var signal = new SemaphoreSlim(0);
        void OnChanged() => signal.Release();
        changes.Changed += OnChanged;
        try
        {
            string? last = null;
            while (true)
            {
                string line = Status.Build(connections).ToJsonString();
                if (line != last)
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                    last = line;
                }
                await signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                await Task.Delay(WatchCoalesce, _stop.Token).ConfigureAwait(false);
                while (signal.CurrentCount > 0)
                {
                    await signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            changes.Changed -= OnChanged;
        }
    }

    /// <summary>Client side: sends <paramref name="args"/> and yields each reply line.</summary>
    public static async IAsyncEnumerable<JsonNode> SendAsync(string[] args)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(LinuxPaths.ControlSocket)).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args) + "\n")).ConfigureAwait(false);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (JsonNode.Parse(line) is { } node)
            {
                yield return node;
            }
        }
    }
}
