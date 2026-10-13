import { test, expect } from '@playwright/test';

/**
 * On My plan, what is under the plan must not be drawn before the plan is.
 *
 * The plan is one line of text while it loads and about 230 px taller once it has arrived. The
 * "Unscheduled work of mine" section used to be drawn straight away underneath, so its "Internal
 * work" button appeared and then jumped down a moment later. A click made in that moment landed on
 * whatever had moved under the pointer: the button took the focus and nothing opened. Seen in the
 * cross-browser run of 12 October 2026 (Firefox, both attempts of workforce-plan.spec.ts:264).
 */
test('the Internal work button is where it will stay from the moment it appears', async ({ page }) => {
  // Hold the plan back, so the moment between "page drawn" and "plan arrived" is long enough to measure.
  await page.route('**/api/bff/api/workforce/people/*/plan*', async (route) => {
    await new Promise((r) => setTimeout(r, 1500));
    await route.continue();
  });

  await page.goto('/dashboard/workforce/my-plan');
  const button = page.getByRole('button', { name: 'Internal work' });
  await button.waitFor();
  const first = await button.boundingBox();

  await expect(page.getByRole('list', { name: 'Plan' })).toBeVisible();
  const after = await button.boundingBox();
  expect(first).not.toBeNull();
  expect(Math.abs(after!.y - first!.y)).toBeLessThan(1);

  // And it does what it says the moment it can be pressed.
  await button.click();
  await expect(page.getByRole('dialog', { name: 'Plan internal work' })).toBeVisible();
});

test('when the plan cannot be loaded, the unscheduled work is still shown', async ({ page }) => {
  await page.route('**/api/bff/api/workforce/people/*/plan*', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ title: 'The plan could not be read.' }) }));

  await page.goto('/dashboard/workforce/my-plan');
  await expect(page.getByRole('alert').first()).toBeVisible();
  await expect(page.getByRole('heading', { name: /Unscheduled work of mine/ })).toBeVisible();
});
