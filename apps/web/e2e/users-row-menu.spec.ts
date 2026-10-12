import { test, expect, type Locator, type Page } from '@playwright/test';

/**
 * The menu at the end of a person's row must be seen, not only present.
 *
 * It used to be placed inside the row, and the list sits in a box that scrolls sideways; such a
 * box cuts off whatever hangs below it. On the last rows the menu was cut off, and on a list of
 * one (a search for one person, which is how an administrator finds whom to invite) it was hidden
 * altogether: "Send invitation" was in the page and could not be seen. Found on 12 October 2026
 * while photographing that very step for the owner's guide.
 */

/** Whether the middle of the control is what a click there would land on, inside the window. */
async function reallyVisible(page: Page, control: Locator): Promise<boolean> {
  const box = await control.boundingBox();
  if (!box) return false;
  const size = page.viewportSize()!;
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  if (x < 0 || y < 0 || x > size.width || y > size.height) return false;
  return control.evaluate((el, at) => {
    const top = document.elementFromPoint(at.x, at.y);
    return top !== null && (top === el || el.contains(top));
  }, { x, y });
}

test('the menu of the only row in the list shows every action', async ({ page }) => {
  const stamp = Date.now().toString().slice(-7);
  const email = `menu${stamp}@example.test`;
  const roles = await (await page.request.get('/api/bff/api/admin/roles')).json() as { id: string; name: string }[];
  const role = roles.find((r) => /technician/i.test(r.name)) ?? roles[0];
  const created = await (await page.request.post('/api/bff/api/admin/users', {
    data: { displayName: `Mina Menu ${stamp}`, email, roleIds: [role.id] },
  })).json() as { id: string };

  await page.goto('/dashboard/users');
  await page.getByPlaceholder('Search name or email…').fill(email);
  const row = page.getByRole('row').filter({ hasText: email });
  await expect(row).toHaveCount(1);
  await row.getByRole('button', { name: 'Actions' }).click();

  const menu = page.getByRole('menu', { name: 'Actions for this person' });
  await expect(menu).toBeVisible();
  for (const name of ['Send invitation', 'Manage', 'Deactivate', 'Copy sign-in instructions', 'Delete']) {
    const item = menu.getByRole('button', { name });
    await expect(item).toBeVisible();
    expect(await reallyVisible(page, item), `"${name}" is covered or cut off`).toBe(true);
  }

  // The menu belongs to its button: when the page moves, it moves with it.
  const actions = row.getByRole('button', { name: 'Actions' });
  const gap = async () => {
    const m = (await menu.boundingBox())!;
    const b = (await actions.boundingBox())!;
    return { across: Math.round(m.x + m.width - (b.x + b.width)), down: Math.round(Math.min(Math.abs(m.y - (b.y + b.height)), Math.abs(b.y - (m.y + m.height)))) };
  };
  expect(await gap()).toEqual({ across: 0, down: 4 });
  await page.mouse.wheel(0, 120);
  await page.waitForTimeout(300);
  await expect(menu).toBeVisible();
  expect(await gap()).toEqual({ across: 0, down: 4 });

  // And it still does its job.
  await menu.getByRole('button', { name: 'Send invitation' }).click();
  await expect(page.getByRole('status')).toContainText(email, { timeout: 15_000 });
  await page.request.delete(`/api/bff/api/admin/users/${created.id}`);
});

test('near the bottom of the window the menu opens upwards and stays whole', async ({ page }) => {
  const stamp = Date.now().toString().slice(-7);
  const email = `low${stamp}@example.test`;
  const roles = await (await page.request.get('/api/bff/api/admin/roles')).json() as { id: string; name: string }[];
  const role = roles.find((r) => /technician/i.test(r.name)) ?? roles[0];
  const created = await (await page.request.post('/api/bff/api/admin/users', {
    data: { displayName: `Lata Low ${stamp}`, email, roleIds: [role.id] },
  })).json() as { id: string };

  // A short window puts the row close to its bottom edge.
  await page.setViewportSize({ width: 1280, height: 560 });
  await page.goto('/dashboard/users');
  await page.getByPlaceholder('Search name or email…').fill(email);
  const row = page.getByRole('row').filter({ hasText: email });
  await expect(row).toHaveCount(1);
  const actions = row.getByRole('button', { name: 'Actions' });
  await actions.evaluate((el) => el.scrollIntoView({ block: 'end' }));
  await actions.click();
  const menu = page.getByRole('menu', { name: 'Actions for this person' });
  await expect(menu).toBeVisible();
  const box = (await menu.boundingBox())!;
  expect(box.y).toBeGreaterThanOrEqual(0);
  expect(box.y + box.height).toBeLessThanOrEqual(560);
  for (const name of ['Send invitation', 'Delete']) {
    expect(await reallyVisible(page, menu.getByRole('button', { name })), `"${name}" is covered or cut off`).toBe(true);
  }
  await page.request.delete(`/api/bff/api/admin/users/${created.id}`);
});
