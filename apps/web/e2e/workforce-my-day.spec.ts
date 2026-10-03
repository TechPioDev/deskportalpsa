import { test, expect, type APIResponse, type Page } from '@playwright/test';

/**
 * Phase 6 through the browser: My Day with planned work, the clock (start, pause, resume, a reload
 * that recovers it, stop), time typed in by hand against the plan, unplanned work started from the
 * unscheduled list, a second tab refused a second clock, Team today for a manager, and an account
 * without the permission refused the page and every endpoint.
 *
 * The signed-in administrator is the technician here (local mode has one account): they get a
 * schedule and plan their own day. Everyone works 08:30-17:30 UTC; this spec plans on its own days
 * (offsets 110/120/130) except the clock flow, which needs "today".
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
/** The three browsers share one account and the same "today": each plans its own hours, so nothing overlaps. */
const hourOffset = () => ({ chromium: 0, firefox: 4, webkit: 8 } as Record<string, number>)[test.info().project.name] ?? 12;
const hh = (h: number) => `${String(h + hourOffset()).padStart(2, '0')}:00`;
const EVERY_DAY = [0, 1, 2, 3, 4, 5, 6].map((day) => ({ day, start: '00:00', end: '23:55', breaks: [] as { start: string; end: string }[] }));
const todayUtc = () => new Date().toISOString().slice(0, 10);

async function me(page: Page) {
  return await (await ask(() => page.request.get(api('/me')))).json() as { userId: string; permissions: string[] };
}
/** The administrator works all day in UTC, so a clock started at any hour of the test is inside the schedule and "today" holds the plan. */
async function allDaySchedule(page: Page, userId: string) {
  expect((await ask(() => page.request.put(api(`/workforce/people/${userId}/schedule`), { data: { timeZone: 'UTC', days: EVERY_DAY } }))).ok(), 'the schedule saves').toBeTruthy();
}
async function board(page: Page) {
  const s = stamp();
  const res = await ask(() => page.request.post(api('/boards'), { data: { name: `Execution ${s}`, key: `X${s.slice(-6)}`, kind: 0 } }));
  expect(res.ok(), 'the board saves').toBeTruthy();
  return (await res.json() as { id: string }).id;
}
async function ticket(page: Page, boardId: string, title: string, holder?: string) {
  const res = await ask(() => page.request.post(api('/boards/tickets'), { data: { boardId, title, description: 'Work execution test', priority: 'Medium', assignedAppUserId: holder ?? null } }));
  expect(res.ok(), 'the ticket saves').toBeTruthy();
  const created = await res.json() as { ticketId: string; number: string; title: string };
  return { id: created.ticketId, number: created.number, title: created.title };
}
async function plan(page: Page, ticketId: string, userId: string, date: string, from: string, to: string) {
  const res = await ask(() => page.request.post(api('/workforce/plan'), { data: { ticketId, appUserId: userId, start: `${date}T${from}:00Z`, end: `${date}T${to}:00Z` } }));
  expect(res.ok(), `planned: ${res.status()} ${await res.text()}`).toBeTruthy();
  return await res.json() as { id: string };
}
/** Whatever clock the account still has running from an earlier test is stopped first, so each test starts clean. */
async function stopEverything(page: Page) {
  const active = await (await ask(() => page.request.get(api('/workforce/work/active')))).json() as { id: string; version: number }[];
  for (const s of active) await ask(() => page.request.post(api(`/workforce/work/${s.id}/stop`), { data: { version: s.version, discard: true } }));
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
const workList = (page: Page) => page.getByRole('list', { name: 'Work' });
const row = (page: Page, reference: string) => workList(page).getByRole('listitem').filter({ hasText: reference });
const current = (page: Page) => page.getByRole('status', { name: 'Current work' });
const figure = (page: Page, label: string) =>
  page.locator('dl > div').filter({ has: page.locator('dt', { hasText: new RegExp(`^${label}$`, 'i') }) }).locator('dd').first();

test('a technician starts, pauses, resumes, reloads and stops the clock on planned work, and the day keeps the facts', async ({ page }) => {
  const who = await me(page);
  await stopEverything(page);
  await allDaySchedule(page, who.userId);
  const work = await ticket(page, await board(page), 'Firewall review', who.userId);
  const date = todayUtc();
  await plan(page, work.id, who.userId, date, hh(1), hh(2));

  await go(page, '/dashboard/workforce/my-day');
  await expect(page.getByRole('heading', { name: 'My day' })).toBeVisible();
  const r = row(page, work.number);
  await expect(r).toContainText('Planned 1h');
  await expect(r).not.toContainText('Working');
  await expect(r).toContainText('Variance −1h');

  // Start: the row says Working, the header shows the running clock with the reference.
  await r.getByRole('button', { name: 'Start work' }).click();
  await expect(r).toContainText('Working');
  await expect(current(page)).toContainText(work.number);
  await expect(current(page)).toContainText(/00:00:0\d/);
  const active = await (await ask(() => page.request.get(api('/workforce/work/active')))).json() as { id: string; status: number; ticketId: string }[];
  expect(active).toHaveLength(1);
  expect(active[0]).toMatchObject({ status: 1, ticketId: work.id });

  // Pause, with a reason: the clock stops; the header says why.
  await r.getByRole('button', { name: `Pause ${work.number}` }).click();
  await expect(r).toContainText('Paused');
  await expect(current(page).getByRole('button', { name: 'Resume work' })).toBeVisible();
  await r.getByRole('button', { name: `Resume ${work.number}` }).click();
  await expect(r).toContainText('Working');

  // A reload recovers the same clock from the server: still one session, same id.
  await page.reload();
  await expect(row(page, work.number)).toContainText('Working');
  await expect(current(page)).toContainText(work.number);
  const after = await (await ask(() => page.request.get(api('/workforce/work/active')))).json() as { id: string }[];
  expect(after.map((s) => s.id)).toEqual([active[0].id]);

  // Stop. Under a minute (the usual case here) nothing is logged and the dialog says so; a slow run that
  // crossed the minute logs the time instead - both are the rule, so the assertion follows the clock.
  const [running] = await (await ask(() => page.request.get(api('/workforce/work/active')))).json() as { activeSeconds: number; runningSince: string | null }[];
  const ranFor = running.activeSeconds + (running.runningSince ? Math.floor((Date.now() - Date.parse(running.runningSince)) / 1000) : 0);
  await row(page, work.number).getByRole('button', { name: `Stop ${work.number}` }).click();
  const dialog = page.getByRole('dialog', { name: 'Stop work' });
  if (ranFor < 50) {
    await expect(dialog).toContainText('Under a minute: nothing is logged');
    await dialog.getByRole('button', { name: 'Stop', exact: true }).click();
    await expect(dialog).toHaveCount(0);
    await expect(row(page, work.number)).not.toContainText('Working');
    const entries = await (await ask(() => page.request.get(api(`/tickets/${work.id}/time`)))).json() as unknown[];
    expect(entries).toHaveLength(0);
  } else {
    await dialog.getByRole('button', { name: 'Discard' }).click();
    await expect(dialog).toHaveCount(0);
  }
  await expect(current(page)).toHaveCount(0);

  // Thirty minutes typed in: actual against planned, as a fact. The day's totals cover everything
  // this account did today (other specs share it), so they are checked against what they were.
  const loggedBefore = (await figure(page, 'Actual logged').textContent()) ?? '';
  await row(page, work.number).getByRole('button', { name: `Add time to ${work.number}` }).click();
  const add = page.getByRole('dialog', { name: 'Add time' });
  await add.getByLabel('Actual minutes').fill('30');
  await add.getByLabel('Time note').fill('Reviewed the rule set');
  await add.getByRole('button', { name: 'Add time' }).click();
  await expect(add).toHaveCount(0);
  await expect(row(page, work.number)).toContainText('Actual 30m');
  await expect(row(page, work.number)).toContainText('Variance −30m');
  await expect(row(page, work.number)).toContainText('In progress');
  await expect(figure(page, 'Actual logged')).not.toHaveText(loggedBefore);
  await expect(figure(page, 'Planned')).toContainText(/\dh|\dm/);
  await expect(figure(page, 'Remaining planned')).toContainText(/\d/);
});

test('unplanned work is started from the unscheduled list and a second tab is refused a second clock', async ({ page, context }) => {
  const who = await me(page);
  await stopEverything(page);
  await allDaySchedule(page, who.userId);
  const b = await board(page);
  const urgent = await ticket(page, b, 'Mail flow stopped', who.userId);
  const other = await ticket(page, b, 'Backup check', who.userId);

  await go(page, '/dashboard/workforce/my-day');
  const unscheduled = page.getByRole('region', { name: /^Unscheduled my work/ });
  await expect(unscheduled.getByRole('listitem').filter({ hasText: urgent.number })).toBeVisible();
  await unscheduled.getByRole('listitem').filter({ hasText: urgent.number }).getByRole('button', { name: 'Start work' }).click();
  await expect(row(page, urgent.number)).toContainText('unplanned');
  await expect(row(page, urgent.number)).toContainText('Working');
  await expect(row(page, urgent.number)).toContainText('not planned');

  // Another tab, another ticket: the server refuses a second clock and the person chooses.
  const tab = await context.newPage();
  await go(tab, '/dashboard/workforce/my-day');
  const other2 = tab.getByRole('region', { name: /^Unscheduled my work/ }).getByRole('listitem').filter({ hasText: other.number });
  await other2.getByRole('button', { name: 'Start work' }).click();
  const choice = tab.getByRole('dialog', { name: 'Work already running' });
  await expect(choice).toContainText(urgent.number);
  await choice.getByRole('button', { name: 'Return' }).click();
  await expect(choice).toHaveCount(0);
  const active = await (await ask(() => tab.request.get(api('/workforce/work/active')))).json() as { ticketId: string; status: number }[];
  expect(active.filter((s) => s.status === 1).map((s) => s.ticketId)).toEqual([urgent.id]);
  // Pause the current and start the new: both are held, one runs.
  await other2.getByRole('button', { name: 'Start work' }).click();
  await tab.getByRole('dialog', { name: 'Work already running' }).getByRole('button', { name: 'Pause current and start new' }).click();
  await expect(tab.getByRole('status', { name: 'Current work' })).toContainText(other.number);
  const both = await (await ask(() => tab.request.get(api('/workforce/work/active')))).json() as { ticketId: string; status: number }[];
  expect(both.map((s) => [s.ticketId, s.status])).toEqual([[other.id, 1], [urgent.id, 2]]);
  await tab.close();
  await stopEverything(page);
});

test('a manager sees the team today: who is working on what, planned against actual', async ({ page }) => {
  const who = await me(page);
  await stopEverything(page);
  await allDaySchedule(page, who.userId);
  const work = await ticket(page, await board(page), 'Switch replacement', who.userId);
  await plan(page, work.id, who.userId, todayUtc(), hh(13), hh(15));
  const started = await (await ask(() => page.request.post(api('/workforce/work/start'), { data: { ticketId: work.id } }))).json() as { id: string; version: number };

  await go(page, '/dashboard/workforce/team-today');
  await expect(page.getByRole('heading', { name: 'Team today' })).toBeVisible();
  const table = page.getByRole('table', { name: 'Team today' });
  const mine = table.getByRole('row').filter({ hasText: work.number });
  await expect(mine).toHaveCount(1);
  await expect(mine).toContainText(/00:00:\d\d/);
  await expect(mine).toContainText(/\dh/); // this test's two hours plus whatever the account already planned today
  await expect(figure(page, 'Working now')).not.toHaveText('0');
  await ask(() => page.request.post(api(`/workforce/work/${started.id}/stop`), { data: { version: started.version, discard: true } }));
});

test('an account without the scheduling permission is refused My day, Team today and every work endpoint', async ({ page }) => {
  const s = stamp();
  const role = await (await ask(() => page.request.post(api('/admin/roles'), { data: { name: `Tickets only ${s}`, grants: [{ permissionKey: 'tickets.view.assigned', scope: 30 }] } }))).json() as { id: string };
  const outsider = await (await ask(() => page.request.post(api('/admin/users'), { data: { displayName: `No Clock ${s}`, email: `no.clock.${s}@techpio.test`, roleIds: [role.id] } }))).json() as { id: string; displayName: string };
  const work = await ticket(page, await board(page), 'Hidden execution', outsider.id);

  await viewAs(page, outsider);
  await go(page, '/dashboard/workforce/my-day');
  await expect(page.getByRole('alert').first()).toBeVisible();
  await expect(page.getByText(work.number)).toHaveCount(0);
  await expect(page.getByRole('status', { name: 'Current work' })).toHaveCount(0);
  for (const path of ['/workforce/work/active', '/workforce/my-day', '/workforce/team-today'])
    expect((await ask(() => page.request.get(api(path)))).status(), path).toBe(403);
  expect((await ask(() => page.request.post(api('/workforce/work/start'), { data: { ticketId: work.id } }))).status()).toBe(403);
  await exitView(page);
});
