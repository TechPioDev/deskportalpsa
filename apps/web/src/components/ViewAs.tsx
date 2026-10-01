'use client';

import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Eye, LogOut, Search, X } from 'lucide-react';
import { api } from '@/lib/api';

const useMe = () => useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });

/** Whether the signed-in person may view the portal as someone else: managing users AND roles. */
function useMayViewAs() {
  const { data: me } = useMe();
  return !!me && !me.viewAs && me.permissions.includes('users.manage') && me.permissions.includes('roles.manage');
}

/** Starts the view and reloads into it: every cached answer on the page belongs to the administrator. */
function useStart() {
  return useMutation({
    mutationFn: (key: string) => api.viewAsStart(key),
    onSuccess: () => window.location.assign('/dashboard'),
  });
}

/**
 * Says, on every page, that this is someone else's view and that it is read-only - with the one
 * way out. Without it an administrator could forget and wonder why nothing saves.
 */
export function ViewAsBanner() {
  const { data: me } = useMe();
  const [leaving, setLeaving] = useState(false);
  if (!me?.viewAs) return null;
  const exit = () => {
    setLeaving(true);
    // Back to the administrator's own view whatever the API answers: the proxy drops the cookie on stop.
    api.viewAsStop().catch(() => undefined).finally(() => window.location.assign('/dashboard/users'));
  };
  return (
    <div role="status" className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-amber-300 bg-amber-100 px-4 py-2 text-sm text-amber-950 dark:border-amber-800 dark:bg-amber-950 dark:text-amber-100 sm:px-6">
      <Eye size={16} aria-hidden="true" className="shrink-0" />
      <span className="min-w-0 flex-1">
        Viewing as <strong>{me.viewAs.name}</strong>
        {me.viewAs.kind === 'client' ? ' (client portal)' : ''} — read-only.
        <span className="hidden sm:inline"> You are signed in as {me.viewAs.by ?? 'an administrator'}; nothing can be changed in this view.</span>
      </span>
      <button type="button" onClick={exit} disabled={leaving}
        className="inline-flex shrink-0 items-center gap-1.5 rounded-lg bg-amber-950 px-3 py-1.5 text-xs font-semibold text-amber-50 hover:opacity-90 disabled:opacity-60 dark:bg-amber-100 dark:text-amber-950">
        <LogOut size={14} /> {leaving ? 'Leaving…' : 'Exit view'}
      </button>
    </div>
  );
}

/** "View as" for one person, from their own page. Administrators only. */
export function ViewAsButton({ personKey, name }: { personKey: string; name: string }) {
  const may = useMayViewAs();
  const start = useStart();
  if (!may) return null;
  return (
    <div className="flex flex-wrap items-center gap-2">
      <button type="button" onClick={() => start.mutate(personKey)} disabled={start.isPending}
        className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-60">
        <Eye size={15} /> {start.isPending ? 'Opening…' : `View as ${name}`}
      </button>
      <span className="text-xs text-[var(--muted)]">See the portal exactly as they do. Read-only, and recorded.</span>
      {start.isError && (
        <p role="alert" className="w-full text-xs text-red-600 dark:text-red-400">
          {start.error instanceof Error ? start.error.message : 'Could not open their view.'}
        </p>
      )}
    </div>
  );
}

/** Find anyone - staff or client portal user - and view the portal as them. Administrators only. */
export function ViewAsPicker() {
  const may = useMayViewAs();
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState('');
  const start = useStart();
  const { data: people, isLoading } = useQuery({
    queryKey: ['view-as-people', q.trim()],
    queryFn: () => api.viewAsPeople(q.trim()),
    enabled: may && open,
  });
  if (!may) return null;
  if (!open) {
    return (
      <button type="button" onClick={() => setOpen(true)}
        className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] px-3.5 py-2 text-sm font-medium hover:bg-[var(--bg)]">
        <Eye size={16} /> View as…
      </button>
    );
  }
  return (
    <div role="dialog" aria-label="View as" className="fixed inset-0 z-50 flex items-start justify-center bg-black/40 p-4 pt-[10vh]" onClick={() => setOpen(false)}>
      <div className="w-full max-w-lg rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between gap-2">
          <h2 className="text-sm font-semibold">View the portal as…</h2>
          <button type="button" aria-label="Close" onClick={() => setOpen(false)} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={16} /></button>
        </div>
        <p className="mt-1 text-xs text-[var(--muted)]">
          See exactly what a colleague or a client sees: their menu, tickets and figures. Read-only, and every view is recorded.
        </p>
        <label className="mt-3 flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 focus-within:border-brand">
          <Search size={15} className="text-[var(--muted)]" aria-hidden="true" />
          <input autoFocus value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name or email" aria-label="Find a person"
            className="min-w-0 flex-1 bg-transparent text-sm outline-none" />
        </label>
        <ul className="mt-3 max-h-80 divide-y divide-[var(--border)] overflow-y-auto">
          {isLoading && <li className="py-3 text-xs text-[var(--muted)]">Looking…</li>}
          {!isLoading && (people ?? []).length === 0 && <li className="py-3 text-xs text-[var(--muted)]">Nobody matches that.</li>}
          {(people ?? []).map((p) => (
            <li key={p.key} className="flex items-center gap-3 py-2">
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm font-medium">{p.name}</p>
                <p className="truncate text-xs text-[var(--muted)]">
                  {p.kind === 'client' ? 'Client' : 'Staff'}{p.detail ? ` · ${p.detail}` : ''} · {p.email}
                </p>
              </div>
              {p.available ? (
                <button type="button" onClick={() => start.mutate(p.key)} disabled={start.isPending}
                  className="shrink-0 rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
                  View as
                </button>
              ) : (
                <span className="shrink-0 text-xs text-[var(--muted)]">{p.reason}</span>
              )}
            </li>
          ))}
        </ul>
        {start.isError && (
          <p role="alert" className="mt-2 text-xs text-red-600 dark:text-red-400">
            {start.error instanceof Error ? start.error.message : 'Could not open their view.'}
          </p>
        )}
      </div>
    </div>
  );
}
