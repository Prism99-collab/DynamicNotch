using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace DynamicNotch.Services;

/// <summary>
/// Google, for the AI: Google only gives results to a real browser, so the search runs in Windows' own Edge engine
/// (WebView2, already on every Windows 11 PC), in an invisible tab, like you'd search yourself; the results and
/// Google's own answer box (when it shows one) are read from the page. The tab closes after two idle minutes.
/// </summary>
public sealed class GoogleSearch
{
    readonly IntPtr _parent;
    readonly Dispatcher _ui;
    CoreWebView2Environment? _env;
    CoreWebView2Controller? _controller;
    readonly DispatcherTimer _idle;
    readonly SemaphoreSlim _one = new(1, 1);

    public GoogleSearch(IntPtr parent, Dispatcher ui)
    {
        _parent = parent;
        _ui = ui;
        _idle = new DispatcherTimer(TimeSpan.FromMinutes(2), DispatcherPriority.Background, (_, _) => Close(), ui) { IsEnabled = false };
    }

    /// <summary>Is Edge's engine here at all? (Without it, the other search engines answer.)</summary>
    public static bool Available
    {
        get { try { return CoreWebView2Environment.GetAvailableBrowserVersionString() is { Length: > 0 }; } catch { return false; } }
    }

    public async Task<List<WebTools.Hit>> Search(string query, int max, CancellationToken cancel)
    {
        if (!await _one.WaitAsync(TimeSpan.FromSeconds(15), cancel)) return new();
        try
        {
            _idle.Stop();
            var core = await Core();
            string url = "https://www.google.com/search?hl=en&num=" + Math.Clamp(max + 2, 5, 15) + "&q=" + Uri.EscapeDataString(query);
            await Go(core, url, cancel);
            // the cookie page some countries get: accepted once, then the search again
            if (core.Source.Contains("consent.google"))
            {
                await core.ExecuteScriptAsync("(() => { const b = [...document.querySelectorAll('button, input[type=submit]')].find(x => /accept|agree|accepter|tout accepter|alle akzeptieren|aceptar/i.test(x.innerText || x.value)); if (b) b.click(); })()");
                await Task.Delay(1500, cancel);
                await Go(core, url, cancel);
            }
            if (core.Source.Contains("/sorry/")) return new(); // Google wants a captcha: the other engines answer instead
            // the results are drawn by the page: wait for them a moment
            string json = "[]";
            for (int i = 0; i < 12 && !cancel.IsCancellationRequested; i++)
            {
                json = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(Extract)) ?? "[]";
                if (json.Length > 2) break;
                await Task.Delay(250, cancel);
            }
            // the AI Overview writes itself for a moment: read once more if it's there but still short
            if (json.Contains("\"Google answer\"") == false && json.Length > 2)
            {
                await Task.Delay(900, cancel);
                string again = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(Extract)) ?? "[]";
                if (again.Length >= json.Length) json = again;
            }
            var rows = (JsonNode.Parse(json)?.AsArray() ?? new JsonArray()).Take(max + 1).ToList();
            var addresses = await Task.WhenAll(rows.Select(r => r?["t"]?.ToString() == "Google answer" ? Task.FromResult(r?["u"]?.ToString() ?? "") : RealAddress(r?["u"]?.ToString() ?? "", r?["c"]?.ToString() ?? "", cancel)));
            var hits = new List<WebTools.Hit>();
            for (int i = 0; i < rows.Count; i++)
            {
                string address = addresses[i];
                if (rows[i]?["t"]?.ToString() != "Google answer" && Uri.TryCreate(address, UriKind.Absolute, out var a) && a.Host.Contains("google.")) continue; // Google's own pages (images, videos tabs)
                hits.Add(new WebTools.Hit(rows[i]?["t"]?.ToString() ?? "", address, rows[i]?["s"]?.ToString() ?? ""));
            }
            return hits;
        }
        catch (Exception ex) { AppLog.Write("google search: " + ex.Message); return new(); }
        finally
        {
            _one.Release();
            _ui.BeginInvoke(() => { _idle.Stop(); _idle.Start(); });
        }
    }

    async Task<CoreWebView2> Core()
    {
        if (_controller?.CoreWebView2 is { } ready) return ready;
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicNotch", "WebView");
        _env ??= await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions("--mute-audio --disable-features=msSmartScreenProtection"));
        _controller = await _env.CreateCoreWebView2ControllerAsync(_parent);
        _controller.IsVisible = false;
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, 1024, 768);
        var core = _controller.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        // pictures, fonts and videos aren't needed to read results: not loaded (faster, lighter)
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Font);
        core.WebResourceRequested += (_, e) => e.Response = _env.CreateWebResourceResponse(null, 404, "Not Found", "");
        core.NewWindowRequested += (_, e) => e.Handled = true;
        return core;
    }

    static async Task Go(CoreWebView2 core, string url, CancellationToken cancel)
    {
        var done = new TaskCompletionSource<bool>();
        void Finished(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult(e.IsSuccess);
        core.NavigationCompleted += Finished;
        try
        {
            core.Navigate(url);
            await Task.WhenAny(done.Task, Task.Delay(9000, cancel));
        }
        finally { core.NavigationCompleted -= Finished; }
    }

    /// <summary>
    /// Read from the page: Google's AI Overview or answer box (when there is one), then each result's title, its link
    /// (Google's own redirect, resolved afterwards), the address shown under it, and its snippet.
    /// </summary>
    const string Extract = """
        (() => {
          const out = [], seen = new Set();
          const text = document.body ? document.body.innerText : '';
          const at = text.indexOf('AI Overview');
          if (at >= 0) {
            let s = text.slice(at + 11, at + 1400);
            for (const stop of ['Show more', 'AI responses may', 'Dive deeper', 'People also ask']) { const k = s.indexOf(stop); if (k > 0) s = s.slice(0, k); }
            s = s.split('\n').filter(l => !/^\s*(Thinking|Searching|Generating)/.test(l)).join('\n').trim();
            if (s.length > 40) out.push({ t: 'Google answer', u: location.href, s: s.slice(0, 900), c: '' });
          }
          if (!out.length) {
            const box = document.querySelector('.hgKElc, .Z0LcW, .IZ6rdc, [data-attrid="wa:/description"], .kno-rdesc span, .LGOjhe');
            if (box && box.innerText.trim().length > 1) out.push({ t: 'Google answer', u: location.href, s: box.innerText.trim().slice(0, 600), c: '' });
          }
          document.querySelectorAll('a h3').forEach(h => {
            const a = h.closest('a');
            if (!a || !a.href || seen.has(a.href)) return;
            seen.add(a.href);
            const card = a.closest('.MjjYud, .g, [data-hveid]') || a.parentElement;
            const sn = card ? card.querySelector('.VwiC3b, [data-sncf], .ITZIwc, .lEBKkf') : null;
            const cite = card ? card.querySelector('cite') : null;
            out.push({ t: h.innerText.trim(), u: a.href, s: sn ? sn.innerText.trim().slice(0, 400) : '', c: cite ? cite.innerText : '' });
          });
          return JSON.stringify(out.slice(0, 14));
        })()
        """;

    static readonly System.Net.Http.HttpClient Resolver = new(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>Google's links go through google.com/goto (or /url): one quick request each says where they really go.</summary>
    static async Task<string> RealAddress(string href, string cite, CancellationToken cancel)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var u) || !u.Host.Contains("google.")) return href;
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        if (q["q"] is { } direct && direct.StartsWith("http")) return direct;
        try
        {
            using var res = await Resolver.GetAsync(href, cancel);
            if (res.Headers.Location is { } loc && loc.IsAbsoluteUri && !loc.Host.Contains("google.")) return loc.ToString();
        }
        catch { }
        // the address shown under the result ("https://site.com › path › page")
        if (cite.StartsWith("http")) return cite.Replace(" › ", "/").Replace("›", "/").Replace(" ", "");
        return href;
    }

    public void Close()
    {
        _idle.Stop();
        try { _controller?.Close(); } catch { }
        _controller = null;
    }
}
