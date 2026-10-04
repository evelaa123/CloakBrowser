/**
 * Real-browser proofs for the humanize review (JS / Puppeteer wrapper).
 *
 * Two CloakBrowser processes: humanize:false (stock Puppeteer reference) and
 * humanize:true. control(...) checks the reference, bug(...) the humanized one.
 */

import { describe, it, beforeAll, afterAll, afterEach } from 'vitest';
import type { Browser, Page } from 'puppeteer-core';
import { launch } from '../../src/puppeteer.js';
import {
  ENABLED, FAST, startServer, events, reset, mainWorldHits, value, timed, short, control, bug,
} from './harness.js';

const d = ENABLED ? describe : describe.skip;

d('humanize repro: Puppeteer (JS)', () => {
  let srv: { url: string; close: () => Promise<void> };
  let stockB: Browser;
  let humanB: Browser;
  const pages: Page[] = [];

  const open = async (b: Browser, p = 'index.html'): Promise<Page> => {
    const page = await b.newPage();
    pages.push(page);
    await page.setViewport({ width: 1280, height: 720 });
    await page.goto(srv.url + p);
    return page;
  };
  const stock = (p?: string) => open(stockB, p);
  const human = (p?: string) => open(humanB, p);

  beforeAll(async () => {
    srv = await startServer();
    stockB = await launch({ headless: true, humanize: false });
    humanB = await launch({ headless: true, humanize: true, humanConfig: FAST as any });
  }, 120_000);

  afterEach(async () => {
    for (const p of pages.splice(0)) await p.close().catch(() => {});
  });

  afterAll(async () => {
    await stockB?.close();
    await humanB?.close();
    await srv?.close();
  });

  it('page.click / page.type stay out of the main world (expected PASS; refutes review claim)', async () => {
    const page = await human();
    await reset(page);
    await page.click('#btn');
    await page.type('#name', 'x');
    const hits = [...new Set((await mainWorldHits(page)).map((h) => h.probe))].sort();
    bug(hits.length === 0, `page.click/type made page-observable reads: ${JSON.stringify(hits)}`);
  });

  it("click({button:'right'})", async () => {
    const s = await stock();
    await reset(s);
    await s.click('#btn', { button: 'right' });
    control(JSON.stringify((await events(s, 'mousedown')).map((e) => e.button)) === '[2]', 'stock right click');
    const page = await human();
    await reset(page);
    await page.click('#btn', { button: 'right' });
    const buttons = (await events(page, 'mousedown')).map((e) => e.button);
    bug(JSON.stringify(buttons) === '[2]', `click({button:'right'}) -> mousedown buttons ${JSON.stringify(buttons)}`);
  });

  it('click(timeout) for a missing element honours the timeout', async () => {
    const page = await human();
    const { dur, err } = await timed(() => (page as any).click('#missing', { timeout: 1500 }));
    bug(!!err && dur < 3, `click('#missing', {timeout:1500}) -> ${dur.toFixed(1)}s, ${short(err)}`);
  });

  it('covered / disabled click: parity with stock Puppeteer (expected PASS)', async () => {
    // Stock Puppeteer has no actionability checks either, so clicking an overlay
    // or a disabled button is NOT a divergence here. Recorded for completeness:
    // the Puppeteer port is the only wrapper without pre-click checks.
    const run = async (page: Page) => {
      await reset(page);
      await page.click('#covered');
      const covered = (await events(page, 'click')).map((e) => e.target).join();
      await page.evaluate(() => { (document.querySelector('#btn') as HTMLButtonElement).disabled = true; });
      const { err } = await timed(() => page.click('#btn'));
      return `${covered}|${err ? 'refused' : 'accepted'}`;
    };
    const expected = await run(await stock());
    control(expected === 'over|accepted', `stock Puppeteer: ${expected}`);
    const got = await run(await human());
    bug(got === expected, `humanized ${got}, stock ${expected}`);
  });

  it('strictness: page.click(".dup") hits the first match silently', async () => {
    const page = await human();
    await reset(page);
    await page.click('.dup');
    const hit = (await events(page, 'click')).map((e) => e.target);
    // Puppeteer itself is not strict; recorded for parity with the other wrappers.
    control(hit.length === 1, `clicked ${JSON.stringify(hit)}`);
  });

  it('type: uppercase typo is typed without Shift', async () => {
    const b = await launch({ headless: true, humanize: true, humanConfig: { ...FAST, mistype_chance: 1 } as any });
    try {
      const page = await b.newPage();
      await page.goto(srv.url + 'index.html');
      await page.evaluate(() => { (document.querySelector('#name') as HTMLInputElement).value = ''; });
      await reset(page);
      await page.type('#name', 'AB');
      const bad = (await events(page, 'keydown'))
        .filter((e) => e.key.length === 1 && /[A-Z]/.test(e.key) && !e.shift).map((e) => e.key);
      bug(bad.length === 0, `uppercase keydowns with shiftKey=false: ${JSON.stringify(bad)}`);
    } finally {
      await b.close();
    }
  });

  it('frame.click delegates to page.click and never resolves inside the frame (#184)', async () => {
    // human-puppeteer/index.ts patchSingleFrame: frame.click -> page.click(selector),
    // so '#finput' is looked up in the *top* document, where it does not exist.
    const s = await stock();
    const sf = s.frames().find((x) => x.name() === 'f')!;
    await sf.waitForSelector('#finput');
    control(!(await timed(() => sf.click('#finput'))).err, 'stock frame.click works');

    const page = await human();
    const f = page.frames().find((x) => x.name() === 'f')!;
    await f.waitForSelector('#finput');
    await reset(f);
    const { dur, err } = await timed(() => Promise.race([
      f.click('#finput', { timeout: 3000 } as any),
      new Promise((_, rej) => setTimeout(() => rej(new Error('HANG: no result after 8s')), 8000)),
    ]));
    const clicks = (await events(f, 'click')).map((e) => e.target);
    bug(!err && clicks.join() === 'finput',
      `frame.click('#finput') -> ${short(err)} after ${dur.toFixed(1)}s, frame clicks ${JSON.stringify(clicks)}`);
  }, 30_000);
});
