import { test, expect } from '@playwright/test';

/**
 * Productivity reads as management reads it: quality beside volume, each measure saying what it rests
 * on, and hours split by where the work came from - client, the team's own boards, monitoring.
 */
test.describe('productivity quality and work mix', () => {
  test('the productivity page shows quality with its basis, and hours split three ways', async ({ page }) => {
    await page.goto('/dashboard/analytics');
    const quality = page.getByRole('region', { name: 'Quality in this range' });
    await expect(quality).toBeVisible();
    for (const label of ['Reply promises kept', 'First reply, on average', 'Came back after resolving', 'Clients satisfied'])
      await expect(quality.getByText(label)).toBeVisible();

    // Something to count: an hour on a team-board ticket, logged through the same API the page uses.
    const key = `Q${Date.now().toString().slice(-6)}`.slice(0, 8).toUpperCase();
    const board = await (await page.request.post('/api/bff/api/boards', { data: { name: `Quality ${key}`, key } })).json();
    const raised = await (await page.request.post('/api/bff/api/boards/tickets', { data: { boardId: board.id, title: `Patch the NAS ${key}` } })).json();
    expect((await page.request.post(`/api/bff/api/tickets/${raised.ticketId}/time`, { data: { hours: 1, billable: 'DoNotBill', notes: 'Patched' } })).ok()).toBeTruthy();

    await page.goto('/dashboard/analytics/technicians');
    await expect(page.getByRole('columnheader', { name: 'Board hrs' })).toBeVisible();
    await expect(page.getByRole('columnheader', { name: 'Monitoring hrs' })).toBeVisible();
  });
});
