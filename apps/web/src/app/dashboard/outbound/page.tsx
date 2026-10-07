'use client';

import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { SyncStateChip } from '@/components/OutboundChanges';

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);
const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '—');

/**
 * Every change made in the portal that a PSA does not have: waiting to be sent, or failed.
 *
 * A reply, a status, an hour or a ticket made while a PSA could not be reached is kept and tried
 * again by itself, with a longer wait each time. This is where to see them all: what has failed
 * first, then the longest waiting. Sending one again, or letting go of it, is done on its ticket.
 */
export default function OutboundPage() {
  const list = useQuery({ queryKey: ['outbound'], queryFn: api.outboundList, refetchInterval: 15_000, staleTime: 0 });
  const rows = list.data ?? [];
  const failed = rows.filter((r) => r.state === 'Sync Failed').length;

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Unsent changes</h1>
        <p className="mt-1 max-w-3xl text-sm text-[var(--muted)]">
          Changes made here that a PSA does not have yet. One made while the PSA could not be reached is kept and tried again by itself:
          after thirty seconds, then twice as long each time, up to an hour, eight tries in all. One the PSA refuses, or that runs out of
          tries, is marked Sync Failed and kept until somebody sends it again or lets go of it. Nothing is called Synced until the PSA has confirmed it.
        </p>
      </div>

      {list.isError && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(list.error, 'The list could not be read.')}</p>}
      {list.isLoading && <p className="text-sm text-[var(--muted)]">Reading…</p>}
      {list.isSuccess && rows.length === 0 && (
        <p className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-6 text-sm text-[var(--muted)]">
          Nothing is waiting. Every change made here has reached its PSA.
        </p>
      )}

      {rows.length > 0 && (
        <section aria-label="Unsent changes" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <p className="border-b border-[var(--border)] px-4 py-2.5 text-xs text-[var(--muted)]">
            {rows.length} {rows.length === 1 ? 'change' : 'changes'} · {failed} failed · {rows.length - failed} waiting
          </p>
          <div className="overflow-x-auto">
            <table className="w-full min-w-[820px] text-left text-sm">
              <thead className="text-xs text-[var(--muted)]">
                <tr>
                  <th className="px-4 py-2 font-medium">Ticket</th>
                  <th className="px-2 py-2 font-medium">Change</th>
                  <th className="px-2 py-2 font-medium">State</th>
                  <th className="px-2 py-2 font-medium">By</th>
                  <th className="px-2 py-2 font-medium">Tried</th>
                  <th className="px-4 py-2 font-medium">Next try, or why not</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-[var(--border)]">
                {rows.map((r) => (
                  <tr key={r.id}>
                    <td className="px-4 py-2">
                      <Link href={`/dashboard/tickets/${r.ticketId}`} className="font-medium underline-offset-2 hover:underline">
                        {r.ticketReference ? `#${r.ticketReference}` : 'Not in the PSA'}
                      </Link>
                      <span className="block max-w-[22rem] truncate text-xs text-[var(--muted)]">{r.ticketTitle} · {r.connectionName}</span>
                    </td>
                    <td className="px-2 py-2">{r.summary}</td>
                    <td className="px-2 py-2"><SyncStateChip state={r.state} /></td>
                    <td className="px-2 py-2 text-[var(--muted)]">{r.requestedBy ?? '—'}<span className="block text-xs">{when(r.requestedAt)}</span></td>
                    <td className="px-2 py-2 tabular-nums text-[var(--muted)]">{r.attempts} of {r.maxAttempts}</td>
                    <td className="px-4 py-2 text-xs">
                      {r.state === 'Pending Sync' && <span className="block text-[var(--muted)]">{when(r.nextAttemptAt)}</span>}
                      {r.lastError}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </div>
  );
}
