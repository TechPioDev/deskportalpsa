import { test, expect } from '@playwright/test';

/**
 * A board that reviews resolved work: resolving puts it in front of a lead instead of closing it,
 * closing without a review is refused, a send-back needs a note and returns it to work, and an
 * approval closes it. Then two tickets linked, read from both sides.
 */
test.describe('review and related tickets', () => {
  test('resolve, refused close, send back, approve; link two tickets', async ({ page }) => {
    const key = `R${Date.now().toString().slice(-6)}`.slice(0, 8).toUpperCase();

    await page.goto('/dashboard/boards');
    await page.getByRole('button', { name: 'New board' }).click();
    const form = page.locator('form').filter({ has: page.getByLabel('Board name') });
    await form.getByLabel('Board name').fill(`Review ${key}`);
    await form.getByLabel('Ticket prefix').fill(key);
    await form.getByLabel('Review resolved work').check();
    await form.getByRole('button', { name: 'Create board' }).click();
    await page.locator('li').filter({ hasText: `Review ${key}` }).getByRole('link', { name: 'Open board' }).click();

    const raise = async (title: string) => {
      await page.getByRole('button', { name: 'New ticket' }).click();
      const f = page.locator('form').filter({ has: page.getByLabel('What needs doing') });
      await f.getByLabel('What needs doing').fill(title);
      await f.getByRole('button', { name: 'Raise ticket' }).click();
      await expect(page.locator('tr').filter({ hasText: title })).toBeVisible();
    };
    // Titles carry this run's key: CI runs every browser against one database, and a link search
    // that finds the same title from an earlier browser's run cannot tell which ticket to pick.
    const firewall = `Open port 443 on the edge firewall ${key}`;
    const intranet = `Publish the intranet externally ${key}`;
    await raise(firewall);
    await raise(intranet);
    const boardUrl = page.url();
    await page.locator('tr').filter({ hasText: firewall })
      .getByRole('link', { name: firewall }).click();
    await expect(page.getByRole('heading', { name: firewall })).toBeVisible();
    await expect(page.getByText('A board lead reviews this work before it closes')).toBeVisible();

    // Resolved: it waits for a lead instead of closing.
    const status = page.getByLabel('Ticket status');
    await status.selectOption('RESOLVED');
    await page.getByRole('form', { name: 'Resolution' }).getByLabel(/What fixed it/).fill('Rule 14 added for 443 from the load balancer.');
    await page.getByRole('form', { name: 'Resolution' }).getByRole('button', { name: 'Mark resolved' }).click();
    const review = page.getByRole('region', { name: 'Review' });
    await expect(review).toContainText('Waiting for review');

    // Closing around the review is refused, and says why.
    await status.selectOption('CLOSED');
    await expect(page.getByText(/reviewed before it closes/)).toBeVisible();
    await expect(status).toHaveValue('RESOLVED');

    // A send-back says what needs doing, and the ticket returns to work.
    await review.getByRole('button', { name: 'Send back' }).click();
    await expect(review.getByRole('alert')).toContainText('Say what needs doing');
    await review.getByLabel('Review note').fill('Rule 14 still allows any source; limit it to the load balancer.');
    await review.getByRole('button', { name: 'Send back' }).click();
    await expect(status).toHaveValue('IN_PROGRESS');
    await expect(page.getByText('sent back once so far')).toBeVisible();
    await expect(page.getByText('Sent back in review: Rule 14 still allows any source').first()).toBeVisible();
    // A send-back is not a reopen.
    await expect(page.getByText(/Reopened once/)).toHaveCount(0);

    // Resolved again and approved: it closes.
    await status.selectOption('RESOLVED');
    await page.getByRole('form', { name: 'Resolution' }).getByLabel(/What fixed it/).fill('Rule 14 now allows 443 from the load balancer only.');
    await page.getByRole('form', { name: 'Resolution' }).getByRole('button', { name: 'Mark resolved' }).click();
    await page.getByRole('region', { name: 'Review' }).getByRole('button', { name: 'Approve and close' }).click();
    await expect(status).toHaveValue('CLOSED');
    await expect(page.getByText(/Approved in review by .+, after 1 send-back\./)).toBeVisible();

    const history = page.getByRole('region', { name: 'History' });
    await history.getByRole('button', { name: /Show all/ }).click();
    await expect(history).toContainText('Sent back in review');
    await expect(history).toContainText('Approved in review');

    // Linked: this job blocks the other one, which reads the link the other way round.
    const related = page.getByRole('region', { name: 'Related tickets' });
    await related.getByRole('button', { name: 'Link a ticket' }).click();
    await related.getByLabel('How it relates').selectOption({ label: 'Blocks' });
    await related.getByLabel('Find a ticket to link').fill(intranet);
    await related.getByRole('button', { name: new RegExp(intranet) }).click();
    await expect(related.getByRole('listitem')).toContainText('Blocks');
    await expect(related.getByRole('listitem')).toContainText(intranet);

    await page.goto(boardUrl);
    await page.locator('tr').filter({ hasText: intranet })
      .getByRole('link', { name: intranet }).click();
    const other = page.getByRole('region', { name: 'Related tickets' });
    await expect(other.getByRole('listitem')).toContainText('Blocked by');
    await expect(other.getByRole('listitem')).toContainText(firewall);
    await expect(page.getByRole('region', { name: 'History' })).toContainText('Linked: Blocked by');

    // Removed from this side, gone from both.
    await other.getByRole('button', { name: /Remove link to/ }).click();
    await expect(other.getByText('None linked.')).toBeVisible();
  });

  test('the workload page counts work awaiting review and lists it', async ({ page }) => {
    await page.goto('/dashboard/workload');
    const tile = page.getByRole('link', { name: /Awaiting review/ });
    await expect(tile).toBeVisible();
    await tile.click();
    await expect(page).toHaveURL(/review=pending/);
    await expect(page.getByRole('button', { name: 'Stop showing only work awaiting review' })).toBeVisible();
  });
});
