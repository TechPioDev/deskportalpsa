import { test, expect, type Page } from '@playwright/test';
import { ask } from './support/ask';

/**
 * Keeping colleagues informed when a ticket is raised on an internal board.
 *
 * The form had "Assign to" for one person and nothing for anybody else. The owner asked for a CC
 * on 12 October 2026: the people named become the ticket's followers, as if each had been added
 * from the ticket afterwards, and the assignee is not listed twice. That they are then told (by
 * e-mail, by the worker) is held by the unit tests; no worker runs beside this suite.
 */

const stamp = () => `${Date.now().toString().slice(-6)}${Math.floor(Math.random() * 90 + 10)}`;

async function person(page: Page, name: string, roleId: string, s: string) {
  const res = await ask(() => page.request.post('/api/bff/api/admin/users', {
    data: { displayName: `${name} ${s}`, email: `${name.toLowerCase().replace(/\s+/g, '.')}.${s}@techpio.test`, roleIds: [roleId] },
  }));
  expect(res.ok(), `${name} is made`).toBeTruthy();
  return await res.json() as { id: string; displayName: string };
}

test('colleagues named under "Keep informed (CC)" follow the ticket that is raised', async ({ page }) => {
  const s = stamp();
  const roles = await (await ask(() => page.request.get('/api/bff/api/admin/roles'))).json() as { id: string; name: string }[];
  const role = (roles.find((r) => /technician/i.test(r.name)) ?? roles[0]).id;
  const ravi = await person(page, 'Ravi Kumar', role, s);
  const priya = await person(page, 'Priya Sharma', role, s);
  const amit = await person(page, 'Amit Verma', role, s);
  const board = await (await ask(() => page.request.post('/api/bff/api/boards', { data: { name: `Projects ${s}`, key: `C${s}`.slice(0, 8).toUpperCase() } }))).json() as { id: string };

  await page.goto(`/dashboard/boards/${board.id}?new=1`);
  const form = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
  await form.getByLabel('What needs doing').fill(`NinjaRMM implementation ${s}`);
  await form.getByLabel('Assign to').selectOption({ label: ravi.displayName });

  // Two colleagues, and a third added by mistake and taken off again.
  const cc = form.getByLabel('Keep informed (CC)');
  await cc.selectOption({ label: priya.displayName });
  await cc.selectOption({ label: amit.displayName });
  const kept = form.getByRole('list', { name: 'People kept informed' });
  await expect(kept.getByRole('listitem')).toHaveCount(2);
  await form.getByRole('button', { name: `Stop keeping ${amit.displayName} informed` }).click();
  await expect(kept.getByRole('listitem')).toHaveCount(1);
  await cc.selectOption({ label: amit.displayName });
  await expect(kept.getByRole('listitem')).toHaveCount(2);

  // The assignee is not offered as a CC: they hear about their own ticket anyway.
  await expect(cc.getByRole('option', { name: ravi.displayName })).toHaveCount(0);
  // Someone already chosen is not offered twice.
  await expect(cc.getByRole('option', { name: priya.displayName })).toHaveCount(0);

  await form.getByRole('button', { name: 'Raise ticket' }).click();
  const row = page.locator('tr').filter({ hasText: `NinjaRMM implementation ${s}` });
  await expect(row).toBeVisible();
  await row.getByRole('link', { name: `NinjaRMM implementation ${s}` }).click();

  // On the ticket: both follow it, the assignee does not.
  await expect(page.getByText('Following (2)')).toBeVisible();
  const ticketId = page.url().split('/').pop()!;
  const followers = await (await ask(() => page.request.get(`/api/bff/api/tickets/${ticketId}/followers`))).json() as { name: string }[];
  expect(followers.map((f) => f.name).sort()).toEqual([amit.displayName, priya.displayName].sort());

  for (const who of [ravi, priya, amit]) await ask(() => page.request.delete(`/api/bff/api/admin/users/${who.id}`));
});

test('a ticket raised with nobody to keep informed has no followers', async ({ page }) => {
  const s = stamp();
  const board = await (await ask(() => page.request.post('/api/bff/api/boards', { data: { name: `Quiet ${s}`, key: `Q${s}`.slice(0, 8).toUpperCase() } }))).json() as { id: string };
  await page.goto(`/dashboard/boards/${board.id}?new=1`);
  const form = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
  await form.getByLabel('What needs doing').fill(`Tidy the store room ${s}`);
  await expect(form.getByRole('list', { name: 'People kept informed' })).toHaveCount(0);
  await form.getByRole('button', { name: 'Raise ticket' }).click();
  const row = page.locator('tr').filter({ hasText: `Tidy the store room ${s}` });
  await row.getByRole('link', { name: `Tidy the store room ${s}` }).click();
  await expect(page.getByText('Nobody is watching this.')).toBeVisible();
});
