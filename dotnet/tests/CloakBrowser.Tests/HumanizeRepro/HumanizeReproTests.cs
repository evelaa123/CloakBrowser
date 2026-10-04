using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace CloakBrowser.Tests.HumanizeRepro;

/// <summary>
/// Real-browser proofs for the humanize review (.NET wrapper).
///
/// Same fixture pages as the Python/JS suites (tests/humanize_repro/site).
/// One CloakBrowser process: <c>Browser</c> is the humanized wrapper,
/// <c>RawBrowser</c> is the stock Playwright reference in the same process.
/// Control(...) checks the reference; Bug(...) fails when the humanized page diverges.
///
/// Run:  CLOAKBROWSER_HUMANIZE_REPRO=1 dotnet test --filter FullyQualifiedName~HumanizeRepro
/// </summary>
public sealed class ReproFixture : IAsyncLifetime
{
    public static bool Enabled => Environment.GetEnvironmentVariable("CLOAKBROWSER_HUMANIZE_REPRO") == "1";

    private HttpListener? _listener;
    public string Url { get; private set; } = "";
    public CloakBrowserHandle? Handle { get; private set; }

    public static readonly Dictionary<string, object> Fast = new()
    {
        ["mistype_chance"] = 0.0,
        ["field_switch_delay"] = new[] { 0.0, 0.0 },
        ["typing_delay"] = 5.0,
        ["typing_delay_spread"] = 0.0,
        ["typing_pause_chance"] = 0.0,
        ["key_hold"] = new[] { 5.0, 5.0 },
        ["shift_down_delay"] = new[] { 5.0, 5.0 },
        ["shift_up_delay"] = new[] { 5.0, 5.0 },
        ["mouse_min_steps"] = 4,
        ["mouse_max_steps"] = 4,
        ["mouse_burst_pause"] = new[] { 0.0, 0.0 },
        ["mouse_overshoot_chance"] = 0.0,
        ["click_aim_delay_input"] = new[] { 5.0, 5.0 },
        ["click_aim_delay_button"] = new[] { 5.0, 5.0 },
        ["click_hold_input"] = new[] { 20.0, 20.0 },
        ["click_hold_button"] = new[] { 20.0, 20.0 },
        ["idle_between_actions"] = false,
        ["scroll_pause_fast"] = new[] { 5.0, 5.0 },
        ["scroll_pause_slow"] = new[] { 5.0, 5.0 },
        ["scroll_settle_delay"] = new[] { 50.0, 50.0 },
        ["scroll_pre_move_delay"] = new[] { 5.0, 5.0 },
        ["scroll_overshoot_chance"] = 0.0,
    };

    public static string SiteDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "humanize_repro", "site")))
            dir = dir.Parent;
        return dir == null ? "" : Path.Combine(dir.FullName, "tests", "humanize_repro", "site");
    }

    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        var site = SiteDir();
        var port = Random.Shared.Next(20000, 60000);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        Url = $"http://127.0.0.1:{port}/";
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                var rel = ctx.Request.Url!.AbsolutePath.TrimStart('/');
                if (rel.Length == 0) rel = "index.html";
                var file = Path.Combine(site, rel);
                if (!File.Exists(file)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
                ctx.Response.ContentType = file.EndsWith(".js") ? "text/javascript" : "text/html";
                var bytes = await File.ReadAllBytesAsync(file);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        Handle = await CloakLauncher.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Humanize = true,
            HumanConfig = Fast,
        });
    }

    public async Task DisposeAsync()
    {
        if (Handle != null) await Handle.DisposeAsync();
        _listener?.Stop();
    }
}

public class HumanizeReproTests : IClassFixture<ReproFixture>
{
    private readonly ReproFixture _f;
    public HumanizeReproTests(ReproFixture f) => _f = f;

    // ------------------------------------------------------------------ helpers

    private async Task<IPage> Human(string path = "index.html")
    {
        var page = await _f.Handle!.Browser.NewPageAsync();
        await page.GotoAsync(_f.Url + path);
        return page;
    }

    private async Task<IPage> Stock(string path = "index.html")
    {
        var page = await _f.Handle!.RawBrowser.NewPageAsync();
        await page.GotoAsync(_f.Url + path);
        return page;
    }

    private static async Task<List<JsonElement>> Events(IPage page, params string[] types)
    {
        var json = await page.EvaluateAsync<string>("JSON.stringify(window.__log)");
        var all = JsonSerializer.Deserialize<List<JsonElement>>(json)!;
        return types.Length == 0 ? all : all.Where(e => types.Contains(e.GetProperty("t").GetString())).ToList();
    }

    private static async Task<List<JsonElement>> FrameEvents(IFrame frame, params string[] types)
    {
        var json = await frame.EvaluateAsync<string>("JSON.stringify(window.__log)");
        var all = JsonSerializer.Deserialize<List<JsonElement>>(json)!;
        return types.Length == 0 ? all : all.Where(e => types.Contains(e.GetProperty("t").GetString())).ToList();
    }

    private static Task Reset(IPage p) => p.EvaluateAsync("window.__reset(); window.__mainWorldHits.length = 0");
    private static Task ResetFrame(IFrame f) => f.EvaluateAsync("window.__reset(); window.__mainWorldHits.length = 0");

    private static Task<string> Value(IPage p, string sel) =>
        p.EvaluateAsync<string>("s => document.querySelector(s).value", sel);

    private static async Task<(double Dur, Exception? Err)> Timed(Func<Task> fn)
    {
        var sw = Stopwatch.StartNew();
        try { await fn(); return (sw.Elapsed.TotalSeconds, null); }
        catch (Exception e) { return (sw.Elapsed.TotalSeconds, e); }
    }

    private static string Short(Exception? e) =>
        e == null ? "no exception" : $"{e.GetType().Name}: {e.Message.Split('\n')[0][..Math.Min(160, e.Message.Split('\n')[0].Length)]}";

    private static void Control(bool cond, string msg) => Assert.True(cond, $"CONTROL FAILED (test is invalid): {msg}");
    private static void Bug(bool cond, string msg) => Assert.True(cond, $"BUG: {msg}");

    private static string Targets(IEnumerable<JsonElement> evs) =>
        string.Join(",", evs.Select(e => e.GetProperty("target").GetString()));

    // ------------------------------------------------------------------ 1. text input

    [Theory]
    [InlineData("#date", "2024-01-31")]
    [InlineData("#range", "80")]
    [InlineData("#color", "#ff0000")]
    public async Task Fill_non_text_input(string sel, string val)
    {
        if (!ReproFixture.Enabled) return;
        var s = await Stock();
        await s.FillAsync(sel, val);
        Control(await Value(s, sel) == val, $"stock fill {sel}");
        var page = await Human();
        var (_, err) = await Timed(() => page.FillAsync(sel, val, new() { Timeout = 5000 }));
        var got = await Value(page, sel);
        Bug(err == null && got == val, $"FillAsync({sel}, {val}) -> '{got}' ({Short(err)})");
    }

    [Fact]
    public async Task Locator_press_sequentially_inserts_at_click_caret()
    {
        if (!ReproFixture.Enabled) return;
        const string text = "hello world hello world hello";
        var s = await Stock();
        await s.EvaluateAsync("t => document.querySelector('#long').value = t", text);
        await s.Locator("#long").PressSequentiallyAsync("X");
        var expected = await Value(s, "#long");
        Control(expected == "X" + text || expected == text + "X", $"stock {expected}");
        var page = await Human();
        var results = new HashSet<string>();
        for (int i = 0; i < 4; i++)
        {
            await page.EvaluateAsync("t => { const e = document.querySelector('#long'); e.value = t; e.blur(); }", text);
            await page.Locator("#long").PressSequentiallyAsync("X");
            results.Add(await Value(page, "#long"));
        }
        Bug(results.Count == 1 && results.Contains(expected), $"expected '{expected}', got {string.Join(" | ", results)}");
    }

    // ------------------------------------------------------------------ 2. dropped options

    [Fact]
    public async Task Trial_click_must_not_click()
    {
        if (!ReproFixture.Enabled) return;
        var s = await Stock();
        await Reset(s);
        await s.ClickAsync("#btn", new() { Trial = true });
        Control((await Events(s, "click")).Count == 0, "stock trial");
        var page = await Human();
        await Reset(page);
        await page.ClickAsync("#btn", new() { Trial = true });
        var n = (await Events(page, "click")).Count;
        Bug(n == 0, $"ClickAsync(Trial=true) dispatched {n} real click(s)");
    }

    [Fact]
    public async Task Right_click_button_option()
    {
        if (!ReproFixture.Enabled) return;
        var s = await Stock();
        await Reset(s);
        await s.ClickAsync("#btn", new() { Button = MouseButton.Right });
        Control((await Events(s, "mousedown")).Select(e => e.GetProperty("button").GetInt32()).SequenceEqual(new[] { 2 }), "stock right click");
        var page = await Human();
        await Reset(page);
        await page.ClickAsync("#btn", new() { Button = MouseButton.Right });
        var buttons = (await Events(page, "mousedown")).Select(e => e.GetProperty("button").GetInt32()).ToArray();
        Bug(buttons.SequenceEqual(new[] { 2 }), $"ClickAsync(Button=Right) -> mousedown buttons [{string.Join(",", buttons)}]");
    }

    [Fact]
    public async Task Click_count_and_modifiers_options()
    {
        if (!ReproFixture.Enabled) return;
        var s = await Stock();
        await Reset(s);
        await s.ClickAsync("#btn", new() { ClickCount = 2, Modifiers = new[] { KeyboardModifier.Shift } });
        var sd = await Events(s, "dblclick");
        Control(sd.Count == 1 && sd[0].GetProperty("shift").GetBoolean(), "stock clickCount+shift");
        var page = await Human();
        await Reset(page);
        await page.ClickAsync("#btn", new() { ClickCount = 2, Modifiers = new[] { KeyboardModifier.Shift } });
        var clicks = await Events(page, "click");
        var dbl = (await Events(page, "dblclick")).Count;
        var shift = clicks.Count > 0 && clicks[0].GetProperty("shift").GetBoolean();
        Bug(dbl == 1 && shift, $"ClickAsync(ClickCount=2, Shift) -> clicks {clicks.Count}, dblclick {dbl}, shiftKey {shift}");
    }

    [Fact]
    public async Task SetDefaultTimeout_is_ignored()
    {
        if (!ReproFixture.Enabled) return;
        async Task<(double, Exception?)> Run(IPage p)
        {
            p.SetDefaultTimeout(1000);
            await p.EvaluateAsync(@"() => setTimeout(() => { const b = document.createElement('button');
                b.id = 'late'; b.textContent = 'late'; document.body.prepend(b); }, 3000)");
            return await Timed(() => p.ClickAsync("#late"));
        }
        var (sd, se) = await Run(await Stock());
        Control(se != null && sd < 2, $"stock fails in {sd:F1}s");
        var (hd, he) = await Run(await Human());
        Bug(he != null && hd < 2, $"SetDefaultTimeout(1000) ignored: click took {hd:F1}s ({Short(he)})");
    }

    // ------------------------------------------------------------------ 3. strictness

    [Fact]
    public async Task Strict_mode_violation_is_swallowed()
    {
        if (!ReproFixture.Enabled) return;
        var s = await Stock();
        var (_, se) = await Timed(() => s.Locator(".dup").ClickAsync(new() { Timeout = 3000 }));
        Control(se != null && se.Message.Contains("strict mode violation"), $"stock: {Short(se)}");
        var page = await Human();
        await Reset(page);
        var (_, err) = await Timed(() => page.Locator(".dup").ClickAsync(new() { Timeout = 3000 }));
        Bug(err != null, $"ambiguous Locator('.dup').ClickAsync() silently clicked [{Targets(await Events(page, "click"))}]");
    }

    [Fact]
    public async Task GetBy_locator_with_no_box_clicks_at_cursor()
    {
        // GetBy* locators carry no selector in the .NET wrapper -> legacy path:
        // box ?? new BoundingBox(cursor.X, cursor.Y, 1, 1). A hidden match is
        // then "clicked" wherever the cursor happens to be.
        if (!ReproFixture.Enabled) return;
        var s = await Stock();
        await s.EvaluateAsync("document.querySelector('#btn').style.visibility='hidden'");
        var (_, se) = await Timed(() => s.GetByText("Press me").ClickAsync(new() { Timeout = 2000 }));
        Control(se != null, "stock refuses a hidden target");
        var page = await Human();
        await page.EvaluateAsync("document.querySelector('#btn').style.visibility='hidden'");
        await page.Mouse.MoveAsync(30, 100);
        await Reset(page);
        var (_, err) = await Timed(() => page.GetByText("Press me").ClickAsync(new() { Timeout = 2000 }));
        var downs = (await Events(page, "mousedown")).Select(e => $"{e.GetProperty("target").GetString()}@{e.GetProperty("x").GetDouble():F0},{e.GetProperty("y").GetDouble():F0}");
        Bug(err != null, $"GetByText on a hidden element returned OK; mouse went down on [{string.Join(";", downs)}]");
    }

    // ------------------------------------------------------------------ 5. frames

    [Fact]
    public async Task Frame_click_skips_pointer_events_check()
    {
        if (!ReproFixture.Enabled) return;
        var s = await Stock();
        var sf = s.Frame("f")!;
        await sf.WaitForLoadStateAsync();
        Control((await Timed(() => sf.ClickAsync("#fcovered", new() { Timeout = 2000 }))).Err != null, "stock refuses");
        var page = await Human();
        Control((await Timed(() => page.ClickAsync("#covered", new() { Timeout = 2000 }))).Err != null, "humanized main page refuses");
        var f = page.Frame("f")!;
        await f.WaitForLoadStateAsync();
        await ResetFrame(f);
        var (_, err) = await Timed(() => f.ClickAsync("#fcovered", new() { Timeout = 2000 }));
        Bug(err != null, $"frame ClickAsync on covered button succeeded; landed on [{Targets(await FrameEvents(f, "click"))}]");
    }

    // ------------------------------------------------------------------ 6. behaviour

    [Fact]
    public async Task Dblclick_event_sequence()
    {
        if (!ReproFixture.Enabled) return;
        async Task<string> Seq(IPage p)
        {
            await Reset(p);
            await p.DblClickAsync("#btn");
            return string.Join(" ", (await Events(p, "mousedown", "click", "dblclick"))
                .Select(e => $"{e.GetProperty("t").GetString()}:{e.GetProperty("detail").GetInt32()}"));
        }
        const string expected = "mousedown:1 click:1 mousedown:2 click:2 dblclick:2";
        Control(await Seq(await Stock()) == expected, "stock sequence");
        var got = await Seq(await Human());
        Bug(got == expected, $"dblclick sequence '{got}', a real double click is '{expected}'");
    }

    [Fact]
    public async Task Unreachable_element_misleading_error()
    {
        if (!ReproFixture.Enabled) return;
        var s = await Stock();
        Control((await Timed(() => s.FillAsync("#neg", "hello", new() { Timeout = 3000 }))).Err == null, "stock fills off-viewport input");
        var page = await Human();
        var (dur, err) = await Timed(() => page.FillAsync("#neg", "hello", new() { Timeout = 3000 }));
        Bug(err == null || (dur < 4 && !Short(err).Contains("covered by <none>")),
            $"FillAsync(Timeout=3000) on element at (-600,-600) took {dur:F1}s -> {Short(err)}");
    }
}
