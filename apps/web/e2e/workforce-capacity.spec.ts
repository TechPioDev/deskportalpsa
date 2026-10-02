import { test, expect, type APIResponse, type Page } from '@playwright/test';

/**
 * Capacity, availability and the technician search through the browser: a technician's own
 * capacity, time away recorded by an administrator, the team table with its filters, finding who
 * can take a piece of work, and an account without the workforce permission being refused.
 * Capacity for planning work - not attendance.
 *
 * Everyone here works 08:30-17:30 with a 12:30-13:30 break, every day of the week, in UTC: every day
 * so the test means the same thing on a Saturday, and UTC so the server and the browser agree on a
 * development machine that cannot read other zones (the zone arithmetic has its own tests).
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
const isoDate = (daysAhead: number) => new Date(Date.now() + daysAhead * 86_400_000).toISOString().slice(0, 10);
const EVERY_DAY = [0, 1, 2, 3, 4, 5, 6].map((day) => ({ day, start: '08:30', end: '17:30', breaks: [{ start: '12:30', end: '13:30' }] }));

async function roleId(page: Page, pattern: RegExp) {
  const roles = await (await ask(() => page.request.get(api('/admin/roles')))).json() as { id: string; name: string }[];
  const role = roles.find((r) => pattern.test(r.name));
  expect(role, `a role matching ${pattern}`).toBeTruthy();
  return role!.id;
}

/** A technician with the standard schedule. */
async function technician(page: Page, name: string, role?: string) {
  const id = role ?? await roleId(page, /technician/i);
  const s = stamp();
  const res = await ask(() => page.request.post(api('/admin/users'), {
    data: { displayName: `${name} ${s}`, email: `${name.split(' ')[0].toLowerCase()}.${s}@techpio.test`, roleIds: [id] },
  }));
  expect(res.ok()).toBeTruthy();
  const person = await res.json() as { id: string; displayName: string };
  const saved = await ask(() => page.request.put(api(`/workforce/people/${person.id}/schedule`), { data: { timeZone: 'UTC', days: EVERY_DAY } }));
  expect(saved.ok(), 'the schedule saves').toBeTruthy();
  return person;
}

async function skill(page: Page, name: string, holders: { id: string }[]) {
  const created = await (await ask(() => page.request.post(api('/workforce/skills'), { data: { name } }))).json() as { id: string };
  for (const p of holders)
    expect((await ask(() => page.request.post(api(`/workforce/people/${p.id}/skills`), { data: { skillId: created.id, level: 3 } }))).ok()).toBeTruthy();
  return created.id;
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

/** A figure in the day's capacity list, by its label. */
const figure = (page: Page, label: string) =>
  page.getByRole('region', { name: 'Capacity' }).locator('div').filter({ has: page.locator('dt', { hasText: new RegExp(`^${label}`) }) }).locator('dd');

test('a technician opens My capacity and sees their own usable time and free windows', async ({ page }) => {
  const jason = await technician(page, 'Jason Carter');
  const other = await technician(page, 'Abbie Noor');

  await viewAs(page, jason);
  await page.goto('/dashboard/workforce/my-capacity');

  await expect(page.getByRole('heading', { name: 'My capacity' })).toBeVisible();
  await expect(figure(page, 'Working window')).toHaveText('08:30–17:30');
  await expect(figure(page, 'Usable capacity')).toHaveText('8h');
  await expect(figure(page, 'Free capacity')).toHaveText('8h');
  const windows = page.getByRole('region', { name: 'Available windows' });
  await expect(windows.getByRole('listitem')).toHaveText(['08:30–12:304h', '13:30–17:304h']);

  // Their own view: no team pages offered, and nothing to record time away with.
  const nav = page.getByRole('navigation', { name: 'Workforce' });
  await expect(nav.getByRole('link', { name: 'My capacity' })).toHaveAttribute('aria-current', 'page');
  await expect(nav.getByRole('link', { name: 'Team capacity' })).toHaveCount(0);
  await expect(nav.getByRole('link', { name: 'Find available technician' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Add time away' })).toHaveCount(0);

  // And the server agrees: a colleague's capacity does not exist for them, the team answer is only
  // themselves, and recording time away is refused.
  expect((await ask(() => page.request.get(api(`/workforce/people/${other.id}/capacity`)))).status()).toBe(404);
  const team = await (await ask(() => page.request.get(api('/workforce/capacity')))).json() as { people: { appUserId: string }[] };
  expect(team.people.map((p) => p.appUserId)).toEqual([jason.id]);
  const found = await (await ask(() => page.request.get(api(`/workforce/availability?from=${isoDate(1)}&duration=60&people=${other.id}`)))).json() as { peopleConsidered: number };
  expect(found.peopleConsidered).toBe(0);

  await exitView(page);
});

test('time away recorded by an administrator changes capacity at once, and a full day leaves none', async ({ page }) => {
  const john = await technician(page, 'John Mehta');
  const date = isoDate(3);

  await page.goto(`/dashboard/workforce/people/${john.id}?tab=availability&date=${date}`);
  await expect(figure(page, 'Usable capacity')).toHaveText('8h');

  // Part of a day: 15:00-16:00.
  await page.getByRole('button', { name: 'Add time away' }).click();
  const form = page.getByRole('form', { name: 'Add time away' });
  await expect(form.getByLabel('Date')).toHaveValue(date);
  await form.getByLabel('Start time').fill('15:00');
  await form.getByLabel('End time').fill('15:00');
  await expect(form.getByText('The start and end times are the same.')).toBeVisible();
  await expect(form.getByRole('button', { name: 'Add', exact: true })).toBeDisabled();
  await form.getByLabel('End time').fill('16:00');
  await form.getByLabel('Reason').selectOption({ label: 'Appointment' });
  await form.getByLabel('Note').fill('<b>Dentist</b>');
  await form.getByRole('button', { name: 'Add', exact: true }).click();

  await expect(figure(page, 'Unavailable')).toHaveText('− 1h');
  await expect(figure(page, 'Usable capacity')).toHaveText('7h');
  await expect(page.getByRole('region', { name: 'Available windows' }).getByRole('listitem'))
    .toHaveText(['08:30–12:304h', '13:30–15:001h 30m', '16:00–17:301h 30m']);
  // The note is shown as the text that was typed, never as markup.
  const row = page.getByRole('listitem').filter({ hasText: '15:00–16:00' });
  await expect(row).toContainText('<b>Dentist</b>');
  await expect(row.locator('b')).toHaveCount(0);

  // The same thing again is refused, in words.
  await page.getByRole('button', { name: 'Add time away' }).click();
  await form.getByLabel('Start time').fill('15:00');
  await form.getByLabel('End time').fill('16:00');
  await form.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(form.getByRole('alert')).toHaveText('That is already recorded for this person.');

  // The whole day instead.
  await form.getByLabel('All day').check();
  await form.getByLabel('Reason').selectOption({ label: 'Time off' });
  await form.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(figure(page, 'Working window')).toHaveText('Away all day');
  await expect(figure(page, 'Usable capacity')).toHaveText('0h');
  await expect(page.getByRole('region', { name: 'Available windows' })).toContainText('Away all day.');
  await expect(page.getByRole('group', { name: 'Week' }).getByRole('button', { pressed: true })).toContainText('Away');

  // Remove the day off: the appointment is still there, and so is the rest of the day.
  page.once('dialog', (d) => d.accept());
  await page.getByRole('listitem').filter({ hasText: 'all day' }).getByRole('button', { name: /^Remove/ }).click();
  await expect(figure(page, 'Usable capacity')).toHaveText('7h');

  // It survives a reload.
  await page.reload();
  await expect(figure(page, 'Usable capacity')).toHaveText('7h');
});

test('team capacity lists each person for a date and narrows by team and skill', async ({ page }) => {
  const s = stamp();
  const jason = await technician(page, 'Jason Carter');
  const abbie = await technician(page, 'Abbie Noor');
  const skillName = `SonicWall ${s}`;
  await skill(page, skillName, [jason]);
  // A team with only Abbie in it.
  const department = await (await ask(() => page.request.post(api('/admin/departments'), { data: { name: `Operations ${s}` } }))).json() as { id: string };
  const team = await (await ask(() => page.request.post(api('/admin/teams'), { data: { departmentId: department.id, name: `NOC ${s}` } }))).json() as { id: string };
  expect((await ask(() => page.request.post(api(`/admin/users/${abbie.id}/departments/${department.id}?isPrimary=true`)))).ok()).toBeTruthy();
  expect((await ask(() => page.request.post(api(`/admin/users/${abbie.id}/teams/${team.id}`)))).ok()).toBeTruthy();
  await ask(() => page.request.post(api(`/workforce/people/${jason.id}/exceptions`), {
    data: { kind: 1, allDay: false, fromDate: isoDate(2), startTime: '09:00', endTime: '11:00', reason: 1, note: null },
  }));

  await page.goto('/dashboard/workforce/capacity');
  await expect(page.getByRole('heading', { name: 'Team capacity' })).toBeVisible();
  const table = page.getByRole('table');
  // Today's rows have arrived, so the page is live: a date typed earlier is wiped as it starts up.
  await expect(table.getByRole('row').filter({ hasText: jason.displayName })).toBeVisible();
  await page.getByLabel('Date', { exact: true }).fill(isoDate(2));
  await expect(page.getByLabel('Date', { exact: true })).toHaveValue(isoDate(2));

  const jasonRow = table.getByRole('row').filter({ hasText: jason.displayName });
  await expect(jasonRow).toContainText('08:30–17:30');
  await expect(jasonRow.getByRole('cell').nth(1)).toHaveText('6h');
  await expect(jasonRow.getByRole('cell').nth(3)).toHaveText('6h');
  await expect(jasonRow).toContainText('08:30–09:00');
  await expect(table.getByRole('row').filter({ hasText: abbie.displayName }).getByRole('cell').nth(1)).toHaveText('8h');

  // By skill: only who holds it.
  await page.getByRole('group', { name: 'Skills' }).getByRole('button', { name: skillName }).click();
  await expect(table.locator('tbody tr')).toHaveCount(1);
  await expect(table.locator('tbody tr')).toContainText(jason.displayName);
  await expect(table.locator('tfoot')).toContainText('6h');
  await page.getByRole('group', { name: 'Skills' }).getByRole('button', { name: skillName }).click();

  // By team: only who is in it.
  await page.getByLabel('Team', { exact: true }).selectOption({ label: `NOC ${s}` });
  await expect(table.locator('tbody tr')).toHaveCount(1);
  await expect(table.locator('tbody tr')).toContainText(abbie.displayName);

  // The name opens that person's availability on the date being looked at.
  await table.getByRole('link', { name: abbie.displayName }).click();
  await expect(page).toHaveURL(new RegExp(`/workforce/people/${abbie.id}\\?tab=availability&date=${isoDate(2)}`));
  await expect(page.getByRole('tab', { name: 'Availability' })).toHaveAttribute('aria-selected', 'true');
  await expect(figure(page, 'Usable capacity')).toHaveText('8h');
});

test('finding who can take ninety minutes with a required skill', async ({ page }) => {
  const s = stamp();
  const jason = await technician(page, 'Jason Carter');
  const abbie = await technician(page, 'Abbie Noor');
  const john = await technician(page, 'John Mehta');
  const skillName = `Firewall ${s}`;
  const skillId = await skill(page, skillName, [jason, abbie]);
  const date = isoDate(4);
  // Jason is free from 14:00; Abbie's afternoon has only an hour left; John has no such skill.
  await ask(() => page.request.post(api(`/workforce/people/${jason.id}/exceptions`), { data: { kind: 1, allDay: false, fromDate: date, startTime: '13:30', endTime: '14:00', reason: 1 } }));
  await ask(() => page.request.post(api(`/workforce/people/${abbie.id}/exceptions`), { data: { kind: 1, allDay: false, fromDate: date, startTime: '13:30', endTime: '16:30', reason: 2 } }));

  await page.goto('/dashboard/workforce/find');
  await expect(page.getByRole('heading', { name: 'Find available technician' })).toBeVisible();
  const form = page.getByRole('form', { name: 'Search' });
  // The skill chips come from a request the page makes once it is live: typing before that is wiped.
  await expect(form.getByRole('group', { name: 'Skills' }).getByRole('button', { name: skillName })).toBeVisible();
  await form.getByLabel('Date', { exact: true }).fill(date);
  await expect(form.getByLabel('Date', { exact: true })).toHaveValue(date);
  await form.getByLabel('Duration in minutes').fill('90');
  await form.getByLabel('Earliest time').fill('13:00');
  await form.getByLabel('Latest time').fill('17:30');
  await form.getByRole('group', { name: 'Skills' }).getByRole('button', { name: skillName }).click();
  await form.getByRole('button', { name: 'Find' }).click();

  const results = page.getByRole('region', { name: 'Results' });
  // Two people hold the skill; one of them has a continuous ninety minutes that afternoon.
  await expect(results.getByRole('listitem')).toHaveCount(1);
  const match = results.getByRole('listitem');
  await expect(match).toContainText(jason.displayName);
  await expect(match).toContainText('14:00–15:30');
  await expect(match).toContainText('14:00–17:30');
  await expect(match).toContainText('3h 30m free in the window');
  await expect(match).toContainText(`${skillName} · Expert`);
  await expect(results).toContainText('can take 1h 30m');
  await expect(results).toContainText('1 with no free slot long enough');
  await expect(results).toContainText('without every skill asked for');
  await expect(results).not.toContainText(john.displayName);
  // Nothing is assigned from here.
  await expect(results.getByRole('button')).toHaveCount(0);

  // The same question through the API gives the same facts, and a conflict check agrees.
  const found = await (await ask(() => page.request.get(api(`/workforce/availability?from=${date}&duration=90&earliest=13:00&latest=17:30&skills=${skillId}`)))).json() as
    { matches: { appUserId: string; recommended: { start: string; end: string } }[]; withNoFittingSlot: number };
  expect(found.matches.map((m) => m.appUserId)).toEqual([jason.id]);
  expect(found.withNoFittingSlot).toBe(1);
  const fits = await (await ask(() => page.request.get(api(`/workforce/people/${jason.id}/conflicts?start=${encodeURIComponent(found.matches[0].recommended.start)}&end=${encodeURIComponent(found.matches[0].recommended.end)}`)))).json() as
    { canSchedule: boolean };
  expect(fits.canSchedule).toBe(true);
  const clash = await (await ask(() => page.request.get(api(`/workforce/people/${jason.id}/conflicts?start=${date}T13:30:00Z&end=${date}T14:30:00Z`)))).json() as
    { canSchedule: boolean; canOverride: boolean; conflicts: { type: number; severity: number }[] };
  expect(clash.canSchedule).toBe(false);
  expect(clash.canOverride).toBe(false);
  expect(clash.conflicts.map((c) => [c.type, c.severity])).toEqual([[4, 3]]);

  await match.getByRole('link', { name: 'View availability' }).click();
  await expect(page).toHaveURL(new RegExp(`/workforce/people/${jason.id}\\?tab=availability&date=${date}`));
});

test('an account without the workforce permission is refused every capacity page and endpoint', async ({ page }) => {
  // A client login cannot be produced in a local run, and a client is exactly this: a signed-in
  // account that holds no workforce permission. (That no client ROLE can ever hold one is proven
  // at the endpoint level in the API's own tests.)
  const s = stamp();
  const role = await (await ask(() => page.request.post(api('/admin/roles'), {
    data: { name: `Tickets only ${s}`, grants: [{ permissionKey: 'tickets.view.assigned', scope: 30 }] },
  }))).json() as { id: string };
  const outsider = await (await ask(() => page.request.post(api('/admin/users'), {
    data: { displayName: `No Workforce ${s}`, email: `no.workforce.${s}@techpio.test`, roleIds: [role.id] },
  }))).json() as { id: string; displayName: string };
  const jason = await technician(page, 'Jason Carter');

  await viewAs(page, outsider);

  // No menu entry...
  await expect(page.getByRole('link', { name: 'Workforce', exact: true })).toHaveCount(0);
  // ...and typing the address gets an error, not data.
  for (const path of ['/dashboard/workforce/capacity', '/dashboard/workforce/my-capacity', `/dashboard/workforce/people/${jason.id}?tab=availability`]) {
    await page.goto(path);
    await expect(page.getByRole('alert').first()).toBeVisible();
    await expect(page.getByText(jason.displayName)).toHaveCount(0);
  }
  await page.goto('/dashboard/workforce/find');
  // Clicked until it takes: a click made before the page is live does nothing.
  await expect(async () => {
    await page.getByRole('form', { name: 'Search' }).getByRole('button', { name: 'Find' }).click();
    await expect(page.getByRole('alert').first()).toBeVisible({ timeout: 3_000 });
  }).toPass({ timeout: 30_000 });
  await expect(page.getByRole('region', { name: 'Results' })).toHaveCount(0);

  // Every capacity endpoint refuses, including a guessed id.
  const date = isoDate(1);
  for (const path of [
    `/workforce/people/${jason.id}/capacity`, `/workforce/people/${outsider.id}/capacity`, '/workforce/capacity', '/workforce/groups',
    `/workforce/availability?from=${date}&duration=60`, `/workforce/people/${jason.id}/conflicts?start=${date}T09:00:00Z&end=${date}T10:00:00Z`,
    `/workforce/people/${jason.id}/exceptions`, '/workforce/people',
  ])
    expect((await ask(() => page.request.get(api(path)))).status(), path).toBe(403);

  await exitView(page);
});
