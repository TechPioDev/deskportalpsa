import { test, expect } from '@playwright/test';

/**
 * A person is invited, follows the link, chooses a password and is told to sign in; the
 * administrator sees where the invitation stands; the organization's wording can be changed and
 * previewed. Local mode runs no identity provider and has no mail account, so the link comes back
 * to the inviter (as it does whenever mail cannot go) and the password is set against the
 * in-memory provider; the real sign-in is Keycloak's and is not driven here.
 */
test('a staff user is invited, sets a password through the link, and the page says where things stand', async ({ page }) => {
  const stamp = Date.now().toString().slice(-7);
  const email = `invitee${stamp}@example.test`;
  const created = await (await page.request.post('/api/bff/api/admin/users', {
    data: { displayName: `Ira Invitee ${stamp}`, email, roleIds: [await technicianRoleId(page)] },
  })).json() as { id: string };

  // The Users page: never signed in, not invited yet, and the row menu offers the invitation.
  await page.goto('/dashboard/users');
  await page.getByPlaceholder('Search name or email…').fill(email);
  const row = page.getByRole('row').filter({ hasText: email });
  await expect(row).toContainText('Not invited yet');
  await row.getByRole('button', { name: 'Actions' }).click();
  await page.getByRole('button', { name: 'Send invitation' }).click();
  const status = page.getByRole('status');
  await expect(status).toContainText(`The invitation for ${email} could not be e-mailed`, { timeout: 15_000 });
  const link = await status.getByLabel('Invitation link').inputValue();
  expect(link).toMatch(/\/invite\/[A-Za-z0-9_-]{40,}$/);
  await expect(row).toContainText('Invited · until');

  // The link, as the person receives it. Its host is the portal's public address (Portal__PublicUrl),
  // not this test server's, so the path is what is followed here.
  const linkPath = new URL(link).pathname;
  await page.goto(linkPath);
  await expect(page.getByRole('heading', { name: 'Welcome, Ira' })).toBeVisible();
  await expect(page.getByText(email)).toBeVisible();
  await page.getByLabel('Password', { exact: true }).fill('short');
  await page.getByLabel('The same again').fill('short');
  await expect(page.getByRole('button', { name: 'Set my password and sign in' })).toBeDisabled();
  await page.getByLabel('Password', { exact: true }).fill('a sentence I will remember');
  await page.getByLabel('The same again').fill('a sentence I will remember');
  await page.getByRole('button', { name: 'Set my password and sign in' }).click();
  const done = page.getByText('Your password is set.').locator('..');
  await expect(done).toBeVisible();
  await expect(done.getByRole('link', { name: 'Sign in' })).toBeVisible();

  // Spent: the same link says so and offers nothing to fill in.
  await page.goto(linkPath);
  await expect(page.getByRole('heading', { name: 'Already used' })).toBeVisible();
  await expect(page.getByLabel('Password', { exact: true })).toHaveCount(0);

  // And an unknown link gives nothing away.
  await page.goto('/invite/not-a-real-link-at-all');
  await expect(page.getByRole('heading', { name: 'This link is not valid' })).toBeVisible();

  // The administrator sees the invitation accepted and the person as active.
  await page.goto('/dashboard/users');
  await page.getByPlaceholder('Search name or email…').fill(email);
  await expect(page.getByRole('row').filter({ hasText: email })).toContainText('Active');
  await page.request.delete(`/api/bff/api/admin/users/${created.id}`);
});

test('the password-reset page asks for an address and answers the same whatever it is', async ({ page }) => {
  await page.goto('/login');
  await page.goto('/reset-password');
  await expect(page.getByRole('heading', { name: 'Forgot your password?' })).toBeVisible();
  await page.getByLabel('E-mail address').fill('nobody@example.test');
  await page.getByRole('button', { name: 'Send me a link' }).click();
  await expect(page.getByText('If that address has a sign-in, a link is on its way.')).toBeVisible();
  await page.goto('/reset-password/not-a-real-link');
  await expect(page.getByRole('heading', { name: 'This link is not valid' })).toBeVisible();
});

test("the organization's e-mail wording is changed, previewed with the change, refused with a wrong placeholder, and put back", async ({ page }) => {
  await page.goto('/dashboard/email-templates');
  await expect(page.getByRole('heading', { name: 'E-mail wording' })).toBeVisible();
  await page.getByRole('navigation', { name: 'Templates' }).getByRole('button', { name: /^Invitation\s+Default wording/ }).click();
  const editor = page.getByRole('region', { name: 'Invitation' });
  const subject = editor.getByLabel('Subject');
  await expect(subject).toHaveValue("You're invited to {{product}} by {{organization.name}}");

  await subject.fill('Come and join {{organization.nmae}}');
  await editor.getByRole('button', { name: 'Save' }).click();
  await expect(editor.getByRole('alert')).toContainText('{{organization.nmae}} is not a placeholder');

  await subject.fill('Come and join {{organization.name}}, {{user.first_name}}');
  await editor.getByRole('button', { name: 'Preview' }).click();
  const previewSubject = editor.locator('p', { hasText: 'Subject:' });
  await expect(previewSubject).toContainText('Come and join');
  await expect(previewSubject).toContainText(', Asha');
  await editor.getByRole('button', { name: 'Save' }).click();
  await expect(editor.getByText('Saved')).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Templates' })).toContainText('Changed by');

  page.once('dialog', (d) => void d.accept());
  await editor.getByRole('button', { name: 'Default wording' }).click();
  await expect(subject).toHaveValue("You're invited to {{product}} by {{organization.name}}");
});

async function technicianRoleId(page: import('@playwright/test').Page): Promise<string> {
  const roles = await (await page.request.get('/api/bff/api/admin/roles')).json() as { id: string; name: string }[];
  const role = roles.find((r) => /technician/i.test(r.name)) ?? roles[0];
  return role.id;
}
