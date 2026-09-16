import { expect, test } from '@playwright/test';

/**
 * The public site. Its job here is the promise it makes: every platform is presented equally, and a
 * platform without a connector must not claim one — the pages are what a prospect reads before
 * anyone from the MSP speaks to them.
 */
test.describe('public site', () => {
  test('the home page loads and offers the way in', async ({ page }) => {
    await page.goto('/');

    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Sign in' }).first()).toBeVisible();
    await expect(page.getByRole('navigation').first().getByRole('link', { name: 'Integrations' })).toBeVisible();
  });

  test('every platform is listed, and none is labelled by readiness', async ({ page }) => {
    await page.goto('/integrations');

    const main = page.locator('main');
    // By the link each tile carries rather than by its text: the same names also sit in the ecosystem
    // diagram and in a mobile-only list, so the assertion keeps only what this viewport really shows.
    for (const id of ['connectwise', 'autotask', 'halo', 'kaseya-bms', 'syncro', 'superops', 'n-able', 'atera']) {
      await expect(main.locator(`a[href="/integrations/${id}"]`).filter({ visible: true }).first()).toBeVisible();
    }
    // No status language anywhere: no badges, no "coming soon", no launch dates (#26).
    await expect(main).not.toContainText(/coming soon|beta|planned|in development|not yet available/i);
  });

  test('a platform without a connector invites a conversation instead of promising a setup', async ({ page }) => {
    await page.goto('/integrations/halo');

    await expect(page.getByRole('heading', { name: /Desk Portal for HaloPSA/ })).toBeVisible();
    await expect(page.getByRole('link', { name: /Talk to us about HaloPSA/ }).first()).toBeVisible();
    // The step-by-step "connect your environment" wizard belongs only to platforms that have one.
    await expect(page.locator('main')).not.toContainText('Connect Desk Portal to your HaloPSA environment');
  });

  test('a platform with a connector still offers the demo and the steps', async ({ page }) => {
    await page.goto('/integrations/autotask');

    await expect(page.getByRole('link', { name: /Book a demo/ }).first()).toBeVisible();
    await expect(page.locator('main')).toContainText('Connect Desk Portal to your Autotask PSA environment');
  });
});
