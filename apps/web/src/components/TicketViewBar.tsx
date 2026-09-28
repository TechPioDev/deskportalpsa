'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Bookmark, BookmarkPlus, Trash2, Users } from 'lucide-react';
import { api } from '@/lib/api';
import type { SavedViewFilters } from '@/lib/types';

/**
 * The row of views above a ticket list: the handful every desk needs, then the ones this desk
 * invented for itself.
 *
 * The built-in views are code rather than seeded rows. They mean the same thing on every desk,
 * nobody should be able to delete "Unassigned", and a seeded row would drift per tenant the first
 * time somebody edited it. Saved views are the opposite case entirely: they only exist because
 * somebody's own work has a shape nobody else's does.
 */

export const EMPTY_FILTERS: SavedViewFilters = {
  search: null, status: null, priority: null, company: null, queue: null, connectionName: null,
  personKey: null, departmentId: null, teamId: null, openness: null,
  mineOnly: false, followingOnly: false, unassignedOnly: false, overdueOnly: false, raisedWithinDays: null,
};

/** The views every desk has. Order is the order a day is worked in. */
const BUILT_IN: { key: string; label: string; title: string; filters: Partial<SavedViewFilters> }[] = [
  { key: 'all', label: 'All', title: 'Every ticket you can see, open or finished', filters: {} },
  { key: 'open', label: 'Open', title: 'Everything still to do', filters: { openness: 'open' } },
  { key: 'mine', label: 'Mine', title: 'Yours, and anything sitting with a team you are in', filters: { mineOnly: true, openness: 'open' } },
  { key: 'unassigned', label: 'Unassigned', title: 'Open tickets nobody holds', filters: { unassignedOnly: true, openness: 'open' } },
  { key: 'overdue', label: 'Overdue', title: 'Past its due date and still open', filters: { overdueOnly: true } },
  { key: 'following', label: 'Following', title: 'Tickets you are watching without holding', filters: { followingOnly: true } },
  { key: 'closed', label: 'Closed', title: 'Resolved and closed tickets', filters: { openness: 'resolved' } },
];

/** Whether the current filters are exactly this view and nothing else. */
function matches(current: SavedViewFilters, view: Partial<SavedViewFilters>) {
  const wanted = { ...EMPTY_FILTERS, ...view };
  return (Object.keys(EMPTY_FILTERS) as (keyof SavedViewFilters)[]).every((k) => {
    const a = current[k] ?? null;
    const b = wanted[k] ?? null;
    return (a === '' ? null : a) === (b === '' ? null : b);
  });
}

export function TicketViewBar({ boardId, filters, onApply, canSave = true }: {
  boardId?: string;
  filters: SavedViewFilters;
  onApply: (filters: SavedViewFilters) => void;
  /** False where saving would have nowhere to be offered again, e.g. a client's own list. */
  canSave?: boolean;
}) {
  const qc = useQueryClient();
  const [naming, setNaming] = useState(false);
  const [name, setName] = useState('');
  const [shared, setShared] = useState(false);

  const { data: saved } = useQuery({
    queryKey: ['ticket-views', boardId ?? null],
    queryFn: () => api.ticketViews(boardId),
    retry: false,
    enabled: canSave,
  });

  const save = useMutation({
    mutationFn: () => api.saveTicketView({ name, shared, boardId, filters }),
    onSuccess: () => {
      setNaming(false); setName(''); setShared(false);
      qc.invalidateQueries({ queryKey: ['ticket-views'] });
    },
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.deleteTicketView(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['ticket-views'] }),
  });

  // Nothing to save when nothing is narrowed: a view called "everything" is the list itself.
  const narrows = (Object.keys(EMPTY_FILTERS) as (keyof SavedViewFilters)[])
    .some((k) => (filters[k] ?? null) !== (EMPTY_FILTERS[k] ?? null));
  const onABuiltIn = BUILT_IN.some((v) => matches(filters, v.filters));

  const chip = (active: boolean) =>
    `inline-flex items-center gap-1.5 rounded-lg border px-2.5 py-1.5 text-xs font-medium transition ${
      active
        ? 'border-brand bg-brand text-brand-fg'
        : 'border-[var(--border)] text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]'}`;

  return (
    <div className="flex flex-wrap items-center gap-2">
      {BUILT_IN.map((v) => (
        <button key={v.key} title={v.title} onClick={() => onApply({ ...EMPTY_FILTERS, ...v.filters })}
          className={chip(matches(filters, v.filters))}>
          {v.label}
        </button>
      ))}

      {(saved ?? []).length > 0 && <span className="mx-1 h-5 w-px bg-[var(--border)]" />}

      {(saved ?? []).map((v) => (
        <span key={v.id} className={`${chip(matches(filters, v.filters))} pr-1.5`}>
          <button onClick={() => onApply({ ...EMPTY_FILTERS, ...v.filters })} className="inline-flex items-center gap-1.5"
            title={v.isMine ? 'Your saved view' : `Shared by ${v.ownerName ?? 'a colleague'}`}>
            {v.shared ? <Users size={12} /> : <Bookmark size={12} />}
            {v.name}
          </button>
          {/* Only the owner is offered the bin. A shared view belongs to whoever made it: deleting
              it under everybody else who uses it is not a thing a colleague should be able to do. */}
          {v.isMine && (
            <button onClick={() => remove.mutate(v.id)} title={`Delete “${v.name}”`}
              className="ml-1 rounded p-0.5 opacity-60 hover:bg-[var(--bg)] hover:opacity-100">
              <Trash2 size={11} />
            </button>
          )}
        </span>
      ))}

      {canSave && narrows && !onABuiltIn && !naming && (
        <button onClick={() => setNaming(true)} className={chip(false)}>
          <BookmarkPlus size={12} /> Save this view
        </button>
      )}

      {naming && (
        <form className="flex flex-wrap items-center gap-2"
          onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
          <input autoFocus required maxLength={60} value={name} onChange={(e) => setName(e.target.value)}
            placeholder="Name this view" aria-label="Name this view"
            className="w-44 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2.5 py-1.5 text-xs outline-none focus:border-brand" />
          <label className="inline-flex items-center gap-1.5 text-xs text-[var(--muted)]">
            <input type="checkbox" checked={shared} onChange={(e) => setShared(e.target.checked)} /> Share with the team
          </label>
          <button type="submit" disabled={save.isPending}
            className="rounded-lg bg-brand px-2.5 py-1.5 text-xs font-medium text-brand-fg disabled:opacity-60">
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
          <button type="button" onClick={() => { setNaming(false); setName(''); }}
            className="rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)]">
            Cancel
          </button>
          {save.isError && (
            <span role="alert" className="text-xs text-red-600 dark:text-red-400">{(save.error as Error).message}</span>
          )}
        </form>
      )}
    </div>
  );
}
