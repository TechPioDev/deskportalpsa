import { test, expect } from '@playwright/test';

/**
 * Ticking a second step while the first is still saving, as happens on a slow connection to the
 * live site. The first save is held for a second; the second box is ticked during it. Both ticks
 * must reach the server, stay ticked on screen, and still be ticked after a reload.
 *
 * Written after main's Firefox run showed a second tick, made while the first was saving, sending
 * no request at all.
 */
test('a step ticked while another is still saving is saved too', async ({ page }) => {
  const key = `K${Date.now().toString().slice(-6)}`.slice(0, 8).toUpperCase();
  await page.goto('/dashboard/boards');
  await page.getByRole('button', { name: 'New board' }).click();
  const form = page.locator('form').filter({ has: page.getByLabel('Board name') });
  await form.getByLabel('Board name').fill(`Ticks ${key}`);
  await form.getByLabel('Ticket prefix').fill(key);
  await form.getByRole('button', { name: 'Create board' }).click();
  await page.locator('li').filter({ hasText: `Ticks ${key}` }).getByRole('link', { name: 'Open board' }).click();
  await page.getByRole('button', { name: 'New ticket' }).click();
  const raise = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
  await raise.getByLabel('What needs doing').fill(`Replace the UPS battery ${key}`);
  await raise.getByRole('button', { name: 'Raise ticket' }).click();
  await page.locator('tr').filter({ hasText: key }).getByRole('link').first().click();

  for (const step of ['Order the battery', 'Fit it and test on mains']) {
    await page.getByLabel('New task').fill(step);
    await page.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(page.getByRole('checkbox', { name: step })).toBeVisible();
  }

  // The first save takes a second, like a slow connection; later ones answer normally.
  let saves = 0;
  let answered = 0;
  await page.route('**/api/tickets/tasks/*/done', async (route) => {
    const mine = ++saves;
    const response = await route.fetch();
    if (mine === 1) await new Promise((r) => setTimeout(r, 1000));
    await route.fulfill({ response });
    answered++;
  });

  await page.getByRole('checkbox', { name: 'Order the battery' }).click();
  await page.waitForTimeout(300);
  await page.getByRole('checkbox', { name: 'Fit it and test on mains' }).click();

  await expect(page.getByText('All done')).toBeVisible();
  await expect(page.getByRole('checkbox', { name: 'Order the battery' })).toBeChecked();
  await expect(page.getByRole('checkbox', { name: 'Fit it and test on mains' })).toBeChecked();
  // Both saves sent and answered (the screen ticks at once, before the server has answered).
  await expect.poll(() => answered, { timeout: 10_000 }).toBe(2);
  expect(saves).toBe(2);
  await expect(page.getByRole('checkbox', { name: 'Fit it and test on mains' })).toBeChecked();

  // What the server holds, not what the screen remembers.
  await page.unroute('**/api/tickets/tasks/*/done');
  await page.reload();
  await expect(page.getByRole('checkbox', { name: 'Order the battery' })).toBeChecked();
  await expect(page.getByRole('checkbox', { name: 'Fit it and test on mains' })).toBeChecked();
});

/**
 * Ticking a step must not move the steps below it a moment later.
 *
 * The line under a done step ("Done by …") used to appear only when the server answered, and
 * pushed every step below down by 16.5 px, the height of a checkbox. A second step clicked
 * straight after the first was clicked where its box had been: no save was sent and the box
 * stayed empty. Seen as a Firefox failure of desk-tools.spec.ts on 12 October 2026, twice in one
 * run, with one save in the server's log where there should have been two.
 */
test('ticking a step does not move the steps below it when the save is answered', async ({ page }) => {
  const key = `M${Date.now().toString().slice(-6)}`.slice(0, 8).toUpperCase();
  await page.goto('/dashboard/boards');
  await page.getByRole('button', { name: 'New board' }).click();
  const form = page.locator('form').filter({ has: page.getByLabel('Board name') });
  await form.getByLabel('Board name').fill(`Still ${key}`);
  await form.getByLabel('Ticket prefix').fill(key);
  await form.getByRole('button', { name: 'Create board' }).click();
  await page.locator('li').filter({ hasText: `Still ${key}` }).getByRole('link', { name: 'Open board' }).click();
  await page.getByRole('button', { name: 'New ticket' }).click();
  const raise = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
  await raise.getByLabel('What needs doing').fill(`Rack the new switch ${key}`);
  await raise.getByRole('button', { name: 'Raise ticket' }).click();
  await page.locator('tr').filter({ hasText: key }).getByRole('link').first().click();

  for (const step of ['Mount the rails', 'Patch the uplinks']) {
    await page.getByLabel('New task').fill(step);
    await page.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(page.getByRole('checkbox', { name: step })).toBeVisible();
  }
  await expect(page.getByText('2 of 2 left')).toBeVisible();

  // The save's answer is held, so "at the tick" and "once answered" are far apart.
  let answered = 0;
  await page.route('**/api/tickets/tasks/*/done', async (route) => {
    const response = await route.fetch();
    await new Promise((r) => setTimeout(r, 700));
    await route.fulfill({ response });
    answered++;
  });

  const below = page.getByRole('checkbox', { name: 'Patch the uplinks' });
  const top = () => below.evaluate((box) => Math.round(box.getBoundingClientRect().top + window.scrollY));

  await page.getByRole('checkbox', { name: 'Mount the rails' }).click();
  // Who did it is on screen with the tick, not after the answer.
  await expect(page.getByText(/^Done by /)).toBeVisible();
  expect(answered).toBe(0);
  const atTheTick = await top();

  await expect.poll(() => answered, { timeout: 10_000 }).toBe(1);
  await expect(page.getByText('1 of 2 left')).toBeVisible();
  await page.waitForTimeout(500); // the reload of the list that follows the last save
  expect(await top(), 'the step below moved after the save was answered').toBe(atTheTick);

  // And the second tick, made at once, lands on its box.
  await below.click();
  await expect(page.getByText('All done')).toBeVisible();
  await expect.poll(() => answered, { timeout: 10_000 }).toBe(2);

  // Unticking takes the line away with the tick, not a moment later either.
  const first = page.getByRole('checkbox', { name: 'Mount the rails' });
  const before = await top();
  await first.click();
  await expect(first).not.toBeChecked();
  const atTheUntick = await top();
  await expect.poll(() => answered, { timeout: 10_000 }).toBe(3);
  await page.waitForTimeout(500);
  expect(await top(), 'the step below moved after the untick was answered').toBe(atTheUntick);
  expect(atTheUntick).toBeLessThan(before);
});
