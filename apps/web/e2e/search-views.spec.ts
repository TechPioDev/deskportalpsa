import { test, expect, type Page } from '@playwright/test';

/**
 * A board of this test's own, with one ticket on it. Each test makes its own rather than relying on
 * what an earlier spec left behind: run alone against a fresh desk there is nothing to search for,
 * and a test that only passes after another one is a test of the order they ran in.
 */
async function raiseTicket(page: Page, title: string) {
  const key = `S${Date.now().toString().slice(-6)}`.slice(0, 8).toUpperCase();
  await page.goto('/dashboard/boards');
  await page.getByRole('button', { name: 'New board' }).click();
  const form = page.locator('form').filter({ has: page.getByLabel('Board name') });
  await form.getByLabel('Board name').fill(`Search test ${key}`);
  await form.getByLabel('Ticket prefix').fill(key);
  await form.getByRole('button', { name: 'Create board' }).click();
  await page.locator('li').filter({ hasText: `Search test ${key}` }).getByRole('link', { name: 'Open board' }).click();

  await page.getByRole('button', { name: 'New ticket' }).click();
  const raise = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
  await raise.getByLabel('What needs doing').fill(title);
  await raise.getByRole('button', { name: 'Raise ticket' }).click();
  await expect(page.locator('tr').filter({ hasText: title })).toBeVisible();
}

/**
 * Searching, views and followers, driven the way somebody working the desk uses them: type in the
 * header box and open what comes back, click a view and watch the list narrow, put a colleague on a
 * ticket without taking it off whoever has it.
 *
 * The header search is the one worth a browser: it was a placeholder for months — a box with a
 * shortcut printed in it that did nothing — and nothing but driving it proves it now answers.
 */
test.describe('search and views', () => {
  test('the header search finds a ticket and opens it', async ({ page }) => {
    // Something to find, with a phrase nothing else in the tenant carries.
    const mark = `probe ${Date.now().toString().slice(-6)}`;
    await raiseTicket(page, `Switch firmware ${mark}`);

    const search = page.getByRole('combobox', { name: /Search tickets/ });
    await search.fill(mark);
    const result = page.getByRole('option').filter({ hasText: `Switch firmware ${mark}` });
    await expect(result).toBeVisible();
    await result.click();

    await expect(page.getByRole('heading', { name: `Switch firmware ${mark}` })).toBeVisible();
  });

  test('a view narrows the list, and a saved one comes back by name', async ({ page }) => {
    await raiseTicket(page, `Firmware rollout ${Date.now().toString().slice(-6)}`);
    await page.goto('/dashboard/tickets');
    await expect(page.getByRole('heading', { name: 'Tickets' })).toBeVisible();

    // Built-in views: the handful every desk needs, which are code rather than rows.
    await page.getByRole('button', { name: 'Unassigned', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Unassigned', exact: true })).toHaveClass(/bg-brand/);

    // A filter nobody has a built-in view for is what a saved view is for.
    await page.getByRole('button', { name: 'All', exact: true }).click();
    await page.getByPlaceholder('Filter by title, number or customer…').fill('firmware');
    const name = `Firmware ${Date.now().toString().slice(-5)}`;
    await page.getByRole('button', { name: 'Save this view' }).click();
    await page.getByLabel('Name this view').fill(name);
    await page.getByRole('button', { name: 'Save', exact: true }).click();

    const saved = page.getByRole('button', { name, exact: true });
    await expect(saved).toBeVisible();

    // Clearing everything and clicking the view has to put the filters back.
    await page.getByRole('button', { name: 'All', exact: true }).click();
    await expect(page.getByPlaceholder('Filter by title, number or customer…')).toHaveValue('');
    await saved.click();
    await expect(page.getByPlaceholder('Filter by title, number or customer…')).toHaveValue('firmware');
  });

  test('following a ticket does not take it off whoever holds it', async ({ page }) => {
    const title = `Watch the UPS ${Date.now().toString().slice(-6)}`;
    await raiseTicket(page, title);
    await page.locator('tr').filter({ hasText: title }).getByRole('link', { name: title }).click();
    await expect(page.getByRole('heading', { name: title })).toBeVisible();

    const workingIt = page.getByText('Working it', { exact: true }).locator('..');
    const before = await workingIt.textContent();

    await page.getByRole('button', { name: 'Follow', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Unfollow' })).toBeVisible();
    // A follower is not an assignee. That distinction is the whole point of the feature.
    await expect(workingIt).toHaveText(before ?? '');

    // And it shows up as followed on the list, without having been taken on.
    await page.goto('/dashboard/tickets');
    await page.getByRole('button', { name: 'Following', exact: true }).click();
    await expect(page.locator('tbody tr').filter({ hasText: title })).toHaveCount(1);
    // Nothing on the list that I have not followed.
    await expect(page.locator('tbody tr').filter({ hasNotText: 'Watch the UPS' })).toHaveCount(0);
  });
});
