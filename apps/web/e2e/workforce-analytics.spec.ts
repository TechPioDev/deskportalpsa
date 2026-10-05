import { test, expect, type APIResponse, type Page } from '@playwright/test';

/**
 * Phase 7 through the browser: the workforce analytics dashboard with planned and recorded work on
 * it, the cards opening the records that make them, a technician's detail, the filters in the query
 * string, My analytics for the person themselves, the CSV export, and an account without the
 * permission refused the pages and every endpoint.
 *
 * The signed-in administrator is the technician here (local mode has one account). Recorded time can
 * only be dated today or in the past and planned work only today or later, so the fixture lives on
 * "today": each browser plans its own hours (02-05, 06-09, 10-13 UTC: hours the My day spec, the
 * only other spec that plans on today, never uses in any browser) and every figure is asserted
 * relative to what the API itself answers, never as an absolute the other browsers could move.
 */
test.describe.configure({ timeout: 180_000 });

async function ask(send: () => Promise<APIResponse>): Promise<APIResponse> {
  for (let attempt = 0; attempt < 8; attempt++) {
    const res = await send();
    if (res.status() !== 429) return res;
    await new Promise((r) => setTimeout(r, 10_000));
  }
  return send();
}
const stamp = () => Date.now().toString().slice(-7);
const api = (path: string) => `/api/bff/api${path}`;
/** My day plans today at 01-02 and 13-15 (Chromium), 05-06 and 17-19 (Firefox), 09-10 and 21-23 (WebKit); these three hours per browser are free of all of them. */
const hourOffset = () => ({ chromium: 2, firefox: 6, webkit: 10 } as Record<string, number>)[test.info().project.name] ?? 2;
const hh = (h: number) => `${String(h + hourOffset()).padStart(2, '0')}:00`;
const EVERY_DAY = [0, 1, 2, 3, 4, 5, 6].map((day) => ({ day, start: '00:00', end: '23:55', breaks: [] as { start: string; end: string }[] }));
const todayUtc = () => new Date().toISOString().slice(0, 10);

type Overview = { period: { from: string; to: string; label: string }; totals: { plannedMinutes: number; actualSeconds: number; completedWork: number; reactiveActualSeconds: number; workItems: number }; people: { appUserId: string; displayName: string }[]; canExport: boolean; seesOthers: boolean };

async function me(page: Page) {
  return await (await ask(() => page.request.get(api('/me')))).json() as { userId: string; displayName: string; permissions: string[] };
}
async function allDaySchedule(page: Page, userId: string) {
  expect((await ask(() => page.request.put(api(`/workforce/people/${userId}/schedule`), { data: { timeZone: 'UTC', days: EVERY_DAY } }))).ok(), 'the schedule saves').toBeTruthy();
}
async function board(page: Page) {
  const s = stamp();
  const res = await ask(() => page.request.post(api('/boards'), { data: { name: `Analytics ${s}`, key: `A${s.slice(-6)}`, kind: 0 } }));
  expect(res.ok(), 'the board saves').toBeTruthy();
  return (await res.json() as { id: string }).id;
}
async function ticket(page: Page, boardId: string, title: string, holder?: string) {
  const res = await ask(() => page.request.post(api('/boards/tickets'), { data: { boardId, title, description: 'Workforce analytics test', priority: 'Medium', assignedAppUserId: holder ?? null } }));
  expect(res.ok(), 'the ticket saves').toBeTruthy();
  const created = await res.json() as { ticketId: string; number: string; title: string };
  return { id: created.ticketId, number: created.number, title: created.title };
}
/** Plans the hour; when a retried run finds its own earlier hour already there, the administrator overrides the double booking with a reason, as the product allows. */
async function plan(page: Page, ticketId: string, userId: string, date: string, from: string, to: string) {
  const body = { ticketId, appUserId: userId, start: `${date}T${from}:00Z`, end: `${date}T${to}:00Z` };
  let res = await ask(() => page.request.post(api('/workforce/plan'), { data: body }));
  if (res.status() === 409) res = await ask(() => page.request.post(api('/workforce/plan'), { data: { ...body, overrideReason: 'Analytics test run again' } }));
  expect(res.ok(), `planned: ${res.status()} ${await res.text()}`).toBeTruthy();
  return await res.json() as { id: string };
}
async function logTime(page: Page, ticketId: string, hours: number, notes: string) {
  const res = await ask(() => page.request.post(api(`/tickets/${ticketId}/time`), { data: { hours, billable: 'Billable', notes } }));
  expect(res.ok(), `time logged: ${res.status()} ${await res.text()}`).toBeTruthy();
}
async function close(page: Page, ticketId: string) {
  const res = await ask(() => page.request.post(api(`/tickets/${ticketId}/status`), { data: { status: 'CLOSED', resolution: 'Done' } }));
  expect(res.ok(), `closed: ${res.status()} ${await res.text()}`).toBeTruthy();
}
async function overview(page: Page, qs = 'period=today'): Promise<Overview> {
  const res = await ask(() => page.request.get(api(`/workforce/analytics/overview?${qs}`)));
  expect(res.ok(), `overview: ${res.status()} ${await res.text()}`).toBeTruthy();
  return await res.json() as Overview;
}
async function viewAs(page: Page, person: { id: string; displayName: string }) {
  await page.goto(`/dashboard/users/${person.id}`);
  await page.getByRole('button', { name: `View as ${person.displayName}` }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Viewing as' })).toBeVisible();
}
async function exitView(page: Page) {
  await page.getByRole('status').filter({ hasText: 'Viewing as' }).getByRole('button', { name: 'Exit view' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Viewing as' })).toHaveCount(0);
}
async function go(page: Page, url: string) {
  await expect(async () => { await page.goto(url); }).toPass({ timeout: 30_000 });
}
/** The value of one summary card, by its label. */
const figure = (page: Page, label: string) =>
  page.locator('dl[aria-label="Summary"] > div').filter({ has: page.locator('dt', { hasText: new RegExp(`^${label}$`, 'i') }) }).locator('dd').first();
const hoursText = (minutes: number) => (minutes > 0 && minutes < 60 ? `${minutes}m` : `${Math.floor(minutes / 60)}h${minutes % 60 ? ` ${String(minutes % 60).padStart(2, '0')}m` : ''}`);

/** Planned and recorded work for today: an hour planned on A with 90 minutes recorded, 30 minutes on B with nothing planned, B finished. Each test plans its own hour (`slot`), so two tests in one browser never clash. */
async function seed(page: Page, slot: number) {
  const who = await me(page);
  await allDaySchedule(page, who.userId);
  const s = stamp();
  const b = await board(page);
  const planned = await ticket(page, b, `Analytics planned ${s}`, who.userId);
  const reactive = await ticket(page, b, `Analytics reactive ${s}`, who.userId);
  await plan(page, planned.id, who.userId, todayUtc(), hh(slot), hh(slot + 1));
  await logTime(page, planned.id, 1.5, 'Planned work');
  await logTime(page, reactive.id, 0.5, 'Unplanned fix');
  await close(page, reactive.id);
  return { who, planned, reactive };
}

test('management sees today\'s cards reconcile with the API, opens the records behind them, filters, and reads a technician\'s detail', async ({ page }) => {
  const before = await overview(page);
  const { who, planned, reactive } = await seed(page, 0);
  const after = await overview(page);
  // The API's own facts moved by what was seeded (other browsers may add to the same day, never subtract).
  expect(after.totals.plannedMinutes - before.totals.plannedMinutes).toBeGreaterThanOrEqual(60);
  expect(after.totals.actualSeconds - before.totals.actualSeconds).toBeGreaterThanOrEqual(7200);
  expect(after.totals.reactiveActualSeconds - before.totals.reactiveActualSeconds).toBeGreaterThanOrEqual(1800);
  expect(after.totals.completedWork - before.totals.completedWork).toBeGreaterThanOrEqual(1);
  expect(after.seesOthers).toBeTruthy();

  await go(page, '/dashboard/workforce/analytics?period=today');
  await expect(page.getByRole('heading', { name: 'Workforce analytics' })).toBeVisible();
  // The cards say what the API says, in the same units.
  const shown = await overview(page);
  await expect(figure(page, 'Planned work')).toHaveText(hoursText(shown.totals.plannedMinutes));
  await expect(figure(page, 'Completed work')).toHaveText(String(shown.totals.completedWork));
  await expect(page.getByText(shown.period.label)).toBeVisible();
  await expect(page.getByRole('region', { name: 'Technicians' })).toContainText(who.displayName);
  await expect(page.getByRole('region', { name: 'Daily trend' })).toBeVisible();
  await expect(page.getByRole('img', { name: 'Capacity, planned and actual hours by day' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Capacity heatmap' }).getByRole('table')).toBeVisible();

  // Drill into actual work: the recorded entries, our two among them, planned and reactive said as such.
  await figure(page, 'Actual work').getByRole('button').click();
  const dialog = page.getByRole('dialog', { name: 'Recorded work' });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('row').filter({ hasText: planned.number })).toContainText('Planned');
  await expect(dialog.getByRole('row').filter({ hasText: reactive.number })).toContainText('Reactive');
  await expect(dialog.getByText(/\d+ records · /)).toBeVisible();
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toHaveCount(0);
  // Drill into completed work: the finished ticket, once.
  await figure(page, 'Completed work').getByRole('button').click();
  const done = page.getByRole('dialog', { name: 'Completed work' });
  await expect(done.getByRole('row').filter({ hasText: reactive.number })).toHaveCount(1);
  await done.getByRole('button', { name: 'Close' }).click();

  // Filters live in the address: the source filter narrows and is bookmarkable; the kind filter keeps planned minutes.
  await page.getByLabel('Source', { exact: true }).selectOption('internal');
  await expect(page).toHaveURL(/source=internal/);
  await expect(figure(page, 'Planned work')).toHaveText(hoursText((await overview(page, 'period=today&source=internal')).totals.plannedMinutes));
  await page.getByLabel('Kind of work', { exact: true }).selectOption('reactive');
  await expect(page).toHaveURL(/kind=reactive/);
  const reactiveOnly = await overview(page, 'period=today&source=internal&kind=reactive');
  await expect(figure(page, 'Planned work')).toHaveText(hoursText(reactiveOnly.totals.plannedMinutes));
  await page.getByRole('button', { name: 'Reset' }).click();
  await expect(page).not.toHaveURL(/kind=/);

  // A technician's detail: the work behind their figures, each ticket once.
  await go(page, `/dashboard/workforce/analytics?period=today`);
  await page.getByRole('region', { name: 'Technicians' }).getByRole('link', { name: who.displayName }).first().click();
  await expect(page.getByRole('heading', { name: 'Technician work analytics' })).toBeVisible();
  await page.getByLabel('Period', { exact: true }).selectOption('today');
  const items = page.getByRole('region', { name: 'Work in this period' });
  await expect(items).toContainText(planned.number);
  await expect(items).toContainText(reactive.number);
  await items.getByRole('button', { name: 'Completed' }).click();
  await expect(items).toContainText(reactive.number);
  await expect(items).not.toContainText(planned.number);
  await page.getByRole('link', { name: 'Workforce analytics' }).first().click();
  await expect(page.getByRole('heading', { name: 'Workforce analytics' })).toBeVisible();
});

test('a person sees their own analytics and the export is a CSV with the period in it', async ({ page }) => {
  const { who, planned } = await seed(page, 1);
  await go(page, '/dashboard/workforce/my-analytics?period=today');
  await expect(page.getByRole('heading', { name: 'My analytics' })).toBeVisible();
  await expect(figure(page, 'Planned')).toBeVisible();
  await expect(page.getByRole('region', { name: 'Work in this period' })).toContainText(planned.number);
  await expect(page.getByLabel('Technician', { exact: true })).toHaveCount(0);

  // A custom range arrives as dates; an incomplete or unknown period is refused, not guessed.
  const custom = await overview(page, `period=custom&from=${todayUtc()}&to=${todayUtc()}`);
  expect([custom.period.from, custom.period.to]).toEqual([todayUtc(), todayUtc()]);
  for (const bad of ['period=custom', 'period=custom&from=2026-02-30&to=2026-03-01', 'period=next-decade', 'period=today&kind=best', 'period=today&source=everything'])
    expect((await ask(() => page.request.get(api(`/workforce/analytics/overview?${bad}`)))).status(), bad).toBe(400);
  expect((await ask(() => page.request.get(api('/workforce/analytics/work?list=ranking&period=today')))).status()).toBe(400);
  expect((await ask(() => page.request.get(api('/workforce/analytics/people/00000000-0000-0000-0000-000000000001?period=today')))).status()).toBe(404);

  // The export: the same figures as a file, with a byte-order mark, the period and the person's name.
  const res = await ask(() => page.request.get(api('/workforce/analytics/export?report=technicians&period=today')));
  expect(res.status()).toBe(200);
  expect(res.headers()['content-type']).toContain('text/csv');
  expect(res.headers()['content-disposition']).toContain(`workforce-analytics-technicians-${todayUtc()}`);
  const body = await res.body();
  expect(body[0]).toBe(0xef);
  const text = body.toString('utf8').slice(1);
  expect(text.startsWith('Workforce analytics: technicians')).toBeTruthy();
  expect(text).toContain('Technician,Teams,Offered for planned work,Capacity (h)');
  expect(text).toContain(who.displayName);
});

test('an account without the scheduling permission is refused the analytics pages and every endpoint', async ({ page }) => {
  const s = stamp();
  const role = await (await ask(() => page.request.post(api('/admin/roles'), { data: { name: `Tickets only ${s}`, grants: [{ permissionKey: 'tickets.view.assigned', scope: 30 }] } }))).json() as { id: string };
  const outsider = await (await ask(() => page.request.post(api('/admin/users'), { data: { displayName: `No Figures ${s}`, email: `no.figures.${s}@techpio.test`, roleIds: [role.id] } }))).json() as { id: string; displayName: string };
  const work = await ticket(page, await board(page), `Hidden analytics ${s}`, outsider.id);
  const admin = await me(page);

  await viewAs(page, outsider);
  await expect(page.getByRole('link', { name: 'Workforce', exact: true })).toHaveCount(0);
  for (const url of ['/dashboard/workforce/analytics', '/dashboard/workforce/my-analytics', `/dashboard/workforce/analytics/${admin.userId}`]) {
    await go(page, url);
    await expect(page.getByRole('alert').first()).toBeVisible();
    await expect(page.getByText(work.number)).toHaveCount(0);
    await expect(page.getByText(admin.displayName, { exact: true })).toHaveCount(0);
  }
  for (const path of ['/workforce/analytics/filters', '/workforce/analytics/overview?period=today', `/workforce/analytics/people/${admin.userId}?period=today`, '/workforce/analytics/work?list=actual&period=today', '/workforce/analytics/export?report=technicians&period=today'])
    expect((await ask(() => page.request.get(api(path)))).status(), path).toBe(403);
  await exitView(page);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 375, height: 740 } });
  test('the dashboard stacks its cards and shows technicians as cards', async ({ page }) => {
    const { who } = await seed(page, 2);
    await go(page, '/dashboard/workforce/analytics?period=today');
    await expect(page.getByRole('heading', { name: 'Workforce analytics' })).toBeVisible();
    await expect(figure(page, 'Actual work')).toBeVisible();
    await expect(page.getByRole('list', { name: 'Technicians' })).toContainText(who.displayName);
    const width = await page.evaluate(() => document.documentElement.scrollWidth);
    expect(width, 'no sideways scroll').toBeLessThanOrEqual(375);
  });
});
