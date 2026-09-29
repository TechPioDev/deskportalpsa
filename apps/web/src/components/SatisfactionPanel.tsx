'use client';

import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Star } from 'lucide-react';
import { api } from '@/lib/api';

export const RATING_LABELS = ['Very poor', 'Poor', 'Okay', 'Good', 'Excellent'];

/**
 * The one question a client is asked when their ticket is done: how did we do? Shown only once the
 * ticket is resolved — asking earlier rates the waiting, not the work — and changeable until the
 * window closes, so a quick "2" in frustration can become a considered "4" the next morning.
 */
export function SatisfactionPanel({ ticketId }: { ticketId: string }) {
  const qc = useQueryClient();
  const { data: state } = useQuery({
    queryKey: ['satisfaction', ticketId], queryFn: () => api.satisfactionState(ticketId), retry: false,
  });
  const [rating, setRating] = useState<number | null>(null);
  const [comment, setComment] = useState('');
  const [editing, setEditing] = useState(false);
  useEffect(() => {
    if (state) { setRating(state.rating); setComment(state.comment ?? ''); }
  }, [state]);

  const save = useMutation({
    mutationFn: () => api.rateTicket(ticketId, rating!, comment.trim() || null),
    onSuccess: (next) => { qc.setQueryData(['satisfaction', ticketId], next); setEditing(false); },
  });

  // Nothing to ask while the ticket is still open, and nothing to show if it was never answered.
  if (!state || (!state.canRate && state.rating === null)) return null;

  const until = state.openUntil
    ? new Date(state.openUntil).toLocaleDateString(undefined, { day: 'numeric', month: 'long' })
    : null;
  const answered = state.rating !== null && !editing;

  return (
    <section aria-labelledby="csat-heading" className="rounded-xl border border-brand/30 bg-brand-tint p-5 dark:bg-brand/10">
      <h2 id="csat-heading" className="text-sm font-semibold">How did we do?</h2>
      {answered ? (
        <div className="mt-2 space-y-1 text-sm">
          <p>
            Thank you — you rated this <strong>{state.rating}/5</strong> ({RATING_LABELS[state.rating! - 1]}).
            {state.comment && <span className="text-[var(--muted)]"> &ldquo;{state.comment}&rdquo;</span>}
          </p>
          {state.canRate && (
            <p className="text-xs text-[var(--muted)]">
              You can change it until {until}.{' '}
              <button type="button" onClick={() => setEditing(true)} className="font-medium text-brand hover:underline">Change my rating</button>
            </p>
          )}
        </div>
      ) : (
        <form className="mt-2 space-y-3" onSubmit={(e) => { e.preventDefault(); if (rating) save.mutate(); }}>
          <p className="text-sm text-[var(--muted)]">
            This ticket is resolved. One click tells the team how it went{until ? ` — you can change it until ${until}` : ''}.
          </p>
          <div role="radiogroup" aria-label="Your rating" className="flex flex-wrap gap-1.5">
            {RATING_LABELS.map((label, i) => {
              const value = i + 1;
              const on = rating !== null && value <= rating;
              return (
                <button key={label} type="button" role="radio" aria-checked={rating === value} aria-label={`${value} — ${label}`}
                  onClick={() => setRating(value)}
                  className={`flex flex-col items-center gap-0.5 rounded-lg border px-2.5 py-1.5 text-[11px] ${rating === value
                    ? 'border-brand bg-[var(--surface)] font-medium' : 'border-transparent text-[var(--muted)] hover:border-[var(--border)]'}`}>
                  <Star size={20} className={on ? 'fill-amber-400 text-amber-500' : 'text-[var(--faint)]'} aria-hidden="true" />
                  {label}
                </button>
              );
            })}
          </div>
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
            Anything you want to add? (optional)
            <textarea rows={2} maxLength={1000} value={comment} onChange={(e) => setComment(e.target.value)}
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-normal text-[var(--fg)] outline-none focus:border-brand" />
          </label>
          {save.isError && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(save.error as Error).message}</p>}
          <div className="flex gap-2">
            <button type="submit" disabled={!rating || save.isPending}
              className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
              {save.isPending ? 'Sending…' : 'Send rating'}
            </button>
            {editing && (
              <button type="button" onClick={() => { setEditing(false); setRating(state.rating); setComment(state.comment ?? ''); }}
                className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--surface)]">Cancel</button>
            )}
          </div>
        </form>
      )}
    </section>
  );
}
