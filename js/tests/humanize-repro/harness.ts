/**
 * Shared harness for the real-browser humanize repro suite (JS).
 *
 * Serves the same fixture pages as the Python suite (tests/humanize_repro/site)
 * and launches the real CloakBrowser binary once per file with a humanized
 * context and a stock Playwright context side by side.
 *
 * Run:  CLOAKBROWSER_HUMANIZE_REPRO=1 npx vitest run -c tests/humanize-repro/vitest.config.ts
 */

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { AddressInfo } from 'node:net';
import { expect } from 'vitest';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const SITE_DIR = path.resolve(HERE, '../../../tests/humanize_repro/site');
export const ENABLED = process.env.CLOAKBROWSER_HUMANIZE_REPRO === '1';

/** Fast, deterministic humanize settings (same code paths, no long sleeps). */
export const FAST = {
  mistype_chance: 0,
  field_switch_delay: [0, 0] as [number, number],
  typing_delay: 5,
  typing_delay_spread: 0,
  typing_pause_chance: 0,
  key_hold: [5, 5] as [number, number],
  shift_down_delay: [5, 5] as [number, number],
  shift_up_delay: [5, 5] as [number, number],
  mouse_min_steps: 4,
  mouse_max_steps: 4,
  mouse_burst_pause: [0, 0] as [number, number],
  mouse_overshoot_chance: 0,
  click_aim_delay_input: [5, 5] as [number, number],
  click_aim_delay_button: [5, 5] as [number, number],
  click_hold_input: [20, 20] as [number, number],
  click_hold_button: [20, 20] as [number, number],
  idle_between_actions: false,
  scroll_pause_fast: [5, 5] as [number, number],
  scroll_pause_slow: [5, 5] as [number, number],
  scroll_settle_delay: [50, 50] as [number, number],
  scroll_pre_move_delay: [5, 5] as [number, number],
  scroll_overshoot_chance: 0,
};

const MIME: Record<string, string> = { '.html': 'text/html', '.js': 'text/javascript' };

export async function startServer(): Promise<{ url: string; close: () => Promise<void> }> {
  const srv = http.createServer((req, res) => {
    const rel = decodeURIComponent((req.url ?? '/').split('?')[0]).replace(/^\/+/, '') || 'index.html';
    const file = path.join(SITE_DIR, rel);
    if (!file.startsWith(SITE_DIR) || !fs.existsSync(file)) { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { 'content-type': MIME[path.extname(file)] ?? 'application/octet-stream' });
    fs.createReadStream(file).pipe(res);
  });
  await new Promise<void>((r) => srv.listen(0, '127.0.0.1', r));
  const { port } = srv.address() as AddressInfo;
  return { url: `http://127.0.0.1:${port}/`, close: () => new Promise((r) => srv.close(() => r())) };
}

/** Events recorded by site/recorder.js, optionally filtered by type. */
export async function events(target: any, ...types: string[]): Promise<any[]> {
  const log: any[] = await target.evaluate('window.__log.slice()');
  return types.length ? log.filter((e) => types.includes(e.t)) : log;
}

export async function reset(target: any): Promise<void> {
  await target.evaluate('window.__reset(); window.__mainWorldHits.length = 0');
}

export async function mainWorldHits(target: any): Promise<any[]> {
  return target.evaluate('window.__mainWorldHits.slice()');
}

export async function value(target: any, selector: string): Promise<string> {
  return target.evaluate(
    (s: string) => { const e: any = document.querySelector(s); return e.isContentEditable ? e.textContent : e.value; },
    selector,
  );
}

export async function timed(fn: () => Promise<unknown>): Promise<{ dur: number; err: any }> {
  const t0 = Date.now();
  try { await fn(); return { dur: (Date.now() - t0) / 1000, err: null }; }
  catch (err) { return { dur: (Date.now() - t0) / 1000, err }; }
}

export const short = (err: any): string =>
  err ? `${err.constructor?.name ?? 'Error'}: ${String(err.message ?? err).split('\n')[0].slice(0, 160)}` : 'no exception';

/** Control assertion: the stock Playwright reference must behave as assumed. */
export function control(cond: boolean, msg: string): void {
  expect(cond, `CONTROL FAILED (test is invalid): ${msg}`).toBe(true);
}

/** Bug assertion: the humanized page must match the control. */
export function bug(cond: boolean, msg: string): void {
  expect(cond, `BUG: ${msg}`).toBe(true);
}
