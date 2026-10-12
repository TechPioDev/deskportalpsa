import { test, expect, type APIResponse, type Page } from '@playwright/test';

/**
 * "New ticket", pressed by a member of staff, asks which internal board the ticket belongs to.
 *
 * It used to open the client's request form, which the server accepts only from a client's own
 * user: the owner filled it in on 12 October 2026 and was told "This endpoint is for client portal
 * users". A member of staff has no use for that form; their tickets go on the team's boards.
 */

// Every test signs in as the same user; a busy run can be told 429. That is the limiter working.
async function ask(send: () => Promise<APIResponse>): Promise<APIResponse> {
  for (let attempt = 0; attempt < 8; attempt++) {
    const res = await send();
    if (res.status() !== 429) return res;
    await new Promise((r) => setTimeout(r, 10_000));
  }
  return send();
}

test.describe.configure({ timeout: 180_000 });

const stamp = () => `${Date.now().toString().slice(-6)}${Math.floor(Math.random() * 90 + 10)}`;

async function makeBoard(page: Page, s: string) {
  const key = `N${s}`.slice(0, 8).toUpperCase();
  const res = await ask(() => page.request.post('/api/bff/api/boards', { data: { name: `Projects ${s}`, key } }));
  expect(res.ok(), 'the board is made').toBeTruthy();
  return await res.json() as { id: string; name: string; key: string };
}

test('a member of staff pressing New ticket chooses a board and lands on its form', async ({ page }) => {
  const s = stamp();
  const board = await makeBoard(page, s);

  // From the Overview, as the owner did.
  await page.goto('/dashboard');
  await page.getByRole('link', { name: 'New Ticket' }).click();
  await expect(page).toHaveURL(/\/dashboard\/tickets\/new$/);
  await expect(page.getByRole('heading', { name: 'New ticket' })).toBeVisible();
  await expect(page.getByText('raised on an internal board')).toBeVisible();

  // The client's form is not what a member of staff is shown.
  await expect(page.getByRole('button', { name: 'Submit ticket' })).toHaveCount(0);
  await expect(page.getByText('This endpoint is for client portal users')).toHaveCount(0);

  const boards = page.getByRole('region', { name: 'Internal boards' });
  const mine = boards.getByRole('link').filter({ hasText: board.name });
  await expect(mine).toContainText(`Tickets numbered ${board.key}-`);
  await mine.click();

  // On the board, with the form already open: that is what they came for.
  await expect(page).toHaveURL(new RegExp(`/dashboard/boards/${board.id}`));
  const raise = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
  await expect(raise).toBeVisible();
  await raise.getByLabel('What needs doing').fill(`NinjaRMM implementation ${s}`);
  await raise.getByRole('button', { name: 'Raise ticket' }).click();
  await expect(page.locator('tr').filter({ hasText: `NinjaRMM implementation ${s}` })).toBeVisible();
});

test('someone who also has a client sign-in can still reach the request form', async ({ page }) => {
  // Staff and a client's own user at once: the development identity where it is seeded that way,
  // nobody in production. The page asks the question only a client's user gets an answer to
  // (their company's devices); the answer is stood in for here, since this sign-in has no company.
  await page.route('**/api/client/device-choices', (route) => route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }));
  await page.goto('/dashboard/tickets/new');
  await expect(page.getByText('raised on an internal board')).toBeVisible();
  await page.getByRole('button', { name: 'raise a request as a client instead' }).click();
  await expect(page.getByText('it goes straight to your IT team')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Submit ticket' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Internal boards' })).toHaveCount(0);
});

test('a technician with no client sign-in is offered the boards and never the request form', async ({ page }) => {
  const s = stamp();
  const board = await makeBoard(page, s);
  const roles = await (await ask(() => page.request.get('/api/bff/api/admin/roles'))).json() as { id: string; name: string }[];
  const tech = roles.find((r) => /technician/i.test(r.name));
  expect(tech, 'a Technician role exists').toBeTruthy();
  const person = await (await ask(() => page.request.post('/api/bff/api/admin/users', {
    data: { displayName: `Tara Tech ${s}`, email: `tara.tech.${s}@techpio.test`, roleIds: [tech!.id] },
  }))).json() as { id: string; displayName: string };

  await page.goto(`/dashboard/users/${person.id}`);
  await page.getByRole('button', { name: `View as ${person.displayName}` }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Viewing as' })).toBeVisible();

  await page.goto('/dashboard/tickets/new');
  const boards = page.getByRole('region', { name: 'Internal boards' });
  await expect(boards.getByRole('link').filter({ hasText: board.name })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Submit ticket' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'raise a request as a client instead' })).toHaveCount(0);
  await expect(page.getByText('cannot be raised from here yet')).toBeVisible();

  await boards.getByRole('link').filter({ hasText: board.name }).click();
  await expect(page.locator('form').filter({ has: page.getByLabel('What needs doing') })).toBeVisible();

  await page.getByRole('status').filter({ hasText: 'Viewing as' }).getByRole('button', { name: 'Exit view' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Viewing as' })).toHaveCount(0);
  await ask(() => page.request.delete(`/api/bff/api/admin/users/${person.id}`));
});
