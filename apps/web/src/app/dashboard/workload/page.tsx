'use client';

import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { AlarmClock, Inbox, Snowflake } from 'lucide-react';
import { api } from '@/lib/api';

/** "3 days", "5 weeks": how long the oldest open ticket has waited. */
function age(iso: string | null): string {
  if (!iso) return '—';
  const days = Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000);
  if (days < 1) return 'today';
  if (days < 14) return `${days} day${days === 1 ? '' : 's'}`;
  return `${Math.floor(days / 7)} weeks`;
}

/**
 * The open work, by who holds it, for a lead deciding where the next ticket goes: who is carrying
 * the most, who is running late, what nobody has picked up, and what has gone quiet. Counted from the
 * tickets the reader can see, from every source. A count, not a judgement: open tickets differ.
 */
export default function WorkloadPage() {
  const { data, isLoading } = useQuery({ queryKey: ['tickets', 'workload'], queryFn: api.teamWorkload });
  const most = Math.max(1, ...(data?.people.map((p) => p.open) ?? [1]));

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Team workload</h1>
        <p className="text-sm text-[var(--muted)]">Open work by who holds it, across Autotask, ConnectWise, team boards and monitoring.</p>
      </div>

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        <Link href="/dashboard/tickets?unassigned=1&view=open" className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 hover:border-brand/40">
          <Inbox size={16} className="text-brand" aria-hidden="true" />
          <p className="mt-2 text-2xl font-semibold tabular-nums">{data?.unassigned ?? '–'}</p>
          <p className="text-xs text-[var(--muted)]">Nobody holds</p>
        </Link>
        <Link href="/dashboard/tickets?unassigned=1&overdue=1" className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 hover:border-brand/40">
          <AlarmClock size={16} className="text-red-600 dark:text-red-400" aria-hidden="true" />
          <p className="mt-2 text-2xl font-semibold tabular-nums">{data?.unassignedOverdue ?? '–'}</p>
          <p className="text-xs text-[var(--muted)]">Nobody holds, and overdue</p>
        </Link>
        <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
          <Snowflake size={16} className="text-sky-600 dark:text-sky-400" aria-hidden="true" />
          <p className="mt-2 text-2xl font-semibold tabular-nums">{data?.stale ?? '–'}</p>
          <p className="text-xs text-[var(--muted)]">No word for {data?.staleDays ?? 7} days or more</p>
        </div>
      </div>

      <section aria-labelledby="people-heading" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <h2 id="people-heading" className="border-b border-[var(--border)] px-5 py-3 text-sm font-semibold">By person</h2>
        {isLoading && <div className="m-4 h-24 animate-pulse rounded-lg bg-[var(--bg)]" />}
        {data && data.people.length === 0 && <p className="px-5 py-8 text-center text-sm text-[var(--muted)]">Nobody holds any open work.</p>}
        {data && data.people.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
                <tr className="border-b border-[var(--border)]">
                  <th className="px-5 py-2.5 font-medium">Person</th>
                  <th className="px-2 py-2.5 font-medium">Open</th>
                  <th className="px-2 py-2.5 text-right font-medium">Overdue</th>
                  <th className="px-2 py-2.5 text-right font-medium">High priority</th>
                  <th className="px-2 py-2.5 text-right font-medium">Gone quiet</th>
                  <th className="px-5 py-2.5 font-medium">Oldest waiting</th>
                </tr>
              </thead>
              <tbody>
                {data.people.map((p) => (
                  <tr key={p.key} className="border-b border-[var(--border)] last:border-0 hover:bg-[var(--bg)]">
                    <td className="px-5 py-3">
                      <Link href={`/dashboard/tickets?tech=${encodeURIComponent(p.key)}&view=open`} className="font-medium hover:underline">{p.name}</Link>
                    </td>
                    <td className="px-2 py-3">
                      <span className="flex items-center gap-2">
                        <span className="w-8 tabular-nums">{p.open}</span>
                        <span className="h-1.5 w-32 overflow-hidden rounded-full bg-[var(--bg)]" aria-hidden="true">
                          <span className="block h-full rounded-full bg-brand" style={{ width: `${(p.open / most) * 100}%` }} />
                        </span>
                      </span>
                    </td>
                    <td className={`px-2 py-3 text-right tabular-nums ${p.overdue ? 'font-semibold text-red-600 dark:text-red-400' : 'text-[var(--muted)]'}`}>{p.overdue}</td>
                    <td className="px-2 py-3 text-right tabular-nums text-[var(--muted)]">{p.highPriority}</td>
                    <td className="px-2 py-3 text-right tabular-nums text-[var(--muted)]">{p.stale}</td>
                    <td className="px-5 py-3 text-xs text-[var(--muted)]">{age(p.oldestRaisedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
