using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DynamicNotch.Services;

/// <summary>One browser tab that plays, as the "Dynamic Notch Media" extension reports it.</summary>
public sealed record WebTab(string Key, int Link, int TabId, string Browser, string Title, string Artist, string Site,
    string? Art, string? Icon, bool Playing, double Pos, double Dur, bool CanNext, bool CanPrev, DateTime At);

/// <summary>
/// The link to the optional browser extension (browser-extension/): a browser tells Windows about one playing tab
/// at most, so with the extension every tab that plays shows in the notch, with its own buttons. The notch listens
/// on 127.0.0.1 only (nothing from the network can reach it, and Windows' firewall doesn't ask), and only a browser
/// extension may connect: a web page can't, its origin gives it away. Without the extension nothing happens here
/// but one idle socket.
/// </summary>
public sealed class WebMediaLink : IDisposable
{
    public const int Port = 47853;
    const int MaxMessage = 512 * 1024;
    static readonly string[] Browsers = { "Chrome", "MSEdge", "Brave", "Opera", "Vivaldi", "Firefox" };

    readonly Dispatcher _ui;
    readonly CancellationTokenSource _cts = new();
    readonly object _gate = new();
    readonly Dictionary<int, Link> _links = new();
    TcpListener? _listener;
    int _nextLink;

    sealed class Link
    {
        public int Id;
        public WebSocket Ws = null!;
        public string Browser = "Chrome";
        public List<WebTab> Tabs = new();
        public readonly SemaphoreSlim Sending = new(1, 1);
    }

    public WebMediaLink(Dispatcher ui) => _ui = ui;

    /// <summary>Every playing (or paused, still open) tab of every connected browser.</summary>
    public IReadOnlyList<WebTab> Tabs { get; private set; } = Array.Empty<WebTab>();
    /// <summary>The browsers connected right now ("Brave", "Chrome"...).</summary>
    public IReadOnlyList<string> Connected { get; private set; } = Array.Empty<string>();
    /// <summary>The tabs or the connected browsers changed (UI thread).</summary>
    public event Action? Changed;

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _ = AcceptLoop();
        }
        catch (Exception ex) { AppLog.Write($"browser link: can't listen on {Port} ({ex.Message})"); _listener = null; }
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        Link? link = null;
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                using (var shake = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    shake.CancelAfter(5000);
                    if (!await Handshake(stream, shake.Token)) return;
                }
                var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });
                link = new Link { Ws = ws };
                lock (_gate) { link.Id = ++_nextLink; _links[link.Id] = link; }
                await Receive(link);
            }
            catch { }
            finally
            {
                if (link != null)
                {
                    lock (_gate) _links.Remove(link.Id);
                    AppLog.Write($"browser link: {link.Browser} disconnected");
                    Publish();
                }
            }
        }
    }

    /// <summary>The WebSocket opening: a GET from a browser extension (chrome-extension://, moz-extension://) only.</summary>
    static async Task<bool> Handshake(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        int n = 0;
        while (true)
        {
            int r = await stream.ReadAsync(buf.AsMemory(n), ct);
            if (r <= 0) return false;
            n += r;
            if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
            if (n == buf.Length) return false;
        }
        var lines = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n");
        if (!lines[0].StartsWith("GET /dynamic-notch ", StringComparison.Ordinal)) return false;
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) h[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        string origin = h.GetValueOrDefault("Origin") ?? "";
        bool extension = origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
            || origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)
            || origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase);
        if (!extension || h.GetValueOrDefault("Sec-WebSocket-Key") is not { Length: > 0 } key)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
            return false;
        }
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), ct);
        return true;
    }

    async Task Receive(Link link)
    {
        var buf = new byte[16 * 1024];
        using var msg = new MemoryStream();
        while (link.Ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            var r = await link.Ws.ReceiveAsync(buf, _cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) break;
            msg.Write(buf, 0, r.Count);
            if (msg.Length > MaxMessage) break; // not what the extension sends
            if (!r.EndOfMessage) continue;
            if (r.MessageType == WebSocketMessageType.Text)
                try { OnMessage(link, msg.GetBuffer().AsSpan(0, (int)msg.Length)); } catch { }
            msg.SetLength(0);
        }
    }

    void OnMessage(Link link, ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        string t = Str(root, "t", 16);
        if (t == "hello")
        {
            string b = Str(root, "browser", 16);
            link.Browser = Browsers.FirstOrDefault(x => x.Equals(b, StringComparison.OrdinalIgnoreCase)) ?? "Chrome";
            AppLog.Write($"browser link: {link.Browser} connected");
            Publish();
            return;
        }
        if (t != "tabs" || !root.TryGetProperty("tabs", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        var now = DateTime.UtcNow;
        var tabs = new List<WebTab>();
        foreach (var e in arr.EnumerateArray().Take(40))
        {
            if (!e.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id)) continue;
            string title = Str(e, "title", 300);
            if (title.Length == 0) continue;
            tabs.Add(new WebTab($"w{link.Id}:{id}", link.Id, id, link.Browser, title, Str(e, "artist", 200), Str(e, "site", 120),
                Url(Str(e, "art", 2048)), Url(Str(e, "icon", 2048)), Bool(e, "playing"), Num(e, "pos"), Num(e, "dur"),
                Bool(e, "next"), Bool(e, "prev"), now));
        }
        link.Tabs = tabs;
        Publish();
    }

    static string Str(JsonElement e, string name, int max)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
        string s = (v.GetString() ?? "").Trim();
        return s.Length > max ? s[..max] : s;
    }
    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) && d >= 0 ? Math.Min(d, 1e7) : 0;
    /// <summary>Pictures from the web only (a page's own cover or icon), never a file on this PC.</summary>
    static string? Url(string s) =>
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? s : null;

    void Publish()
    {
        List<WebTab> all;
        List<string> connected;
        lock (_gate)
        {
            all = _links.Values.SelectMany(l => l.Tabs).ToList();
            connected = _links.Values.Select(l => l.Browser).Distinct().ToList();
        }
        _ui.InvokeAsync(() =>
        {
            Tabs = all;
            Connected = connected;
            Changed?.Invoke();
        });
    }

    /// <summary>A button on the notch for one tab: "toggle", "play", "pause", "next", "prev", "seek" (seconds), "focus".</summary>
    public void Send(WebTab tab, string cmd, double pos = 0)
    {
        Link? link;
        lock (_gate) _links.TryGetValue(tab.Link, out link);
        if (link == null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { t = "cmd", tab = tab.TabId, cmd, pos });
        _ = Task.Run(async () =>
        {
            await link.Sending.WaitAsync();
            try { if (link.Ws.State == WebSocketState.Open) await link.Ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token); }
            catch { }
            finally { link.Sending.Release(); }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
