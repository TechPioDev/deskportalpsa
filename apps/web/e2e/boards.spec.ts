import { test, expect } from '@playwright/test';

/**
 * The team's own boards, driven the way the team will: create a board, raise a ticket on it, hand it
 * to a colleague with a note, and post the day's update against it. The assertions that matter most
 * are the ones about what is NOT offered — there is no client on this side of the product.
 */
test.describe('internal boards', () => {
  test('a board takes a ticket, and that ticket is the team’s own', async ({ page }) => {
    await page.goto('/dashboard/boards');
    await expect(page.getByRole('heading', { name: 'Internal boards' })).toBeVisible();

    // A prefix people will quote. Unique per run so repeats do not collide on it.
    const key = `E${Date.now().toString().slice(-6)}`.slice(0, 8).toUpperCase();
    await page.getByRole('button', { name: 'New board' }).click();
    const form = page.locator('form').filter({ has: page.getByLabel('Board name') });
    await form.getByLabel('Board name').fill(`Browser test ${key}`);
    await form.getByLabel('Ticket prefix').fill(key);
    await form.getByRole('button', { name: 'Create board' }).click();

    const card = page.locator('li').filter({ hasText: `Browser test ${key}` });
    await expect(card).toContainText('Staff only');
    await expect(card).toContainText('Whole team');

    await card.getByRole('link', { name: 'Open board' }).click();
    await expect(page.getByRole('heading', { name: `Browser test ${key}` })).toBeVisible();

    await page.getByRole('button', { name: 'New ticket' }).click();
    const raise = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
    await raise.getByLabel('What needs doing').fill('Rebuild the spare laptop');
    await raise.getByRole('button', { name: 'Raise ticket' }).click();

    // The number is what people quote to each other, so it is on screen from the first moment.
    const row = page.locator('tr').filter({ hasText: 'Rebuild the spare laptop' });
    await expect(row).toContainText(`${key}-000001`);
    await expect(row).toContainText('Unclaimed');

    await row.getByRole('link', { name: 'Rebuild the spare laptop' }).click();
    await expect(page.getByRole('heading', { name: 'Rebuild the spare laptop' })).toBeVisible();

    // A ticket on the team's own board has no provider reference and no client to write to.
    await expect(page.getByText(`${key}-000001`)).toBeVisible();
    await page.getByText('Add a reply, internal note, time or status change…').click();
    await expect(page.getByRole('button', { name: /Sent to the client/ })).toHaveCount(0);

    await page.getByPlaceholder('Add an internal note — the client will not see this…')
      .fill('Day shift: imaged it, drivers left for the night shift.');
    await page.getByLabel('Hours to log with this reply').fill('1.5');
    // The composer's own submit, whatever it is labelled with: it carries the shortcut hint too.
    await page.locator('form').filter({ has: page.getByLabel('Hours to log with this reply') })
      .locator('button[type="submit"]').click();

    // Recorded, not "not recorded": there is nowhere to push it and nothing is outstanding.
    await expect(page.getByText('Day shift: imaged it, drivers left for the night shift.')).toBeVisible();
    await expect(page.getByText('NOT RECORDED')).toHaveCount(0);
  });
});
