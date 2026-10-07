import { test, expect } from '@playwright/test';

/**
 * A client user is given a further company by the desk, and chooses which of their companies to
 * look at.
 *
 * What a grant lets someone see, and everything it refuses, is held on the server by
 * ClientCompanyAccessTests through the real rule for what a client may see. This suite cannot
 * sign in as a client (the local sign-in is an administrator, and importing tickets would turn it
 * into a client for every test after it), so two things are checked here:
 *
 * - against the real API: the desk's page loads, and the API refuses to give a company to a user
 *   that does not exist, and refuses a client's own choice of company when there is no such grant;
 * - with the API's answers stood in: what the page sends when a company is given, narrowed and
 *   taken away, and what the switcher does when a client chooses a company.
 */
const api = '/api/bff/api/admin/client-access';
const priya = '11111111-1111-4111-8111-111111111111';
const acme = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const bolt = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const cord = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';

test('the desk gives a client user a further company, and the least first', async ({ page }) => {
  // The real API first: the page is there for an administrator, and nothing can be given to nobody.
  await page.goto('/dashboard/client-access');
  await expect(page.getByRole('heading', { name: 'Client access to more than one company' })).toBeVisible();
  await expect(page.locator('a[href="/dashboard/client-access"]').first()).toBeAttached();
  expect((await page.request.get(`${api}/users`)).ok()).toBeTruthy();
  expect((await page.request.put(`${api}/users/${priya}/companies/${bolt}`, { data: { seesAllTickets: true, canCreate: true } })).status()).toBe(404);
  expect((await page.request.delete(`${api}/users/${priya}/companies/${bolt}`)).status()).toBe(404);
  // An administrator is not a client: there are no companies of their own to choose between.
  expect(await (await page.request.get('/api/bff/api/my-companies')).json()).toEqual([]);

  // The page itself, over stood-in answers.
  type Grant = { companyId: string; companyName: string; seesAllTickets: boolean; canCreate: boolean; grantedAt: string };
  const grants: Grant[] = [];
  const sent: { method: string; company: string; body: unknown }[] = [];
  const user = () => ({ id: priya, displayName: 'Priya Nair', email: 'priya@acme.test', isActive: true, hasSignedIn: true, homeCompanyId: acme, homeCompanyName: 'Acme', grants });
  await page.route(`**${api}/users*`, (route) => route.fulfill({ json: [user()] }));
  await page.route(`**${api}/companies`, (route) => route.fulfill({ json: [{ id: acme, name: 'Acme' }, { id: bolt, name: 'Bolt' }, { id: cord, name: 'Cord' }] }));
  await page.route(`**${api}/users/${priya}/companies/*`, async (route) => {
    const company = route.request().url().split('/').pop()!;
    const method = route.request().method();
    const body = method === 'PUT' ? route.request().postDataJSON() as { seesAllTickets: boolean; canCreate: boolean } : null;
    sent.push({ method, company, body });
    const at = grants.findIndex((g) => g.companyId === company);
    if (method === 'DELETE') { if (at >= 0) grants.splice(at, 1); }
    else if (at >= 0) Object.assign(grants[at], body);
    else grants.push({ companyId: company, companyName: company === bolt ? 'Bolt' : 'Cord', grantedAt: new Date().toISOString(), ...body! });
    await route.fulfill({ json: user() });
  });
  await page.reload();

  const row = page.getByRole('listitem').filter({ hasText: 'Priya Nair' });
  await expect(row.getByText('priya@acme.test · Acme')).toBeVisible();
  await expect(row.getByText('Their own company only')).toBeVisible();
  await row.getByRole('button', { name: /Priya Nair/ }).click();
  const panel = page.getByRole('group', { name: 'Companies Priya Nair can see' });
  await expect(panel.getByText('is their own company.')).toBeVisible();

  // Her own company is not on offer: it needs no grant. Given with nothing ticked, a grant is the least it can be.
  const choose = panel.getByLabel('Company to give Priya Nair');
  await expect(choose.locator('option')).toHaveText(['Choose…', 'Bolt', 'Cord']);
  await expect(panel.getByRole('button', { name: 'Give access' })).toBeDisabled();
  await choose.selectOption({ label: 'Bolt' });
  await panel.getByRole('button', { name: 'Give access' }).click();
  await expect(panel.getByLabel('Bolt: sees every ticket')).not.toBeChecked();
  await expect(panel.getByLabel('Bolt: may raise and reply')).not.toBeChecked();
  await expect(panel.getByText('With neither ticked they see only the tickets they themselves raised in that company, and cannot change anything.')).toBeVisible();
  expect(sent).toEqual([{ method: 'PUT', company: bolt, body: { seesAllTickets: false, canCreate: false } }]);
  await expect(row.getByText('Also: Bolt')).toBeVisible();
  await expect(choose.locator('option')).toHaveText(['Choose…', 'Cord']);

  // Each thing more is said separately, and each is one request that says both.
  // The box follows what the server has saved, so it is clicked and then waited for, not set.
  await panel.getByLabel('Bolt: sees every ticket').click();
  await expect(panel.getByLabel('Bolt: sees every ticket')).toBeChecked();
  expect(sent.at(-1)).toEqual({ method: 'PUT', company: bolt, body: { seesAllTickets: true, canCreate: false } });
  await panel.getByLabel('Bolt: may raise and reply').click();
  await expect(panel.getByLabel('Bolt: may raise and reply')).toBeChecked();
  expect(sent.at(-1)).toEqual({ method: 'PUT', company: bolt, body: { seesAllTickets: true, canCreate: true } });

  // Taken away.
  await panel.getByRole('button', { name: 'Take Bolt away from Priya Nair' }).click();
  await expect(row.getByText('Their own company only')).toBeVisible();
  expect(sent.at(-1)).toEqual({ method: 'DELETE', company: bolt, body: null });
  await expect(choose.locator('option')).toHaveText(['Choose…', 'Bolt', 'Cord']);
});

test('a client with more than one company chooses which to look at, and one with a single company is shown no choice', async ({ page, context }) => {
  // The signed-in person stood in as a client: no staff permission at all.
  const asClient = async (companies: unknown[]) => {
    await page.route('**/api/bff/api/me', async (route) => {
      const response = await route.fetch();
      await route.fulfill({ response, json: { ...(await response.json()), permissions: ['tickets.create', 'tickets.note.public.add'] } });
    });
    await page.route('**/api/bff/api/my-companies', (route) => route.fulfill({ json: companies }));
  };
  const chosen = async () => (await context.cookies()).find((c) => c.name === 'desk_company')?.value ?? null;
  const own = { id: acme, name: 'Acme', isOwn: true, seesAllTickets: true, canCreate: true };

  // One company, which is nearly everyone: nothing to choose, and nothing shown.
  await asClient([own]);
  await page.goto('/dashboard');
  await expect(page.locator('header').first()).toBeVisible();
  await page.waitForLoadState('networkidle');
  await expect(page.getByLabel('Company', { exact: true })).toHaveCount(0);

  // Given two more: their own first, and the one they may only look at says so.
  await page.unroute('**/api/bff/api/my-companies');
  await page.route('**/api/bff/api/my-companies', (route) => route.fulfill({
    json: [own, { id: bolt, name: 'Bolt', isOwn: false, seesAllTickets: true, canCreate: true }, { id: cord, name: 'Cord', isOwn: false, seesAllTickets: false, canCreate: false }],
  }));
  await page.reload();
  const company = page.getByLabel('Company', { exact: true });
  await expect(company).toHaveValue(acme);
  await expect(company.locator('option')).toHaveText(['Acme', 'Bolt', 'Cord (view only)']);
  expect(await chosen()).toBeNull();

  // Choosing one keeps the choice where every request carries it, and loads the portal afresh.
  await Promise.all([page.waitForURL('**/dashboard'), company.selectOption(cord)]);
  await expect(page.getByLabel('Company', { exact: true })).toHaveValue(cord);
  expect(await chosen()).toBe(cord);
  await expect(page.getByText('View only', { exact: true })).toBeVisible();

  // Back to their own: the choice is dropped, not kept as "their own".
  await Promise.all([page.waitForURL('**/dashboard'), page.getByLabel('Company', { exact: true }).selectOption(acme)]);
  await expect(page.getByLabel('Company', { exact: true })).toHaveValue(acme);
  expect(await chosen()).toBeNull();

  // A company that was chosen and has since been taken away is let go of.
  await context.addCookies([{ name: 'desk_company', value: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd', url: page.url() }]);
  await page.reload();
  await expect(page.getByLabel('Company', { exact: true })).toHaveValue(acme);
  await expect.poll(chosen).toBeNull();
});
