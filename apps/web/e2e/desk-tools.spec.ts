import { test, expect } from '@playwright/test';

/**
 * Step three of the desk, driven the way a lead sets it up and a technician uses it: an SLA plan on a
 * board, a canned reply with placeholders, a table in a note, and a task list that has to be finished
 * before the ticket will close.
 */
test.describe('desk tools', () => {
  test('an SLA plan, a canned reply and a task list work together on one ticket', async ({ page }) => {
    const run = Date.now().toString().slice(-6);

    // A plan: reply within an hour, resolve within eight, round the clock.
    await page.goto('/dashboard/boards/sla');
    await page.getByRole('button', { name: 'New plan' }).click();
    await page.getByLabel('Plan name').fill(`Priority ${run}`);
    await page.getByLabel('First reply within (hours)').fill('1');
    await page.getByLabel('Resolved within (hours)').fill('8');
    await page.getByRole('button', { name: 'Add plan' }).click();
    await expect(page.locator('tr').filter({ hasText: `Priority ${run}` })).toContainText('Round the clock');

    // A canned response offered on every ticket.
    await page.goto('/dashboard/boards/responses');
    await page.getByRole('button', { name: 'New response' }).click();
    await page.getByLabel('Name', { exact: true }).fill(`Picked up ${run}`);
    await page.getByLabel('Text').fill('Picked up {ticket.number} — {me} is on it.');
    await page.getByRole('button', { name: 'Add response' }).click();
    await expect(page.getByRole('heading', { name: `Picked up ${run}` })).toBeVisible();

    // A board whose tickets run on that plan.
    const key = `T${run}`.slice(0, 8);
    await page.goto('/dashboard/boards');
    await page.getByRole('button', { name: 'New board' }).click();
    const form = page.locator('form').filter({ has: page.getByLabel('Board name') });
    await form.getByLabel('Board name').fill(`Tools ${run}`);
    await form.getByLabel('Ticket prefix').fill(key);
    await form.getByLabel('SLA plan').selectOption({ label: `Priority ${run}` });
    await form.getByRole('button', { name: 'Create board' }).click();
    const card = page.locator('li').filter({ hasText: `Tools ${run}` });
    await expect(card).toContainText(`Priority ${run}`);
    await card.getByRole('link', { name: 'Open board' }).click();

    await page.getByRole('button', { name: 'New ticket' }).click();
    const raise = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
    await raise.getByLabel('What needs doing').fill(`Swap the UPS battery ${run}`);
    await raise.getByRole('button', { name: 'Raise ticket' }).click();
    const row = page.locator('tr').filter({ hasText: `Swap the UPS battery ${run}` });
    // The plan became a reply promise the moment the ticket was raised.
    await expect(row).toContainText('Reply by');

    await row.getByRole('link', { name: `Swap the UPS battery ${run}` }).click();
    await expect(page.getByText('SLA plan', { exact: true })).toBeVisible();

    // Two steps; the ticket must not close while either is open.
    await page.getByLabel('New task').fill('Order the battery');
    await page.getByRole('button', { name: 'Add', exact: true }).click();
    await page.getByLabel('New task').fill('Fit it and test on mains');
    await page.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(page.getByText('2 of 2 left')).toBeVisible();

    await page.getByLabel('Ticket status').selectOption('CLOSED');
    await expect(page.getByText(/tasks on this ticket are still open/)).toBeVisible();

    await page.getByRole('checkbox', { name: 'Order the battery' }).click();
    await expect(page.getByRole('checkbox', { name: 'Order the battery' })).toBeChecked();
    await page.getByRole('checkbox', { name: 'Fit it and test on mains' }).click();
    await expect(page.getByRole('checkbox', { name: 'Fit it and test on mains' })).toBeChecked();
    await expect(page.getByText('All done')).toBeVisible();

    // A canned reply, filled in from this ticket, plus a table from the toolbar.
    await page.getByText('Add a reply, internal note, time or status change…').click();
    await page.getByRole('button', { name: 'Canned response' }).click();
    await page.getByLabel('Insert a canned response').selectOption({ label: `Picked up ${run}` });
    const box = page.getByPlaceholder('Add an internal note — the client will not see this…');
    await expect(box).toHaveValue(new RegExp(`Picked up ${key}-000001 — .+ is on it\\.`));
    await box.press('End');
    await page.getByRole('button', { name: 'Table' }).click();
    await page.locator('form').filter({ has: page.getByLabel('Hours to log with this reply') })
      .locator('button[type="submit"]').click();

    await expect(page.getByText(`Picked up ${key}-000001`).first()).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'Item' })).toBeVisible();
    // Answering kept the reply promise.
    await expect(page.getByText(/^Met — /)).toBeVisible();

    // Everything ticked, so now it closes.
    await page.getByLabel('Ticket status').selectOption('CLOSED');
    await expect(page.getByText(/still open/)).toHaveCount(0);
  });
});
