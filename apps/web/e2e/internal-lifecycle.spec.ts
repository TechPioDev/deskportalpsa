import { test, expect } from '@playwright/test';

/**
 * A ticket on the team's own board, all the way through: raised, its details corrected, time logged,
 * resolved with what fixed it (the board asks for that), reopened when the fault came back, resolved
 * again, closed - and every step on its History with who did it.
 */
test.describe('internal ticket lifecycle', () => {
  test('raise, edit, log time, resolve with a resolution, reopen, close', async ({ page }) => {
    const key = `L${Date.now().toString().slice(-6)}`.slice(0, 8).toUpperCase();

    // A board that asks for a resolution.
    await page.goto('/dashboard/boards');
    await page.getByRole('button', { name: 'New board' }).click();
    const form = page.locator('form').filter({ has: page.getByLabel('Board name') });
    await form.getByLabel('Board name').fill(`Lifecycle ${key}`);
    await form.getByLabel('Ticket prefix').fill(key);
    await form.getByLabel('Ask for a resolution').check();
    await form.getByRole('button', { name: 'Create board' }).click();
    await page.locator('li').filter({ hasText: `Lifecycle ${key}` }).getByRole('link', { name: 'Open board' }).click();

    await page.getByRole('button', { name: 'New ticket' }).click();
    const raise = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
    await raise.getByLabel('What needs doing').fill('Meeting room screen flickers');
    await raise.getByRole('button', { name: 'Raise ticket' }).click();
    await page.locator('tr').filter({ hasText: 'Meeting room screen flickers' })
      .getByRole('link', { name: 'Meeting room screen flickers' }).click();
    await expect(page.getByRole('heading', { name: 'Meeting room screen flickers' })).toBeVisible();

    // Details corrected after the fact.
    await page.getByRole('button', { name: 'Edit details' }).click();
    const edit = page.getByRole('form', { name: 'Edit ticket details' });
    await edit.getByLabel('Title').fill('Meeting room 2 screen flickers');
    await edit.getByLabel('Priority').selectOption('HIGH');
    await edit.getByRole('button', { name: 'Save details' }).click();
    await expect(page.getByRole('heading', { name: 'Meeting room 2 screen flickers' })).toBeVisible();

    // Time logged with an update.
    await page.getByText('Add a reply, internal note, time or status change…').click();
    await page.getByPlaceholder('Add an internal note — the client will not see this…').fill('Swapped the HDMI cable, watching it.');
    await page.getByLabel('Hours to log with this reply').fill('0.5');
    await page.locator('form').filter({ has: page.getByLabel('Hours to log with this reply') })
      .locator('button[type="submit"]').click();
    // On the thread and on the time entry, whose notes default to the reply text.
    await expect(page.getByText('Swapped the HDMI cable, watching it.').first()).toBeVisible();

    // Resolving asks what fixed it, and will not take no for an answer on this board.
    const status = page.getByLabel('Ticket status');
    await status.selectOption('RESOLVED');
    const resolution = page.getByRole('form', { name: 'Resolution' });
    await expect(resolution).toBeVisible();
    await resolution.getByLabel(/What fixed it/).fill('Replaced the HDMI cable behind the panel.');
    await resolution.getByRole('button', { name: 'Mark resolved' }).click();
    await expect(page.getByText('Replaced the HDMI cable behind the panel.')).toBeVisible();

    // The fault came back.
    await status.selectOption('IN_PROGRESS');
    await expect(page.getByText(/Reopened once/)).toBeVisible();

    // Resolved again: the last resolution is offered, so the next person sees what was tried.
    await status.selectOption('RESOLVED');
    await expect(page.getByRole('form', { name: 'Resolution' }).getByLabel(/What fixed it/))
      .toHaveValue('Replaced the HDMI cable behind the panel.');
    await page.getByRole('form', { name: 'Resolution' }).getByLabel(/What fixed it/).fill('Replaced the panel\'s HDMI port; cable was fine.');
    await page.getByRole('form', { name: 'Resolution' }).getByRole('button', { name: 'Mark resolved' }).click();
    await expect(page.getByText('Replaced the panel\'s HDMI port; cable was fine.')).toBeVisible();

    // Resolved to closed does not ask again.
    await status.selectOption('CLOSED');
    await expect(page.getByRole('form', { name: 'Resolution' })).toHaveCount(0);
    await expect(status).toHaveValue('CLOSED');

    // The whole story, with who did it.
    const history = page.getByRole('region', { name: 'History' });
    await history.getByRole('button', { name: /Show all/ }).click();
    await expect(history).toContainText('Status Resolved → Closed');
    await expect(history).toContainText('Reopened: Resolved → In progress');
    await expect(history).toContainText('Logged 0.5h');
    await expect(history).toContainText('Changed title (Meeting room screen flickers → Meeting room 2 screen flickers) and priority (Normal → High)');
    await expect(history).toContainText('Raised on Lifecycle');
  });
});
