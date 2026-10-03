import { test, expect, type APIResponse, type Locator, type Page } from '@playwright/test';

/**
 * The team scheduler through the browser: the day board with its rows, capacity, free time and
 * blocks; scheduling from a free slot and from the unscheduled queue without dragging; dragging a
 * block to another row (a clash refused, then overridden with a reason); resizing; the week view;
 * a technician seeing only themselves; an account without the permission refused; a phone.
 *
 * Everyone works 08:30-17:30 with a 12:30-13:30 break, every day, in UTC (see workforce-plan.spec).
 * Each browser plans on its own days.
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
const browserOffset = () => ({ chromium: 40, firefox: 50, webkit: 60 } as Record<string, number>)[test.info().project.name] ?? 70;
const isoDate = (daysAhead: number) => new Date(Date.now() + (daysAhead + browserOffset()) * 86_400_000).toISOString().slice(0, 10);
const EVERY_DAY = [0, 1, 2, 3, 4, 5, 6].map((day) => ({ day, start: '08:30', end: '17:30', breaks: [{ start: '12:30', end: '13:30' }] }));

async function roleId(page: Page, pattern: RegExp) {
  const roles = await (await ask(() => page.request.get(api('/admin/roles')))).json() as { id: string; name: string }[];
  const role = roles.find((r) => pattern.test(r.name));
  expect(role, `a role matching ${pattern}`).toBeTruthy();
  return role!.id;
}
async function schedule(page: Page, userId: string) {
  expect((await ask(() => page.request.put(api(`/workforce/people/${userId}/schedule`), { data: { timeZone: 'UTC', days: EVERY_DAY } }))).ok(), 'the schedule saves').toBeTruthy();
}
/** A technician with the standard schedule. A shared `tag` in the names lets a test narrow the board to its own people. */
async function technician(page: Page, name: string, tag?: string) {
  const s = stamp();
  const role = await roleId(page, /technician/i);
  const res = await ask(() => page.request.post(api('/admin/users'), {
    data: { displayName: `${name} ${tag ?? s}`, email: `${name.split(' ')[0].toLowerCase()}.${s}@techpio.test`, roleIds: [role] },
  }));
  expect(res.ok()).toBeTruthy();
  const person = await res.json() as { id: string; displayName: string };
  await schedule(page, person.id);
  return person;
}
async function board(page: Page) {
  const s = stamp();
  const key = `S${s.slice(-6)}`;
  const res = await ask(() => page.request.post(api('/boards'), { data: { name: `Scheduling ${s}`, key, kind: 0 } }));
  expect(res.ok(), 'the board saves').toBeTruthy();
  return { id: (await res.json() as { id: string }).id, key };
}
async function ticket(page: Page, boardId: string, title: string, holder?: string) {
  const res = await ask(() => page.request.post(api('/boards/tickets'), { data: { boardId, title, description: 'Scheduler test', priority: 'Medium', assignedAppUserId: holder ?? null } }));
  expect(res.ok(), 'the ticket saves').toBeTruthy();
  const created = await res.json() as { ticketId: string; number: string; title: string };
  return { id: created.ticketId, number: created.number, title: created.title };
}
async function plan(page: Page, ticketId: string, userId: string, date: string, from: string, to: string, extra: Record<string, unknown> = {}) {
  const res = await ask(() => page.request.post(api('/workforce/plan'), { data: { ticketId, appUserId: userId, start: `${date}T${from}:00Z`, end: `${date}T${to}:00Z`, ...extra } }));
  expect(res.ok(), `planned: ${res.status()} ${await res.text()}`).toBeTruthy();
  return await res.json() as { id: string; version: number };
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

const grid = (page: Page) => page.getByRole('grid', { name: /^Team schedule for/ });
const row = (page: Page, name: string) => grid(page).getByRole('row').filter({ has: page.getByRole('rowheader').filter({ hasText: name }) });
const track = (page: Page, name: string) => row(page, name).getByRole('gridcell');
const block = (page: Page, reference: string) => grid(page).getByRole('button', { name: new RegExp(`^${reference},`) });
const figure = (page: Page, label: string) =>
  page.locator('dl > div').filter({ has: page.locator('dt', { hasText: new RegExp(`^${label}$`, 'i') }) }).locator('dd');

/** The x inside a track for a wall-clock time, read off the axis the page actually drew ("Hours from 08:00 to 18:00"; a night axis ends past midnight). */
async function xFor(page: Page, trackLocator: Locator, hm: string) {
  const label = await grid(page).getByRole('columnheader', { name: /^Hours from/ }).getAttribute('aria-label');
  const [, from, to] = /Hours from (\d\d:\d\d) to (\d\d:\d\d)/.exec(label ?? '')!;
  const h = (t: string) => Number(t.slice(0, 2)) + Number(t.slice(3)) / 60;
  const start = h(from);
  const end = h(to) <= start ? h(to) + 24 : h(to);
  const box = (await trackLocator.boundingBox())!;
  return { x: ((h(hm) - start) / (end - start)) * box.width, y: box.height / 2 };
}

/** Just this test's people on the board, next to each other, whatever else the database holds. */
async function narrowTo(page: Page, tag: string, rows: number) {
  await page.getByLabel('Search people and work').fill(tag);
  await expect(grid(page).getByRole('rowheader')).toHaveCount(rows);
}

test('a manager sees the team for a day: rows, capacity, free time and planned work, and opens a block', async ({ page }) => {
  const jason = await technician(page, 'Jason Carter');
  const abbie = await technician(page, 'Abbie Noor');
  const work = await ticket(page, (await board(page)).id, 'Rebuild backup job', jason.id);
  const date = isoDate(2);
  await plan(page, work.id, jason.id, date, '09:00', '11:00', { isFixed: true, note: 'Change window' });

  await go(page, `/dashboard/workforce/schedule?date=${date}`);
  await expect(page.getByRole('heading', { name: 'Team schedule' })).toBeVisible();
  await expect(grid(page)).toBeVisible();
  await expect(page.getByRole('group', { name: 'View' }).getByRole('button', { name: 'Day' })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByText('Times in UTC')).toBeVisible();

  // Jason's row: two hours planned of eight, the block, the break and the free time around it.
  const jasonRow = row(page, jason.displayName);
  await expect(jasonRow).toContainText('2h planned / 8h · 6h free');
  const b = block(page, work.number);
  await expect(b).toBeVisible();
  await expect(b).toHaveAccessibleName(new RegExp(`${work.number}, Rebuild backup job, 09:00–11:00, 2h, fixed`));
  await expect(jasonRow.getByRole('button', { name: /^Available 08:30–09:00/ })).toBeVisible();
  await expect(jasonRow.getByRole('button', { name: /^Available 11:00–12:30/ })).toBeVisible();
  await expect(jasonRow.getByLabel('Break 12:30–13:30')).toBeVisible();
  await expect(row(page, abbie.displayName)).toContainText('0h planned / 8h · 8h free');

  // The block opens a panel with the facts and the actions; the ticket page is a link away.
  await b.click();
  const drawer = page.getByRole('dialog', { name: `Planned work ${work.number}` });
  await expect(drawer).toContainText(jason.displayName);
  await expect(drawer).toContainText('09:00–11:00');
  await expect(drawer).toContainText('Scheduled by you · fixed in place');
  await expect(drawer).toContainText('Change window');
  await expect(drawer.getByRole('link', { name: 'Open ticket' })).toHaveAttribute('href', `/dashboard/tickets/${work.id}`);
  await expect(drawer.getByRole('button', { name: 'Reschedule' })).toBeVisible();
  await expect(drawer.getByRole('button', { name: 'Give to someone else' })).toBeVisible();
  await expect(drawer.getByRole('button', { name: 'Remove from plan' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(drawer).toHaveCount(0);
});

test('work is scheduled from a free slot and from the unscheduled queue without dragging', async ({ page }) => {
  const abbie = await technician(page, 'Abbie Noor');
  const b = await board(page);
  const first = await ticket(page, b.id, 'Onboard the new starters', abbie.id);
  const second = await ticket(page, b.id, 'Patch the NAS', abbie.id);
  const date = isoDate(3);

  await go(page, `/dashboard/workforce/schedule?date=${date}`);
  await expect(grid(page)).toBeVisible();
  // The queue lists what Abbie holds and nobody has planned.
  const queue = page.getByRole('complementary', { name: /^Unscheduled work/ });
  await expect(queue.getByRole('listitem').filter({ hasText: first.number })).toContainText(`Held by ${abbie.displayName}`);
  await expect(queue.getByRole('listitem').filter({ hasText: second.number })).toBeVisible();

  // A free slot in her row: pick the work, confirm the time it opened with.
  await row(page, abbie.displayName).getByRole('button', { name: /^Available 13:30–17:30/ }).click();
  const picker = page.getByRole('dialog', { name: 'Choose work to plan' });
  await picker.getByLabel('Search tickets').fill(first.number);
  await picker.getByRole('button', { name: new RegExp(first.number) }).click();
  const dialog = page.getByRole('dialog', { name: 'Plan work' });
  await expect(dialog).toContainText(`for ${abbie.displayName}`);
  await expect(dialog.getByLabel('Plan date')).toHaveValue(date);
  await expect(dialog.getByLabel('Start time')).toHaveValue('13:30');
  await dialog.getByLabel('Planned duration').fill('90');
  await dialog.getByRole('button', { name: 'Add to plan' }).click();
  await expect(dialog).toHaveCount(0);
  await expect(block(page, first.number)).toHaveAccessibleName(/13:30–15:00, 1h 30m/);
  await expect(row(page, abbie.displayName)).toContainText('1h 30m planned / 8h');
  await expect(queue.getByRole('listitem').filter({ hasText: first.number })).toHaveCount(0);

  // From the queue: Plan opens the dialog with her preselected; a free window is picked.
  await queue.getByRole('listitem').filter({ hasText: second.number }).getByRole('button', { name: 'Plan' }).click();
  const dialog2 = page.getByRole('dialog', { name: 'Plan work' });
  await expect(dialog2.getByLabel('Person')).toHaveValue(abbie.id);
  await dialog2.getByLabel('Plan date').fill(date);
  const chip = dialog2.getByRole('group', { name: 'Free windows' }).getByRole('button', { name: /^08:30/ });
  await expect(chip).toBeVisible();
  await chip.click();
  await dialog2.getByRole('button', { name: 'Add to plan' }).click();
  await expect(dialog2).toHaveCount(0);
  await expect(block(page, second.number)).toHaveAccessibleName(/08:30–09:30, 1h/);
  await expect(queue.getByRole('listitem').filter({ hasText: second.number })).toHaveCount(0);
});

test('dragging a block to another row gives the work away; a clash is refused and overridden with a reason', async ({ page, browserName }) => {
  test.skip(browserName !== 'chromium', 'HTML drag and drop is driven on Chromium; the same actions are covered without dragging');
  const tag = stamp();
  const jason = await technician(page, 'Jason Carter', tag);
  const abbie = await technician(page, 'Abbie Noor', tag);
  const b = await board(page);
  const moving = await ticket(page, b.id, 'Firewall review', jason.id);
  const busy = await ticket(page, b.id, 'Mailbox migration', abbie.id);
  const waiting = await ticket(page, b.id, 'Printer queue', jason.id);
  const date = isoDate(4);
  await plan(page, moving.id, jason.id, date, '09:00', '10:00');
  await plan(page, busy.id, abbie.id, date, '09:00', '10:00');

  await go(page, `/dashboard/workforce/schedule?date=${date}`);
  await narrowTo(page, tag, 2);
  await expect(block(page, moving.number)).toBeVisible();

  // Unscheduled work dragged onto Abbie's 14:00 opens the dialog at that time; nothing is saved by the drop itself.
  const queue = page.getByRole('complementary', { name: /^Unscheduled work/ });
  const abbieTrack = track(page, abbie.displayName);
  await queue.getByRole('listitem').filter({ hasText: waiting.number }).dragTo(abbieTrack, { targetPosition: await xFor(page, abbieTrack, '14:00') });
  const dropped = page.getByRole('dialog', { name: 'Plan work' });
  await expect(dropped).toContainText(`for ${abbie.displayName}`);
  await expect(dropped.getByLabel('Start time')).toHaveValue('14:00');
  await dropped.getByRole('button', { name: 'Cancel' }).click();
  await expect(dropped).toHaveCount(0);
  await expect(block(page, waiting.number)).toHaveCount(0);

  // Onto Abbie's 09:00: she already has work there. The clash is named; the administrator may override.
  const target = track(page, abbie.displayName);
  await block(page, moving.number).dragTo(target, { targetPosition: await xFor(page, target, '09:30') });
  const conflict = page.getByRole('dialog', { name: 'Conflict' });
  await expect(conflict).toContainText('Conflict detected');
  await expect(conflict).toContainText('Already planned');
  // Not saved: the block is back in Jason's row.
  await expect(row(page, jason.displayName).getByRole('button', { name: new RegExp(`^${moving.number},`) })).toBeVisible();
  await conflict.getByLabel('Override reason').fill('Both on the same server');
  await conflict.getByRole('button', { name: 'Override and save' }).click();
  await expect(conflict).toHaveCount(0);
  await expect(row(page, abbie.displayName).getByRole('button', { name: new RegExp(`^${moving.number},`) })).toBeVisible();
  await expect(row(page, jason.displayName).getByRole('button', { name: new RegExp(`^${moving.number},`) })).toHaveCount(0);

  // What the server holds: given to Abbie at the same time, the reason kept.
  const hers = await (await ask(() => page.request.get(api(`/workforce/people/${abbie.id}/plan?from=${date}&to=${date}`)))).json() as { allocations: { reference: string; startsAt: string; overrideReason: string | null }[] };
  expect(hers.allocations.find((a) => a.reference === moving.number)).toMatchObject({ startsAt: `${date}T09:00:00+00:00`, overrideReason: 'Both on the same server' });

  // A clean move within the row, by dragging to 14:00: saved without a dialog.
  const own = track(page, abbie.displayName);
  await block(page, moving.number).dragTo(own, { targetPosition: await xFor(page, own, '14:30') });
  await expect(block(page, moving.number)).toHaveAccessibleName(/14:00–15:00/);
});

test('a block is resized by its edge, and the planned time follows', async ({ page, browserName }) => {
  test.skip(browserName !== 'chromium', 'pointer resizing is driven on Chromium; Reschedule in the panel covers the rest');
  const tag = stamp();
  const jason = await technician(page, 'Jason Carter', tag);
  const work = await ticket(page, (await board(page)).id, 'Switch firmware', jason.id);
  const date = isoDate(5);
  await plan(page, work.id, jason.id, date, '09:00', '10:00');

  await go(page, `/dashboard/workforce/schedule?date=${date}`);
  await narrowTo(page, tag, 1);
  const handle = grid(page).getByRole('separator', { name: `Resize ${work.number}` });
  await expect(handle).toBeVisible();
  const from = (await handle.boundingBox())!;
  const t = track(page, jason.displayName);
  const to = await xFor(page, t, '11:00');
  const tb = (await t.boundingBox())!;
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(tb.x + to.x, tb.y + to.y, { steps: 8 });
  await page.mouse.up();
  await expect(block(page, work.number)).toHaveAccessibleName(/09:00–11:00, 2h/);
  await expect(row(page, jason.displayName)).toContainText('2h planned / 8h');
  const his = await (await ask(() => page.request.get(api(`/workforce/people/${jason.id}/plan?from=${date}&to=${date}`)))).json() as { allocations: { plannedMinutes: number }[] };
  expect(his.allocations[0].plannedMinutes).toBe(120);
});

test('the week view shows planned against usable per day and opens a day', async ({ page }) => {
  const jason = await technician(page, 'Jason Carter');
  const work = await ticket(page, (await board(page)).id, 'Weekly review', jason.id);
  const date = isoDate(6);
  await plan(page, work.id, jason.id, date, '10:00', '11:00');

  await go(page, `/dashboard/workforce/schedule?date=${date}&view=week`);
  const table = page.getByRole('table', { name: 'Planned and usable time per person per day' });
  await expect(table).toBeVisible();
  await expect(table.getByRole('columnheader')).toHaveCount(8);
  const cell = table.getByRole('row').filter({ hasText: jason.displayName }).getByRole('button').first();
  await expect(cell).toContainText('1h planned / 8h');
  await expect(cell).toContainText(work.number);
  await cell.click();
  await expect(page.getByRole('group', { name: 'View' }).getByRole('button', { name: 'Day' })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByLabel('Date')).toHaveValue(date);
  await expect(block(page, work.number)).toBeVisible();
});

test('find available technician runs inside the scheduler and offers to schedule from a result', async ({ page }) => {
  const jason = await technician(page, 'Jason Carter');
  const work = await ticket(page, (await board(page)).id, 'Long job', jason.id);
  const date = isoDate(7);
  await plan(page, work.id, jason.id, date, '08:30', '12:30');

  await go(page, `/dashboard/workforce/schedule?date=${date}`);
  await page.getByRole('button', { name: 'Find available technician' }).click();
  const panel = page.getByRole('dialog', { name: 'Find available technician' });
  await panel.getByLabel('Duration').fill('240');
  await panel.getByRole('button', { name: 'Find' }).click();
  const results = panel.getByRole('region', { name: 'Results' });
  await expect(results).toBeVisible();
  // Jason's morning is taken; his afternoon still holds four hours.
  const jasonResult = results.getByRole('listitem').filter({ hasText: jason.displayName });
  await expect(jasonResult).toContainText('13:30–17:30');
  await jasonResult.getByRole('button', { name: 'Schedule work' }).click();
  await expect(page.getByRole('dialog', { name: 'Choose work to plan' })).toContainText(`Plan work for ${jason.displayName}`);
});

test('a technician has no team schedule entry and the page shows only themselves', async ({ page }) => {
  const jason = await technician(page, 'Jason Carter');
  const abbie = await technician(page, 'Abbie Noor');
  const work = await ticket(page, (await board(page)).id, 'Private work', abbie.id);
  const date = isoDate(8);
  await plan(page, work.id, abbie.id, date, '09:00', '10:00');

  await viewAs(page, jason);
  await go(page, '/dashboard/workforce/my-plan');
  await expect(page.getByRole('navigation', { name: 'Workforce' }).getByRole('link', { name: 'Team schedule' })).toHaveCount(0);
  await go(page, `/dashboard/workforce/schedule?date=${date}`);
  await expect(grid(page)).toBeVisible();
  await expect(grid(page).getByRole('rowheader')).toHaveCount(1);
  await expect(grid(page).getByRole('rowheader')).toContainText(jason.displayName);
  await expect(page.getByText(abbie.displayName)).toHaveCount(0);
  await expect(page.getByText(work.number)).toHaveCount(0);
  const team = await (await ask(() => page.request.get(api(`/workforce/plan/team?from=${date}&to=${date}`)))).json() as { people: { appUserId: string }[] };
  expect(team.people.map((p) => p.appUserId)).toEqual([jason.id]);
  await exitView(page);
});

test('an account without the scheduling permission is refused the scheduler and its endpoints', async ({ page }) => {
  const s = stamp();
  const role = await (await ask(() => page.request.post(api('/admin/roles'), { data: { name: `Tickets only ${s}`, grants: [{ permissionKey: 'tickets.view.assigned', scope: 30 }] } }))).json() as { id: string };
  const outsider = await (await ask(() => page.request.post(api('/admin/users'), { data: { displayName: `No Schedule ${s}`, email: `no.schedule.${s}@techpio.test`, roleIds: [role.id] } }))).json() as { id: string; displayName: string };
  const jason = await technician(page, 'Jason Carter');
  const work = await ticket(page, (await board(page)).id, 'Hidden work', jason.id);
  const date = isoDate(9);
  await plan(page, work.id, jason.id, date, '09:00', '10:00');

  await viewAs(page, outsider);
  await go(page, `/dashboard/workforce/schedule?date=${date}`);
  await expect(page.getByRole('alert').first()).toBeVisible();
  await expect(page.getByText(jason.displayName)).toHaveCount(0);
  await expect(page.getByText(work.number)).toHaveCount(0);
  for (const path of [`/workforce/plan/team?from=${date}&to=${date}`, '/workforce/plan/unscheduled/team', `/workforce/plan/team?from=${date}&to=${date}&teamId=${outsider.id}`])
    expect((await ask(() => page.request.get(api(path)))).status(), path).toBe(403);
  await exitView(page);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 375, height: 740 } });

  test('the schedule becomes cards with each person\'s day, and nothing scrolls sideways', async ({ page }) => {
    const jason = await technician(page, 'Jason Carter');
    const work = await ticket(page, (await board(page)).id, 'Phone-sized work', jason.id);
    const date = isoDate(10);
    await plan(page, work.id, jason.id, date, '09:00', '10:00');

    await go(page, `/dashboard/workforce/schedule?date=${date}`);
    const cards = page.getByRole('list', { name: 'People' });
    await expect(cards).toBeVisible();
    const card = cards.getByRole('listitem').filter({ hasText: jason.displayName });
    await expect(card).toContainText('1h planned / 8h');
    await expect(card.getByRole('button', { name: new RegExp(work.number) })).toContainText('09:00–10:00');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'no sideways scroll').toBeTruthy();
    await card.getByRole('button', { name: new RegExp(work.number) }).click();
    await expect(page.getByRole('dialog', { name: `Planned work ${work.number}` })).toContainText('Phone-sized work');
  });
});
