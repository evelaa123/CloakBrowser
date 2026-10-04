/**
 * Real-browser proofs for the humanize review (JS / Playwright wrapper).
 *
 * control(...) -> stock Playwright reference; a failure means the test is wrong.
 * bug(...)     -> humanized page must match the control; a failure confirms the bug.
 */

import { describe, it, beforeAll, afterAll, afterEach } from 'vitest';
import type { Browser, BrowserContext, Page, Frame } from 'playwright-core';
import { launch } from '../../src/playwright.js';
import { patchContext } from '../../src/human/index.js';
import { resolveConfig } from '../../src/human/config.js';
import {
  ENABLED, FAST, startServer, events, reset, mainWorldHits, value, timed, short, control, bug,
} from './harness.js';

const d = ENABLED ? describe : describe.skip;

d('humanize repro: Playwright (JS)', () => {
  let srv: { url: string; close: () => Promise<void> };
  let browser: Browser;
  let stockCtx: BrowserContext;
  let humanCtx: BrowserContext;
  const extra: BrowserContext[] = [];

  const humanContext = async (overrides: Record<string, unknown> = {}) => {
    const ctx = await browser.newContext();
    patchContext(ctx, resolveConfig('default', { ...FAST, ...overrides } as any));
    extra.push(ctx);
    return ctx;
  };
  const open = async (ctx: BrowserContext, p = 'index.html'): Promise<Page> => {
    const page = await ctx.newPage();
    await page.goto(srv.url + p);
    return page;
  };
  const human = (p?: string) => open(humanCtx, p);
  const stock = (p?: string) => open(stockCtx, p);
  const frameOf = async (page: Page): Promise<Frame> => {
    const f = page.frame({ name: 'f' })!;
    await f.waitForLoadState();
    return f;
  };

  beforeAll(async () => {
    srv = await startServer();
    browser = await launch({ headless: true, humanize: false });
    stockCtx = await browser.newContext();
    humanCtx = await humanContext();
  }, 120_000);

  afterEach(async () => {
    for (const ctx of [stockCtx, humanCtx]) for (const p of ctx.pages()) await p.close().catch(() => {});
  });

  afterAll(async () => {
    await browser?.close();
    await srv?.close();
  });

  // ---------------------------------------------------------------- 1. text input

  it('fill on masked field: correcting Backspace deletes a real character', async () => {
    const s = await stock();
    await s.fill('#hex', 'ab');
    control((await value(s, '#hex')) === 'ab', 'stock fill');
    const page = await open(await humanContext({ mistype_chance: 1 }));
    await page.fill('#hex', 'ab');
    const got = await value(page, '#hex');
    bug(got === 'ab', `fill('#hex','ab') with typos enabled produced ${JSON.stringify(got)}`);
  });

  for (const [sel, val] of [['#date', '2024-01-31'], ['#range', '80'], ['#color', '#ff0000']]) {
    it(`fill non-text input ${sel}`, async () => {
      const s = await stock();
      await s.fill(sel, val);
      control((await value(s, sel)) === val, `stock fill ${sel}`);
      const page = await human();
      const { err } = await timed(() => page.fill(sel, val, { timeout: 5000 }));
      const got = await value(page, sel);
      bug(!err && got === val, `fill(${sel}, ${val}) -> ${JSON.stringify(got)} (${short(err)})`);
    });
  }

  it('ElementHandle.press does not focus the element', async () => {
    const run = async (page: Page) => {
      await page.focus('#fa');
      await (await page.$('#fb'))!.press('x');
      return [await value(page, '#fa'), await value(page, '#fb')];
    };
    control(JSON.stringify(await run(await stock())) === '["","x"]', 'stock handle.press');
    const got = await run(await human());
    bug(JSON.stringify(got) === '["","x"]', `handle('#fb').press('x') -> #fa=${got[0]}, #fb=${got[1]}`);
  });

  it('pressSequentially inserts at the click caret', async () => {
    const text = 'hello world hello world hello';
    const s = await stock();
    await s.evaluate((t) => { (document.querySelector('#long') as HTMLInputElement).value = t; }, text);
    await s.locator('#long').pressSequentially('X');
    const expected = await value(s, '#long');
    control(expected === 'X' + text || expected === text + 'X', `stock ${expected}`);
    const page = await human();
    const results = new Set<string>();
    for (let i = 0; i < 4; i++) {
      await page.evaluate((t) => {
        const e = document.querySelector('#long') as HTMLInputElement; e.value = t; e.blur();
      }, text);
      await page.locator('#long').pressSequentially('X');
      results.add(await value(page, '#long'));
    }
    bug(results.size === 1 && results.has(expected), `expected ${expected}, got ${[...results].join(' | ')}`);
  });

  // ---------------------------------------------------------------- 2. dropped options

  it('click({trial:true}) must not click', async () => {
    const s = await stock();
    await reset(s);
    await s.click('#btn', { trial: true });
    control((await events(s, 'click')).length === 0, 'stock trial');
    const page = await human();
    await reset(page);
    await page.click('#btn', { trial: true } as any);
    const n = (await events(page, 'click')).length;
    bug(n === 0, `click({trial:true}) dispatched ${n} real click(s)`);
  });

  it("click({button:'right'})", async () => {
    const s = await stock();
    await reset(s);
    await s.click('#btn', { button: 'right' });
    control(JSON.stringify((await events(s, 'mousedown')).map((e) => e.button)) === '[2]', 'stock right click');
    const page = await human();
    await reset(page);
    await page.click('#btn', { button: 'right' } as any);
    const buttons = (await events(page, 'mousedown')).map((e) => e.button);
    bug(JSON.stringify(buttons) === '[2]', `click({button:'right'}) -> mousedown buttons ${JSON.stringify(buttons)}`);
  });

  it('click({clickCount:2})', async () => {
    const s = await stock();
    await reset(s);
    await s.click('#btn', { clickCount: 2 });
    control((await events(s, 'dblclick')).length === 1, 'stock clickCount');
    const page = await human();
    await reset(page);
    await page.click('#btn', { clickCount: 2 } as any);
    bug((await events(page, 'dblclick')).length === 1,
      `click({clickCount:2}) -> click x${(await events(page, 'click')).length}, dblclick x${(await events(page, 'dblclick')).length}`);
  });

  it("click({modifiers:['Shift']})", async () => {
    const s = await stock();
    await reset(s);
    await s.click('#btn', { modifiers: ['Shift'] });
    control((await events(s, 'click'))[0].shift === true, 'stock shift click');
    const page = await human();
    await reset(page);
    await page.click('#btn', { modifiers: ['Shift'] } as any);
    const c = (await events(page, 'click'))[0];
    bug(c?.shift === true, `click({modifiers:['Shift']}) -> shiftKey=${c?.shift}`);
  });

  it('page.setDefaultTimeout is ignored', async () => {
    const run = async (page: Page) => {
      page.setDefaultTimeout(1000);
      await page.evaluate(() => setTimeout(() => {
        const b = document.createElement('button'); b.id = 'late'; b.textContent = 'late'; document.body.prepend(b);
      }, 3000));
      return timed(() => page.click('#late'));
    };
    const s = await run(await stock());
    control(!!s.err && s.dur < 2, `stock fails in ${s.dur}s`);
    const h = await run(await human());
    bug(!!h.err && h.dur < 2, `setDefaultTimeout(1000) ignored: click took ${h.dur.toFixed(1)}s (${short(h.err)})`);
  });

  it('timeout: 0 means no timeout', async () => {
    const s = await stock();
    control(!(await timed(() => s.click('#btn', { timeout: 0 }))).err, 'stock timeout 0');
    const page = await human();
    const { err } = await timed(() => page.click('#btn', { timeout: 0 }));
    bug(!err, `click({timeout:0}) -> ${short(err)}`);
  });

  // ---------------------------------------------------------------- 3. strictness

  it('strict mode violation is swallowed', async () => {
    const s = await stock();
    const se = (await timed(() => s.locator('.dup').click({ timeout: 3000 }))).err;
    control(!!se && /strict mode violation/.test(se.message), `stock: ${short(se)}`);
    const page = await human();
    await reset(page);
    const { err } = await timed(() => page.locator('.dup').click({ timeout: 3000 }));
    const hit = (await events(page, 'click')).map((e) => e.target);
    bug(!!err, `ambiguous locator('.dup').click() silently clicked ${JSON.stringify(hit)}`);
  });

  // ---------------------------------------------------------------- 4. selectors (#522)

  const UNSUPPORTED: Record<string, (p: Page) => Promise<unknown>> = {
    getByRole: (p) => p.getByRole('button', { name: 'Press me' }).click({ timeout: 3000 }),
    chained: (p) => p.locator('#buttons').locator('#btn').click({ timeout: 3000 }),
    filter: (p) => p.locator('button').filter({ hasText: 'Press me' }).click({ timeout: 3000 }),
    visiblePseudo: (p) => p.click('#btn:visible', { timeout: 3000 }),
    frameLocator: (p) => p.frameLocator('#frame').locator('#finput').click({ timeout: 3000 }),
  };
  for (const [name, fn] of Object.entries(UNSUPPORTED)) {
    it(`standard locator rejected: ${name}`, async () => {
      const s = await stock();
      const se = (await timed(() => fn(s))).err;
      control(!se, `stock ${name}: ${short(se)}`);
      const page = await human();
      const { err } = await timed(() => fn(page));
      bug(!err, `${name} -> ${short(err)}`);
    });
  }

  // ---------------------------------------------------------------- 5. frames / handles

  it('baseline: main-page actions read nothing in the main world (expected PASS)', async () => {
    const page = await human();
    await reset(page);
    await page.click('#btn');
    await page.fill('#name', 'x');
    await page.press('#name', 'a');
    bug((await mainWorldHits(page)).length === 0, JSON.stringify(await mainWorldHits(page)));
  });

  it('frame actions read the DOM in the main world', async () => {
    const page = await human();
    const f = await frameOf(page);
    await reset(f);
    await f.click('#finput');
    await f.press('#finput', 'a');
    const hits = [...new Set((await mainWorldHits(f)).map((h) => h.probe))].sort();
    bug(hits.length === 0, `frame.click/press made page-observable reads: ${JSON.stringify(hits)}`);
  });

  it('frame click on a covered button succeeds (no pointer-events check)', async () => {
    const s = await stock();
    control(!!(await timed(() => s.frame({ name: 'f' })!.click('#fcovered', { timeout: 2000 }))).err, 'stock refuses');
    const page = await human();
    control(!!(await timed(() => page.click('#covered', { timeout: 2000 }))).err, 'humanized main page refuses');
    const f = await frameOf(page);
    await reset(f);
    const { err } = await timed(() => f.click('#fcovered', { timeout: 2000 }));
    const hit = (await events(f, 'click')).map((e) => e.target);
    bug(!!err, `frame.click on covered button succeeded; click landed on ${JSON.stringify(hit)}`);
  });

  it('frame click below the frame fold silently misses', async () => {
    const s = await stock();
    const sf = await frameOf(s);
    await reset(sf);
    const se = (await timed(() => sf.click('#fbottom', { timeout: 5000 }))).err;
    control(!se && (await events(sf, 'click')).map((e) => e.target).join() === 'fbottom', `stock: ${short(se)}`);
    const page = await human();
    const f = await frameOf(page);
    await reset(f); await reset(page);
    const { err } = await timed(() => f.click('#fbottom', { timeout: 5000 }));
    const clicks = (await events(f, 'click')).map((e) => e.target);
    const downs = (await events(page, 'mousedown')).map((e) => [e.target, Math.round(e.y)]);
    bug(!!err || clicks.join() === 'fbottom',
      `frame.click('#fbottom') returned OK; frame clicks ${JSON.stringify(clicks)}, mouse down at ${JSON.stringify(downs)}`);
  });

  // ---------------------------------------------------------------- 6. behaviour

  it('locator.check() teleports the cursor to the page origin (idle_between_actions)', async () => {
    const page = await open(await humanContext({
      idle_between_actions: true, idle_between_duration: [0.3, 0.3], idle_pause_range: [20, 20],
    }));
    await page.mouse.move(500, 300);
    await reset(page);
    await page.locator('#chk').check();
    const near = (await events(page, 'mousemove')).filter((e) => e.x < 10 && e.y < 10).map((e) => [e.x, e.y]);
    bug(near.length === 0, `locator.check() moved the cursor to ${JSON.stringify(near.slice(0, 5))}`);
  });

  it('dblclick event sequence', async () => {
    const seq = async (page: Page) => {
      await reset(page);
      await page.dblclick('#btn');
      return (await events(page, 'mousedown', 'click', 'dblclick')).map((e) => `${e.t}:${e.detail}`).join(' ');
    };
    const expected = 'mousedown:1 click:1 mousedown:2 click:2 dblclick:2';
    control((await seq(await stock())) === expected, 'stock sequence');
    const got = await seq(await human());
    bug(got === expected, `dblclick sequence "${got}", real double click is "${expected}"`);
  });

  it('uppercase typo is typed without Shift', async () => {
    const page = await open(await humanContext({ mistype_chance: 1 }));
    await page.fill('#name', '');
    await reset(page);
    await page.locator('#name').pressSequentially('AB');
    const bad = (await events(page, 'keydown'))
      .filter((e) => e.key.length === 1 && /[A-Z]/.test(e.key) && !e.shift).map((e) => e.key);
    bug(bad.length === 0, `uppercase keydowns with shiftKey=false: ${JSON.stringify(bad)}`);
  });

  // ---------------------------------------------------------------- 7. timeouts (#329)

  it('unreachable element: misleading "covered by <none>"', async () => {
    const s = await stock();
    control(!(await timed(() => s.fill('#neg', 'hello', { timeout: 3000 }))).err, 'stock fills off-viewport input');
    const page = await human();
    const { dur, err } = await timed(() => page.fill('#neg', 'hello', { timeout: 3000 }));
    bug(!err || (dur < 4 && !short(err).includes('covered by <none>')),
      `fill(timeout 3000) on element at (-600,-600) took ${dur.toFixed(1)}s -> ${short(err)}`);
  });
});
