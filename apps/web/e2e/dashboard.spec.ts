import { expect, test } from '@playwright/test';

/**
 * The pages a technician or manager opens every day. A fresh local desk holds no tickets, so these
 * assert the frame, the navigation and the empty states — the things that break when a query, a
 * permission or a route changes, and that no unit test can see.
 */
test.describe('dashboard', () => {
  test('the overview opens with its navigation and the signed-in user', async ({ page }) => {
    await page.goto('/dashboard');

    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    // Every section a manager needs is reachable from the menu.
    for (const label of ['Tickets', 'Productivity', 'Technician hours', 'Scheduled reports', 'Integration Health']) {
      await expect(page.getByRole('link', { name: label, exact: true })).toBeVisible();
    }
    // Nothing rendered an error card in place of the page.
    await expect(page.getByText(/could not load|something went wrong/i)).toHaveCount(0);
  });

  test('technician hours offers calendar periods and states its range', async ({ page }) => {
    await page.goto('/dashboard/analytics/technicians');
    const range = page.getByLabel('Date range');
    await expect(range).toBeVisible();

    await expect(range.locator('option')).toContainText([
      'Today', 'Yesterday', 'Last 7 days', 'Last 30 days', 'This month', 'Last month',
      'This quarter', 'Last quarter', 'Last 90 days', 'Custom range…',
    ]);

    // The subtitle says which dates are covered, and changing the period changes it.
    const subtitle = page.locator('h1 + p');
    const before = await subtitle.innerText();
    await range.selectOption('last-month');
    await expect(subtitle).not.toHaveText(before);
    // Empty state, said once per panel and once in the table.
    await expect(page.getByText('No time logged and nothing resolved in this range.').first()).toBeVisible();
  });

  test('a client portal page tells a staff account this is not for them', async ({ page }) => {
    // The dev administrator is MSP staff, not a client user: every Control Panel endpoint refuses
    // them, and the shell must say so once instead of failing page by page.
    await page.goto('/control-panel/users');

    await expect(page.getByRole('heading', { name: 'The Control Panel is for your clients' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Back to the dashboard' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Ticket Instructions' })).toHaveCount(0);
  });
});
