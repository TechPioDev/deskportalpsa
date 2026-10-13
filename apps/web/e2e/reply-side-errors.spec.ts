import { test, expect, type Page } from '@playwright/test';
import { ask } from './support/ask';

/**
 * A reply that was sent, with something beside it that was not: the page says so, where it can
 * be read.
 *
 * On 13 October 2026 the owner posted an internal note with an hour and "Set status: CLOSED" on a
 * ConnectWise ticket. The note and the hour were saved and the status was refused by the PSA.
 * The page did have a message for that, but it was drawn inside the reply window, and the window
 * closes the moment the reply is sent. Nothing was ever seen, and the ticket simply stayed NEW.
 *
 * The browser suite has no PSA ticket, so the refusal is put in place of the server's answer to
 * the status change on a board ticket: this is a test of what the page does with a refusal. Why
 * ConnectWise refused, and that it no longer does, is held by ConnectWiseBoardStatusTests.
 */

async function ticket(page: Page) {
  const key = `R${Date.now().toString().slice(-6)}${Math.floor(Math.random() * 9)}`.slice(0, 8).toUpperCase();
  const board = await (await ask(() => page.request.post('/api/bff/api/boards', { data: { name: `Replies ${key}`, key } }))).json() as { id: string };
  const raised = await (await ask(() => page.request.post('/api/bff/api/boards/tickets', {
    data: { boardId: board.id, title: `Printer on the second floor ${key}` },
  }))).json() as { ticketId: string };
  return raised.ticketId;
}

const REFUSAL = "Status id 17 does not exist on this ticket's board. Available: New, In Progress, Signed off.";
const notice = (page: Page) => page.getByRole('alert', { name: 'Not everything went through' });
const replyWindow = (page: Page) => page.getByRole('dialog', { name: 'Reply to ticket' });

async function reply(page: Page, text: string, status?: string) {
  await page.getByText('Add a reply, internal note, time or status change…').click();
  await page.getByPlaceholder('Add an internal note — the client will not see this…').fill(text);
  if (status) await page.getByLabel('Set status with this reply').selectOption(status);
  await replyWindow(page).locator('button[type="submit"]').click();
  // The reply is sent, so the window goes.
  await expect(replyWindow(page)).toHaveCount(0);
}

test('a status the PSA refuses is said on the ticket page after the reply window has closed', async ({ page }) => {
  const id = await ticket(page);
  await page.route(`**/api/bff/api/tickets/${id}/status`, (route) => route.request().method() === 'POST'
    ? route.fulfill({
        status: 502, contentType: 'application/problem+json',
        body: JSON.stringify({ type: 'https://desk.portal/errors/psa_error', title: 'psa_error', status: 502, detail: REFUSAL }),
      })
    : route.continue());
  await page.goto(`/dashboard/tickets/${id}`);

  await reply(page, 'All done, the drum was replaced.', 'CLOSED');

  // The note is on the thread, and what did not go through is said on the page itself.
  await expect(page.getByText('All done, the drum was replaced.').first()).toBeVisible();
  await expect(notice(page)).toBeVisible();
  await expect(notice(page)).toContainText('Your reply was sent, but one thing did not go through');
  await expect(notice(page)).toContainText('The status was not changed to CLOSED');
  await expect(notice(page)).toContainText(REFUSAL.replace(/\.$/, ''));
  await expect(notice(page)).toContainText('choose it again from the status menu to retry');
  await expect(page.getByLabel('Ticket status')).toHaveValue('NEW');

  // It stays until it is put away.
  await notice(page).getByRole('button', { name: 'Dismiss' }).click();
  await expect(notice(page)).toHaveCount(0);
});

test('a reply where everything went through says nothing more', async ({ page }) => {
  const id = await ticket(page);
  await page.goto(`/dashboard/tickets/${id}`);

  await reply(page, 'Looking at it now.', 'IN_PROGRESS');

  await expect(page.getByText('Looking at it now.').first()).toBeVisible();
  await expect(page.getByLabel('Ticket status')).toHaveValue('IN_PROGRESS');
  await expect(notice(page)).toHaveCount(0);
});
