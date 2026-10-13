import { test, expect, type Page } from '@playwright/test';
import { ask } from './support/ask';

/**
 * The "Client approval" panel on a ticket is shown when there is something to do or to read, and
 * not otherwise.
 *
 * It used to be shown on every client ticket whose client had no approver listed, as a prompt to
 * set approvers up. On 13 October 2026 no client had one, so the prompt sat above the reply box
 * of every client ticket, and the owner asked for it to go: "no need yet".
 *
 * The browser suite has no PSA ticket, and approvals apply only to those. So a board ticket's
 * page is opened and what the server would say about approvals is put in its place: this is a
 * test of what the page does with each answer. The answers themselves are held by ApprovalTests.
 */

async function ticket(page: Page) {
  const key = `A${Date.now().toString().slice(-6)}${Math.floor(Math.random() * 9)}`.slice(0, 8).toUpperCase();
  const board = await (await ask(() => page.request.post('/api/bff/api/boards', { data: { name: `Approvals ${key}`, key } }))).json() as { id: string };
  const raised = await (await ask(() => page.request.post('/api/bff/api/boards/tickets', {
    data: { boardId: board.id, title: `New laptop for the front desk ${key}` },
  }))).json() as { ticketId: string };
  return raised.ticketId;
}

async function openWith(page: Page, ticketId: string, approvals: unknown) {
  await page.route(`**/api/bff/api/tickets/${ticketId}/approvals`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(approvals) }));
  const answered = page.waitForResponse((r) => r.url().endsWith(`/tickets/${ticketId}/approvals`));
  await page.goto(`/dashboard/tickets/${ticketId}`);
  await answered;
  // The page is drawn: the reply launcher sits right under where the panel would be.
  await expect(page.getByRole('button', { name: /Add a reply/ })).toBeVisible();
}

const panel = (page: Page) => page.getByRole('region', { name: 'Client approval' });

test('a client with no approver listed shows no approval panel', async ({ page }) => {
  const id = await ticket(page);
  await openWith(page, id, {
    applies: true, canAsk: false, missingApprovers: true, approvals: [], approvers: [],
    reason: 'This client has no approvers listed yet. Their administrator adds them in the Control Panel, under Approvers.',
  });

  await expect(panel(page)).toHaveCount(0);
  await expect(page.getByText('no approvers listed yet')).toHaveCount(0);
});

test('a client with an approver shows the panel and offers to ask', async ({ page }) => {
  const id = await ticket(page);
  await openWith(page, id, {
    applies: true, canAsk: true, missingApprovers: false, reason: null, approvals: [],
    approvers: [{ id: 'a1', name: 'Meera Joshi', email: 'meera@needtech.test', scope: 'Purchases', canAnswerInPortal: true }],
  });

  await expect(panel(page)).toBeVisible();
  await expect(panel(page).getByRole('button', { name: 'Ask for approval' })).toBeVisible();
});

test('an answer already given stays readable when nothing more can be asked', async ({ page }) => {
  const id = await ticket(page);
  await openWith(page, id, {
    applies: true, canAsk: false, missingApprovers: true, approvers: [],
    reason: 'This client has no approvers listed yet. Their administrator adds them in the Control Panel, under Approvers.',
    approvals: [{
      id: 'p1', approverName: 'Meera Joshi', request: 'Buy one Lenovo T14', requestedByName: 'Harpal Singh',
      requestedAt: '2026-10-01T09:00:00Z', state: 'Approved', decidedAt: '2026-10-01T11:30:00Z', decisionComment: 'Go ahead',
      channel: 'Phone', recordedByName: 'Harpal Singh', canAnswer: false,
    }],
  });

  await expect(panel(page)).toBeVisible();
  await expect(panel(page)).toContainText('Approved');
  await expect(panel(page)).toContainText('Buy one Lenovo T14');
});
