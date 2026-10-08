import { test, expect } from '@playwright/test';

/**
 * A board ticket's due date is moved later with a reason; the page shows the new date and the
 * move; a date that is not later, and a missing reason, are refused. A PSA ticket's extension
 * goes to the PSA first, which the unit tests hold against a stand-in; the browser suite has no
 * PSA ticket to move.
 */
test('a due date is extended with a reason, shown on the ticket, and a date that is not later is refused', async ({ page }) => {
  const key = `D${Date.now().toString().slice(-6)}`.slice(0, 8).toUpperCase();
  const board = await (await page.request.post('/api/bff/api/boards', { data: { name: `Due ${key}`, key } })).json() as { id: string };
  const dueAt = new Date(Date.now() + 2 * 3600_000);
  const raised = await (await page.request.post('/api/bff/api/boards/tickets', {
    data: { boardId: board.id, title: `Replace the switch ${key}`, dueAt: dueAt.toISOString() },
  })).json() as { ticketId: string };

  // Refused by the API before anything changes: not later, and no reason.
  const earlier = await page.request.post(`/api/bff/api/tickets/${raised.ticketId}/due-date`, {
    data: { dueAt: new Date(Date.now() + 3600_000).toISOString(), reason: 'Sooner, please.' },
  });
  expect(earlier.status()).toBe(400);
  expect(await earlier.text()).toContain('later than the current one');
  const noReason = await page.request.post(`/api/bff/api/tickets/${raised.ticketId}/due-date`, {
    data: { dueAt: new Date(Date.now() + 5 * 3600_000).toISOString(), reason: '' },
  });
  expect(noReason.status()).toBe(400);
  expect(await noReason.text()).toContain('Say why');

  // Through the page.
  await page.goto(`/dashboard/tickets/${raised.ticketId}`);
  await expect(page.getByText('Due date moved')).toHaveCount(0);
  await page.getByRole('button', { name: 'Extend due date' }).click();
  const form = page.getByRole('form', { name: 'Extend due date' });
  const later = new Date(Date.now() + 3 * 24 * 3600_000);
  const local = new Date(later.getTime() - later.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
  await form.getByLabel('New due date').fill(local);
  await expect(form.getByRole('button', { name: 'Move the due date' })).toBeDisabled();
  await form.getByLabel('Why').fill('Parts are on back-order until Friday.');
  await form.getByRole('button', { name: 'Move the due date' }).click();

  await expect(page.getByText('Due date moved')).toBeVisible();
  await expect(page.getByText('Once')).toBeVisible();
  await expect(page.getByText('first due')).toBeVisible();
  await expect(page.getByText('Parts are on back-order until Friday.')).toBeVisible();
  await expect(page.getByRole('form', { name: 'Extend due date' })).toHaveCount(0);

  // A second move counts, and the first date is still the one named.
  await page.getByRole('button', { name: 'Extend due date' }).click();
  const evenLater = new Date(Date.now() + 5 * 24 * 3600_000);
  await page.getByRole('form', { name: 'Extend due date' }).getByLabel('New due date')
    .fill(new Date(evenLater.getTime() - evenLater.getTimezoneOffset() * 60_000).toISOString().slice(0, 16));
  await page.getByRole('form', { name: 'Extend due date' }).getByLabel('Why').fill('Supplier slipped a week.');
  await page.getByRole('form', { name: 'Extend due date' }).getByRole('button', { name: 'Move the due date' }).click();
  await expect(page.getByText('2 times')).toBeVisible();
  await expect(page.getByText('Supplier slipped a week.')).toBeVisible();

  const detail = await (await page.request.get(`/api/bff/api/tickets/${raised.ticketId}`)).json() as { dueDate: { extensions: number; originalDueAt: string } };
  expect(detail.dueDate.extensions).toBe(2);
  expect(new Date(detail.dueDate.originalDueAt).getTime()).toBe(dueAt.getTime());
});
