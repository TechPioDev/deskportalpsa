import { test, expect, type APIResponse, type Page } from '@playwright/test';

/**
 * Planned work through the browser: a person putting their own held work into their time, someone
 * who schedules others placing fixed work for a technician and overriding a clash with a reason,
 * planned work given to someone else, a small piece of internal work raised and planned in one go,
 * and an account without the scheduling permission being refused every planning page and endpoint.
 * Planned time is a capacity boundary, never time logged and never anything a client can see.
 *
 * Everyone here works 08:30-17:30 with a 12:30-13:30 break, every day of the week, in UTC: every day
 * so the test means the same thing on a Saturday, and UTC so the server and the browser agree on a
 * development machine that cannot read other zones. Each test plans on its own day, so what one
 * test places never sits in another test's free windows.
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
// The three browsers share one API and one database in a run, and the signed-in administrator is
// the same person in all of them: each browser plans on its own days, so what Chromium placed is
// not sitting in WebKit's free windows.
const browserOffset = () => ({ chromium: 0, firefox: 10, webkit: 20 } as Record<string, number>)[test.info().project.name] ?? 30;
const isoDate = (daysAhead: number) => new Date(Date.now() + (daysAhead + browserOffset()) * 86_400_000).toISOString().slice(0, 10);
const EVERY_DAY = [0, 1, 2, 3, 4, 5, 6].map((day) => ({ day, start: '08:30', end: '17:30', breaks: [{ start: '12:30', end: '13:30' }] }));

async function roleId(page: Page, pattern: RegExp) {
  const roles = await (await ask(() => page.request.get(api('/admin/roles')))).json() as { id: string; name: string }[];
  const role = roles.find((r) => pattern.test(r.name));
  expect(role, `a role matching ${pattern}`).toBeTruthy();
  return role!.id;
}

async function schedule(page: Page, userId: string) {
  const saved = await ask(() => page.request.put(api(`/workforce/people/${userId}/schedule`), { data: { timeZone: 'UTC', days: EVERY_DAY } }));
  expect(saved.ok(), 'the schedule saves').toBeTruthy();
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
  await schedule(page, person.id);
  return person;
}

/** The signed-in administrator, with the standard schedule so they have time to plan. */
async function me(page: Page) {
  const who = await (await ask(() => page.request.get(api('/me')))).json() as { userId: string; displayName: string };
  await schedule(page, who.userId);
  return who;
}

/** An internal board and a ticket raised on it, held by someone or by nobody. */
async function board(page: Page) {
  const s = stamp();
  const key = `P${s.slice(-6)}`;
  const res = await ask(() => page.request.post(api('/boards'), { data: { name: `Planning ${s}`, key, kind: 0 } }));
  expect(res.ok(), 'the board saves').toBeTruthy();
  return { id: (await res.json() as { id: string }).id, key };
}
async function ticket(page: Page, boardId: string, title: string, holder?: string) {
  const res = await ask(() => page.request.post(api('/boards/tickets'), { data: { boardId, title, description: 'Planned-work test', priority: 'Medium', assignedAppUserId: holder ?? null } }));
  expect(res.ok(), 'the ticket saves').toBeTruthy();
  const created = await res.json() as { ticketId: string; number: string; title: string };
  return { id: created.ticketId, number: created.number, title: created.title };
}

async function viewAs(page: Page, person: { id: string; displayName: string }) {
  await go(page, `/dashboard/users/${person.id}`);
  await page.getByRole('button', { name: `View as ${person.displayName}` }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Viewing as' })).toBeVisible();
}

async function exitView(page: Page) {
  await page.getByRole('status').filter({ hasText: 'Viewing as' }).getByRole('button', { name: 'Exit view' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Viewing as' })).toHaveCount(0);
}

/** Opened until it takes: WebKit can report a page's own late URL normalisation as a competing navigation. */
async function go(page: Page, url: string) {
  await expect(async () => { await page.goto(url); }).toPass({ timeout: 30_000 });
}

const planList = (page: Page) => page.getByRole('list', { name: 'Plan' });
const figure = (page: Page, label: string) =>
  page.locator('dl > div').filter({ has: page.locator('dt', { hasText: new RegExp(`^${label}$`, 'i') }) }).locator('dd');

/** The plan dialog is live once its free windows have loaded: typing before that is lost. */
async function freeWindow(page: Page, dialog: ReturnType<Page['getByRole']>, startsWith: string) {
  const chip = dialog.getByRole('group', { name: 'Free windows' }).getByRole('button', { name: new RegExp(`^${startsWith}`) });
  await expect(chip).toBeVisible();
  return chip;
}

test('a person plans their own held work into a free window and sees it in My plan', async ({ page }) => {
  const who = await me(page);
  const work = await ticket(page, (await board(page)).id, 'Review Secure Score', who.userId);
  const date = isoDate(2);

  await go(page, `/dashboard/workforce/my-plan?date=${date}`);
  await expect(page.getByRole('heading', { name: 'My plan' })).toBeVisible();
  await expect(figure(page, 'Capacity')).toHaveText('8h');

  // The work is unscheduled: held by me, open, not in my plan.
  const row = page.getByRole('listitem').filter({ hasText: work.number });
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Add to plan' }).click();

  const dialog = page.getByRole('dialog', { name: 'Plan work' });
  await expect(dialog).toContainText(work.number);
  await dialog.getByLabel('Plan date').fill(date);
  await (await freeWindow(page, dialog, '08:30')).click();
  await expect(dialog.getByLabel('Start time')).toHaveValue('08:30');
  await dialog.getByLabel('Planned duration').fill('90');
  await dialog.getByLabel('Planning note').fill('First thing');
  await dialog.getByRole('button', { name: 'Add to plan' }).click();
  await expect(dialog).toHaveCount(0);

  // In the plan, at that time, planned by me; the figures and the free windows follow.
  const planned = planList(page).getByRole('listitem').filter({ hasText: work.number });
  await expect(planned).toContainText('08:30–10:00');
  await expect(planned).toContainText('Planned by you');
  await expect(planned).toContainText('First thing');
  await expect(figure(page, 'Planned')).toHaveText('1h 30m');
  await expect(figure(page, 'Free')).toHaveText('6h 30m');
  await expect(planList(page).getByRole('listitem').filter({ hasText: 'Available · 2h 30m' })).toBeVisible();
  await expect(page.getByRole('listitem').filter({ hasText: work.number }).filter({ has: page.getByRole('button', { name: 'Add to plan' }) })).toHaveCount(0);

  // Mine to move and to take out, since I planned it myself; taking it out leaves the ticket alone.
  await expect(planned.getByRole('button', { name: `Move ${work.number}`, exact: true })).toBeVisible();
  page.once('dialog', (d) => d.accept());
  await planned.getByRole('button', { name: `Remove ${work.number} from the plan` }).click();
  await expect(planList(page).getByRole('listitem').filter({ hasText: work.number })).toHaveCount(0);
  await expect(figure(page, 'Planned')).toHaveText('0h');
  const after = await (await ask(() => page.request.get(api(`/tickets/${work.id}`)))).json() as { portalStatus: string };
  expect(after.portalStatus).not.toMatch(/closed|resolved/i);
});

test('someone who schedules others places fixed work for a technician, is refused a clash, and overrides it with a reason', async ({ page }) => {
  const jason = await technician(page, 'Jason Carter');
  const b = (await board(page)).id;
  const first = await ticket(page, b, 'Rebuild backup job', jason.id);
  const second = await ticket(page, b, 'Patch review', jason.id);
  const date = isoDate(3);

  await go(page, `/dashboard/workforce/people/${jason.id}?tab=plan&date=${date}`);
  await expect(page.getByRole('tab', { name: 'Plan' })).toBeVisible();
  await expect(figure(page, 'Free')).toHaveText('8h');

  // Pick the work, give it two hours after lunch, fixed.
  await page.getByRole('button', { name: 'Plan work' }).click();
  const picker = page.getByRole('dialog', { name: 'Choose work to plan' });
  await picker.getByLabel('Search tickets').fill(first.number);
  await picker.getByRole('button', { name: new RegExp(first.number) }).click();
  let dialog = page.getByRole('dialog', { name: 'Plan work' });
  await expect(dialog).toContainText(`for ${jason.displayName}`);
  await dialog.getByLabel('Plan date').fill(date);
  await dialog.getByLabel('Planned duration').fill('120');
  await (await freeWindow(page, dialog, '13:30')).click();
  await dialog.getByLabel('Fixed').check();
  await dialog.getByRole('button', { name: 'Add to plan' }).click();
  await expect(dialog).toHaveCount(0);
  const placed = planList(page).getByRole('listitem').filter({ hasText: first.number });
  await expect(placed).toContainText('13:30–15:30');
  await expect(placed).toContainText('Scheduled by you');
  await expect(placed).toContainText('Fixed');
  await expect(figure(page, 'Planned')).toHaveText('2h');

  // The same hour again is a clash: refused with the conflict named, then placed with a reason.
  await page.getByRole('button', { name: 'Plan work' }).click();
  await picker.getByLabel('Search tickets').fill(second.number);
  await picker.getByRole('button', { name: new RegExp(second.number) }).click();
  dialog = page.getByRole('dialog', { name: 'Plan work' });
  await dialog.getByLabel('Plan date').fill(date);
  await freeWindow(page, dialog, '08:30');
  await dialog.getByLabel('Start time').fill('14:00');
  await dialog.getByRole('button', { name: 'Add to plan' }).click();
  const notice = dialog.getByRole('alert');
  await expect(notice).toContainText('has a conflict');
  await expect(notice).toContainText('Already planned');
  await expect(planList(page).getByRole('listitem').filter({ hasText: second.number })).toHaveCount(0);
  await dialog.getByLabel('Override reason').fill('Both jobs on the same server');
  await dialog.getByRole('button', { name: 'Override and save' }).click();
  await expect(dialog).toHaveCount(0);
  const overridden = planList(page).getByRole('listitem').filter({ hasText: second.number });
  await expect(overridden).toContainText('14:00–15:00');
  await expect(overridden).toContainText('Override: Both jobs on the same server');

  // What the API holds: two plans, one fixed, one with its reason, both scheduled by the caller.
  const plan = await (await ask(() => page.request.get(api(`/workforce/people/${jason.id}/plan?from=${date}&to=${date}`)))).json() as {
    allocations: { reference: string; isFixed: boolean; method: number; overrideReason: string | null; overriddenConflicts: number[] }[];
  };
  expect(plan.allocations.map((a) => a.reference).sort()).toEqual([first.number, second.number].sort());
  expect(plan.allocations.find((a) => a.reference === first.number)).toMatchObject({ isFixed: true, method: 2, overrideReason: null });
  expect(plan.allocations.find((a) => a.reference === second.number)).toMatchObject({ isFixed: false, method: 2, overrideReason: 'Both jobs on the same server', overriddenConflicts: [1] });
});

test('planned work given to someone else moves with the ticket, and the technician sees it but cannot give it on', async ({ page }) => {
  const jason = await technician(page, 'Jason Carter');
  const abbie = await technician(page, 'Abbie Noor');
  const work = await ticket(page, (await board(page)).id, 'Onboard the new starters', jason.id);
  const date = isoDate(4);
  const placed = await ask(() => page.request.post(api('/workforce/plan'), {
    data: { ticketId: work.id, appUserId: jason.id, start: `${date}T09:00:00Z`, end: `${date}T10:00:00Z` },
  }));
  expect(placed.ok(), `the administrator plans it for Jason: ${placed.status()} ${await placed.text()}`).toBeTruthy();

  await go(page, `/dashboard/workforce/people/${jason.id}?tab=plan&date=${date}`);
  const row = planList(page).getByRole('listitem').filter({ hasText: work.number });
  await expect(row).toContainText('09:00–10:00');
  await row.getByRole('button', { name: `Give ${work.number} to someone else` }).click();
  const dialog = page.getByRole('dialog', { name: 'Give work to someone else' });
  await expect(dialog).toContainText(jason.displayName);
  await dialog.locator('select').selectOption(abbie.id);
  await dialog.getByRole('button', { name: 'Give the work' }).click();
  await expect(dialog).toHaveCount(0);
  await expect(planList(page).getByRole('listitem').filter({ hasText: work.number })).toHaveCount(0);

  // On Abbie's plan at the same time, and the ticket is hers now.
  await go(page, `/dashboard/workforce/people/${abbie.id}?tab=plan&date=${date}`);
  const moved = planList(page).getByRole('listitem').filter({ hasText: work.number });
  await expect(moved).toContainText('09:00–10:00');
  await expect(moved).toContainText('Scheduled by you');
  const held = await (await ask(() => page.request.get(api(`/tickets/search?q=${work.number}&take=3`)))).json() as { items: { id: string; assignedToName: string | null }[] };
  expect(held.items.find((t) => t.id === work.id)?.assignedToName).toBe(abbie.displayName);

  // The ticket's own page says who has it planned.
  await go(page, `/dashboard/tickets/${work.id}`);
  const panel = page.getByRole('region', { name: 'Planned work' });
  await expect(panel).toContainText(abbie.displayName);
  await expect(panel).toContainText('09:00–10:00');

  // Abbie sees it on her plan: flexible, so hers to move, but not hers to give away or take out.
  await viewAs(page, abbie);
  await go(page, `/dashboard/workforce/my-plan?date=${date}`);
  const hers = planList(page).getByRole('listitem').filter({ hasText: work.number });
  await expect(hers).toContainText('Scheduled by');
  await expect(hers.getByRole('button', { name: `Move ${work.number}`, exact: true })).toBeVisible();
  await expect(hers.getByRole('button', { name: `Give ${work.number} to someone else` })).toHaveCount(0);
  await expect(hers.getByRole('button', { name: `Remove ${work.number} from the plan` })).toHaveCount(0);
  // And Jason no longer sees it anywhere: not his plan, not his unscheduled work.
  await exitView(page);
  await viewAs(page, jason);
  await go(page, `/dashboard/workforce/my-plan?date=${date}`);
  await expect(figure(page, 'Planned')).toHaveText('0h');
  await expect(page.getByText(work.number)).toHaveCount(0);
  await exitView(page);
});

test('a small piece of internal work is raised on a board and planned in one step', async ({ page }) => {
  await me(page);
  const b = await board(page);
  const date = isoDate(5);
  const title = `Tidy the switch labels ${stamp()}`;

  await go(page, `/dashboard/workforce/my-plan?date=${date}`);
  await page.getByRole('button', { name: 'Internal work' }).click();
  const dialog = page.getByRole('dialog', { name: 'Plan internal work' });
  await dialog.getByLabel('Work title').fill(title);
  await expect(dialog.getByLabel('Board')).not.toHaveValue('');
  await dialog.getByLabel('Board').selectOption(b.id);
  await dialog.getByLabel('Internal work date').fill(date);
  await dialog.getByLabel('Internal work start').fill('10:00');
  await dialog.getByLabel('Internal work duration').fill('30');
  await dialog.getByRole('button', { name: 'Raise and plan' }).click();
  await expect(dialog).toHaveCount(0);

  const row = planList(page).getByRole('listitem').filter({ hasText: title });
  await expect(row).toContainText('10:00–10:30');
  await expect(row).toContainText('Planned by you');
  await expect(row).toContainText(`${b.key}-`);
  // A real ticket, on the board, held by me, open.
  const found = await (await ask(() => page.request.get(api(`/tickets/search?q=${encodeURIComponent(title)}&take=5`)))).json() as { items: { number: string | null; assignedToName: string | null; portalStatus: string }[] };
  expect(found.items).toHaveLength(1);
  expect(found.items[0].number).toBeTruthy();
  expect(found.items[0].portalStatus).not.toMatch(/closed|resolved/i);
});

test('an account without the scheduling permission is refused My plan and every planning endpoint', async ({ page }) => {
  // A client login cannot be produced in a local run, and a client is exactly this: a signed-in
  // account that holds no scheduling permission. That no client ROLE can ever hold one is proven
  // at the endpoint level in the API's own tests.
  const s = stamp();
  const role = await (await ask(() => page.request.post(api('/admin/roles'), {
    data: { name: `Tickets only ${s}`, grants: [{ permissionKey: 'tickets.view.assigned', scope: 30 }] },
  }))).json() as { id: string };
  const outsider = await (await ask(() => page.request.post(api('/admin/users'), {
    data: { displayName: `No Planning ${s}`, email: `no.planning.${s}@techpio.test`, roleIds: [role.id] },
  }))).json() as { id: string; displayName: string };
  const jason = await technician(page, 'Jason Carter');
  const work = await ticket(page, (await board(page)).id, 'Private work', jason.id);
  const date = isoDate(6);
  const planned = await ask(() => page.request.post(api('/workforce/plan'), {
    data: { ticketId: work.id, appUserId: jason.id, start: `${date}T09:00:00Z`, end: `${date}T10:00:00Z` },
  }));
  expect(planned.ok(), `${planned.status()} ${await planned.text()}`).toBeTruthy();

  await viewAs(page, outsider);
  await expect(page.getByRole('link', { name: 'Workforce', exact: true })).toHaveCount(0);
  for (const path of [`/dashboard/workforce/my-plan?date=${date}`, `/dashboard/workforce/people/${jason.id}?tab=plan&date=${date}`]) {
    await go(page, path);
    await expect(page.getByRole('alert').first()).toBeVisible();
    await expect(page.getByText(work.number)).toHaveCount(0);
    await expect(page.getByText(work.title)).toHaveCount(0);
  }
  // The ticket page carries no planned-work panel for them, whatever the address.
  await go(page, `/dashboard/tickets/${work.id}`);
  await expect(page.getByRole('region', { name: 'Planned work' })).toHaveCount(0);
  for (const path of [
    `/workforce/people/${jason.id}/plan?from=${date}&to=${date}`, `/workforce/people/${outsider.id}/plan`, '/workforce/plan/unscheduled', '/workforce/plan/people',
    `/workforce/tickets/${work.id}/plan`,
  ])
    expect((await ask(() => page.request.get(api(path)))).status(), path).toBe(403);
  await exitView(page);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 375, height: 740 } });

  test('My plan fits the screen and still plans work', async ({ page }) => {
    const who = await me(page);
    const work = await ticket(page, (await board(page)).id, 'Phone-sized work', who.userId);
    const date = isoDate(7);

    await go(page, `/dashboard/workforce/my-plan?date=${date}`);
    await expect(page.getByRole('heading', { name: 'My plan' })).toBeVisible();
    await expect(figure(page, 'Free')).toHaveText('8h');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'no sideways scroll').toBeTruthy();

    await page.getByRole('listitem').filter({ hasText: work.number }).getByRole('button', { name: 'Add to plan' }).click();
    const dialog = page.getByRole('dialog', { name: 'Plan work' });
    await dialog.getByLabel('Plan date').fill(date);
    await (await freeWindow(page, dialog, '13:30')).click();
    await dialog.getByRole('button', { name: 'Add to plan' }).click();
    await expect(dialog).toHaveCount(0);
    await expect(planList(page).getByRole('listitem').filter({ hasText: work.number })).toContainText('13:30–14:30');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'no sideways scroll with work planned').toBeTruthy();
  });
});
