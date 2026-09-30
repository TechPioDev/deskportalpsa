'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link2, X } from 'lucide-react';
import { api } from '@/lib/api';
import type { TicketDetail } from '@/lib/types';

/**
 * Where an opt-in review stands. Waiting: a board lead approves it (it closes) or sends it back with a
 * note (it returns to work). Nobody reviews their own work, so the holder sees the state, not the buttons.
 */
export function ReviewBanner({ ticket, canReview }: { ticket: TicketDetail; canReview: boolean }) {
  const qc = useQueryClient();
  const d = ticket.boardDetails;
  const [note, setNote] = useState('');
  const review = useMutation({
    mutationFn: (approve: boolean) => api.reviewTicket(ticket.id, { approve, note: note.trim() || null }),
    onSuccess: () => {
      setNote('');
      for (const key of [['ticket', ticket.id], ['ticket-history', ticket.id], ['tickets']])
        qc.invalidateQueries({ queryKey: key });
    },
  });
  if (!d || (!d.requireReview && d.reviewState === 0)) return null;

  if (d.reviewState === 2) {
    const sendBacks = d.reviewSendBacks > 0 ? `, after ${d.reviewSendBacks} send-back${d.reviewSendBacks === 1 ? '' : 's'}` : '';
    return (
      <p className="mt-3 rounded-lg bg-emerald-50 px-3 py-2 text-xs text-emerald-800 dark:bg-emerald-950/40 dark:text-emerald-300">
        Approved in review{d.reviewedBy ? ` by ${d.reviewedBy}` : ''}
        {d.reviewedAt ? ` on ${new Date(d.reviewedAt).toLocaleDateString()}` : ''}{sendBacks}.
      </p>
    );
  }
  if (d.reviewState !== 1) {
    return (
      <p className="mt-3 text-xs text-[var(--muted)]">
        A board lead reviews this work before it closes
        {d.reviewSendBacks > 0 ? ` (sent back ${d.reviewSendBacks === 1 ? 'once' : `${d.reviewSendBacks} times`} so far)` : ''}.
      </p>
    );
  }
  return (
    <section aria-label="Review" className="mt-3 space-y-2 rounded-lg border border-amber-300 bg-amber-50 p-3 dark:border-amber-800 dark:bg-amber-950/30">
      <p className="text-sm font-medium text-amber-900 dark:text-amber-200">Waiting for review</p>
      <p className="text-xs text-amber-900/80 dark:text-amber-200/80">
        {canReview
          ? 'Approve it and it closes, or send it back with a note and it returns to work.'
          : 'A board lead approves it or sends it back. Nobody reviews their own work.'}
      </p>
      {canReview && (
        <>
          <textarea rows={2} maxLength={2000} value={note} onChange={(e) => setNote(e.target.value)} aria-label="Review note"
            placeholder="What needs doing, if you send it back (optional when approving)"
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm outline-none focus:border-brand" />
          {review.isError && (
            <p role="alert" className="text-xs text-red-600 dark:text-red-400">
              {review.error instanceof Error ? review.error.message : 'The review could not be saved.'}
            </p>
          )}
          <div className="flex flex-wrap justify-end gap-2">
            <button type="button" disabled={review.isPending} onClick={() => review.mutate(false)}
              className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-60">
              Send back
            </button>
            <button type="button" disabled={review.isPending} onClick={() => review.mutate(true)}
              className="rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
              Approve and close
            </button>
          </div>
        </>
      )}
    </section>
  );
}

/** How a new link reads from this ticket, and which way round it is stored. */
const LINK_KINDS: { label: string; kind: number; reverse: boolean }[] = [
  { label: 'Related to', kind: 0, reverse: false },
  { label: 'Duplicate of', kind: 1, reverse: false },
  { label: 'Parent of', kind: 2, reverse: false },
  { label: 'Part of', kind: 2, reverse: true },
  { label: 'Blocks', kind: 3, reverse: false },
  { label: 'Blocked by', kind: 3, reverse: true },
];

/**
 * Tickets tied to this one: an investigation and the PSA ticket it came from, a job and its parts, a
 * request raised twice. Only tickets the reader can see are listed or offered. A link changes no count.
 */
export function RelatedTicketsPanel({ ticketId, canUpdate }: { ticketId: string; canUpdate: boolean }) {
  const qc = useQueryClient();
  const { data: links } = useQuery({ queryKey: ['ticket-links', ticketId], queryFn: () => api.ticketLinks(ticketId), retry: false });
  const [adding, setAdding] = useState(false);
  const [kindIndex, setKindIndex] = useState(0);
  const [q, setQ] = useState('');
  const term = q.trim();
  const { data: found } = useQuery({
    queryKey: ['link-search', term],
    queryFn: () => api.searchTickets({ q: term, take: 8 }),
    enabled: adding && term.length >= 2,
  });
  // Both ends: the other ticket's panel and history change too.
  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['ticket-links'] });
    qc.invalidateQueries({ queryKey: ['ticket-history'] });
  };
  const add = useMutation({
    mutationFn: (otherId: string) => {
      const k = LINK_KINDS[kindIndex];
      return k.reverse ? api.addTicketLink(otherId, ticketId, k.kind) : api.addTicketLink(ticketId, otherId, k.kind);
    },
    onSuccess: () => { setAdding(false); setQ(''); refresh(); },
  });
  const remove = useMutation({ mutationFn: (linkId: string) => api.removeTicketLink(ticketId, linkId), onSuccess: refresh });
  const rows = links ?? [];
  const candidates = (found?.items ?? []).filter((t) => t.id !== ticketId && !rows.some((l) => l.otherTicketId === t.id));

  return (
    <section aria-labelledby="links-heading" className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
      <div className="flex items-center justify-between gap-2">
        <h2 id="links-heading" className="flex items-center gap-1.5 text-sm font-semibold"><Link2 size={14} /> Related tickets</h2>
        {canUpdate && !adding && (
          <button type="button" onClick={() => setAdding(true)} className="text-xs font-medium text-brand hover:underline">Link a ticket</button>
        )}
      </div>
      {rows.length === 0 && !adding && <p className="mt-2 text-xs text-[var(--muted)]">None linked.</p>}
      {rows.length > 0 && (
        <ul className="mt-2 space-y-1.5">
          {rows.map((l) => (
            <li key={l.id} className="flex items-start gap-2 text-xs">
              <span className="shrink-0 text-[var(--muted)]">{l.relation}</span>
              <a href={`/dashboard/tickets/${l.otherTicketId}`} className="min-w-0 flex-1 truncate font-medium hover:underline">
                {l.otherReference ? `${l.otherReference} · ` : ''}{l.otherTitle}
              </a>
              <span className="shrink-0 text-[var(--muted)]">{l.otherStatus.replace(/_/g, ' ').toLowerCase()}</span>
              {canUpdate && (
                <button type="button" aria-label={`Remove link to ${l.otherReference ?? l.otherTitle}`} disabled={remove.isPending}
                  onClick={() => remove.mutate(l.id)} className="shrink-0 text-[var(--muted)] hover:text-red-600">
                  <X size={13} />
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {remove.isError && (
        <p role="alert" className="mt-2 text-xs text-red-600 dark:text-red-400">
          {remove.error instanceof Error ? remove.error.message : 'The link could not be removed.'}
        </p>
      )}
      {adding && (
        <div className="mt-3 space-y-2">
          <div className="flex gap-2">
            <select value={kindIndex} onChange={(e) => setKindIndex(Number(e.target.value))} aria-label="How it relates"
              className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-xs outline-none focus:border-brand">
              {LINK_KINDS.map((k, i) => <option key={k.label} value={i}>{k.label}</option>)}
            </select>
            <input autoFocus value={q} onChange={(e) => setQ(e.target.value)} placeholder="Find by number or title" aria-label="Find a ticket to link"
              className="min-w-0 flex-1 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-xs outline-none focus:border-brand" />
          </div>
          {term.length >= 2 && found && candidates.length === 0 && (
            <p className="text-xs text-[var(--muted)]">No other ticket you can see matches that.</p>
          )}
          {candidates.length > 0 && (
            <ul className="max-h-48 divide-y divide-[var(--border)] overflow-y-auto rounded-lg border border-[var(--border)]">
              {candidates.map((t) => (
                <li key={t.id}>
                  <button type="button" disabled={add.isPending} onClick={() => add.mutate(t.id)}
                    className="w-full truncate px-2 py-1.5 text-left text-xs hover:bg-[var(--bg)] disabled:opacity-60">
                    <span className="font-mono text-[var(--muted)]">{t.number ?? (t.externalTicketId ? `#${t.externalTicketId}` : '')}</span> {t.title}
                  </button>
                </li>
              ))}
            </ul>
          )}
          {add.isError && (
            <p role="alert" className="text-xs text-red-600 dark:text-red-400">
              {add.error instanceof Error ? add.error.message : 'The link could not be added.'}
            </p>
          )}
          <button type="button" onClick={() => { setAdding(false); setQ(''); add.reset(); }} className="text-xs text-[var(--muted)] hover:underline">
            Cancel
          </button>
        </div>
      )}
    </section>
  );
}
