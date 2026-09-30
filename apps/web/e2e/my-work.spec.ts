import { test, expect } from '@playwright/test';

/**
 * The working day: take an unclaimed ticket from a board, move it along from the row, find it in My
 * work with everything else that is mine, see it counted against me on Team workload, and find the
 * board's tickets in the paged list by where they came from.
 */
test.describe('my work and the board workspace', () => {
  test('take a ticket, move it from the row, and find it in My work, the workload and the list', async ({ page }) => {
    const key = `W${Date.now().toString().slice(-6)}`.slice(0, 8).toUpperCase();

    await page.goto('/dashboard/boards');
    await page.getByRole('button', { name: 'New board' }).click();
    const form = page.locator('form').filter({ has: page.getByLabel('Board name') });
    await form.getByLabel('Board name').fill(`Workspace ${key}`);
    await form.getByLabel('Ticket prefix').fill(key);
    await form.getByRole('button', { name: 'Create board' }).click();
    await page.locator('li').filter({ hasText: `Workspace ${key}` }).getByRole('link', { name: 'Open board' }).click();

    for (const title of [`Replace toner ${key}`, `Reset VPN token ${key}`]) {
      await page.getByRole('button', { name: 'New ticket' }).click();
      const raise = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
      await raise.getByLabel('What needs doing').fill(title);
      await raise.getByRole('button', { name: 'Raise ticket' }).click();
      await expect(page.locator('tr').filter({ hasText: title })).toContainText('Unclaimed');
    }

    // Taken from the row, and moved along from the row.
    await page.getByRole('button', { name: `Take ${key}-000001` }).click();
    const taken = page.locator('tr').filter({ hasText: `Replace toner ${key}` });
    await expect(taken).toContainText('Demo Admin');
    await taken.getByLabel(`Status of ${key}-000001`).selectOption('IN_PROGRESS');
    await expect(taken.getByLabel(`Status of ${key}-000001`)).toHaveValue('IN_PROGRESS');
    // Only mine, by who holds it.
    await page.getByLabel('Only mine').check();
    await expect(page.locator('tr').filter({ hasText: `Reset VPN token ${key}` })).toHaveCount(0);
    await expect(taken).toBeVisible();

    // My work: mine, from every source; the other ticket is nobody's yet.
    await page.goto('/dashboard/my-work');
    const mine = page.getByRole('region', { name: /My open work/ });
    await expect(mine).toContainText(`Replace toner ${key}`);
    await expect(mine).not.toContainText(`Reset VPN token ${key}`);

    // Team workload counts it against me, and opens exactly my open tickets.
    await page.goto('/dashboard/workload');
    const me = page.getByRole('row').filter({ has: page.getByRole('link', { name: 'Demo Admin' }) });
    await expect(me).toBeVisible();
    await me.getByRole('link', { name: 'Demo Admin' }).click();
    await expect(page.getByRole('link', { name: `Replace toner ${key}` })).toBeVisible();

    // The paged list, by where the ticket came from.
    await page.goto('/dashboard/tickets');
    await page.getByLabel('Filter tickets').fill(key);
    await page.getByLabel('Kind').selectOption('internal');
    await expect(page.getByText('1–2 of 2 tickets')).toBeVisible();
    await expect(page.getByRole('link', { name: `Reset VPN token ${key}` })).toBeVisible();
  });
});
