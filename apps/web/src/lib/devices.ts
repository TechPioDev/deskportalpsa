/**
 * How a device's warranty reads, one rule for every page that shows it: ended, ending within
 * {@link WARRANTY_SOON_DAYS} days (worth planning a renewal or a replacement), or fine.
 */
export const WARRANTY_SOON_DAYS = 60;

export type WarrantyState = { text: string; tone: 'ended' | 'soon' | 'ok' };

export function warrantyState(expiresAt: string | null, now: Date = new Date()): WarrantyState | null {
  if (!expiresAt) return null;
  const end = new Date(expiresAt);
  const date = end.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
  const days = Math.ceil((end.getTime() - now.getTime()) / 86_400_000);
  if (days < 0) return { text: `Warranty ended ${date}`, tone: 'ended' };
  if (days <= WARRANTY_SOON_DAYS) return { text: `Warranty ends ${date} (${days} day${days === 1 ? '' : 's'})`, tone: 'soon' };
  return { text: `Warranty until ${date}`, tone: 'ok' };
}

export const WARRANTY_TONE: Record<WarrantyState['tone'], string> = {
  ended: 'text-red-700 dark:text-red-400',
  soon: 'text-amber-700 dark:text-amber-400',
  ok: 'text-[var(--muted)]',
};
