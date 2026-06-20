using CloakBrowser;
using CloakBrowser.Human;

// CloakBrowser .NET examples. Run a specific example by name:
//   dotnet run --project examples/CloakBrowser.Examples -- basic
//   dotnet run --project examples/CloakBrowser.Examples -- humanize
//   dotnet run --project examples/CloakBrowser.Examples -- context
//   dotnet run --project examples/CloakBrowser.Examples -- persistent
//   dotnet run --project examples/CloakBrowser.Examples -- proxy-geoip

string which = args.Length > 0 ? args[0] : "basic";

switch (which)
{
    case "basic": await Basic(); break;
    case "humanize": await Humanize(); break;
    case "context": await Context(); break;
    case "persistent": await Persistent(); break;
    case "proxy-geoip": await ProxyGeoip(); break;
    default:
        Console.Error.WriteLine($"Unknown example: {which}");
        Console.Error.WriteLine("Available: basic, humanize, context, persistent, proxy-geoip");
        Environment.Exit(2);
        break;
}

// ---------------------------------------------------------------------------
// Basic launch — open a page and print the title.
// ---------------------------------------------------------------------------
static async Task Basic()
{
    await using var browser = await CloakLauncher.LaunchAsync(new LaunchOptions
    {
        Headless = true,
    });
    var page = await browser.NewPageAsync();
    await page.GotoAsync("https://bot.incolumitas.com/");
    Console.WriteLine($"Title: {await page.TitleAsync()}");
}

// ---------------------------------------------------------------------------
// Humanized interaction — Bezier mouse, human typing, scroll, actionability.
// ---------------------------------------------------------------------------
static async Task Humanize()
{
    await using var browser = await CloakLauncher.LaunchAsync(new LaunchOptions
    {
        Headless = false,
        Humanize = true,
        HumanPreset = HumanPreset.Careful,
        HumanConfig = new Dictionary<string, object>
        {
            ["typing_delay"] = 90.0,
            ["mouse_overshoot_chance"] = 0.2,
        },
    });

    // NewHumanPageAsync returns a HumanPage wrapper with the configured behavior.
    HumanPage human = await browser.NewHumanPageAsync();
    await human.GotoAsync("https://example.com/");

    // Humanized actions go through actionability checks + Bezier movement.
    await human.ClickAsync("a");
    // await human.FillAsync("#search", "hello world");
    // await human.PressAsync("#search", "Enter");

    Console.WriteLine($"Title: {await human.Page.TitleAsync()}");
}

// ---------------------------------------------------------------------------
// Context with viewport, user agent, locale, timezone.
// ---------------------------------------------------------------------------
static async Task Context()
{
    await using var ctx = await CloakLauncher.LaunchContextAsync(new LaunchContextOptions
    {
        Headless = true,
        Locale = "en-US",
        Timezone = "America/New_York",
        Viewport = (1280, 800),
        ColorScheme = "dark",
    });
    var page = await ctx.NewPageAsync();
    await page.GotoAsync("https://example.com/");
    Console.WriteLine($"Title: {await page.TitleAsync()}");
}

// ---------------------------------------------------------------------------
// Persistent profile — cookies/localStorage survive across runs.
// ---------------------------------------------------------------------------
static async Task Persistent()
{
    await using var ctx = await CloakLauncher.LaunchPersistentContextAsync(
        "./cloak-profile",
        new LaunchContextOptions { Headless = true });
    var page = await ctx.NewPageAsync();
    await page.GotoAsync("https://example.com/");
    Console.WriteLine($"Title: {await page.TitleAsync()} (profile saved to ./cloak-profile)");
}

// ---------------------------------------------------------------------------
// Proxy + GeoIP — timezone/locale auto-detected, WebRTC IP spoofed to exit IP.
// ---------------------------------------------------------------------------
static async Task ProxyGeoip()
{
    await using var browser = await CloakLauncher.LaunchAsync(new LaunchOptions
    {
        Headless = true,
        Proxy = "http://user:pass@proxy.example.com:8080",
        GeoIp = true, // resolves timezone/locale + WebRTC exit IP from the proxy
        Args = new List<string> { "--fingerprint-webrtc-ip=auto" },
    });
    var page = await browser.NewPageAsync();
    await page.GotoAsync("https://ipinfo.io/json");
    Console.WriteLine(await page.InnerTextAsync("body"));
}
