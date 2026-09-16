import { defineConfig, devices } from '@playwright/test';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

/**
 * Browser tests against the real app: the Next.js server talking to the real API over HTTP.
 *
 * The API runs in LOCAL MODE — a throwaway SQLite file and a signed-in dev administrator — so the
 * suite needs neither Keycloak nor Postgres nor a PSA. A fresh directory per run means every test
 * starts from the same empty desk, and nothing a test creates can leak into the next run.
 *
 * Ports are deliberately not the ones a developer runs by hand (5400/3400), so a suite started
 * while you are working does not talk to your server or take your port.
 */
const API_PORT = 5411;
const WEB_PORT = 3411;
const dataDir = process.env.DESK_E2E_DATA ?? mkdtempSync(join(tmpdir(), 'desk-e2e-'));

export default defineConfig({
  testDir: './e2e',
  // One worker: the servers hold one SQLite file, and tests that create schedules would otherwise
  // see each other's rows.
  workers: 1,
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: `http://localhost:${WEB_PORT}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'dotnet run --project apps/api --no-launch-profile',
      cwd: '../..',
      port: API_PORT,
      timeout: 240_000,
      reuseExistingServer: !process.env.CI,
      stdout: 'pipe',
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: `http://localhost:${API_PORT}`,
        LocalMode__Enabled: 'true',
        LocalMode__SqlitePath: join(dataDir, 'e2e.db'),
        LocalMode__AttachmentsPath: join(dataDir, 'files'),
        LocalMode__SecretsPath: join(dataDir, 'secrets.json'),
      },
    },
    {
      command: `npx next dev --port ${WEB_PORT}`,
      port: WEB_PORT,
      timeout: 240_000,
      reuseExistingServer: !process.env.CI,
      stdout: 'pipe',
      env: { DESK_API_BASE: `http://localhost:${API_PORT}` },
    },
  ],
});
