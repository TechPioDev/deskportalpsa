import { test, expect, type APIResponse, type Page } from '@playwright/test';

/**
 * Phase 8 through the browser: management insights (the capacity forecast, what needs attention,
 * the records behind each figure, the comparison, the quality signals and the planning data), the
 * report center with its preview and its CSV and XLSX exports, a person who sees only themselves,
 * and an account without the permission refused the pages and every endpoint.
 *
 * The signed-in administrator is the technician here (local mode has one account). A forecast looks
 * ahead, so the fixture is planned on days of its own: 111-137 days from now, a week per browser,
 * which no other spec plans on. Every figure is asserted against what the API itself answers for
 * the same question, never as an absolute that an earlier run could have moved.
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
const browserOffset = () => ({ chromium: 110, firefox: 120, webkit: 130 } as Record<string, number>)[test.info().project.name] ?? 140;
const isoDate = (daysAhead: number) => new Date(Date.now() + (daysAhead + browserOffset()) * 86_400_000).toISOString().slice(0, 10);
const EVERY_DAY = [0, 1, 2, 3, 4, 5, 6].map((day) => ({ day, start: '08:30', end: '17:30', breaks: [{ start: '12:30', end: '13:30' }] }));
/** The week the fixture is planned in, as the page and the API are asked for it. */
const windowQs = () => `window=custom&from=${isoDate(1)}&to=${isoDate(7)}`;

type Figures = { capacityMinutes: number | null; confirmedMinutes: number; tentativeMinutes: number; unscheduledMinutes: number; unscheduledItems: number; unestimatedItems: number; projectedMinutes: number; gapMinutes: number | null; projectedPercent: number | null };
type Forecast = { window: { from: string; to: string; label: string }; totals: Figures; people: { appUserId: string; displayName: string; figures: Figures }[]; attention: { key: string; severity: number; title: string; rule: string }[]; seesOthers: boolean; canExport: boolean; canSeeHealth: boolean; unassigned: Figures | null };

async function me(page: Page) {
  return await (await ask(() => page.request.get(api('/me')))).json() as { userId: string; displayName: string; permissions: string[] };
}
async function board(page: Page) {
  const s = stamp();
  const res = await ask(() => page.request.post(api('/boards'), { data: { name: `Insights ${s}`, key: `N${s.slice(-6)}`, kind: 0 } }));
  expect(res.ok(), 'the board saves').toBeTruthy();
  return (await res.json() as { id: string }).id;
}
async function ticket(page: Page, boardId: string, title: string, holder?: string) {
  const res = await ask(() => page.request.post(api('/boards/tickets'), { data: { boardId, title, description: 'Management insights test', priority: 'Medium', assignedAppUserId: holder ?? null } }));
  expect(res.ok(), 'the ticket saves').toBeTruthy();
  const created = await res.json() as { ticketId: string; number: string; title: string };
  return { id: created.ticketId, number: created.number, title: created.title };
}
/** Plans the time; when a retried run finds its own earlier plan already there, the administrator overrides the double booking with a reason, as the product allows. */
async function plan(page: Page, ticketId: string, userId: string, date: string, from: string, to: string, tentative = false) {
  const body = { ticketId, appUserId: userId, start: `${date}T${from}:00Z`, end: `${date}T${to}:00Z`, tentative };
  let res = await ask(() => page.request.post(api('/workforce/plan'), { data: body }));
  if (res.status() === 409) res = await ask(() => page.request.post(api('/workforce/plan'), { data: { ...body, overrideReason: 'Insights test run again' } }));
  expect(res.ok(), `planned: ${res.status()} ${await res.text()}`).toBeTruthy();
}
async function estimate(page: Page, ticketId: string, minutes: number) {
  const res = await ask(() => page.request.put(api(`/workforce/plan/requirements/${ticketId}`), { data: { requiredMinutes: minutes, splittable: true } }));
  expect(res.ok(), `estimated: ${res.status()} ${await res.text()}`).toBeTruthy();
}
async function forecast(page: Page, qs = windowQs()): Promise<Forecast> {
  const res = await ask(() => page.request.get(api(`/workforce/insights/forecast?${qs}`)));
  expect(res.ok(), `forecast: ${res.status()} ${await res.text()}`).toBeTruthy();
  return await res.json() as Forecast;
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
/** The value of one forecast summary card, by its label. */
const figure = (page: Page, label: string) =>
  page.locator('dl[aria-label="Forecast summary"] > div').filter({ has: page.locator('dt', { hasText: new RegExp(`^${label}$`, 'i') }) }).locator('dd').first();
const hoursText = (minutes: number) => (minutes > 0 ? (minutes < 60 ? `${minutes}m` : `${Math.floor(minutes / 60)}h${minutes % 60 ? ` ${String(minutes % 60).padStart(2, '0')}m` : ''}`) : '0m');

/**
 * A week of the administrator's own work, 111 or more days ahead: A is sized at five hours with two
 * confirmed and one tentative (two hours still to allocate); B is sized at four hours with nothing
 * planned; C has neither an estimate nor a plan.
 */
async function seed(page: Page) {
  const who = await me(page);
  expect((await ask(() => page.request.put(api(`/workforce/people/${who.userId}/schedule`), { data: { timeZone: 'UTC', days: EVERY_DAY } }))).ok(), 'the schedule saves').toBeTruthy();
  const s = stamp();
  const b = await board(page);
  const planned = await ticket(page, b, `Insights planned ${s}`, who.userId);
  const waiting = await ticket(page, b, `Insights waiting ${s}`, who.userId);
  const unsized = await ticket(page, b, `Insights unsized ${s}`, who.userId);
  await estimate(page, planned.id, 300);
  await estimate(page, waiting.id, 240);
  await plan(page, planned.id, who.userId, isoDate(2), '09:00', '11:00');
  await plan(page, planned.id, who.userId, isoDate(3), '14:00', '15:00', true);
  return { who, planned, waiting, unsized };
}

test('management reads the forecast, opens the records behind it, sees why something needs attention, and moves through the tabs', async ({ page }) => {
  const before = await forecast(page);
  const { who, planned, waiting, unsized } = await seed(page);
  const after = await forecast(page);
  // The API's own facts moved by what was seeded (an earlier run may have added to the same week, never taken away).
  expect(after.totals.confirmedMinutes - before.totals.confirmedMinutes).toBeGreaterThanOrEqual(120);
  expect(after.totals.tentativeMinutes - before.totals.tentativeMinutes).toBeGreaterThanOrEqual(60);
  expect(after.totals.unscheduledMinutes - before.totals.unscheduledMinutes).toBe(120 + 240);
  expect(after.totals.unscheduledItems - before.totals.unscheduledItems).toBe(2);
  expect(after.totals.unestimatedItems - before.totals.unestimatedItems).toBe(1);
  // Every component of the projection is its own figure, and they add up.
  expect(after.totals.projectedMinutes).toBe(after.totals.confirmedMinutes + after.totals.tentativeMinutes + after.totals.unscheduledMinutes);
  expect(after.totals.gapMinutes).toBe((after.totals.capacityMinutes ?? 0) - after.totals.projectedMinutes);
  expect(after.people.reduce((sum, p) => sum + p.figures.projectedMinutes, 0) + (after.unassigned?.unscheduledMinutes ?? 0)).toBe(after.totals.projectedMinutes);
  expect([after.window.from, after.window.to]).toEqual([isoDate(1), isoDate(7)]);
  expect(after.seesOthers).toBeTruthy();

  await go(page, `/dashboard/workforce/insights?${windowQs()}`);
  await expect(page.getByRole('heading', { name: 'Management insights' })).toBeVisible();
  // The cards say what the API says, in the same units.
  const shown = await forecast(page);
  await expect(page.getByText(shown.window.label)).toBeVisible();
  await expect(figure(page, 'Confirmed')).toHaveText(hoursText(shown.totals.confirmedMinutes));
  await expect(figure(page, 'Tentative')).toHaveText(hoursText(shown.totals.tentativeMinutes));
  await expect(figure(page, 'Est. unscheduled')).toHaveText(hoursText(shown.totals.unscheduledMinutes));
  await expect(figure(page, 'Unestimated')).toHaveText(String(shown.totals.unestimatedItems));
  await expect(figure(page, 'Projected demand')).toHaveText(hoursText(shown.totals.projectedMinutes));
  await expect(figure(page, 'Capacity gap')).toHaveText(/ (left|short)$/);
  await expect(page.getByRole('img', { name: 'Capacity, confirmed, tentative and unscheduled hours by day' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Forecast by technician' })).toContainText(who.displayName);
  await expect(page.getByRole('region', { name: 'Schedule coverage' })).toBeVisible();

  // What needs attention: a statement, its severity as a word, the rule and the numbers behind it, and the records.
  const attention = page.getByRole('region', { name: 'Needs attention' });
  const unscheduled = attention.getByRole('listitem').filter({ hasText: 'has no time allocated' });
  await expect(unscheduled).toContainText('Watch');
  await unscheduled.getByText('Why this is shown').click();
  await expect(unscheduled).toContainText('Estimated effort of open work less the time already allocated to it');
  await expect(unscheduled).toContainText('Estimated unscheduled');
  await unscheduled.getByRole('button', { name: 'Show the work' }).click();
  const list = page.getByRole('dialog', { name: 'Estimated work with no time allocated' });
  await expect(list).toBeVisible();
  await expect(list.getByRole('button', { name: 'Close' })).toBeFocused();
  await expect(list.getByRole('row').filter({ hasText: planned.number })).toContainText('2h');
  await expect(list.getByRole('row').filter({ hasText: planned.number })).toContainText('of 5h estimated');
  await expect(list.getByRole('row').filter({ hasText: waiting.number })).toContainText('of 4h estimated');
  await expect(list.getByRole('row').filter({ hasText: unsized.number })).toHaveCount(0);
  await expect(list.getByText(new RegExp(`${shown.totals.unscheduledItems} work items? · ${hoursText(shown.totals.unscheduledMinutes)}`))).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(list).toHaveCount(0);
  await expect(unscheduled.getByRole('button', { name: 'Show the work' })).toBeFocused();

  // Confirmed and tentative are never merged: each opens its own records.
  await figure(page, 'Confirmed').getByRole('button').click();
  const confirmed = page.getByRole('dialog', { name: 'Confirmed work ahead' });
  await expect(confirmed.getByRole('row').filter({ hasText: planned.number })).toContainText('Planned');
  await confirmed.getByRole('button', { name: 'Close' }).click();
  await figure(page, 'Tentative').getByRole('button').click();
  const tentative = page.getByRole('dialog', { name: 'Tentative work ahead' });
  await expect(tentative.getByRole('row').filter({ hasText: planned.number })).toContainText('Tentative');
  await tentative.getByRole('button', { name: 'Close' }).click();
  // Unestimated work is a count that opens the items, never hours.
  await figure(page, 'Unestimated').getByRole('button').click();
  const unknown = page.getByRole('dialog', { name: 'Open work with no estimate and no plan' });
  await expect(unknown.getByRole('row').filter({ hasText: unsized.number })).toContainText('No estimate');
  await expect(unknown.getByRole('row').filter({ hasText: waiting.number })).toHaveCount(0);
  await unknown.getByRole('button', { name: 'Close' }).click();

  // The tabs live in the address, so a view can be bookmarked and shared inside the team.
  await page.getByRole('tab', { name: 'What changed' }).click();
  await expect(page).toHaveURL(/view=history/);
  const compared = page.getByRole('region', { name: 'This period against the one before' });
  await expect(compared.getByRole('row').filter({ hasText: 'Actual work' })).toBeVisible();
  await expect(compared.getByRole('row').filter({ hasText: 'Reactive share' })).toBeVisible();
  await expect(page.getByRole('img', { name: 'Planned actual and reactive hours by week' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Estimate variance' })).toContainText('never attributed to a person');
  await page.getByRole('tab', { name: 'Quality signals' }).click();
  await expect(page).toHaveURL(/view=quality/);
  const signals = page.getByRole('list', { name: 'Quality signals' }).getByRole('listitem');
  await expect(signals).toHaveCount(7);
  await expect(signals.filter({ hasText: 'Escalated work' })).toContainText('Not available');
  await expect(signals.filter({ hasText: 'Escalated work' })).toContainText('No escalation is recorded anywhere');
  await page.getByRole('tab', { name: 'Data and integrations' }).click();
  await expect(page).toHaveURL(/view=health/);
  await expect(page.getByRole('region', { name: 'Planning data' })).toContainText('Estimated');
  await page.getByRole('tab', { name: 'Forecast' }).click();
  await expect(page).not.toHaveURL(/view=/);

  // The window and the filters live in the address too.
  await page.getByLabel('Forecast window', { exact: true }).selectOption('next-14');
  await expect(page).toHaveURL(/window=next-14/);
  await expect(page.getByText((await forecast(page, 'window=next-14')).window.label)).toBeVisible();
  await page.getByLabel('Source', { exact: true }).selectOption('internal');
  await expect(page).toHaveURL(/source=internal/);
  await page.getByRole('button', { name: 'Reset' }).click();
  await expect(page).not.toHaveURL(/window=|source=/);
});

test('the report center previews a report under its filters and exports the same rows as CSV and XLSX', async ({ page }) => {
  const { who } = await seed(page);
  const catalogue = await (await ask(() => page.request.get(api('/workforce/reports')))).json() as { key: string; category: string; title: string }[];
  expect(catalogue.map((r) => r.key)).toEqual(expect.arrayContaining(['workforce-utilization', 'technician-work-summary', 'team-work-summary', 'capacity-demand', 'future-capacity', 'client-workload',
    'planned-vs-actual', 'reactive-work', 'estimate-variance', 'work-sources', 'operational-quality']));

  await go(page, '/dashboard/workforce/reports');
  await expect(page.getByRole('heading', { name: 'Reports', exact: true })).toBeVisible();
  for (const category of ['Workforce', 'Capacity', 'Clients', 'Delivery', 'Quality'])
    await expect(page.getByRole('region', { name: category, exact: true })).toBeVisible();
  await page.getByRole('link', { name: /Future capacity by technician/ }).click();
  await expect(page).toHaveURL(/\/dashboard\/workforce\/reports\/future-capacity/);
  await expect(page.getByRole('heading', { name: 'Future capacity by technician' })).toBeVisible();

  // The preview under the fixture's week: the forecast's own figures, the person's row, what was applied.
  await go(page, `/dashboard/workforce/reports/future-capacity?${windowQs()}`);
  const fc = await forecast(page);
  const preview = page.getByRole('region', { name: 'Preview' });
  await expect(preview.getByRole('row').filter({ hasText: who.displayName })).toBeVisible();
  await expect(page.getByText(fc.window.label)).toBeVisible();
  const summary = page.locator('dl[aria-label="Summary"]');
  await expect(summary).toContainText('Projected demand');
  await expect(summary).toContainText(`${Math.round((fc.totals.projectedMinutes / 60) * 100) / 100} h`);
  await expect(page.getByRole('list', { name: 'Applied filters' })).toContainText('None: everyone and all the work you may see');
  await expect(page.getByRole('region', { name: 'What this report covers' })).toContainText('not a judgement of the person');

  // The same report as a file, through the button a person uses.
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Export CSV' }).click()]);
  expect(download.suggestedFilename()).toBe(`pio-manage-future-capacity-${isoDate(1)}-${isoDate(7)}.csv`);

  // And through the API: a byte-order mark, the title, the period, the same people; a workbook for XLSX.
  const csv = await ask(() => page.request.get(api(`/workforce/reports/future-capacity/export?format=csv&${windowQs()}`)));
  expect(csv.status()).toBe(200);
  expect(csv.headers()['content-type']).toContain('text/csv');
  expect(csv.headers()['content-disposition']).toContain(`pio-manage-future-capacity-${isoDate(1)}-${isoDate(7)}.csv`);
  const body = await csv.body();
  expect(body[0]).toBe(0xef);
  const text = body.toString('utf8').slice(1);
  expect(text.startsWith('PIO MANAGE: Future capacity by technician')).toBeTruthy();
  expect(text).toContain('Technician,Teams,Offered for planned work,Capacity (h),Confirmed (h)');
  expect(text).toContain(who.displayName);
  const report = await (await ask(() => page.request.get(api(`/workforce/reports/future-capacity?${windowQs()}`)))).json() as { totalRows: number; truncated: boolean; rows: unknown[][] };
  expect([report.rows.length, report.truncated]).toEqual([report.totalRows, false]);
  for (const row of report.rows) expect(text, 'every previewed row is in the file').toContain(String(row[0]));
  const xlsx = await ask(() => page.request.get(api(`/workforce/reports/future-capacity/export?format=xlsx&${windowQs()}`)));
  expect(xlsx.status()).toBe(200);
  expect(xlsx.headers()['content-type']).toContain('spreadsheetml.sheet');
  expect(xlsx.headers()['content-disposition']).toContain('.xlsx');
  const sheet = await xlsx.body();
  expect([sheet[0], sheet[1]], 'a workbook is a zip').toEqual([0x50, 0x4b]);

  // A report that compares, with its own grouping in the address.
  await go(page, '/dashboard/workforce/reports/estimate-variance');
  await page.getByLabel('Group by', { exact: true }).selectOption('client');
  await expect(page).toHaveURL(/by=client/);
  await expect(page.getByRole('list', { name: 'Applied filters' })).toContainText('Grouped by: client');
  await page.getByLabel('Comparison', { exact: true }).selectOption('last-7');
  await expect(page).toHaveURL(/compare=last-7/);
  await page.getByRole('link', { name: 'All reports' }).click();
  await expect(page).toHaveURL(/\/dashboard\/workforce\/reports$/);

  // Nothing is guessed: an unknown report, format, window, list, comparison or person is refused.
  for (const [path, status] of [
    ['/workforce/reports/payroll', 404], ['/workforce/reports/payroll/export?format=csv', 404], ['/workforce/reports/future-capacity/export?format=pdf', 400],
    ['/workforce/reports/estimate-variance?by=technician', 400], ['/workforce/insights/forecast?window=custom', 400], ['/workforce/insights/forecast?window=next-year', 400],
    ['/workforce/insights/forecast?window=custom&from=2020-01-01&to=2020-01-05', 400], ['/workforce/insights/forecast/work?list=ranking', 400], ['/workforce/insights/forecast/work?list=skill', 400],
    ['/workforce/insights/forecast/work?list=skill&skillId=00000000-0000-0000-0000-000000000001', 404], ['/workforce/insights/trends?compare=forever', 400],
    ['/workforce/insights/forecast?appUserId=00000000-0000-0000-0000-000000000001', 404], ['/workforce/insights/forecast?clientId=00000000-0000-0000-0000-000000000001', 404],
  ] as const)
    expect((await ask(() => page.request.get(api(path)))).status(), path).toBe(status);
});

test('a person who sees only their own schedule gets their own forecast, no export and no integration health', async ({ page }) => {
  const admin = await me(page);
  const s = stamp();
  const role = await (await ask(() => page.request.post(api('/admin/roles'), { data: { name: `Own schedule ${s}`, grants: [{ permissionKey: 'schedule.view', scope: 40 }, { permissionKey: 'tickets.view.assigned', scope: 30 }] } }))).json() as { id: string };
  const person = await (await ask(() => page.request.post(api('/admin/users'), { data: { displayName: `Own Forecast ${s}`, email: `own.forecast.${s}@techpio.test`, roleIds: [role.id] } }))).json() as { id: string; displayName: string };
  const theirs = await ticket(page, await board(page), `Own insights ${s}`, person.id);

  await viewAs(page, person);
  const own = await forecast(page);
  expect(own.people.map((p) => p.displayName)).toEqual([person.displayName]);
  expect([own.seesOthers, own.canExport, own.canSeeHealth, own.unassigned]).toEqual([false, false, false, null]);
  expect(own.totals.unestimatedItems).toBe(1);
  // Nobody else by any filter, no file, no integration health; the catalogue does not offer what they may not open.
  for (const [path, status] of [
    [`/workforce/insights/forecast?appUserId=${admin.userId}`, 404], [`/workforce/insights/trends?appUserId=${admin.userId}`, 404], [`/workforce/reports/future-capacity?appUserId=${admin.userId}`, 404],
    ['/workforce/insights/health', 403], ['/workforce/reports/future-capacity/export?format=csv', 403], ['/workforce/reports/future-capacity/export?format=xlsx', 403], ['/workforce/reports/integration-health', 403],
    ['/workforce/insights/trends', 200], ['/workforce/reports/future-capacity', 200],
  ] as const)
    expect((await ask(() => page.request.get(api(path)))).status(), path).toBe(status);
  const offered = await (await ask(() => page.request.get(api('/workforce/reports')))).json() as { key: string }[];
  expect(offered.map((r) => r.key)).not.toContain('integration-health');

  await go(page, '/dashboard/workforce/insights');
  await expect(page.getByRole('region', { name: 'Your forecast' })).toContainText(person.displayName);
  await expect(page.getByText(admin.displayName, { exact: true })).toHaveCount(0);
  await expect(page.getByLabel('Technician', { exact: true })).toHaveCount(0);
  await figure(page, 'Unestimated').getByRole('button').click();
  await expect(page.getByRole('dialog', { name: 'Open work with no estimate and no plan' }).getByRole('row').filter({ hasText: theirs.number })).toBeVisible();
  await page.keyboard.press('Escape');
  await go(page, '/dashboard/workforce/reports/future-capacity');
  await expect(page.getByRole('region', { name: 'Preview' })).toContainText(person.displayName);
  await expect(page.getByRole('button', { name: 'Export CSV' })).toHaveCount(0);
  await exitView(page);
});

test('an account without the scheduling permission is refused the insights and report pages and every endpoint', async ({ page }) => {
  const s = stamp();
  const role = await (await ask(() => page.request.post(api('/admin/roles'), { data: { name: `Tickets only ${s}`, grants: [{ permissionKey: 'tickets.view.assigned', scope: 30 }] } }))).json() as { id: string };
  const outsider = await (await ask(() => page.request.post(api('/admin/users'), { data: { displayName: `No Insights ${s}`, email: `no.insights.${s}@techpio.test`, roleIds: [role.id] } }))).json() as { id: string; displayName: string };
  const work = await ticket(page, await board(page), `Hidden insights ${s}`, outsider.id);
  const admin = await me(page);

  await viewAs(page, outsider);
  await expect(page.getByRole('link', { name: 'Workforce', exact: true })).toHaveCount(0);
  for (const url of ['/dashboard/workforce/insights', '/dashboard/workforce/reports', '/dashboard/workforce/reports/future-capacity']) {
    await go(page, url);
    await expect(page.getByRole('alert').first()).toBeVisible();
    await expect(page.getByText(work.number)).toHaveCount(0);
    await expect(page.getByText(admin.displayName, { exact: true })).toHaveCount(0);
  }
  for (const path of ['/workforce/insights/forecast', '/workforce/insights/forecast/work?list=unscheduled', '/workforce/insights/trends', '/workforce/insights/health', '/workforce/reports',
    '/workforce/reports/future-capacity', '/workforce/reports/future-capacity/export?format=csv', '/workforce/reports/operational-quality/export?format=xlsx'])
    expect((await ask(() => page.request.get(api(path)))).status(), path).toBe(403);
  await exitView(page);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 375, height: 740 } });
  test('insights stack their cards, show technicians as cards, and a report scrolls inside its own frame', async ({ page }) => {
    const { who } = await seed(page);
    await go(page, `/dashboard/workforce/insights?${windowQs()}`);
    await expect(page.getByRole('heading', { name: 'Management insights' })).toBeVisible();
    await expect(figure(page, 'Projected demand')).toBeVisible();
    await expect(page.getByRole('list', { name: 'Forecast by technician' })).toContainText(who.displayName);
    expect(await page.evaluate(() => document.documentElement.scrollWidth), 'no sideways scroll on insights').toBeLessThanOrEqual(375);
    await page.getByRole('tab', { name: 'Quality signals' }).click();
    await expect(page.getByRole('list', { name: 'Quality signals' })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth), 'no sideways scroll on quality signals').toBeLessThanOrEqual(375);

    await go(page, `/dashboard/workforce/reports/future-capacity?${windowQs()}`);
    await expect(page.getByRole('region', { name: 'Preview' })).toContainText(who.displayName);
    expect(await page.evaluate(() => document.documentElement.scrollWidth), 'the table scrolls in its own frame, not the page').toBeLessThanOrEqual(375);
  });
});
