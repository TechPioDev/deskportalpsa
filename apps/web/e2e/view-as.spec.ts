import { test, expect } from '@playwright/test';

/**
 * An administrator views the portal as a technician: the technician's own access, a banner that
 * says whose view it is, nothing can be changed, and one click back.
 */
test('view the portal as a technician, read-only, and exit', async ({ page }) => {
  const stamp = Date.now().toString().slice(-6);
  const roles = await page.request.get('/api/bff/api/admin/roles').then((r) => r.json()) as { id: string; name: string }[];
  const tech = roles.find((r) => /technician/i.test(r.name));
  expect(tech, 'a Technician role exists').toBeTruthy();
  const created = await page.request.post('/api/bff/api/admin/users', {
    data: { displayName: `View Test ${stamp}`, email: `view.test.${stamp}@techpio.test`, roleIds: [tech!.id] },
  });
  expect(created.ok()).toBeTruthy();
  const person = await created.json() as { id: string; displayName: string };

  await page.goto(`/dashboard/users/${person.id}`);
  await page.getByRole('button', { name: `View as ${person.displayName}` }).click();

  // Their view, said plainly, on every page.
  const banner = page.getByRole('status').filter({ hasText: 'Viewing as' });
  await expect(banner).toContainText(person.displayName);
  await expect(banner).toContainText('read-only');
  const me = await page.request.get('/api/bff/api/me').then((r) => r.json()) as { displayName: string; permissions: string[]; viewAs: { name: string } | null };
  expect(me.viewAs?.name).toBe(person.displayName);
  expect(me.displayName).toBe(person.displayName);
  expect(me.permissions).not.toContain('users.manage');

  // Nothing can be changed as them.
  const write = await page.request.post('/api/bff/api/boards', { data: { name: `Nope ${stamp}`, key: `N${stamp}`.slice(0, 6) } });
  expect(write.status()).toBe(403);
  expect(await write.text()).toContain('read-only');

  // Their admin pages are not theirs to see.
  expect((await page.request.get('/api/bff/api/admin/users')).status()).toBe(403);

  await banner.getByRole('button', { name: 'Exit view' }).click();
  await expect(page).toHaveURL(/\/dashboard\/users$/);
  await expect(page.getByRole('status').filter({ hasText: 'Viewing as' })).toHaveCount(0);
  const back = await page.request.get('/api/bff/api/me').then((r) => r.json()) as { viewAs: unknown; permissions: string[] };
  expect(back.viewAs).toBeNull();
  expect(back.permissions).toContain('users.manage');
});

test('find anyone to view as from the Users page', async ({ page }) => {
  // Its own person: tests run in parallel, so nothing another test created can be relied on.
  const stamp = Date.now().toString().slice(-6);
  const roles = await page.request.get('/api/bff/api/admin/roles').then((r) => r.json()) as { id: string; name: string }[];
  const tech = roles.find((r) => /technician/i.test(r.name))!;
  await page.request.post('/api/bff/api/admin/users', {
    data: { displayName: `Picker Person ${stamp}`, email: `picker.${stamp}@techpio.test`, roleIds: [tech.id] },
  });
  await page.goto('/dashboard/users');
  await page.getByRole('button', { name: 'View as…' }).click();
  const dialog = page.getByRole('dialog', { name: 'View as' });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Find a person').fill(`picker person ${stamp}`);
  await expect(dialog.getByRole('listitem').first()).toContainText('Staff');
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toHaveCount(0);
});
