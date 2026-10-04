import { defineConfig } from 'vitest/config';

// Real-browser repro suite: one browser per file, run files sequentially,
// generous per-test budget (each test opens pages in a live Chromium).
export default defineConfig({
  test: {
    include: ['tests/humanize-repro/**/*.repro.test.ts'],
    setupFiles: ['./tests/setup.ts'],
    testTimeout: 60_000,
    hookTimeout: 120_000,
    fileParallelism: false,
  },
});
