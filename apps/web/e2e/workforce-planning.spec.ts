import { test, expect, type APIResponse, type Page } from '@playwright/test';

/**
 * Advanced planning through the browser: the planning queue with why work waits, what the work
 * needs set in place and Schedule pencilling it in; tentative work on the team board, confirmed from
 * its drawer and pencilled back in; effort in a window from a ticket, previewed with what does not
 * fit and then confirmed; an account without the permission refused.
 *
 * Everyone works 08:30-17:30 with a 12:30-13:30 break, every day, in UTC (see workforce-plan.spec).
 * Each browser plans on its own days, and this spec on days of its own.
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
const browserOffset = () => ({ chromium: 70, firefox: 80, webkit: 90 } as Record<string, number>)[test.info().project.name] ?? 100;
const isoDate = (daysAhead: number) => new Date(Date.now() + (daysAhead + browserOffset()) * 86_400_000).toISOString().slice(0, 10);
const EVERY_DAY = [0, 1, 2, 3, 4, 5, 6].map((day) => ({ day, start: '08:30', end: '17:30', breaks: [{ start: '12:30', end: '13:30' }] }));

async function roleId(page: Page, pattern: RegExp) {
  const roles = await (await ask(() => page.request.get(api('/admin/roles')))).json() as { id: string; name: string }[];
  const role = roles.find((r) => pattern.test(r.name));
  expect(role, `a role matching ${pattern}`).toBeTruthy();
  return role!.id;
}
async function technician(page: Page, name: string, tag: string) {
  const s = stamp();
  const role = await roleId(page, /technician/i);
  const res = await ask(() => page.request.post(api('/admin/users'), {
    data: { displayName: `${name} ${tag}`, email: `${name.split(' ')[0].toLowerCase()}.${s}@techpio.test`, roleIds: [role] },
  }));
  expect(res.ok()).toBeTruthy();
  const person = await res.json() as { id: string; displayName: string };
  expect((await ask(() => page.request.put(api(`/workforce/people/${person.id}/schedule`), { data: { timeZone: 'UTC', days: EVERY_DAY } }))).ok(), 'the schedule saves').toBeTruthy();
  return person;
}
async function board(page: Page) {
  const s = stamp();
  const res = await ask(() => page.request.post(api('/boards'), { data: { name: `Planning ${s}`, key: `P${s.slice(-6)}`, kind: 0 } }));
  expect(res.ok(), 'the board saves').toBeTruthy();
  return (await res.json() as { id: string }).id;
}
async function ticket(page: Page, boardId: string, title: string, holder?: string) {
  const res = await ask(() => page.request.post(api('/boards/tickets'), { data: { boardId, title, description: 'Planning test', priority: 'Medium', assignedAppUserId: holder ?? null } }));
  expect(res.ok(), 'the ticket saves').toBeTruthy();
  const created = await res.json() as { ticketId: string; number: string; title: string };
  return { id: created.ticketId, number: created.number, title: created.title };
}
async function plan(page: Page, ticketId: string, userId: string, date: string, from: string, to: string, extra: Record<string, unknown> = {}) {
  const res = await ask(() => page.request.post(api('/workforce/plan'), { data: { ticketId, appUserId: userId, start: `${date}T${from}:00Z`, end: `${date}T${to}:00Z`, ...extra } }));
  expect(res.ok(), `planned: ${res.status()} ${await res.text()}`).toBeTruthy();
  return await res.json() as { id: string; version: number; status: number };
}
async function planOf(page: Page, ticketId: string) {
  return await (await ask(() => page.request.get(api(`/workforce/tickets/${ticketId}/plan`)))).json() as { id: string; status: number; startsAt: string; endsAt: string }[];
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
const block = (page: Page, reference: string) => grid(page).getByRole('button', { name: new RegExp(`^${reference},`) });
const figure = (page: Page, label: string) =>
  page.locator('dl > div').filter({ has: page.locator('dt', { hasText: new RegExp(`^${label}$`, 'i') }) }).locator('dd').first();
const queueTable = (page: Page) => page.getByRole('table', { name: 'Work waiting to be planned' });

test('the planning queue says what waits and why; what the work needs is set in place; Schedule pencils it in', async ({ page }) => {
  const tag = stamp();
  const jason = await technician(page, 'Jason Carter', tag);
  const work = await ticket(page, await board(page), 'Replace the core switch', jason.id);

  await go(page, '/dashboard/workforce/queue');
  await expect(page.getByRole('heading', { name: 'Planning queue' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Workforce' }).getByRole('link', { name: 'Planning queue' })).toHaveAttribute('aria-current', 'page');
  await expect(figure(page, 'Waiting')).toBeVisible();
  await page.getByLabel('Search the queue').fill(tag);
  const r = queueTable(page).getByRole('row').filter({ hasText: work.number });
  await expect(r).toHaveCount(1);
  await expect(r).toContainText(jason.displayName);
  await expect(r).toContainText('Awaiting planning');
  await expect(r).toContainText('no estimate');

  // What it needs, set in the row: two hours that may be split.
  await r.getByRole('button', { name: `Show what ${work.number} needs` }).click();
  await page.getByRole('button', { name: 'Set what it needs' }).click();
  const needs = page.getByRole('form', { name: 'What the work needs' });
  await needs.getByLabel('Effort needed').fill('120');
  await needs.getByLabel('May be split').check();
  await needs.getByRole('button', { name: 'Save' }).click();
  await expect(needs).toHaveCount(0);
  await expect(page.getByText('2h needed · 0h planned, 2h to plan · may be split')).toBeVisible();
  await expect(queueTable(page).getByRole('row').filter({ hasText: work.number }).first()).toContainText('2h');
  await expect(queueTable(page).getByRole('row').filter({ hasText: work.number }).first()).toContainText('may split');
  const requirement = await (await ask(() => page.request.get(api(`/workforce/plan/requirements/${work.id}`)))).json() as { requiredMinutes: number; splittable: boolean; remainingMinutes: number };
  expect(requirement).toMatchObject({ requiredMinutes: 120, splittable: true, remainingMinutes: 120 });

  // Schedule opens the plan dialog for the holder with the remaining effort; pencilled in, the work leaves the queue.
  await queueTable(page).getByRole('row').filter({ hasText: work.number }).first().getByRole('button', { name: 'Schedule' }).click();
  const dialog = page.getByRole('dialog', { name: 'Plan work' });
  await expect(dialog.getByLabel('Person')).toHaveValue(jason.id);
  await expect(dialog.getByLabel('Planned duration')).toHaveValue('120');
  const date = isoDate(1);
  await dialog.getByLabel('Plan date').fill(date);
  await dialog.getByRole('group', { name: 'Free windows' }).getByRole('button', { name: /^08:30/ }).click();
  await dialog.getByLabel('Pencil in').check();
  await dialog.getByRole('button', { name: 'Pencil in' }).click();
  await expect(dialog).toHaveCount(0);
  await expect(queueTable(page).getByRole('row').filter({ hasText: work.number })).toHaveCount(0);
  const planned = await planOf(page, work.id);
  expect(planned).toHaveLength(1);
  expect(planned[0]).toMatchObject({ status: 2, startsAt: `${date}T08:30:00+00:00`, endsAt: `${date}T10:30:00+00:00` });
});

test('tentative work takes no confirmed capacity on the board, is confirmed from its drawer and pencilled back in', async ({ page }) => {
  const tag = stamp();
  const jason = await technician(page, 'Jason Carter', tag);
  const work = await ticket(page, await board(page), 'Migrate the file server', jason.id);
  const date = isoDate(2);
  await plan(page, work.id, jason.id, date, '09:00', '11:00', { tentative: true });

  await go(page, `/dashboard/workforce/schedule?date=${date}`);
  await expect(grid(page)).toBeVisible();
  await page.getByLabel('Search people and work').fill(tag);
  await expect(grid(page).getByRole('rowheader')).toHaveCount(1);
  await expect(block(page, work.number)).toHaveAccessibleName(/09:00–11:00, 2h, tentative/);
  await expect(row(page, jason.displayName)).toContainText('0h planned / 8h · 8h free · 2h tentative');
  await expect(figure(page, 'Tentative')).toHaveText('2h');

  // The drawer: marked tentative, and Confirm checks the time again as committed work.
  await block(page, work.number).click();
  const drawer = page.getByRole('dialog', { name: `Planned work ${work.number}` });
  await expect(drawer).toContainText('Tentative');
  await expect(drawer).toContainText('takes no confirmed capacity');
  await drawer.getByRole('button', { name: 'Confirm' }).click();
  const confirm = page.getByRole('dialog', { name: 'Confirm planned work' });
  await expect(confirm).toContainText(`Confirm ${work.number}`);
  await confirm.getByRole('button', { name: 'Confirm' }).click();
  await expect(confirm).toHaveCount(0);
  await expect(drawer).toHaveCount(0);
  await expect(block(page, work.number)).not.toHaveAccessibleName(/tentative/);
  await expect(row(page, jason.displayName)).toContainText('2h planned / 8h · 6h free');
  expect((await planOf(page, work.id))[0]).toMatchObject({ status: 1 });

  // Back to pencil, from the drawer: committed work stops taking capacity.
  page.once('dialog', (d) => d.accept());
  await block(page, work.number).click();
  await drawer.getByRole('button', { name: 'Pencil in' }).click();
  await expect(drawer).toHaveCount(0);
  await expect(block(page, work.number)).toHaveAccessibleName(/tentative/);
  await expect(row(page, jason.displayName)).toContainText('0h planned / 8h · 8h free · 2h tentative');
  expect((await planOf(page, work.id))[0]).toMatchObject({ status: 2 });
});

test('effort in a window is previewed from a ticket, says what does not fit, and is written only on confirmation', async ({ page }) => {
  const tag = stamp();
  const jason = await technician(page, 'Jason Carter', tag);
  const work = await ticket(page, await board(page), 'Rebuild the hypervisor', jason.id);
  const date = isoDate(3);

  await go(page, `/dashboard/tickets/${work.id}`);
  const panel = page.getByRole('region', { name: 'Planned work' });
  await expect(panel).toContainText('No estimate');
  await panel.getByRole('button', { name: 'Plan with a window' }).click();
  const dialog = page.getByRole('dialog', { name: 'Plan with a window' });
  await expect(dialog.getByLabel('Person')).toHaveValue(jason.id);
  await dialog.getByLabel('Earliest start').fill(`${date}T08:00`);
  await dialog.getByLabel('Latest finish').fill(`${date}T18:00`);
  await dialog.getByLabel('Effort').fill('600');
  await dialog.getByLabel('May be split').check();
  await dialog.getByRole('button', { name: 'Calculate' }).click();

  // Ten hours into a day with eight free: two pieces, two hours said to remain. Nothing is written yet.
  const proposal = dialog.getByRole('region', { name: 'Proposed plan' });
  await expect(proposal).toContainText('8h of 10h placed');
  await expect(proposal).toContainText('2h unallocated');
  await expect(proposal).toContainText('Only 8h of 10h fits in the window; 2h remains unallocated.');
  const pieces = proposal.getByRole('list', { name: 'Pieces' }).getByRole('listitem');
  await expect(pieces).toHaveCount(2);
  await expect(pieces.nth(0)).toContainText('08:30–12:30');
  await expect(pieces.nth(1)).toContainText('13:30–17:30');
  expect(await planOf(page, work.id)).toHaveLength(0);

  await proposal.getByLabel('Plan note').fill('Spread over the day');
  await proposal.getByRole('button', { name: 'Confirm plan' }).click();
  await expect(dialog).toHaveCount(0);
  await expect(panel.getByRole('listitem')).toHaveCount(2);
  await expect(panel).toContainText('08:30–12:30');
  await expect(panel).toContainText('13:30–17:30');
  await expect(panel).toContainText('Spread over the day');
  await expect(panel).toContainText('8h planned');
  const written = await planOf(page, work.id);
  expect(written.map((a) => a.status)).toEqual([1, 1]);
});

test('an account without the scheduling permission is refused the queue and every planning endpoint', async ({ page }) => {
  const s = stamp();
  const role = await (await ask(() => page.request.post(api('/admin/roles'), { data: { name: `Tickets only ${s}`, grants: [{ permissionKey: 'tickets.view.assigned', scope: 30 }] } }))).json() as { id: string };
  const outsider = await (await ask(() => page.request.post(api('/admin/users'), { data: { displayName: `No Planning ${s}`, email: `no.planning.${s}@techpio.test`, roleIds: [role.id] } }))).json() as { id: string; displayName: string };
  const jason = await technician(page, 'Jason Carter', s);
  const work = await ticket(page, await board(page), 'Hidden planning', jason.id);
  const date = isoDate(4);

  await viewAs(page, outsider);
  await go(page, '/dashboard/workforce/queue');
  await expect(page.getByRole('alert').first()).toBeVisible();
  await expect(page.getByText(jason.displayName)).toHaveCount(0);
  await expect(page.getByText(work.number)).toHaveCount(0);
  for (const path of ['/workforce/plan/queue', `/workforce/plan/requirements/${work.id}`,
    `/workforce/plan/preview?ticketId=${work.id}&appUserId=${jason.id}&earliest=${date}T08:00:00Z&latest=${date}T18:00:00Z&minutes=60`])
    expect((await ask(() => page.request.get(api(path)))).status(), path).toBe(403);
  expect((await ask(() => page.request.put(api(`/workforce/plan/requirements/${work.id}`), { data: { requiredMinutes: 60, splittable: false } }))).status()).toBe(403);
  await exitView(page);
});
