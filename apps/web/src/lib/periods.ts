/**
 * Report periods a manager actually asks for — "today", "last month", "last quarter" — as well as
 * rolling windows. Calendar periods use the viewer's LOCAL calendar: "today" for a desk in India
 * starts at their midnight, not UTC's, or the first five and a half hours of work land on yesterday.
 *
 * A period that is still running (today, this month, this quarter) ends now, so its figures are
 * the running total rather than a window reaching into the future.
 */
export type PeriodKey =
  | 'today' | 'yesterday' | '7d' | '30d' | 'this-month' | 'last-month' | 'this-quarter' | 'last-quarter' | '90d';

export type Period = { key: PeriodKey; label: string };

export const PERIODS: Period[] = [
  { key: 'today', label: 'Today' },
  { key: 'yesterday', label: 'Yesterday' },
  { key: '7d', label: 'Last 7 days' },
  { key: '30d', label: 'Last 30 days' },
  { key: 'this-month', label: 'This month' },
  { key: 'last-month', label: 'Last month' },
  { key: 'this-quarter', label: 'This quarter' },
  { key: 'last-quarter', label: 'Last quarter' },
  { key: '90d', label: 'Last 90 days' },
];

const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate());
const endOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate(), 23, 59, 59, 999);

/** The inclusive bounds of a period, as instants. */
export function periodRange(key: PeriodKey, now: Date = new Date()): { from: Date; to: Date } {
  const y = now.getFullYear();
  const m = now.getMonth();
  const q = Math.floor(m / 3) * 3; // first month of this quarter
  switch (key) {
    case 'today': return { from: startOfDay(now), to: now };
    case 'yesterday': {
      const d = new Date(y, m, now.getDate() - 1);
      return { from: startOfDay(d), to: endOfDay(d) };
    }
    case '7d': return { from: new Date(now.getTime() - 7 * 86_400_000), to: now };
    case '30d': return { from: new Date(now.getTime() - 30 * 86_400_000), to: now };
    case '90d': return { from: new Date(now.getTime() - 90 * 86_400_000), to: now };
    case 'this-month': return { from: new Date(y, m, 1), to: now };
    // Day 0 of a month is the last day of the one before, so this is correct across year ends too.
    case 'last-month': return { from: new Date(y, m - 1, 1), to: endOfDay(new Date(y, m, 0)) };
    case 'this-quarter': return { from: new Date(y, q, 1), to: now };
    case 'last-quarter': return { from: new Date(y, q - 3, 1), to: endOfDay(new Date(y, q, 0)) };
  }
}

/** "1 Aug 2026 – 31 Aug 2026", or a single date when the period is one day. */
export function describeRange(from: Date, to: Date): string {
  const f = (d: Date) => d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
  return f(from) === f(to) ? f(from) : `${f(from)} – ${f(to)}`;
}
