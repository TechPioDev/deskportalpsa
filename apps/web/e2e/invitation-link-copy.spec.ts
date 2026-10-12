import { test, expect, type Page } from '@playwright/test';

/**
 * The Copy button beside an invitation link says what it did.
 *
 * It used to call the clipboard and say nothing: pressed, nothing on the page changed, and the
 * owner reported "copy not working" on 12 October 2026. Whether the link had been copied could
 * not be told from the screen. And where a browser does not offer the clipboard to the page, the
 * button did nothing at all. The clipboard itself belongs to the browser and cannot be read back
 * in every browser the suite runs in, so it is replaced here by a stand-in that records what it
 * was given, or refuses.
 */

type Clipboard = 'takes it' | 'refuses, the older way works' | 'refuses, and so does the older way';

async function inviteSomeone(page: Page, clipboard: Clipboard): Promise<{ id: string; email: string; link: string }> {
  await page.addInitScript((mode) => {
    const w = window as unknown as { __copied: string[]; __olderWay: number };
    w.__copied = [];
    w.__olderWay = 0;
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: {
        writeText: (text: string) => {
          if (mode === 'takes it') { w.__copied.push(text); return Promise.resolve(); }
          return Promise.reject(new DOMException('Write permission denied.', 'NotAllowedError'));
        },
      },
    });
    document.execCommand = ((command: string) => {
      if (command !== 'copy') return false;
      w.__olderWay++;
      if (mode === 'refuses, and so does the older way') return false;
      w.__copied.push(String(window.getSelection() ?? '') || (document.activeElement as HTMLInputElement | null)?.value || '');
      return true;
    }) as typeof document.execCommand;
  }, clipboard);

  const stamp = `${Date.now().toString().slice(-7)}${Math.floor(Math.random() * 90 + 10)}`;
  const email = `copy${stamp}@example.test`;
  const roles = await (await page.request.get('/api/bff/api/admin/roles')).json() as { id: string; name: string }[];
  const role = roles.find((r) => /technician/i.test(r.name)) ?? roles[0];
  const created = await (await page.request.post('/api/bff/api/admin/users', {
    data: { displayName: `Cora Copy ${stamp}`, email, roleIds: [role.id] },
  })).json() as { id: string };

  await page.goto('/dashboard/users');
  await page.getByPlaceholder('Search name or email…').fill(email);
  const row = page.getByRole('row').filter({ hasText: email });
  await expect(row).toHaveCount(1);
  await row.getByRole('button', { name: 'Actions' }).click();
  await page.getByRole('menu', { name: 'Actions for this person' }).getByRole('button', { name: 'Send invitation' }).click();
  const field = page.getByLabel('Invitation link');
  await expect(field).toBeVisible({ timeout: 15_000 });
  return { id: created.id, email, link: await field.inputValue() };
}

const copied = (page: Page) => page.evaluate(() => (window as unknown as { __copied: string[] }).__copied);

test('Copy puts the invitation link on the clipboard and says so', async ({ page }) => {
  const who = await inviteSomeone(page, 'takes it');
  await expect(page.getByRole('button', { name: 'Copy', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Copy', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Copied' })).toBeVisible();
  await expect(page.getByText('The link is copied. Paste it where you want to send it.')).toBeVisible();
  expect(await copied(page)).toEqual([who.link]);
  await page.request.delete(`/api/bff/api/admin/users/${who.id}`);
});

test('where the clipboard is refused, the older way copies the link and the button still says so', async ({ page }) => {
  const who = await inviteSomeone(page, 'refuses, the older way works');
  await page.getByRole('button', { name: 'Copy', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Copied' })).toBeVisible();
  expect(await copied(page)).toEqual([who.link]);
  await page.request.delete(`/api/bff/api/admin/users/${who.id}`);
});

test('where nothing can copy, the link is left selected and the person is told to press Ctrl+C', async ({ page }) => {
  const who = await inviteSomeone(page, 'refuses, and so does the older way');
  await page.getByRole('button', { name: 'Copy', exact: true }).click();
  await expect(page.getByText('Your browser did not allow the copy. The link is selected: press Ctrl+C.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Copied' })).toHaveCount(0);
  const field = page.getByLabel('Invitation link');
  await expect(field).toBeFocused();
  const selection = await field.evaluate((el: HTMLInputElement) => [el.selectionStart, el.selectionEnd, el.value.length]);
  expect(selection).toEqual([0, who.link.length, who.link.length]);
  expect(await copied(page)).toEqual([]);
  await page.request.delete(`/api/bff/api/admin/users/${who.id}`);
});
