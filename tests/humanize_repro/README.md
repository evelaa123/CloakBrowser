# Humanize repro suite (real browser)

Proofs for the humanize-layer review. Every test drives the **real CloakBrowser
binary** against the static fixture pages in `site/` and compares the humanized
page with a **stock** page from the same browser (Playwright reference, or stock
Puppeteer for the Puppeteer port).

* `control(...)` / `Control(...)` - asserts the stock reference behaves as the
  test assumes. A `CONTROL FAILED` means the test itself is wrong.
* `bug(...)` / `Bug(...)` - asserts the humanized page matches the reference.
  A failure starting with `BUG:` **confirms** the reported problem.
* Tests marked *expected PASS* are baselines or claims that were checked and
  refuted; they stay as regression guards.

The suites are opt-in, so the regular CI run is unaffected.

## Fixture site

| File | Purpose |
|---|---|
| `site/index.html` | text / non-text inputs, masked field, focus targets, buttons, duplicates, covered button, off-viewport input, iframe |
| `site/frame.html` | iframe content: input, covered button, target below the frame fold |
| `site/recorder.js` | records every input event (trusted flag, coords, button, detail, modifiers, key, inputType) into `window.__log`; live panel when opened by hand |
| `site/detect.html` | page that records every automation trace a site script can see |
| `site/hooks.js` | main-world probes (`getAttribute('contenteditable')`, `document.activeElement`, `elementFromPoint`, `document.querySelector`) into `window.__mainWorldHits` |

Open it by hand: `python -m http.server -d tests/humanize_repro/site 8000`.

## Run

```bash
# Python (sync wrapper)
CLOAKBROWSER_HUMANIZE_REPRO=1 pytest tests/humanize_repro -v -rA

# JS (Playwright + Puppeteer)
cd js && CLOAKBROWSER_HUMANIZE_REPRO=1 npx vitest run -c tests/humanize-repro/vitest.config.ts

# .NET
CLOAKBROWSER_HUMANIZE_REPRO=1 dotnet test dotnet/tests/CloakBrowser.Tests -c Release \
  --filter "FullyQualifiedName~HumanizeRepro"
```

Note for sandboxes where `/tmp` is a small tmpfs: export `TMPDIR` to a disk path,
Chromium profiles fill it quickly.

## Detection evidence (`test_repro_detection.py`)

Explains *why* the humanize layer resolves elements in its own isolated world
instead of asking Playwright. Measured with a page script only:

* every Playwright lookup that returns geometry or runs checks
  (`bounding_box`, `wait_for`, `evaluate`, `query_selector`, `scroll_into_view_if_needed`,
  `get_by_role`, `is_enabled`, `click(trial=True)`, `click`, `focus`) is visible to
  the page: Playwright installs listeners in the page's main world (the stack names
  `addHitTargetInterceptorListeners`) and/or fires `__playwright_mark_target__`
  events;
* the humanized selector pipeline (`page.click/fill/check`, `locator.click/press`)
  leaves no trace;
* humanized `ElementHandle` and frame paths do leave the trace (BUG);
* an isolated world inside an iframe, and resolving a node into an isolated world
  by `backendNodeId`, both work without any trace - this is how those two leaks can
  be fixed.
