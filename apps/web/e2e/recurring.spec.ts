import { test, expect } from '@playwright/test';

/**
 * Step four of the desk, driven the way a lead sets it up: a holiday on the calendar, a schedule
 * that raises its own ticket with a checklist, and a ticket whose SLA stops while it waits on the
 * customer.
 */
test.describe('recurring tickets and the SLA pause', () => {
  test('a schedule raises its ticket, and waiting on the customer pauses the clock', async ({ page }) => {
    const run = Date.now().toString().slice(-6);

    // A plan that pauses, and a closed day on the calendar.
    await page.goto('/dashboard/boards/sla');
    await page.getByRole('button', { name: 'New plan' }).click();
    await page.getByLabel('Plan name').fill(`Standard ${run}`);
    await page.getByLabel('Resolved within (hours)').fill('24');
    await expect(page.getByRole('checkbox', { name: /Pause while waiting/ })).toBeChecked();
    await page.getByRole('button', { name: 'Add plan' }).click();
    await expect(page.locator('tr').filter({ hasText: `Standard ${run}` })).toContainText('Pauses while waiting');

    // A date of this run's own. The cross-browser job runs every browser against one database, and a
    // desk has one entry per closed day — so a fixed date was, correctly, refused as a duplicate the
    // second time round.
    const next = new Date(Date.now() + (30 + (Number(run) % 900)) * 86_400_000).toISOString().slice(0, 10);
    await page.getByLabel('Holiday date').fill(next);
    await page.getByLabel('Holiday name').fill(`Closure ${run}`);
    await page.getByRole('button', { name: 'Add holiday' }).click();
    await expect(page.getByText(`Closure ${run}`)).toBeVisible();

    // A board on that plan.
    const key = `R${run}`.slice(0, 8);
    await page.goto('/dashboard/boards');
    await page.getByRole('button', { name: 'New board' }).click();
    const form = page.locator('form').filter({ has: page.getByLabel('Board name') });
    await form.getByLabel('Board name').fill(`Routine ${run}`);
    await form.getByLabel('Ticket prefix').fill(key);
    await form.getByLabel('SLA plan').selectOption({ label: `Standard ${run}` });
    await form.getByRole('button', { name: 'Create board' }).click();
    await expect(page.locator('li').filter({ hasText: `Routine ${run}` })).toBeVisible();

    // A schedule, raised now to see it does what was meant.
    await page.getByRole('link', { name: 'Recurring' }).click();
    await page.getByRole('button', { name: 'New schedule' }).click();
    await page.getByLabel('Ticket title').fill(`Backup restore test ${run}`);
    await page.getByRole('combobox', { name: 'Board', exact: true }).selectOption({ label: `Routine ${run}` });
    await page.getByLabel('Repeats').selectOption({ label: 'Every month' });
    await page.getByLabel('On day').selectOption({ label: 'Last day of the month' });
    await page.getByLabel('Checklist — one step per line').fill('Pick a random file\nRestore it\nRecord the result');
    await page.getByRole('button', { name: 'Add schedule' }).click();

    const card = page.locator('li').filter({ hasText: `Backup restore test ${run}` });
    await expect(card).toContainText('Last day of every month at 09:00');
    await card.getByRole('button', { name: 'Raise now' }).click();
    await expect(card.getByRole('link', { name: `Raised ${key}-000001.` })).toBeVisible();

    await card.getByRole('link', { name: `Raised ${key}-000001.` }).click();
    await expect(page.getByRole('heading', { name: `Backup restore test ${run}` })).toBeVisible();
    await expect(page.getByText('3 of 3 left')).toBeVisible();
    await expect(page.getByText(`Standard ${run}`)).toBeVisible();

    // Waiting on the customer stops the clock; the rail says so instead of counting down.
    await page.getByLabel('Ticket status').selectOption('WAITING_CUSTOMER');
    await expect(page.getByText(/^Paused since /).first()).toBeVisible();

    await page.getByLabel('Ticket status').selectOption('IN_PROGRESS');
    await expect(page.getByText(/^Paused since /)).toHaveCount(0);
    await expect(page.getByText(/^By /).first()).toBeVisible();
  });

  test('a night shift plan can be set up and says when it closes', async ({ page }) => {
    const run = Date.now().toString().slice(-6);
    await page.goto('/dashboard/boards/sla');
    await page.getByRole('button', { name: 'New plan' }).click();
    await page.getByLabel('Plan name').fill(`Nights ${run}`);
    await page.getByLabel('Resolved within (hours)').fill('8');
    await page.getByRole('radio', { name: /Working hours only/ }).check();
    await page.getByRole('combobox', { name: 'Opens', exact: true }).selectOption('22');
    await page.getByRole('combobox', { name: 'Closes', exact: true }).selectOption('6');
    await expect(page.getByText(/A night shift: each working day's shift opens at 22:00/)).toBeVisible();
    await page.getByRole('button', { name: 'Add plan' }).click();
    await expect(page.locator('tr').filter({ hasText: `Nights ${run}` })).toContainText('22:00–06:00 next morning');
  });
});
