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
    // One member, and not a person: the account the integration itself signs in as.
    if (path.endsWith('/system/members')) { res.end('[{"id":20,"identifier":"api","firstName":"API","lastName":"Account","primaryEmail":"","inactiveFlag":false}]'); return; }
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

  // "Sync now" where a worker runs the syncs: the API answers at once that the sync has been
  // asked for, and the card follows it until it has finished. The local API has no worker and
  // runs a sync inside the request, so the three things the card is told - asked for, running,
  // done - are stood in for here, over the real list. Nothing is synced, for the reason below.
  let stage: 'asked' | 'running' | 'done' = 'asked';
  const syncUrl = '**/api/bff/api/admin/connections/*/sync';
  const listUrl = '**/api/bff/api/admin/connections';
  const stateUrl = `**/api/bff/api/admin/connections/${saved.id}/sync-state*`;
  await page.route(syncUrl, (route) => route.fulfill({ status: 202, json: { queued: true, requestedAt: new Date().toISOString(), full: false } }));
  await page.route(listUrl, async (route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const response = await route.fetch();
    const list = await response.json() as { id: string; state: number; syncRequestedAt: string | null }[];
    for (const c of list) {
      if (c.id !== saved.id || stage === 'done') continue;
      c.syncRequestedAt = new Date().toISOString();
      if (stage === 'running') c.state = 2;
    }
    await route.fulfill({ response, json: list });
  });
  await page.route(stateUrl, (route) => route.fulfill({
    json: {
      connectionId: saved.id, watermark: null, readInProgress: false, pagesReadSoFar: 0, running: false, openFailures: 0, needsReview: 0,
      requestedAt: null, requestedFull: false,
      runs: [{
        id: '00000000-0000-4000-8000-000000000001', trigger: 'Manual', status: 'Succeeded',
        startedAt: new Date().toISOString(), finishedAt: new Date().toISOString(),
        fetched: 7, created: 5, updated: 2, skipped: 0, pages: 1, notes: 0, attachments: 0,
        failedRecords: 0, retried: 0, recovered: 0, error: null, notice: null, requestedBy: 'Demo Admin',
      }],
    },
  }));
  await card.getByRole('button', { name: 'Sync now' }).click();
  await expect(card.getByText('A sync has been asked for. It starts within a few seconds, and this card follows it.')).toBeVisible();
  await expect(card.getByText('A sync has been asked for and starts within a few seconds.')).toBeVisible();
  stage = 'running';
  await expect(card.getByText('Syncing', { exact: true })).toBeVisible({ timeout: 20_000 });
  await expect(card.getByText('A sync has been asked for and starts within a few seconds.')).toHaveCount(0);
  stage = 'done';
  await expect(card.getByText('Synced · 5 new, 2 updated (7 fetched)')).toBeVisible({ timeout: 20_000 });
  await expect(card.getByText('Syncing', { exact: true })).toHaveCount(0);
  for (const url of [syncUrl, listUrl, stateUrl]) await page.unroute(url);

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

  // Mapped on the Field Mapping page, from what the PSA sends. The two statuses whose words are
  // the portal's own are suggested, shown as a list of changes, and saved together.
  await page.goto(`/dashboard/mappings?connection=${saved.id}&tab=status`);
  const sends = page.getByRole('region', { name: 'What the PSA sends' });
  await expect(sends.getByText('0 of 2 mapped')).toBeVisible();
  await expect(sends.getByLabel('What New becomes in the portal')).toHaveValue('__none');
  await sends.getByRole('button', { name: 'Suggest exact matches (2)' }).click();
  await expect(sends.getByText('2 changes not saved yet')).toBeVisible();
  await expect(sends.getByText('(was not mapped)')).toHaveCount(2);
  expect((await (await page.request.get('/api/bff/api/admin/mappings?provider=1')).json() as unknown[]).length, 'a suggestion is not a saved rule').toBe(0);
  await sends.getByRole('button', { name: 'Save these 2 changes' }).click();
  await expect(sends.getByText('Saved 2 changes.')).toBeVisible();
  await expect(sends.getByText('2 of 2 mapped')).toBeVisible();
  await page.reload();
  await expect(page.getByRole('region', { name: 'What the PSA sends' }).getByLabel('What New becomes in the portal')).toHaveValue('NEW');
  await expect(page.getByRole('region', { name: 'What the PSA sends' }).getByLabel('What Closed becomes in the portal')).toHaveValue('CLOSED');

  // The connection's own report, and its sample of tickets, now say the same. A classification
  // rule written for the connection is counted there too, with the way to the rules.
  const classification = `/api/bff/api/admin/connections/${saved.id}/classification`;
  expect((await page.request.put(classification, { data: [{ ticketType: 'Hardware', issueType: null, subIssueType: null, category: null, workType: 'Break/fix', subcategory: null }] })).ok()).toBeTruthy();
  await page.goto('/dashboard/connections');
  await card.getByRole('button', { name: 'Mapping', exact: true }).click();
  await expect(card.getByText('Mapping health')).toBeVisible();
  await expect(card.getByText('1 rule · 0 of 0 classified tickets named by one.')).toBeVisible();
  await expect(card.getByRole('link', { name: 'Classification rules' })).toHaveAttribute('href', `/dashboard/mappings?connection=${saved.id}&tab=classification`);
  expect((await page.request.put(classification, { data: [] })).ok()).toBeTruthy();
  await expect(card.getByText('Blocking', { exact: true })).toHaveCount(0);
  await expect(statusRow.getByText('Not mapped')).toHaveCount(0);
  await expect(statusRow).toContainText('NEW');
  await expect(sampleRow).toContainText('NEW');
  expect((await (await page.request.get('/api/bff/api/tickets')).json() as unknown[]).length, 'the sample was not kept').toBe(ticketsBefore);

  // The PSA's one login is the integration's own account, which nobody will ever be linked to.
  // Said once, it is no longer listed as still to do.
  const technicians = card.locator('details', { hasText: 'Technicians' });
  await technicians.locator('summary').click();
  await expect(technicians).toContainText('0 of 1 linked');
  await technicians.getByRole('button', { name: 'Leave API Account alone' }).click();
  await expect(technicians).toContainText('none left to link · 1 left alone');
  await expect(technicians.getByRole('button', { name: /^Leave .* alone$/ })).toHaveCount(0);

  // In all of that, nothing was written to the PSA.
  expect(asked.filter((r) => r.method !== 'GET')).toEqual([]);

  await archive(page, name);
});

test('what a PSA files a ticket under means something here only by a rule, which is previewed before it is saved', async ({ page }) => {
  const stamp = Date.now().toString().slice(-7);
  const name = `E2E classify ${stamp}`;
  asked.length = 0;
  const wizard = await saveInSetup(page, name, standInBase, `cls${stamp}`);
  await expect(wizard.getByText('Nothing was changed in the PSA.')).toBeVisible({ timeout: 90_000 });
  await wizard.getByRole('button', { name: 'Close' }).click();
  const saved = (await (await page.request.get('/api/bff/api/admin/connections')).json() as { id: string; name: string }[]).find((c) => c.name === name)!;
  const api = `/api/bff/api/admin/connections/${saved.id}/classification`;
  const rulesNow = async () => (await (await page.request.get(api)).json() as { rules: Record<string, string | null>[] }).rules;

  await page.goto(`/dashboard/mappings?connection=${saved.id}&tab=classification`);
  const rules = page.getByRole('region', { name: 'Classification rules' });
  // ConnectWise's own names for its three levels, and the portal's three beside them.
  for (const heading of ['Type (PSA)', 'Subtype (PSA)', 'Item (PSA)', 'Category', 'Work type', 'Subcategory']) {
    await expect(rules.getByRole('columnheader', { name: heading, exact: true }).first()).toBeVisible();
  }
  await expect(rules.getByText(/No rules\. Every ticket keeps .* own classification and nothing is translated\./)).toBeVisible();
  await expect(rules.getByText('No ticket of this connection is filed under anything yet.')).toBeVisible();
  // The field-by-field table of the other tabs is not this tab's.
  await expect(page.getByRole('heading', { name: 'Classification Field Mapping' })).toHaveCount(0);

  // A rule for "everything else" is refused in the server's own words, and nothing is saved.
  await rules.getByRole('button', { name: 'Add a rule' }).click();
  await rules.getByLabel('Rule 1: Category', { exact: true }).fill('Everything');
  await expect(rules.getByText('Not saved yet')).toBeVisible();
  await rules.getByRole('button', { name: 'Save rules' }).click();
  await expect(rules.getByRole('alert')).toContainText('there is no rule for everything else');
  expect(await rulesNow()).toEqual([]);

  // Named, previewed - which saves nothing - and then saved.
  await rules.getByLabel('Rule 1: Type', { exact: true }).fill('Hardware');
  await rules.getByLabel('Rule 1: Subtype', { exact: true }).fill('Printer');
  await rules.getByLabel('Rule 1: Category', { exact: true }).fill('Support');
  await rules.getByLabel('Rule 1: Work type', { exact: true }).fill('Break/fix');
  await rules.getByRole('button', { name: 'Preview' }).click();
  await expect(rules.getByText('These rules would change no ticket already here.')).toBeVisible();
  await expect(rules.getByText('Nothing has been saved or changed.')).toBeVisible();
  expect(await rulesNow(), 'a preview is not a save').toEqual([]);
  await rules.getByRole('button', { name: 'Save rules' }).click();
  await expect(rules.getByText('Saved.')).toBeVisible();
  await expect(rules.getByText('Not saved yet')).toHaveCount(0);
  expect(await rulesNow()).toMatchObject([{ ticketType: 'Hardware', issueType: 'Printer', subIssueType: null, category: 'Support', workType: 'Break/fix', subcategory: null }]);

  await page.reload();
  await expect(rules.getByLabel('Rule 1: Type', { exact: true })).toHaveValue('Hardware');
  await expect(rules.getByLabel('Rule 1: Subtype', { exact: true })).toHaveValue('Printer');
  await expect(rules.getByLabel('Rule 1: Item', { exact: true })).toHaveValue('');
  await expect(rules.getByLabel('Rule 1: Work type', { exact: true })).toHaveValue('Break/fix');
  await expect(rules.getByText('1 rule names nothing any ticket is filed under')).toBeVisible();
  // Who changed the rules, and what, is in the audit log.
  const audited = await (await page.request.get('/api/bff/api/admin/audit')).json() as { action: string; entityId: string | null }[];
  expect(audited.filter((a) => a.action === 'classification.mapping.changed' && a.entityId === saved.id)).toHaveLength(1);

  // What tickets are filed under, and what applying does to the ones already here. No ticket
  // can be imported in this suite (see the test above for why), so the server's answers about
  // tickets are stood in for here; the answers themselves are held by ClassificationMappingTests
  // on a SQL translator. Everything above this line was the real API.
  const health = { rules: 1, classifiedTickets: 12, unmappedTickets: 7, classifications: 2, mappedClassifications: 1, coverage: 41.7, rulesMatchingNothing: 0, ticketsOutOfStep: 5 };
  const seen = [
    { ticketType: 'Software', issueType: 'Licence', subIssueType: null, tickets: 7, mapped: false, category: null, workType: null, subcategory: null },
    { ticketType: 'Hardware', issueType: 'Printer', subIssueType: 'Toner', tickets: 5, mapped: true, category: 'Support', workType: 'Break/fix', subcategory: null },
  ];
  let applied = false;
  await page.route(`**${api}`, async (route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const response = await route.fetch();
    const real = await response.json();
    await route.fulfill({ response, json: { ...real, seen, health: applied ? { ...health, ticketsOutOfStep: 0 } : health } });
  });
  await page.route(`**${api}/preview`, (route) => route.fulfill({
    json: {
      seen, health: { ...health, rules: 2, unmappedTickets: 0, mappedClassifications: 2, ticketsOutOfStep: 12 }, ticketsThatWouldChange: 12,
      sample: [{
        reference: '9001', title: 'Renew the design licences', ticketType: 'Software', issueType: 'Licence', subIssueType: null,
        categoryNow: 'Standard', categoryThen: 'Standard', workTypeNow: null, workTypeThen: 'Licensing', subcategoryNow: null, subcategoryThen: null,
      }],
    },
  }));
  await page.route(`**${api}/apply`, (route) => { applied = true; return route.fulfill({ json: { ticketsChanged: 5, changes: ['Hardware / Printer / Toner \u2192 Support, Break/fix, \u2013 (5)'] } }); });
  await page.reload();

  await expect(rules.getByText('5 of 12 classified tickets are named by a rule (41.7%) · 7 unmapped.')).toBeVisible();
  const unmapped = rules.locator('tr', { hasText: 'Licence' });
  await expect(unmapped.getByText('Unmapped', { exact: true })).toBeVisible();
  await expect(unmapped).not.toContainText('Support');
  await expect(rules.locator('tr', { hasText: 'Toner' })).toContainText('Category: Support · Work type: Break/fix');
  await rules.getByLabel('Unmapped only').check();
  await expect(rules.locator('tr', { hasText: 'Toner' })).toHaveCount(0);

  // Saved rules, and five tickets that do not hold what they say yet: put right when asked.
  await expect(rules.getByText('5 tickets already here hold something other than what the rules say.')).toBeVisible();
  await rules.getByRole('button', { name: 'Apply to tickets already here' }).click();
  await expect(rules.getByText('Changed 5 tickets.')).toBeVisible();
  await expect(rules.getByRole('button', { name: 'Apply to tickets already here' })).toHaveCount(0);

  // A rule is written from the unmapped line, with the PSA's levels filled in and nothing guessed for the portal's.
  await unmapped.getByRole('button', { name: 'Write a rule for Software / Licence / \u2013' }).click();
  await expect(rules.getByLabel('Rule 2: Type', { exact: true })).toHaveValue('Software');
  await expect(rules.getByLabel('Rule 2: Subtype', { exact: true })).toHaveValue('Licence');
  for (const target of ['Category', 'Work type', 'Subcategory']) await expect(rules.getByLabel(`Rule 2: ${target}`, { exact: true })).toHaveValue('');
  await expect(unmapped.getByRole('button', { name: /^Write a rule/ })).toHaveCount(0);
  await rules.getByLabel('Rule 2: Work type', { exact: true }).fill('Licensing');
  await rules.getByRole('button', { name: 'Preview' }).click();
  await expect(rules.getByText('These rules would change 12 tickets already here.')).toBeVisible();
  await expect(rules.getByText('#9001')).toBeVisible();
  await expect(rules.getByText('Category: Standard · Work type: Licensing')).toBeVisible();
  await rules.getByRole('button', { name: 'Discard' }).click();
  await expect(rules.getByLabel('Rule 2: Type', { exact: true })).toHaveCount(0);
  for (const url of [`**${api}`, `**${api}/preview`, `**${api}/apply`]) await page.unroute(url);

  // The connection's own mapping health counts the same rule. (Its card shows the line once the
  // connection is live: the test above reads it there.)
  const reported = await (await page.request.get(`/api/bff/api/admin/connections/${saved.id}/mapping-health`)).json() as { classification: Record<string, number> };
  expect(reported.classification).toMatchObject({ rules: 1, classifiedTickets: 0, unmappedTickets: 0, ticketsOutOfStep: 0 });

  // Taken away again: no rules, and the audit log has both changes.
  await page.goto(`/dashboard/mappings?connection=${saved.id}&tab=classification`);
  await rules.getByRole('button', { name: 'Remove rule 1' }).click();
  await rules.getByRole('button', { name: 'Save rules' }).click();
  await expect(rules.getByText('Saved.')).toBeVisible();
  expect(await rulesNow()).toEqual([]);

  // In all of that, nothing was written to the PSA.
  expect(asked.filter((r) => r.method !== 'GET')).toEqual([]);
  await page.goto('/dashboard/connections');
  await archive(page, name);
});
