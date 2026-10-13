import { test, expect, type Page } from '@playwright/test';
import { ask } from './support/ask';

/**
 * A client that is in no PSA, added by hand so that a ticket on a board can say who the work
 * was for.
 *
 * The "For a client" list held only what a PSA had sent. The owner tried to raise the team's own
 * work for NeedTech on 12 October 2026 and NeedTech was not in it, with no way to add them.
 */

const stamp = () => `${Date.now().toString().slice(-6)}${Math.floor(Math.random() * 90 + 10)}`;

async function board(page: Page, s: string) {
  const res = await ask(() => page.request.post('/api/bff/api/boards', { data: { name: `Projects ${s}`, key: `L${s}`.slice(0, 8).toUpperCase() } }));
  expect(res.ok()).toBeTruthy();
  return await res.json() as { id: string };
}

test('a client that is not in the list is added from the ticket form and the ticket is raised for them', async ({ page }) => {
  const s = stamp();
  const b = await board(page, s);
  const name = `NeedTech ${s}`;

  await page.goto(`/dashboard/boards/${b.id}?new=1`);
  const form = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
  await form.getByLabel('What needs doing').fill(`NinjaRMM implementation ${s}`);
  await expect(form.getByLabel('For a client (optional)').getByRole('option', { name })).toHaveCount(0);

  await form.getByRole('button', { name: 'The client is not in the list — add them' }).click();
  const add = form.getByRole('group', { name: 'Add a client' });
  await expect(add.getByRole('button', { name: 'Add client' })).toBeDisabled();
  await add.getByLabel('Name of the client to add').fill(`  ${name}  `);
  await add.getByRole('button', { name: 'Add client' }).click();

  // Added, chosen for this ticket, and filed under "added by hand".
  await expect(form.getByRole('group', { name: 'Add a client' })).toHaveCount(0);
  const select = form.getByLabel('For a client (optional)');
  await expect(select.locator('optgroup[label="Added by hand (not in a PSA)"] option', { hasText: name })).toHaveCount(1);
  await expect(select.locator('option:checked')).toHaveText(name);

  await form.getByRole('button', { name: 'Raise ticket' }).click();
  const row = page.locator('tr').filter({ hasText: `NinjaRMM implementation ${s}` });
  await row.getByRole('link', { name: `NinjaRMM implementation ${s}` }).click();
  // On the ticket itself. The board's own row already shows the client's name, so the page is
  // waited for first: without that the next step set off while this one was still opening.
  await page.waitForURL(/\/dashboard\/tickets\/[0-9a-f-]{36}$/);
  await expect(page.getByRole('heading', { name: `NinjaRMM implementation ${s}` })).toBeVisible();
  await expect(page.getByText(name).first()).toBeVisible();

  // Adding the same name again is refused, in words that say why.
  await page.goto(`/dashboard/boards/${b.id}?new=1`);
  const again = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
  await again.getByRole('button', { name: 'The client is not in the list — add them' }).click();
  await again.getByLabel('Name of the client to add').fill(name.toUpperCase());
  await again.getByRole('button', { name: 'Add client' }).click();
  await expect(again.getByRole('alert')).toContainText('is already in the list');
});

test('the clients added by hand are renamed, retired and brought back from the Boards page', async ({ page }) => {
  const s = stamp();
  const b = await board(page, s);
  const made = await (await ask(() => page.request.post('/api/bff/api/boards/clients/local', { data: { name: `Kiran Traders ${s}`, isActive: true } }))).json() as { id: string };
  await ask(() => page.request.post('/api/bff/api/boards/tickets', { data: { boardId: b.id, title: `Printer setup ${s}`, description: null, clientCompanyId: made.id } }));

  await page.goto('/dashboard/boards');
  const panel = page.getByRole('region', { name: 'Clients not in a PSA' });
  const row = () => panel.getByRole('listitem').filter({ hasText: s });
  await expect(row()).toContainText(`Kiran Traders ${s}`);
  await expect(row()).toContainText('1 ticket');

  await row().getByRole('button', { name: `Rename Kiran Traders ${s}` }).click();
  // While it is being renamed the name is in a text box, not in the row's text.
  await panel.getByLabel(`New name for Kiran Traders ${s}`).fill(`Kiran Traders Pvt Ltd ${s}`);
  await panel.getByRole('button', { name: 'Save' }).click();
  await expect(row()).toContainText(`Kiran Traders Pvt Ltd ${s}`);

  await row().getByRole('button', { name: `Retire Kiran Traders Pvt Ltd ${s}` }).click();
  await expect(row()).toContainText('Retired');

  // Retired: not offered on a new ticket.
  const offered = async () => (await (await ask(() => page.request.get('/api/bff/api/boards/clients'))).json() as { id: string }[]).some((c) => c.id === made.id);
  expect(await offered()).toBe(false);
  // And a ticket cannot be raised for it by asking the server directly.
  const refused = await ask(() => page.request.post('/api/bff/api/boards/tickets', { data: { boardId: b.id, title: `Second job ${s}`, description: null, clientCompanyId: made.id } }));
  expect(refused.status()).toBe(400);
  expect(await refused.text()).toContain('has been retired');

  await row().getByRole('button', { name: `Bring back Kiran Traders Pvt Ltd ${s}` }).click();
  await expect(row()).not.toContainText('Retired');
  expect(await offered()).toBe(true);

  // Adding from the panel, and the clash said plainly.
  await panel.getByLabel('Name of the client to add').fill(`kiran traders pvt ltd ${s}`);
  await panel.getByRole('button', { name: 'Add client' }).click();
  await expect(panel.getByRole('alert')).toContainText('is already in the list');
});
