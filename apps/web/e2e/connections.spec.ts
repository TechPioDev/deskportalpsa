import { test, expect } from '@playwright/test';

/**
 * A PSA connection's life through the browser: what can be connected comes from the server, a new
 * connection is tested before it is switched on, and one that is not wanted is put away and can
 * be brought back.
 *
 * No PSA is needed. The address is a closed port on this machine, so the connection's test fails
 * the way a wrong address does — which is the case that matters: it must not end up switched on.
 */
test.describe.configure({ timeout: 180_000 });

test('the PSAs that can be connected are offered with their own fields, and the planned ones cannot be chosen', async ({ page }) => {
  await page.goto('/dashboard/connections');
  await page.getByRole('button', { name: 'Add connection' }).click();

  const provider = page.getByLabel('Provider');
  await expect(provider.locator('> option')).toHaveText(['ConnectWise PSA', 'Datto Autotask PSA']);

  // Named, and not selectable: there is no connector behind them.
  const planned = provider.locator('optgroup[label="Coming soon"] option');
  await expect(planned.first()).toBeAttached();
  for (const option of await planned.all()) await expect(option).toHaveJSProperty('disabled', true);
  await expect(provider.locator('> option').first()).toHaveJSProperty('disabled', false);

  // Each connector says what it needs; the form asks for exactly that.
  await provider.selectOption({ label: 'Datto Autotask PSA' });
  await expect(page.getByLabel('API integration code')).toBeVisible();
  await expect(page.getByLabel('Company ID')).toHaveCount(0);

  await provider.selectOption({ label: 'ConnectWise PSA' });
  await expect(page.getByLabel('Company ID')).toBeVisible();
  await expect(page.getByLabel('API integration code')).toHaveCount(0);
});

test('a new connection is not switched on until a test passes, and can be put away and brought back', async ({ page }) => {
  const stamp = Date.now().toString().slice(-7);
  const name = `E2E CW ${stamp}`;

  await page.goto('/dashboard/connections');
  await page.getByRole('button', { name: 'Add connection' }).click();
  await page.getByLabel('Provider').selectOption({ label: 'ConnectWise PSA' });
  await page.getByLabel('Name').fill(name);
  await page.getByLabel('API endpoint').fill('http://127.0.0.1:9/v4_6_release/apis/3.0/');
  await page.getByLabel('Company ID').fill(`e2e${stamp}`);
  await page.getByLabel('Public key').fill('e2e-public');
  await page.getByLabel('Private key').fill('e2e-private');
  await page.getByLabel('Client ID').fill('e2e-client');
  await page.getByRole('button', { name: 'Save and test' }).click();

  // Saved, tried, and left in setup with the reason. Nothing answers at that address.
  const card = page.locator('div.rounded-2xl', { has: page.getByRole('heading', { name }) });
  await expect(card.getByText('Not switched on —')).toBeVisible({ timeout: 90_000 });
  await expect(card.getByText('Setup', { exact: true })).toBeVisible();
  await expect(card.getByRole('button', { name: 'Test and switch on' })).toBeVisible();
  await expect(card.getByRole('button', { name: 'Sync now' })).toHaveCount(0);

  // The server holds the same thing the card says: it is not live.
  const listed = await (await page.request.get('/api/bff/api/admin/connections')).json() as { id: string; name: string; isEnabled: boolean; state: number }[];
  const mine = listed.find((c) => c.name === name)!;
  expect(mine).toMatchObject({ isEnabled: false, state: 0 });

  // And it cannot be switched on by asking for that directly.
  const forced = await page.request.post(`/api/bff/api/admin/connections/${mine.id}/activate`);
  expect(forced.ok()).toBeFalsy();
  const enabled = await page.request.post(`/api/bff/api/admin/connections/${mine.id}/enabled`, { data: true });
  expect(enabled.ok()).toBeFalsy();

  // Put away: off the list, and in the list of the ones put away.
  page.once('dialog', (dialog) => dialog.accept());
  await card.getByRole('button', { name: 'Manage' }).click();
  await card.getByRole('button', { name: 'Archive' }).click();
  await expect(page.getByRole('heading', { name })).toHaveCount(0);

  const archived = page.locator('details', { hasText: 'Archived connections' });
  await archived.locator('summary').click();
  await expect(archived.locator('li', { hasText: name })).toBeVisible();

  // Brought back, and still not switched on.
  await archived.locator('li', { hasText: name }).getByRole('button', { name: 'Restore' }).click();
  await expect(card.getByText('Setup', { exact: true })).toBeVisible();
  await expect(card.getByText('Restored, and still switched off.')).toBeVisible();

  // Left put away, so the tests after this one do not find a half-made connection. (Its Manage
  // panel is still open: the card came back as the same card.)
  page.once('dialog', (dialog) => dialog.accept());
  await card.getByRole('button', { name: 'Archive' }).click();
  await expect(page.getByRole('heading', { name })).toHaveCount(0);
});
