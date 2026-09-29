import { test, expect } from '@playwright/test';

/**
 * The team writes an article for clients, publishes it, and finds it again by a word in its body.
 * Other specs share the database, so the article is named per run and found by that name.
 */
test.describe('Knowledge base', () => {
  test('an article is written, published and found by search', async ({ page }) => {
    const run = Date.now().toString().slice(-6);
    const title = `Connect to the VPN ${run}`;

    await page.goto('/dashboard/knowledge');
    await page.getByRole('button', { name: 'New article' }).click();
    await page.getByLabel('Title', { exact: true }).fill(title);
    await page.getByLabel('Category', { exact: true }).fill('VPN');
    await page.getByLabel('Article', { exact: true }).fill(`Open **FortiClient** and choose office-${run}.`);
    await page.getByLabel('Published', { exact: true }).click();
    await expect(page.getByLabel('Published', { exact: true })).toBeChecked();
    await page.getByRole('button', { name: 'Preview' }).click();
    await expect(page.locator('form strong', { hasText: 'FortiClient' })).toBeVisible();
    await page.getByRole('button', { name: 'Save article' }).click();

    const row = page.getByRole('button', { name: new RegExp(title) });
    await expect(row).toBeVisible();
    await expect(row).toContainText('All clients');
    await expect(row).not.toContainText('Draft');

    await page.getByLabel('Search articles').fill(`office-${run}`);
    await expect(page.getByRole('button', { name: new RegExp(title) })).toBeVisible();
  });
});
