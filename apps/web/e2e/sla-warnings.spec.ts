import { test, expect } from '@playwright/test';

/**
 * SLA trouble surfaces before it becomes a breach: a ticket due within hours appears on the
 * Overview banner, and the banner's link opens the Due soon view with that ticket in it.
 * Other specs share the database, so the assertions are about this run's ticket, not totals.
 */
test.describe('SLA warnings', () => {
  test('a ticket due soon is flagged on the overview and listed under Due soon', async ({ page }) => {
    const run = Date.now().toString().slice(-6);

    await page.goto('/dashboard/boards/sla');
    await page.getByRole('button', { name: 'New plan' }).click();
    await page.getByLabel('Plan name').fill(`Fast ${run}`);
    await page.getByLabel('First reply within (hours)').fill('1');
    await page.getByLabel('Resolved within (hours)').fill('4');
    await page.getByRole('button', { name: 'Add plan' }).click();
    await expect(page.locator('tr').filter({ hasText: `Fast ${run}` })).toBeVisible();

    const key = `W${run}`.slice(0, 8);
    await page.goto('/dashboard/boards');
    await page.getByRole('button', { name: 'New board' }).click();
    const form = page.locator('form').filter({ has: page.getByLabel('Board name') });
    await form.getByLabel('Board name').fill(`Warnings ${run}`);
    await form.getByLabel('Ticket prefix').fill(key);
    await form.getByLabel('SLA plan').selectOption({ label: `Fast ${run}` });
    await form.getByRole('button', { name: 'Create board' }).click();
    await page.locator('li').filter({ hasText: `Warnings ${run}` }).getByRole('link', { name: 'Open board' }).click();
    await page.getByRole('button', { name: 'New ticket' }).click();
    const raise = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
    await raise.getByLabel('What needs doing').fill(`Replace the failing PSU ${run}`);
    await raise.getByRole('button', { name: 'Raise ticket' }).click();
    await expect(page.locator('tr').filter({ hasText: `Replace the failing PSU ${run}` })).toBeVisible();

    await page.goto('/dashboard');
    const banner = page.getByRole('status').filter({ hasText: 'due within 8 hours' });
    await expect(banner).toBeVisible();
    await banner.getByRole('link', { name: /due within 8 hours/ }).click();

    await expect(page).toHaveURL(/due=soon/);
    await expect(page.getByRole('button', { name: 'Due soon', exact: true })).toHaveClass(/bg-brand/);
    await expect(page.getByRole('link', { name: `Replace the failing PSU ${run}` })).toBeVisible();
  });
});
