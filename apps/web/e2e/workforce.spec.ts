import { test, expect, type APIResponse, type Page } from '@playwright/test';

/**
 * The workforce foundation through the browser: working schedules (day and overnight, with
 * breaks), the skill catalogue and skills on a person, the overview's skill filter, and a
 * technician's own-only view. A schedule is a capacity boundary, not attendance.
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

async function technician(page: Page, name: string) {
  const roles = await (await ask(() => page.request.get('/api/bff/api/admin/roles'))).json() as { id: string; name: string }[];
  const tech = roles.find((r) => /technician/i.test(r.name))!;
  const stamp = Date.now().toString().slice(-7);
  const res = await ask(() => page.request.post('/api/bff/api/admin/users', {
    data: { displayName: `${name} ${stamp}`, email: `${name.split(' ')[0].toLowerCase()}.${stamp}@techpio.test`, roleIds: [tech.id] },
  }));
  expect(res.ok()).toBeTruthy();
  return await res.json() as { id: string; displayName: string };
}

test('an administrator sets a day schedule with a break, and it is still there after a reload', async ({ page }) => {
  const jason = await technician(page, 'Jason Carter');
  await page.goto(`/dashboard/users/${jason.id}`);
  await page.getByRole('button', { name: 'Work schedule' }).click();

  // A new schedule starts as Mon–Fri 08:30–17:30 with a 12:30–13:30 break; Friday finishes early.
  await expect(page.getByLabel('Monday start')).toHaveValue('08:30');
  await page.getByLabel('Friday end').fill('16:30');
  await expect(page.getByText('Week: 39h to work')).toBeVisible();
  await page.getByRole('button', { name: 'Save schedule' }).click();
  await expect(page.getByRole('button', { name: 'Saved' })).toBeVisible();

  await page.reload();
  await page.getByRole('button', { name: 'Work schedule' }).click();
  await expect(page.getByLabel('Friday end')).toHaveValue('16:30');
  await expect(page.getByLabel('Monday break 1 start')).toHaveValue('12:30');
  await expect(page.getByLabel('Saturday working')).not.toBeChecked();
  await expect(page.getByText(/In force since/)).toBeVisible();
});

test('a night technician works past midnight, and a break outside the window is refused', async ({ page }) => {
  const abbie = await technician(page, 'Abbie Noor');
  await page.goto(`/dashboard/workforce/people/${abbie.id}`);

  for (const day of ['Tuesday', 'Wednesday', 'Thursday', 'Friday']) await page.getByLabel(`${day} working`).uncheck();
  await page.getByLabel('Monday start').fill('18:00');
  await page.getByLabel('Monday end').fill('03:00');
  await expect(page.getByText('Ends next day')).toBeVisible();

  // A 12:30 break is outside an 18:00–03:00 night: shown at once, and Save stays off.
  await expect(page.getByText(/is outside the working window/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save schedule' })).toBeDisabled();
  await page.getByLabel('Monday break 1 start').fill('00:00');
  await page.getByLabel('Monday break 1 end').fill('00:30');
  await expect(page.getByText('Week: 8h 30m to work')).toBeVisible();
  await page.getByRole('button', { name: 'Save schedule' }).click();
  await expect(page.getByRole('button', { name: 'Saved' })).toBeVisible();

  await page.reload();
  await expect(page.getByLabel('Monday end')).toHaveValue('03:00');
  await expect(page.getByText('Ends next day')).toBeVisible();

  // The server checks too, whatever the page allows: overlapping breaks are refused with a reason.
  const bad = await ask(() => page.request.put(`/api/bff/api/workforce/people/${abbie.id}/schedule`, {
    data: { timeZone: 'UTC', days: [{ day: 1, start: '08:30', end: '17:30', breaks: [{ start: '12:00', end: '13:00' }, { start: '12:30', end: '13:30' }] }] },
  }));
  expect(bad.status()).toBe(400);
  expect(await bad.text()).toContain('overlap');
});

test('skills are added to the catalogue, given to a person, and filter the workforce overview', async ({ page }) => {
  const sam = await technician(page, 'Sam Shah');
  const skill = `SonicWall ${Date.now().toString().slice(-6)}`;

  await page.goto('/dashboard/workforce/skills');
  // The list has loaded, so the page is live: typing earlier can be wiped as it starts up.
  await expect(page.getByText('Loading…')).toHaveCount(0);
  await page.getByLabel('Skill name').fill(skill);
  await expect(page.getByRole('button', { name: 'Add skill' })).toBeEnabled();
  await page.getByRole('button', { name: 'Add skill' }).click();
  await expect(page.getByText(skill, { exact: true })).toBeVisible();

  await page.goto(`/dashboard/workforce/people/${sam.id}`);
  await page.getByRole('tab', { name: 'Skills' }).click();
  await page.getByLabel('Skill to add').selectOption({ label: skill });
  await page.getByLabel('Level').selectOption({ label: 'Expert' });
  await page.getByRole('button', { name: 'Add skill' }).click();
  await expect(page.getByLabel(`${skill} level`)).toHaveValue('3');

  await page.goto('/dashboard/workforce');
  await page.getByRole('group', { name: 'Skills' }).getByRole('button', { name: skill }).click();
  await expect(page.getByRole('link', { name: sam.displayName })).toBeVisible();
  await expect(page.locator('tbody tr')).toHaveCount(1);

  // Retired: the holder keeps it, nobody new can be given it.
  await page.goto('/dashboard/workforce/skills');
  await page.locator('li').filter({ hasText: skill }).getByRole('button', { name: 'Retire' }).click();
  await expect(page.locator('li').filter({ hasText: skill }).getByRole('button', { name: 'Reactivate' })).toBeVisible();
});

test('a technician sees only their own schedule', async ({ page }) => {
  const lee = await technician(page, 'Lee Tan');
  const other = await technician(page, 'Other Person');

  await page.goto(`/dashboard/users/${lee.id}`);
  await page.getByRole('button', { name: `View as ${lee.displayName}` }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Viewing as' })).toBeVisible();

  const mine = await (await ask(() => page.request.get('/api/bff/api/workforce/people'))).json() as { appUserId: string }[];
  expect(mine.map((p) => p.appUserId)).toEqual([lee.id]);
  expect((await ask(() => page.request.get(`/api/bff/api/workforce/people/${other.id}/schedule`))).status()).toBe(404);
  const own = await (await ask(() => page.request.get(`/api/bff/api/workforce/people/${lee.id}/schedule`))).json() as { canManage: boolean };
  expect(own.canManage).toBe(false);

  await page.getByRole('status').filter({ hasText: 'Viewing as' }).getByRole('button', { name: 'Exit view' }).click();
  await expect(page).toHaveURL(/\/dashboard\/users$/);
});
