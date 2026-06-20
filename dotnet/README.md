# CloakBrowser for .NET

A complete, faithful **.NET 8 / C#** port of the [CloakBrowser](https://github.com/CloakHQ/CloakBrowser)
Python wrapper — stealth Chromium that passes bot-detection tests, built on top of
[`Microsoft.Playwright`](https://playwright.dev/dotnet/).

CloakBrowser is a thin wrapper around a closed-source, source-level patched
Chromium binary (58 C++ fingerprint patches). This port reproduces **all** of the
wrapper functionality:

- Automatic binary **download / cache / auto-update** with SHA-256 verification
- **Stealth launch args** (random fingerprint seed, platform spoofing)
- **Proxy** handling — HTTP/HTTPS and SOCKS5, inline credentials, URL-encoding
- **GeoIP** timezone/locale resolution from the proxy exit IP (MaxMind GeoLite2)
- **WebRTC** IP spoofing (`--fingerprint-webrtc-ip=auto` → resolved exit IP)
- **Widevine** CDM hint seeding (Linux)
- **Persistent contexts** (profile reuse)
- A full **humanize** behavioral layer — Bezier mouse curves, human typing
  (CDP `Input.dispatchKeyEvent` stealth path), smooth scrolling, and
  Playwright-style actionability checks
- A **CLI** (`install` / `info` / `update` / `clear-cache`)

> Because .NET's Playwright exposes **sealed interfaces** (`IPage`, `ILocator`, …)
> that can't be monkey-patched the way the Python/JS clients replace methods at
> runtime, the humanize layer is exposed as an explicit **`HumanPage` wrapper**
> rather than by replacing `page.click`/`page.fill`/etc. in place. The behavior
> (curves, timings, checks, stealth paths) is identical.

---

## Requirements

- .NET 8 SDK
- `Microsoft.Playwright` 1.49.0 (pulled in transitively)
- `MaxMind.GeoIP2` 5.2.0 (for the GeoIP feature)

## Project layout

```
dotnet/
├── CloakBrowser.sln
├── src/
│   ├── CloakBrowser/                 # the library
│   │   ├── Config.cs                 # ← cloakbrowser/config.py
│   │   ├── Download.cs               # ← cloakbrowser/download.py
│   │   ├── GeoIp.cs                  # ← cloakbrowser/geoip.py
│   │   ├── Widevine.cs               # ← cloakbrowser/widevine.py
│   │   ├── CloakLauncher.cs          # ← cloakbrowser/browser.py (launch funcs, build_args)
│   │   ├── ProxyResolver.cs          # ← cloakbrowser/browser.py (proxy URL helpers)
│   │   ├── ProxySettings.cs          # ← ProxySettings TypedDict
│   │   ├── LaunchOptions.cs          # launch option records
│   │   ├── Handles.cs                # CloakBrowserHandle / CloakContextHandle
│   │   ├── CloakLog.cs               # logging facade
│   │   └── Human/                    # ← cloakbrowser/human/*
│   │       ├── HumanConfig.cs        #   ← human/config.py
│   │       ├── HumanRandom.cs        #   ← human/config.py (rand/sleep helpers)
│   │       ├── HumanMouse.cs         #   ← human/mouse.py
│   │       ├── HumanKeyboard.cs      #   ← human/keyboard.py
│   │       ├── HumanScroll.cs        #   ← human/scroll.py
│   │       ├── Actionability.cs      #   ← human/actionability.py
│   │       ├── IsolatedWorld.cs      #   ← _AsyncIsolatedWorld (human/__init__.py)
│   │       ├── PlaywrightAdapters.cs #   adapters: IMouse/IKeyboard/ICDPSession → raw
│   │       └── HumanPage.cs          #   ← patch_page flows (human/__init__.py)
│   └── CloakBrowser.Cli/             # ← cloakbrowser/__main__.py
├── examples/CloakBrowser.Examples/   # runnable examples
└── tests/CloakBrowser.Tests/         # xUnit tests
```

---

## Quick start

```csharp
using CloakBrowser;

await using var browser = await CloakLauncher.LaunchAsync(new LaunchOptions
{
    Headless = true,
});

var page = await browser.NewPageAsync();
await page.GotoAsync("https://bot.incolumitas.com/");
Console.WriteLine(await page.TitleAsync());
```

The patched Chromium binary is downloaded automatically on first launch and
cached under `~/.cloakbrowser` (override with `CLOAKBROWSER_CACHE_DIR`).

### Humanized interactions

```csharp
using CloakBrowser;
using CloakBrowser.Human;

await using var browser = await CloakLauncher.LaunchAsync(new LaunchOptions
{
    Headless = false,
    Humanize = true,
    HumanPreset = HumanPreset.Careful,
    HumanConfig = new Dictionary<string, object> { ["typing_delay"] = 90.0 },
});

HumanPage human = await browser.NewHumanPageAsync();
await human.GotoAsync("https://example.com/login");
await human.FillAsync("#username", "alice");
await human.FillAsync("#password", "s3cr3t!");
await human.ClickAsync("button[type=submit]");
```

`HumanPage` exposes `ClickAsync`, `DblClickAsync`, `HoverAsync`, `TypeAsync`,
`FillAsync`, `CheckAsync`, `UncheckAsync`, `SelectOptionAsync`, `PressAsync`,
`MouseMoveAsync`, `MouseClickAsync`, and `KeyboardTypeAsync`. The underlying raw
Playwright page is always available via `human.Page`.

### Context with emulation

```csharp
await using var ctx = await CloakLauncher.LaunchContextAsync(new LaunchContextOptions
{
    Locale = "en-US",
    Timezone = "America/New_York",
    Viewport = (1280, 800),
    ColorScheme = "dark",
});
var page = await ctx.NewPageAsync();
```

> Locale and timezone are applied via **binary flags** (`--lang`,
> `--fingerprint-locale`, `--fingerprint-timezone`) — *not* detectable CDP
> emulation — matching the Python wrapper.

### Persistent profile

```csharp
await using var ctx = await CloakLauncher.LaunchPersistentContextAsync(
    "./my-profile", new LaunchContextOptions { Headless = true });
```

### Proxy + GeoIP + WebRTC

```csharp
await using var browser = await CloakLauncher.LaunchAsync(new LaunchOptions
{
    Proxy = "http://user:pass@proxy.example.com:8080",   // or a ProxySettings
    GeoIp = true,                                         // tz/locale from exit IP
    Args = new List<string> { "--fingerprint-webrtc-ip=auto" },
});
```

SOCKS5 and credentialed HTTP proxies are routed through Chrome's `--proxy-server`
with inline, URL-encoded credentials (matching the Python logic, including the
`linux-x64` / `windows-x64` + binary-version gate for HTTP inline auth).

---

## CLI

```bash
dotnet run --project src/CloakBrowser.Cli -- install      # download the binary
dotnet run --project src/CloakBrowser.Cli -- info         # version / path / platform
dotnet run --project src/CloakBrowser.Cli -- update       # check + download newer
dotnet run --project src/CloakBrowser.Cli -- clear-cache  # remove cached binaries
```

---

## Environment variables

Same set as the Python wrapper:

| Variable | Purpose |
| --- | --- |
| `CLOAKBROWSER_BINARY_PATH` | Use a local binary, skip download |
| `CLOAKBROWSER_CACHE_DIR` | Override the cache directory |
| `CLOAKBROWSER_DOWNLOAD_URL` | Override the download URL |
| `CLOAKBROWSER_AUTO_UPDATE` | Enable/disable background auto-update |
| `CLOAKBROWSER_SKIP_CHECKSUM` | Skip SHA-256 verification |
| `CLOAKBROWSER_GEOIP_TIMEOUT_SECONDS` | GeoIP HTTP timeout |
| `CLOAKBROWSER_WIDEVINE_CDM` / `CLOAKBROWSER_WIDEVINE` | Widevine seeding control |

---

## Building & testing

```bash
cd dotnet
dotnet build CloakBrowser.sln
dotnet test  CloakBrowser.sln
```

The test suite (xUnit) covers the pure-logic ports: version comparison, stealth
args, `build_args` deduplication, proxy URL resolution/encoding, humanize config
presets & overrides, GeoIP private-IP classification, mouse-target math, and the
actionability error hierarchy.

---

## License

MIT — same as the upstream CloakBrowser project.
