using CloakBrowser.Human;
using Microsoft.Playwright;

namespace CloakBrowser;

/// <summary>
/// A launched stealth browser. Owns the underlying Playwright instance and disposes
/// it on <see cref="CloseAsync"/>. Use <see cref="Browser"/> for the raw Playwright API.
/// </summary>
public sealed class CloakBrowserHandle : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly bool _humanize;
    private readonly HumanConfig? _humanCfg;

    /// <summary>The underlying Playwright browser.</summary>
    public IBrowser Browser { get; }

    /// <summary>The owning Playwright instance (used internally to transfer ownership to a context handle).</summary>
    internal IPlaywright PlaywrightInstance => _playwright;

    internal CloakBrowserHandle(IPlaywright playwright, IBrowser browser, bool humanize, HumanConfig? humanCfg)
    {
        _playwright = playwright;
        Browser = browser;
        _humanize = humanize;
        _humanCfg = humanCfg;
    }

    /// <summary>Create a new page (raw Playwright <see cref="IPage"/>).</summary>
    public Task<IPage> NewPageAsync(BrowserNewPageOptions? options = null) => Browser.NewPageAsync(options);

    /// <summary>Create a new page wrapped in a <see cref="HumanPage"/> with this browser's humanize config.</summary>
    public async Task<HumanPage> NewHumanPageAsync(BrowserNewPageOptions? options = null)
    {
        var page = await Browser.NewPageAsync(options).ConfigureAwait(false);
        return await HumanPage.CreateAsync(page, _humanCfg ?? new HumanConfig()).ConfigureAwait(false);
    }

    /// <summary>Whether the humanize layer is enabled for this browser.</summary>
    public bool HumanizeEnabled => _humanize;

    /// <summary>Close the browser and stop the underlying Playwright instance.</summary>
    public async Task CloseAsync()
    {
        try { await Browser.CloseAsync().ConfigureAwait(false); }
        finally { _playwright.Dispose(); }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}

/// <summary>
/// A launched stealth browser context. Owns the underlying Playwright instance (and,
/// for non-persistent contexts, the browser) and cleans them up on <see cref="CloseAsync"/>.
/// </summary>
public sealed class CloakContextHandle : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser? _browser; // null for persistent contexts
    private readonly bool _humanize;
    private readonly HumanConfig? _humanCfg;

    /// <summary>The underlying Playwright browser context.</summary>
    public IBrowserContext Context { get; }

    internal CloakContextHandle(IPlaywright playwright, IBrowser? browser, IBrowserContext context,
        bool humanize, HumanConfig? humanCfg)
    {
        _playwright = playwright;
        _browser = browser;
        Context = context;
        _humanize = humanize;
        _humanCfg = humanCfg;
    }

    /// <summary>Create a new page (raw Playwright <see cref="IPage"/>).</summary>
    public Task<IPage> NewPageAsync() => Context.NewPageAsync();

    /// <summary>Create a new page wrapped in a <see cref="HumanPage"/> with this context's humanize config.</summary>
    public async Task<HumanPage> NewHumanPageAsync()
    {
        var page = await Context.NewPageAsync().ConfigureAwait(false);
        return await HumanPage.CreateAsync(page, _humanCfg ?? new HumanConfig()).ConfigureAwait(false);
    }

    /// <summary>Whether the humanize layer is enabled for this context.</summary>
    public bool HumanizeEnabled => _humanize;

    /// <summary>Close the context (and browser, if owned) and stop Playwright.</summary>
    public async Task CloseAsync()
    {
        try
        {
            await Context.CloseAsync().ConfigureAwait(false);
            if (_browser != null)
                await _browser.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _playwright.Dispose();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
