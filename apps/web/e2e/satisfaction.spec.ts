import { test, expect } from '@playwright/test';

/**
 * The satisfaction page opens from the sidebar and says plainly when there is nothing to show.
 * Rating itself is a client's act on a resolved PSA ticket; local mode signs in as an administrator
 * and holds no PSA tickets, so that path is covered by the service tests rather than a browser.
 */
test.describe('customer satisfaction', () => {
  test('the page is reachable and explains an empty period', async ({ page }) => {
    await page.goto('/dashboard');
    await page.getByRole('link', { name: 'Satisfaction' }).click();
    await expect(page.getByRole('heading', { name: 'Customer satisfaction' })).toBeVisible();
    await page.getByLabel('Date range').selectOption('last-quarter');
    await expect(page.getByText(/No ratings in this period/)).toBeVisible();
  });
});
