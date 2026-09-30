import { test, expect } from '@playwright/test';

/**
 * Credit goes to whoever does the work in the portal: one click takes a ticket, and resolving it
 * records who did - whatever the PSA's own assignee says.
 */
test('take a ticket in one click, resolve it, and it is credited to you', async ({ page }) => {
  const key = `C${Date.now().toString().slice(-6)}`.slice(0, 8).toUpperCase();

  await page.goto('/dashboard/boards');
  await page.getByRole('button', { name: 'New board' }).click();
  const form = page.locator('form').filter({ has: page.getByLabel('Board name') });
  await form.getByLabel('Board name').fill(`Credit ${key}`);
  await form.getByLabel('Ticket prefix').fill(key);
  await form.getByRole('button', { name: 'Create board' }).click();
  await page.locator('li').filter({ hasText: `Credit ${key}` }).getByRole('link', { name: 'Open board' }).click();

  await page.getByRole('button', { name: 'New ticket' }).click();
  const raise = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
  await raise.getByLabel('What needs doing').fill('Outlook not opening for reception');
  await raise.getByRole('button', { name: 'Raise ticket' }).click();
  await page.locator('tr').filter({ hasText: 'Outlook not opening for reception' })
    .getByRole('link', { name: 'Outlook not opening for reception' }).click();

  const me = await page.request.get('/api/bff/api/me').then((r) => r.json()) as { displayName: string };
  const workingIt = page.locator('dt', { hasText: /^Working it$/ }).locator('xpath=following-sibling::dd[1]');
  await expect(workingIt).toHaveText('Unclaimed');

  await page.getByRole('button', { name: 'Take it' }).click();
  await expect(workingIt).toHaveText(me.displayName);
  // Held by you: nothing left to take.
  await expect(page.getByRole('button', { name: 'Take it' })).toHaveCount(0);

  await page.getByLabel('Ticket status').selectOption('RESOLVED');
  await page.getByRole('form', { name: 'Resolution' }).getByRole('button', { name: 'Mark resolved' }).click();
  const resolvedBy = page.locator('dt', { hasText: /^Resolved by$/ }).locator('xpath=following-sibling::dd[1]');
  await expect(resolvedBy).toHaveText(me.displayName);

  await page.goto('/dashboard/analytics/technicians');
  await expect(page.getByText(/Credit goes to whoever does the work in the portal/)).toBeVisible();
});
