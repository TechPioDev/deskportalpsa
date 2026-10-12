'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Building2, Check, Pencil, Plus, X } from 'lucide-react';
import { api, type LocalClient } from '@/lib/api';

/**
 * The clients the desk added by hand: in no PSA, kept so that the team's own tickets can say who
 * the work was for. Shown to the people who look after the boards. A client here can be renamed,
 * retired (no new ticket can name it; the ones that do keep its name) and brought back. It cannot
 * be deleted while tickets name it, and there is no need to: retiring takes it out of every list.
 */
export function LocalClientsPanel() {
  const qc = useQueryClient();
  const { data: clients, isLoading } = useQuery({ queryKey: ['local-clients'], queryFn: api.localClients, retry: false });
  const [adding, setAdding] = useState('');
  const [editing, setEditing] = useState<{ id: string; name: string } | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['local-clients'] });
    qc.invalidateQueries({ queryKey: ['board-clients'] });
    qc.invalidateQueries({ queryKey: ['report-clients'] });
  };
  const add = useMutation({
    mutationFn: (name: string) => api.addLocalClient(name),
    onSuccess: () => { setAdding(''); setError(null); refresh(); },
    onError: (e) => setError((e as Error).message),
  });
  const save = useMutation({
    mutationFn: (v: { id: string; name: string; isActive: boolean }) => api.updateLocalClient(v.id, v.name, v.isActive),
    onSuccess: () => { setEditing(null); setError(null); refresh(); },
    onError: (e) => setError((e as Error).message),
  });

  const rows = clients ?? [];
  return (
    <section aria-labelledby="local-clients-heading" className="space-y-2">
      <div>
        <h2 id="local-clients-heading" className="flex items-center gap-2 text-sm font-semibold"><Building2 size={15} aria-hidden="true" /> Clients not in a PSA</h2>
        <p className="text-xs text-[var(--muted)]">
          Clients you work for that are not set up in your PSA. Add one here and a ticket on any board can name it.
          It only records who the work was for: the client is never shown the ticket and is never told about it.
        </p>
      </div>

      <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        {isLoading ? (
          <p className="px-4 py-3 text-sm text-[var(--muted)]">Loading…</p>
        ) : rows.length === 0 ? (
          <p className="px-4 py-3 text-sm text-[var(--muted)]">None yet.</p>
        ) : (
          <ul className="divide-y divide-[var(--border)]" aria-label="Clients added by hand">
            {rows.map((c) => (
              <li key={c.id} className="flex flex-wrap items-center gap-3 px-4 py-2.5 text-sm">
                {editing?.id === c.id ? (
                  <form className="flex min-w-0 flex-1 items-center gap-2"
                    onSubmit={(e) => { e.preventDefault(); if (editing.name.trim()) save.mutate({ id: c.id, name: editing.name, isActive: c.isActive }); }}>
                    <input autoFocus value={editing.name} onChange={(e) => setEditing({ id: c.id, name: e.target.value })} maxLength={200}
                      aria-label={`New name for ${c.name}`}
                      className="min-w-0 flex-1 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2.5 py-1 text-sm outline-none focus:border-brand" />
                    <button type="submit" disabled={save.isPending || !editing.name.trim()}
                      className="inline-flex items-center gap-1 rounded-lg bg-brand px-2.5 py-1 text-xs font-medium text-brand-fg disabled:opacity-50"><Check size={12} /> Save</button>
                    <button type="button" onClick={() => { setEditing(null); setError(null); }}
                      className="inline-flex items-center gap-1 rounded-lg border border-[var(--border)] px-2.5 py-1 text-xs"><X size={12} /> Cancel</button>
                  </form>
                ) : (
                  <>
                    <span className={`min-w-0 flex-1 truncate font-medium ${c.isActive ? '' : 'text-[var(--muted)] line-through'}`}>{c.name}</span>
                    <span className="text-xs text-[var(--muted)]">{c.tickets === 1 ? '1 ticket' : `${c.tickets} tickets`}</span>
                    {!c.isActive && <span className="rounded-full bg-[var(--bg)] px-2 py-0.5 text-xs text-[var(--muted)]">Retired</span>}
                    <button type="button" onClick={() => { setEditing({ id: c.id, name: c.name }); setError(null); }}
                      aria-label={`Rename ${c.name}`}
                      className="inline-flex items-center gap-1 rounded-lg border border-[var(--border)] px-2.5 py-1 text-xs hover:bg-[var(--bg)]"><Pencil size={12} /> Rename</button>
                    <button type="button" disabled={save.isPending}
                      onClick={() => save.mutate({ id: c.id, name: c.name, isActive: !c.isActive })}
                      aria-label={c.isActive ? `Retire ${c.name}` : `Bring back ${c.name}`}
                      className="rounded-lg border border-[var(--border)] px-2.5 py-1 text-xs hover:bg-[var(--bg)] disabled:opacity-50">
                      {c.isActive ? 'Retire' : 'Bring back'}
                    </button>
                  </>
                )}
              </li>
            ))}
          </ul>
        )}

        <form className="flex flex-wrap items-center gap-2 border-t border-[var(--border)] px-4 py-3"
          onSubmit={(e) => { e.preventDefault(); if (adding.trim()) add.mutate(adding); }}>
          <input value={adding} onChange={(e) => setAdding(e.target.value)} maxLength={200} placeholder="Client name, e.g. NeedTech"
            aria-label="Name of the client to add"
            className="min-w-0 flex-1 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-1.5 text-sm outline-none focus:border-brand" />
          <button type="submit" disabled={add.isPending || adding.trim().length < 2}
            className="inline-flex items-center gap-1 rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
            <Plus size={13} /> {add.isPending ? 'Adding…' : 'Add client'}
          </button>
        </form>
        {error && <p role="alert" className="border-t border-[var(--border)] px-4 py-2 text-xs text-red-600 dark:text-red-400">{error}</p>}
      </div>
    </section>
  );
}
