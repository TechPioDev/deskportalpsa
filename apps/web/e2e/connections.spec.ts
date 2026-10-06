import http from 'node:http';
import type { AddressInfo } from 'node:net';
import { test, expect, type Page } from '@playwright/test';

/**
 * A PSA connection's life through the browser: what can be connected comes from the server, a new
 * connection is added a step at a time and switched on only at the end, one that fails its test
 * stays in setup, and one that is not wanted is put away and can be brought back.
 *
 * No real PSA is involved. The failing case points at a closed port on this machine, so its test
 * fails the way a wrong address does. The passing case points at a stand-in ConnectWise started
 * here, which answers the handful of reads the wizard makes and would refuse nothing: what is
 * under test is the portal's side of the conversation.
 */
test.describe.configure({ timeout: 240_000 });

/** Every request the stand-in was sent, so a test can say what was and was not asked of it. */
const asked: { method: string; path: string }[] = [];
let standIn: http.Server;
let standInBase = '';

test.beforeAll(async () => {
  standIn = http.createServer((req, res) => {
    const url = new URL(req.url ?? '/', 'http://stand-in');
    asked.push({ method: req.method ?? 'GET', path: url.pathname + url.search });
    res.setHeader('Content-Type', 'application/json');
    const path = url.pathname;
    const conditions = url.searchParams.get('conditions') ?? '';
    if (req.method !== 'GET') { res.statusCode = 405; res.end('{"code":"MethodNotAllowed","message":"The stand-in is read-only."}'); return; }
    if (path.endsWith('/service/tickets/count')) { res.end(JSON.stringify({ count: conditions.includes('closedFlag=false') ? 5 : 7 })); return; }
    // One ticket, as ConnectWise answers a read: references expanded to {id, name}.
    const ticket = {
      id: 501, summary: 'Printer offline', initialDescription: 'The front desk printer will not print.',
      status: { id: 10, name: 'New' }, priority: { id: 3, name: 'Priority 1 - High' },
      board: { id: 1, name: 'Service Desk' }, company: { id: 1, name: 'Acme Corp' }, lastUpdated: '2026-10-01T09:00:00Z',
    };
    if (path.endsWith('/service/tickets')) { res.end(JSON.stringify((url.searchParams.get('page') ?? '1') === '1' ? [ticket] : [])); return; }
    if (path.endsWith('/service/tickets/501')) { res.end(JSON.stringify(ticket)); return; }
    if (path.endsWith('/service/boards')) { res.end('[{"id":1,"name":"Service Desk"},{"id":2,"name":"Projects"}]'); return; }
    if (/\/service\/boards\/\d+\/statuses$/.test(path)) { res.end('[{"id":10,"name":"New"},{"id":11,"name":"Closed","closedStatus":true}]'); return; }
    if (path.endsWith('/service/priorities')) { res.end('[{"id":3,"name":"Priority 1 - High"}]'); return; }
    if (path.endsWith('/company/companies')) { res.end('[{"id":1,"name":"Acme Corp","deletedFlag":false}]'); return; }
    res.end('[]');
  });
  await new Promise<void>((resolve) => standIn.listen(0, '127.0.0.1', resolve));
  standInBase = `http://127.0.0.1:${(standIn.address() as AddressInfo).port}/v4_6_release/apis/3.0/`;
});

test.afterAll(async () => {
  await new Promise((resolve) => standIn.close(resolve));
});

const wizardOf = (page: Page) => page.getByRole('region', { name: 'Add a PSA connection' });

/** Steps 1 to 3: choose ConnectWise, name it, give it an address and keys, and save it. */
async function saveInSetup(page: Page, name: string, endpoint: string, company: string) {
  await page.goto('/dashboard/connections');
  await page.getByRole('button', { name: 'Add connection' }).click();
  const wizard = wizardOf(page);
  await wizard.getByRole('button', { name: 'ConnectWise PSA' }).click();
  await wizard.getByRole('button', { name: 'Next' }).click();
  await wizard.getByLabel('Name').fill(name);
  await wizard.getByLabel('API endpoint').fill(endpoint);
  await wizard.getByRole('button', { name: 'Next' }).click();
  await wizard.getByLabel('Company ID').fill(company);
  await wizard.getByLabel('Public key').fill('e2e-public');
  await wizard.getByLabel('Private key').fill('e2e-private');
  await wizard.getByLabel('Client ID').fill('e2e-client');
  await wizard.getByRole('button', { name: 'Save and test' }).click();
  return wizard;
}

const cardOf = (page: Page, name: string) => page.locator('div.rounded-2xl', { has: page.getByRole('heading', { name }) });

async function archive(page: Page, name: string, openManage = true) {
  const card = cardOf(page, name);
  page.once('dialog', (dialog) => dialog.accept());
  if (openManage) await card.getByRole('button', { name: 'Manage' }).click();
  await card.getByRole('button', { name: 'Archive' }).click();
  await expect(page.getByRole('heading', { name })).toHaveCount(0);
}

test('the PSAs that can be connected are offered with their own fields, and the planned ones cannot be chosen', async ({ page }) => {
  await page.goto('/dashboard/connections');
  await page.getByRole('button', { name: 'Add connection' }).click();
  const wizard = wizardOf(page);

  // Named, and not selectable: there is no connector behind them.
  await expect(wizard.getByRole('heading', { name: 'Coming soon' })).toBeVisible();
  await expect(wizard.getByText('HaloPSA', { exact: true })).toBeVisible();
  await expect(wizard.getByRole('button', { name: 'HaloPSA' })).toHaveCount(0);
  await expect(wizard.getByRole('button', { name: 'Next' })).toBeDisabled();

  // Each connector says what it needs; the wizard asks for exactly that.
  await wizard.getByRole('button', { name: 'Datto Autotask PSA' }).click();
  await wizard.getByRole('button', { name: 'Next' }).click();
  await wizard.getByLabel('Name').fill('Catalog check');
  await wizard.getByLabel('API endpoint').fill('https://webservices5.autotask.net/ATServicesRest/');
  await wizard.getByRole('button', { name: 'Next' }).click();
  await expect(wizard.getByLabel('API integration code')).toBeVisible();
  await expect(wizard.getByLabel('Company ID')).toHaveCount(0);

  await wizard.getByRole('button', { name: 'Back' }).click();
  await wizard.getByRole('button', { name: 'Back' }).click();
  await wizard.getByRole('button', { name: 'ConnectWise PSA' }).click();
  await wizard.getByRole('button', { name: 'Next' }).click();
  await wizard.getByRole('button', { name: 'Next' }).click();
  await expect(wizard.getByLabel('Company ID')).toBeVisible();
  await expect(wizard.getByLabel('API integration code')).toHaveCount(0);

  // Nothing was saved by looking.
  await wizard.getByRole('button', { name: 'Close' }).click();
  await expect(page.getByRole('heading', { name: 'Catalog check' })).toHaveCount(0);
});

test('a connection that fails its test stays in setup, cannot be switched on, and can be put away and brought back', async ({ page }) => {
  const stamp = Date.now().toString().slice(-7);
  const name = `E2E refused ${stamp}`;
  const wizard = await saveInSetup(page, name, 'http://127.0.0.1:9/v4_6_release/apis/3.0/', `e2e${stamp}`);

  // Saved, tried, and stopped at the test. Nothing answers at that address.
  await expect(wizard.getByText('Not ready:')).toBeVisible({ timeout: 90_000 });
  await expect(wizard.locator('li', { hasText: 'Authentication' }).getByText('Fail', { exact: true })).toBeVisible();
  await expect(wizard.locator('li', { hasText: 'Read tickets' }).getByText('Not tried', { exact: true })).toBeVisible();
  await expect(wizard.getByRole('button', { name: 'Next' })).toBeDisabled();
  await wizard.getByRole('button', { name: 'Close' }).click();

  const card = cardOf(page, name);
  await expect(card.getByText('Setup', { exact: true })).toBeVisible();
  await expect(card.getByRole('button', { name: 'Continue setup' })).toBeVisible();
  await expect(card.getByRole('button', { name: 'Sync now' })).toHaveCount(0);

  // The server holds the same thing the card says: it is not live.
  const listed = await (await page.request.get('/api/bff/api/admin/connections')).json() as { id: string; name: string; isEnabled: boolean; state: number }[];
  const mine = listed.find((c) => c.name === name)!;
  expect(mine).toMatchObject({ isEnabled: false, state: 0 });

  // And it cannot be switched on by asking for that directly.
  expect((await page.request.post(`/api/bff/api/admin/connections/${mine.id}/activate`)).ok()).toBeFalsy();
  expect((await page.request.post(`/api/bff/api/admin/connections/${mine.id}/enabled`, { data: true })).ok()).toBeFalsy();

  // Carrying on opens the wizard at the test, not at the beginning.
  await card.getByRole('button', { name: 'Continue setup' }).click();
  const resumed = page.getByRole('region', { name: 'Add a PSA connection' });
  await expect(resumed.getByText('Step 4 of 9: Test.')).toBeVisible();
  await expect(resumed.getByText('Not ready:')).toBeVisible({ timeout: 90_000 });
  await resumed.getByRole('button', { name: 'Close' }).click();

  // Put away: off the list, and in the list of the ones put away.
  await archive(page, name);
  const archived = page.locator('details', { hasText: 'Archived connections' });
  await archived.locator('summary').click();
  await expect(archived.locator('li', { hasText: name })).toBeVisible();

  // Brought back, and still not switched on.
  await archived.locator('li', { hasText: name }).getByRole('button', { name: 'Restore' }).click();
  await expect(card.getByText('Setup', { exact: true })).toBeVisible();
  await expect(card.getByText('Restored, and still switched off.')).toBeVisible();

  // Left put away, so the tests after this one do not find a half-made connection. (Its Manage
  // panel is still open: the card came back as the same card.)
  await archive(page, name, false);
});

test('a connection is added a step at a time against a PSA and is switched on only at the last step', async ({ page }) => {
  const stamp = Date.now().toString().slice(-7);
  const name = `E2E wizard ${stamp}`;
  asked.length = 0;
  const ticketsBefore = (await (await page.request.get('/api/bff/api/tickets')).json() as unknown[]).length;
  const wizard = await saveInSetup(page, name, standInBase, `wiz${stamp}`);

  // 4. The test, line by line. Notes are tried on the ticket it read; a write is never tried.
  await expect(wizard.getByText('Nothing was changed in the PSA.')).toBeVisible({ timeout: 90_000 });
  await expect(wizard.locator('li', { hasText: 'Authentication' }).getByText('Pass', { exact: true })).toBeVisible();
  await expect(wizard.locator('li', { hasText: 'Read ticket notes' }).getByText('Pass', { exact: true })).toBeVisible();
  await expect(wizard.locator('li', { hasText: 'Update tickets and add notes' }).getByText('Not tried', { exact: true })).toBeVisible();
  const saved = (await (await page.request.get('/api/bff/api/admin/connections')).json() as { id: string; name: string; isEnabled: boolean; state: number }[])
    .find((c) => c.name === name)!;
  expect(saved, 'a passed test does not switch it on').toMatchObject({ isEnabled: false, state: 0 });
  await wizard.getByRole('button', { name: 'Next' }).click();

  // 5. What the PSA offers.
  await expect(wizard.getByText('Queues or boards')).toBeVisible();
  await expect(wizard.locator('summary', { hasText: 'Queues or boards' })).toContainText('(2)');
  await wizard.getByRole('button', { name: 'Next' }).click();

  // 6. What its values become. Nothing maps them yet, and that is said rather than guessed.
  await expect(wizard.getByText('Not mapped').first()).toBeVisible();
  await expect(wizard.getByText(/no mapping\. Nothing is guessed/)).toBeVisible();
  // A real ticket from the PSA, with what the rules would make of it: read, mapped, not kept.
  const sampled = wizard.locator('tr', { hasText: 'Printer offline' });
  await expect(sampled).toContainText('New');
  await expect(sampled.getByText('Not mapped').first()).toBeVisible();
  await wizard.getByRole('button', { name: 'Next' }).click();

  // 7. What to bring in, chosen from the PSA's own list.
  await wizard.getByLabel('Service Desk').check();
  await expect(wizard.getByText('Tickets come from the 1 ticked.')).toBeVisible();
  await wizard.getByRole('button', { name: 'Save scope' }).click();

  // 8. How much that is, asked of the PSA as a count and under the scope just chosen.
  await expect(wizard.locator('p', { hasText: 'Would be imported' }).locator('xpath=following-sibling::p')).toHaveText('5');
  await expect(wizard.locator('p', { hasText: 'All tickets' }).locator('xpath=following-sibling::p')).toHaveText('7');
  await expect(wizard.getByText('Before it is switched on')).toBeVisible();
  expect(asked.some((r) => r.path.includes('/service/tickets/count') && decodeURIComponent(r.path).includes('board/id=1')),
    'the count carries the board that was ticked').toBeTruthy();
  await wizard.getByRole('button', { name: 'Next' }).click();

  // 9. Switched on only here, and only once the warning about unmapped values has been read.
  const enable = wizard.getByRole('button', { name: 'Enable connection' });
  await expect(enable).toBeDisabled();
  await wizard.getByLabel(/I have read the/).check();
  await enable.click();

  const card = cardOf(page, name);
  await expect(card.getByText('Connected', { exact: true })).toBeVisible();
  await expect(card.getByText('Switched on.')).toBeVisible();
  await expect(card.getByRole('button', { name: 'Sync now' })).toBeEnabled();

  // Mapping health, before anything has been imported: the statuses the PSA lists, none of them
  // mapped, and a ticket read from the PSA to show what the rules make of it.
  //
  // Nothing is synced here on purpose. In local mode the first sync that brings tickets in links
  // the demo sign-in to a client so the client pages have something to show, and every test after
  // this one would then be run as that client's user. What "apply" does to imported tickets is
  // held by MappingHealthTests and, through a SQL translator, by RelationalQueryTests.
  await card.getByRole('button', { name: 'Mapping', exact: true }).click();
  await expect(card.getByText('Mapping health')).toBeVisible();
  await expect(card.getByText('Blocking', { exact: true })).toBeVisible();
  const statusRow = card.locator('table', { hasText: 'Statuses' }).locator('tr', { hasText: 'New' }).first();
  await expect(statusRow.getByText('Not mapped')).toBeVisible();
  const sampleRow = card.locator('table', { hasText: 'Ticket' }).locator('tr', { hasText: 'Printer offline' });
  await expect(sampleRow.getByText('Not mapped').first()).toBeVisible();
  await expect(card.getByText('read from the PSA just now and not kept')).toBeVisible();

  // Mapped, the report and the sample both say so the next time they are asked.
  const rule = await page.request.post('/api/bff/api/admin/mappings', {
    data: {
      provider: 1, scope: 2, psaConnectionId: saved.id, portalField: 'status', portalValue: 'NEW',
      externalField: 'status', externalValue: 'New', direction: 3, isRequired: false, fallbackValue: null,
    },
  });
  expect(rule.ok()).toBeTruthy();
  await card.getByRole('button', { name: 'Check again' }).click();
  await expect(statusRow.getByText('Not mapped')).toHaveCount(0);
  await expect(statusRow).toContainText('NEW');
  await expect(card.getByText('Blocking', { exact: true })).toHaveCount(0);
  await expect(sampleRow).toContainText('NEW');
  expect((await (await page.request.get('/api/bff/api/tickets')).json() as unknown[]).length, 'the sample was not kept').toBe(ticketsBefore);

  // In all of that, nothing was written to the PSA.
  expect(asked.filter((r) => r.method !== 'GET')).toEqual([]);

  await archive(page, name);
});
