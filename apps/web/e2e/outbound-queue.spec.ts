import { test, expect } from '@playwright/test';

/**
 * What a person sees of a change that has not reached the PSA: on the ticket, on the note, when a
 * status is asked for, and in the list of everything unsent.
 *
 * What the queue does (keeps a change, waits longer each time, gives up after eight tries, never
 * sends twice, never changes a status the PSA has not accepted) is held on the server by
 * OutboundQueueTests. This suite has no PSA ticket to make a change to (it cannot import any, for
 * the reason given in connections.spec.ts), so the screens are driven on a real ticket of the
 * team's own board with the server's answers about waiting changes stood in. The list of unsent
 * changes, and the refusal to act on a change that is not this ticket's, are the real API.
 */
test('a change that has not reached the PSA is said on the ticket, and is sent again or let go of there', async ({ page }) => {
  // The real API first: nothing waits, and a change cannot be reached through a ticket it is not on.
  await page.goto('/dashboard/outbound');
  await expect(page.getByRole('heading', { name: 'Unsent changes' })).toBeVisible();
  await expect(page.getByText('Nothing is waiting. Every change made here has reached its PSA.')).toBeVisible();
  await expect(page.locator('a[href="/dashboard/outbound"]').first()).toBeAttached();
  const key = `O${Date.now().toString().slice(-6)}`.slice(0, 8).toUpperCase();
  const board = await (await page.request.post('/api/bff/api/boards', { data: { name: `Outbound ${key}`, key } })).json() as { id: string };
  const raised = await (await page.request.post('/api/bff/api/boards/tickets', { data: { boardId: board.id, title: `Printer offline ${key}` } })).json() as { ticketId: string };
  const nobody = '99999999-9999-4999-8999-999999999999';
  expect((await page.request.post(`/api/bff/api/tickets/${raised.ticketId}/outbound/${nobody}/retry`)).status()).toBe(404);
  expect((await page.request.delete(`/api/bff/api/tickets/${raised.ticketId}/outbound/${nobody}`)).status()).toBe(404);
  expect((await page.request.post(`/api/bff/api/tickets/${nobody}/outbound/${nobody}/retry`)).status()).toBe(404);

  // The ticket, as the server would answer for one with a reply waiting and a status the PSA refused.
  const waiting = {
    id: '11111111-1111-4111-8111-111111111111', kind: 'Note', summary: 'Reply', state: 'Pending Sync', attempts: 2, maxAttempts: 8,
    requestedAt: '2026-10-07T09:00:00Z', requestedBy: 'Asha Rao', nextAttemptAt: '2026-10-07T09:01:30Z', lastError: 'The PSA did not answer.',
  };
  const refused = {
    id: '22222222-2222-4222-8222-222222222222', kind: 'StatusChange', summary: 'Status to RESOLVED', state: 'Sync Failed', attempts: 2, maxAttempts: 8,
    requestedAt: '2026-10-07T09:05:00Z', requestedBy: 'Asha Rao', nextAttemptAt: null, lastError: 'This board does not allow Resolved from New.',
  };
  let outbound = [waiting, refused];
  const sent: string[] = [];
  await page.route(`**/api/bff/api/tickets/${raised.ticketId}`, async (route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const response = await route.fetch();
    const real = await response.json();
    await route.fulfill({
      response,
      json: {
        ...real, outbound: outbound.length ? outbound : null,
        conversation: [...real.conversation, {
          id: '33333333-3333-4333-8333-333333333333', authorName: 'Asha Rao', authoredByClient: false, body: 'We are sending an engineer at 2pm.',
          createdAt: '2026-10-07T09:00:00Z', isPublic: true, syncState: outbound.includes(waiting) ? 'Pending Sync' : null,
        }],
      },
    });
  });
  await page.route(`**/api/bff/api/tickets/${raised.ticketId}/outbound/**`, async (route) => {
    const id = route.request().url().split('/outbound/')[1].split('/')[0];
    sent.push(`${route.request().method()} ${id}`);
    if (route.request().method() === 'DELETE') { outbound = outbound.filter((o) => o.id !== id); return route.fulfill({ status: 204 }); }
    outbound = outbound.filter((o) => o.id !== id);   // sent again, and this time the PSA took it
    return route.fulfill({ json: { id, state: 'Pending Sync' } });
  });

  await page.goto(`/dashboard/tickets/${raised.ticketId}`);
  const panel = page.getByRole('region', { name: 'Not in the PSA yet' });
  await expect(panel.getByRole('heading', { name: '2 changes are not in the PSA yet' })).toBeVisible();
  const reply = panel.getByRole('listitem').filter({ hasText: 'Reply' });
  await expect(reply.getByText('Pending Sync', { exact: true })).toBeVisible();
  await expect(reply).toContainText('Asha Rao');
  await expect(reply).toContainText('tried 2 of 8');
  await expect(reply).toContainText('next try');
  await expect(reply).toContainText('The PSA did not answer.');
  const status = panel.getByRole('listitem').filter({ hasText: 'Status to RESOLVED' });
  await expect(status.getByText('Sync Failed', { exact: true })).toBeVisible();
  await expect(status).toContainText('This board does not allow Resolved from New.');
  await expect(status).not.toContainText('next try');
  await expect(panel.getByText('Synced', { exact: true })).toHaveCount(0);
  // The reply itself says it is waiting, so it is not taken for one the customer has.
  const note = page.locator('div', { hasText: 'We are sending an engineer at 2pm.' }).last();
  await expect(page.getByText('Pending Sync', { exact: true })).toHaveCount(2);
  await expect(note).toBeVisible();

  // The refused status is let go of: asked first, then gone, and not sent.
  page.once('dialog', (dialog) => { expect(dialog.message()).toContain('It will not be sent to the PSA'); void dialog.accept(); });
  await status.getByRole('button', { name: 'Let go of: Status to RESOLVED' }).click();
  await expect(panel.getByRole('heading', { name: '1 change is not in the PSA yet' })).toBeVisible();
  expect(sent).toEqual([`DELETE ${refused.id}`]);

  // The waiting reply is tried now, and with nothing left the panel is gone.
  await reply.getByRole('button', { name: 'Send again: Reply' }).click();
  await expect(panel).toHaveCount(0);
  expect(sent).toEqual([`DELETE ${refused.id}`, `POST ${waiting.id}`]);
  await expect(page.getByText('Pending Sync', { exact: true })).toHaveCount(0);

  // A status asked for while the PSA is away: the ticket keeps the status it has, and says why.
  await page.route(`**/api/bff/api/tickets/${raised.ticketId}/status`, (route) => route.fulfill({
    status: 202, json: { portalStatus: 'NEW', queued: true, requested: 'IN_PROGRESS', operationId: waiting.id, state: 'Pending Sync' },
  }));
  await page.getByLabel('Ticket status').selectOption('IN_PROGRESS');
  const said = page.getByRole('status').filter({ hasText: 'The PSA could not be reached' });
  await expect(said).toContainText('the status is still NEW');
  await expect(said).toContainText('The change to IN_PROGRESS is kept');
  await expect(page.getByLabel('Ticket status')).toHaveValue('NEW');
  // The page goes on asking for the ticket; an answer still on its way as the test ends is let go of.
  await page.unrouteAll({ behavior: 'ignoreErrors' });
});

test('the list of unsent changes shows what failed first, with why, and leads to the ticket', async ({ page }) => {
  const ticket = '44444444-4444-4444-8444-444444444444';
  await page.route('**/api/bff/api/admin/outbound', (route) => route.fulfill({
    json: [
      {
        id: 'a1111111-1111-4111-8111-111111111111', ticketId: ticket, ticketReference: '500', ticketTitle: 'Printer offline', connectionName: 'Main',
        kind: 'StatusChange', summary: 'Status to RESOLVED', state: 'Sync Failed', attempts: 8, maxAttempts: 8,
        requestedAt: '2026-10-07T08:00:00Z', requestedBy: 'Asha Rao', nextAttemptAt: null, lastError: 'The PSA could not be reached in 8 tries.',
      },
      {
        id: 'b2222222-2222-4222-8222-222222222222', ticketId: ticket, ticketReference: null, ticketTitle: 'The scanner will not feed', connectionName: 'Main',
        kind: 'TicketCreate', summary: 'New ticket', state: 'Pending Sync', attempts: 3, maxAttempts: 8,
        requestedAt: '2026-10-07T09:00:00Z', requestedBy: 'Priya Nair', nextAttemptAt: '2026-10-07T09:04:00Z', lastError: 'The PSA did not answer.',
      },
    ],
  }));
  await page.goto('/dashboard/outbound');
  const list = page.getByRole('region', { name: 'Unsent changes' });
  await expect(list.getByText('2 changes · 1 failed · 1 waiting')).toBeVisible();
  const rows = list.getByRole('row');
  await expect(rows.nth(1)).toContainText('#500');
  await expect(rows.nth(1)).toContainText('Sync Failed');
  await expect(rows.nth(1)).toContainText('8 of 8');
  await expect(rows.nth(1)).toContainText('The PSA could not be reached in 8 tries.');
  await expect(rows.nth(2)).toContainText('Not in the PSA');
  await expect(rows.nth(2)).toContainText('Pending Sync');
  await expect(rows.nth(2)).toContainText('New ticket');
  await expect(list.getByText('Synced', { exact: true })).toHaveCount(0);
  await expect(rows.nth(1).getByRole('link', { name: '#500' })).toHaveAttribute('href', `/dashboard/tickets/${ticket}`);
});
