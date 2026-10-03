import { defineConfig } from '@playwright/test';

/**
 * Unit tests for the web app's pure modules (the scheduler's timeline arithmetic, for one), run by
 * the Playwright test runner in Node: no browser, no server, full time-zone tables. A test here
 * never takes the `page` fixture.
 */
export default defineConfig({
  testDir: './src',
  testMatch: '**/*.unit.ts',
  timeout: 20_000,
  fullyParallel: true,
  reporter: process.env.CI ? [['github'], ['list']] : 'list',
});
