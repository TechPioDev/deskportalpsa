'use client';

import { useMutation, useQueryClient } from '@tanstack/react-query';
import { CloudOff, RotateCw } from 'lucide-react';
import { api } from '@/lib/api';
import type { TicketOutbound } from '@/lib/types';

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

/** The chip for where a change stands. Three states only; "Synced" is never shown for something the PSA has not confirmed. */
export function SyncStateChip({ state }: { state: string }) {
  const failed = state === 'Sync Failed';
  return (
    <span className={'whitespace-nowrap rounded px-1.5 py-0.5 text-[11px] font-medium '
      + (failed ? 'bg-rose-100 text-rose-800 dark:bg-rose-950 dark:text-rose-200' : 'bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-200')}>
      {state}
    </span>
  );
}

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : null);

/**
 * What was changed on a ticket here and has not reached the PSA: waiting to be sent, or failed.
 *
 * A change made while the PSA could not be reached is kept and tried again by itself. This says
 * so on the ticket, with why and when it is next tried, so that nobody takes a reply that is
 * still waiting for one the customer has. A failed one stays here until somebody sends it again
 * or lets go of it; neither happens by itself.
 */
export function OutboundChanges({ ticketId, changes, canChange }: { ticketId: string; changes: TicketOutbound[]; canChange: boolean }) {
  const qc = useQueryClient();
  const refresh = () => [['ticket', ticketId], ['outbound']].forEach((k) => qc.invalidateQueries({ queryKey: k }));
  const retry = useMutation({ mutationFn: (id: string) => api.retryOutbound(ticketId, id), onSuccess: refresh });
  const letGo = useMutation({ mutationFn: (id: string) => api.discardOutbound(ticketId, id), onSuccess: refresh });
  if (changes.length === 0) return null;
  const failed = changes.filter((c) => c.state === 'Sync Failed').length;

  return (
    <section aria-label="Not in the PSA yet" className="rounded-xl border border-amber-300 bg-amber-50/70 px-4 py-3 dark:border-amber-900 dark:bg-amber-950/30">
      <h2 className="flex items-center gap-2 text-sm font-semibold">
        <CloudOff size={15} aria-hidden="true" />
        {changes.length === 1 ? '1 change is' : `${changes.length} changes are`} not in the PSA yet
      </h2>
      <p className="mt-0.5 text-xs text-[var(--muted)]">
        {failed === changes.length
          ? 'The PSA did not take these. They are kept here until somebody sends them again or lets go of them.'
          : 'Made while the PSA could not be reached. Each is kept and tried again by itself; nothing here is in the PSA until it reads Synced.'}
      </p>
      <ul className="mt-2 divide-y divide-amber-200 dark:divide-amber-900">
        {changes.map((c) => (
          <li key={c.id} className="flex flex-wrap items-start gap-x-3 gap-y-1 py-2 text-sm">
            <span className="min-w-0 flex-1">
              <span className="font-medium">{c.summary}</span>{' '}
              <SyncStateChip state={c.state} />
              <span className="block text-xs text-[var(--muted)]">
                {c.requestedBy ? `${c.requestedBy} · ` : ''}{when(c.requestedAt)}
                {' · '}tried {c.attempts} of {c.maxAttempts}
                {c.state === 'Pending Sync' && c.nextAttemptAt && ` · next try ${when(c.nextAttemptAt)}`}
              </span>
              {c.lastError && <span className="block text-xs text-[var(--fg)]">{c.lastError}</span>}
            </span>
            {canChange && (
              <span className="flex gap-2">
                <button type="button" disabled={retry.isPending || letGo.isPending} onClick={() => retry.mutate(c.id)}
                  aria-label={`Send again: ${c.summary}`}
                  className="inline-flex items-center gap-1 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2.5 py-1 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50">
                  <RotateCw size={12} aria-hidden="true" /> {c.state === 'Sync Failed' ? 'Send again' : 'Try now'}
                </button>
                <button type="button" disabled={retry.isPending || letGo.isPending}
                  onClick={() => { if (window.confirm(`Let go of "${c.summary}"? It will not be sent to the PSA.`)) letGo.mutate(c.id); }}
                  aria-label={`Let go of: ${c.summary}`}
                  className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2.5 py-1 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50">
                  Let go
                </button>
              </span>
            )}
          </li>
        ))}
      </ul>
      {(retry.isError || letGo.isError) && (
        <p role="alert" className="mt-1 text-xs text-rose-600 dark:text-rose-400">{message(retry.error ?? letGo.error, 'That could not be done.')}</p>
      )}
    </section>
  );
}
