'use client';

import { useMemo, useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ClipboardList, Plus, Users, EyeOff, Eye, Archive, RotateCcw, ArrowRight, Radio } from 'lucide-react';
import { api, type Board, type BoardInput } from '@/lib/api';

const KIND = { internal: 0, rmm: 1 } as const;

/**
 * The team's own boards: work that never came from a PSA. Kept on its own page, and its own part of
 * the sidebar, so provider queues and the team's own work are never read as one list.
 */
export default function BoardsPage() {
  const qc = useQueryClient();
  const [showClosed, setShowClosed] = useState(false);
  const [editing, setEditing] = useState<Board | 'new' | null>(null);
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const canManage = !!me?.permissions?.includes('boards.manage');
  const { data, isLoading } = useQuery({
    queryKey: ['boards', showClosed],
    queryFn: () => api.boards(showClosed),
  });

  const boards = data ?? [];
  const internal = boards.filter((b) => b.kind === KIND.internal);
  const monitoring = boards.filter((b) => b.kind === KIND.rmm);

  const setActive = useMutation({
    mutationFn: ({ id, active }: { id: string; active: boolean }) => api.setBoardActive(id, active),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['boards'] }),
  });

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Internal boards</h1>
          <p className="text-sm text-[var(--muted)]">
            The team&apos;s own work: tasks you raise for each other, and alerts that arrive from monitoring.
            Nothing here is sent to a PSA, and nothing here is visible to a client unless a monitoring
            board says so.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <label className="inline-flex items-center gap-2 text-sm text-[var(--muted)]">
            <input type="checkbox" checked={showClosed} onChange={(e) => setShowClosed(e.target.checked)} />
            Show closed boards
          </label>
          {canManage && (
            <Link href="/dashboard/boards/sources"
              className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">
              <Radio size={15} /> Monitoring tools
            </Link>
          )}
          {canManage && (
            <button onClick={() => setEditing('new')}
              className="inline-flex items-center gap-2 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
              <Plus size={15} /> New board
            </button>
          )}
        </div>
      </div>

      {editing && (
        <BoardForm
          board={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); qc.invalidateQueries({ queryKey: ['boards'] }); }} />
      )}

      {isLoading && <div className="h-28 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}

      {!isLoading && boards.length === 0 && (
        <div className="flex flex-col items-center rounded-xl border border-dashed border-[var(--border)] px-6 py-12 text-center">
          <ClipboardList className="mb-3 text-[var(--faint)]" size={26} />
          <p className="text-sm text-[var(--muted)]">
            No boards yet. {canManage
              ? 'Create one for the work your team does for itself, such as Internal IT.'
              : 'A lead can create the first one.'}
          </p>
        </div>
      )}

      <Section title="Our work" boards={internal} canManage={canManage}
        onEdit={setEditing} onActive={(id, active) => setActive.mutate({ id, active })} />
      <Section title="From monitoring" boards={monitoring} canManage={canManage}
        onEdit={setEditing} onActive={(id, active) => setActive.mutate({ id, active })}
        hint="Alerts raised automatically by an RMM. A board here can be shown to the client it names." />
    </div>
  );
}

function Section({ title, boards, canManage, onEdit, onActive, hint }: {
  title: string; boards: Board[]; canManage: boolean; hint?: string;
  onEdit: (b: Board) => void; onActive: (id: string, active: boolean) => void;
}) {
  if (boards.length === 0) return null;
  return (
    <section className="space-y-2">
      <div>
        <h2 className="text-sm font-semibold uppercase tracking-wide text-[var(--faint)]">{title}</h2>
        {hint && <p className="text-xs text-[var(--muted)]">{hint}</p>}
      </div>
      <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
        {boards.map((b) => (
          <li key={b.id} className={`rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 ${b.isActive ? '' : 'opacity-70'}`}>
            <div className="flex items-start justify-between gap-2">
              <div className="min-w-0">
                <Link href={`/dashboard/boards/${b.id}`} className="font-medium hover:underline">{b.name}</Link>
                <div className="mt-0.5 flex flex-wrap items-center gap-1.5 text-xs text-[var(--muted)]">
                  <span className="rounded bg-[var(--bg)] px-1.5 py-0.5 font-mono">{b.key}-000000</span>
                  <span>·</span>
                  <span>{b.openTickets} open</span>
                  <span>·</span>
                  <span className="inline-flex items-center gap-1">
                    <Users size={11} aria-hidden="true" />
                    {b.memberCount === 0 ? 'Whole team' : `${b.memberCount} member${b.memberCount === 1 ? '' : 's'}`}
                  </span>
                </div>
              </div>
              <span className={`inline-flex shrink-0 items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-medium ${b.clientVisible
                ? 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300'
                : 'bg-[var(--bg)] text-[var(--muted)]'}`}>
                {b.clientVisible ? <><Eye size={11} /> Client can see</> : <><EyeOff size={11} /> Staff only</>}
              </span>
            </div>
            {b.description && <p className="mt-2 line-clamp-2 text-xs text-[var(--muted)]">{b.description}</p>}
            <div className="mt-3 flex items-center gap-2">
              <Link href={`/dashboard/boards/${b.id}`}
                className="inline-flex items-center gap-1 text-sm font-medium text-brand hover:underline">
                Open board <ArrowRight size={13} />
              </Link>
              {canManage && (
                <span className="ml-auto flex gap-2">
                  <button onClick={() => onEdit(b)} className="text-xs font-medium text-[var(--muted)] hover:text-[var(--fg)]">Edit</button>
                  <button onClick={() => onActive(b.id, !b.isActive)}
                    className="inline-flex items-center gap-1 text-xs font-medium text-[var(--muted)] hover:text-[var(--fg)]">
                    {b.isActive ? <><Archive size={12} /> Close</> : <><RotateCcw size={12} /> Reopen</>}
                  </button>
                </span>
              )}
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}

function BoardForm({ board, onClose, onSaved }: { board: Board | null; onClose: () => void; onSaved: () => void }) {
  const [v, setV] = useState<BoardInput>({
    name: board?.name ?? '',
    key: board?.key ?? '',
    description: board?.description ?? '',
    kind: board?.kind ?? KIND.internal,
    clientVisible: board?.clientVisible ?? false,
    sortOrder: board?.sortOrder ?? 0,
  });
  const save = useMutation({
    mutationFn: () => (board ? api.updateBoard(board.id, v) : api.createBoard(v)),
    onSuccess: onSaved,
  });
  const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand';
  const isRmm = v.kind === KIND.rmm;
  const hasTickets = (board?.openTickets ?? 0) > 0;
  const suggestion = useMemo(
    () => v.name.replace(/[^A-Za-z]/g, '').slice(0, 3).toUpperCase(),
    [v.name]);

  return (
    <form className="grid gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 sm:grid-cols-2"
      onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Board name
        <input required value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })}
          placeholder="Internal IT" className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Ticket prefix
        <input required value={v.key} onChange={(e) => setV({ ...v, key: e.target.value.toUpperCase() })}
          placeholder={suggestion || 'INT'} maxLength={8} disabled={hasTickets}
          className={`${field} font-mono disabled:opacity-60`} />
        <span className="block text-[11px] font-normal text-[var(--faint)]">
          {hasTickets
            ? 'Tickets already carry this prefix, so it cannot change.'
            : 'Two to eight letters or digits. Tickets read as INT-000123.'}
        </span>
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
        What this board is for
        <input value={v.description ?? ''} onChange={(e) => setV({ ...v, description: e.target.value })}
          placeholder="Work we do for ourselves" className={field} />
      </label>
      {!board && (
        <div className="space-y-1 text-xs font-medium text-[var(--muted)]" role="group" aria-label="Board kind">
          Kind
          <div className="flex gap-1.5">
            {[{ k: KIND.internal, label: 'Our work' }, { k: KIND.rmm, label: 'From monitoring' }].map((o) => (
              <button key={o.k} type="button" aria-pressed={v.kind === o.k}
                onClick={() => setV({ ...v, kind: o.k, clientVisible: false })}
                className={`rounded-lg border px-3 py-1.5 text-xs font-medium ${v.kind === o.k
                  ? 'border-brand bg-brand-tint text-brand dark:bg-brand/20'
                  : 'border-[var(--border)] text-[var(--muted)] hover:bg-[var(--bg)]'}`}>
                {o.label}
              </button>
            ))}
          </div>
        </div>
      )}
      {isRmm && (
        <label className="flex items-start gap-2 text-xs text-[var(--muted)]">
          <input type="checkbox" checked={!!v.clientVisible} className="mt-0.5"
            onChange={(e) => setV({ ...v, clientVisible: e.target.checked })} />
          <span>
            Show these alerts to the client they name, in their own portal. Leave this off and the
            board stays staff only.
          </span>
        </label>
      )}
      {save.isError && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400 sm:col-span-2">{(save.error as Error).message}</p>
      )}
      <div className="flex justify-end gap-2 sm:col-span-2">
        <button type="button" onClick={onClose}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
        <button type="submit" disabled={save.isPending}
          className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
          {save.isPending ? 'Saving…' : board ? 'Save board' : 'Create board'}
        </button>
      </div>
    </form>
  );
}
