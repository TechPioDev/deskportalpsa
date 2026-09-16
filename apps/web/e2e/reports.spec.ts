import { expect, test } from '@playwright/test';

/**
 * Scheduled reports, end to end through the browser: a schedule is created, run on demand, and the
 * PDF it produced downloads. The unit tests prove the figures; this proves the page, the API and the
 * PDF renderer still line up when a real browser drives them.
 */
test.describe('scheduled reports', () => {
  test('a schedule can be created, refuses a bad address, runs, and yields a PDF', async ({ page, request }) => {
    await page.goto('/dashboard/reports');
    await expect(page.getByRole('heading', { name: 'Scheduled reports' })).toBeVisible();

    await page.getByRole('button', { name: 'New schedule' }).click();
    const form = page.locator('form').filter({ hasText: 'How often' });
    await form.getByLabel('Name').fill('Monthly technician report');
    await form.getByLabel('How often').selectOption({ label: 'Monthly — last calendar month' });

    // A typo in the recipients is caught on save, not hours later when nobody receives the report.
    await form.getByLabel('Send to').fill('manager@techpio.test, not-an-address');
    await form.getByRole('button', { name: 'Create schedule' }).click();
    await expect(page.getByText('Not a valid email address: not-an-address.')).toBeVisible();

    await form.getByLabel('Send to').fill('manager@techpio.test');
    await form.getByRole('button', { name: 'Create schedule' }).click();

    const row = page.locator('div').filter({ hasText: /^Monthly technician report/ }).first();
    await expect(row).toContainText('Monthly, covers last calendar month');
    await expect(row).toContainText('all clients');
    await expect(row).toContainText('Next:');

    await page.getByRole('button', { name: 'Run now' }).click();
    const history = page.locator('section').filter({ hasText: 'Sent and generated' });
    await expect(history.getByText(/Technician productivity — /)).toBeVisible();
    // No mail account in a local run, so the report is kept rather than sent — and says which.
    await expect(history.getByText(/not configured|Emailed to/)).toBeVisible();

    const pdfUrl = await history.getByRole('link', { name: 'PDF' }).first().getAttribute('href');
    const pdf = await request.get(pdfUrl!);
    expect(pdf.status()).toBe(200);
    expect(pdf.headers()['content-type']).toContain('application/pdf');
    expect((await pdf.body()).subarray(0, 5).toString()).toBe('%PDF-');
  });

  test('the page says email is not set up and points at where to set it up', async ({ page }) => {
    await page.goto('/dashboard/reports');

    const notice = page.locator('p').filter({ hasText: 'Email is not set up yet' });
    await expect(notice).toBeVisible();
    // The notice itself carries the link; the sidebar's own link to the same page is not the point.
    await expect(notice.getByRole('link')).toHaveAttribute('href', '/dashboard/health');
  });
});

test.describe('email settings', () => {
  test('an administrator can add a mail account, and the password never comes back', async ({ page, request }) => {
    await page.goto('/dashboard/health');
    const card = page.locator('div').filter({ hasText: /^Email delivery/ }).first();
    await expect(card).toContainText('Not set up');

    await page.getByRole('button', { name: 'Set up email' }).click();
    const form = page.locator('form').filter({ hasText: 'Mail server (SMTP)' });
    await form.getByRole('button', { name: 'Other' }).click();
    await form.getByLabel('Mail server (SMTP)').fill('https://smtp.example.test');
    await form.getByLabel('Send from (address)').fill('reports@example.test');
    await form.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByText('Enter only the server name, without http:// or a path.')).toBeVisible();

    await form.getByLabel('Mail server (SMTP)').fill('smtp.example.test');
    await form.getByLabel('Username').fill('reports@example.test');
    await form.getByLabel('Password').fill('not-a-real-password');
    await form.getByRole('button', { name: 'Save' }).click();

    await expect(page.getByText('Scheduled reports are emailed from reports@example.test')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Send test email' })).toBeVisible();

    // The API returns the account without ever returning the password.
    const settings = await request.get('/api/bff/api/admin/email/settings');
    const body = await settings.text();
    expect(settings.status()).toBe(200);
    expect(body).toContain('"hasPassword":true');
    expect(body).not.toContain('not-a-real-password');

    // Leave the desk as it was found, so a re-run starts from "Not set up".
    await page.getByRole('button', { name: 'Edit mail settings' }).click();
    page.once('dialog', (d) => d.accept());
    await page.getByRole('button', { name: 'Remove account' }).click();
    await expect(page.getByText('Not set up')).toBeVisible();
  });
});
