'use client';

import { use, useMemo, useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Plus, UserCircle2, Users } from 'lucide-react';
import { api } from '@/lib/api';
import type { TicketListItem } from '@/lib/types';

const OPEN_STATUSES = ['NEW', 'OPEN', 'IN_PROGRESS', 'IN PROGRESS', 'ON_HOLD', 'ON HOLD'];
const isOpen = (status: string) => !/(CLOSED|RESOLV)/i.test(status);

/**
 * One board: what is on it, who holds each ticket, and the form to raise another. The team works
 * here all day, so the list leads with who is holding what rather than with provider metadata.
 */
export default function BoardPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const qc = useQueryClient();
  const [raising, setRaising] = useState(false);
  const [mineOnly, setMineOnly] = useState(false);

  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const { data: boards } = useQuery({ queryKey: ['boards', true], queryFn: () => api.boards(true) });
  const { data: tickets, isLoading } = useQuery({
    queryKey: ['board-tickets', id],
    queryFn: () => api.tickets(id),
  });
  const { data: members } = useQuery({
    queryKey: ['board-members', id],
    queryFn: () => api.boardMembers(id),
    retry: false,
    enabled: !!me?.permissions?.includes('boards.manage'),
  });

  const board = boards?.find((b) => b.id === id);
  const rows = tickets ?? [];
  const mine = rows.filter((t) => t.assignedToName && me?.displayName && t.assignedToName === me.displayName);
  const shown = mineOnly ? mine : rows;
  const open = shown.filter((t) => isOpen(t.portalStatus));
  const done = shown.filter((t) => !isOpen(t.portalStatus));

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <Link href="/dashboard/boards" className="inline-flex items-center gap-1 text-xs text-[var(--muted)] hover:text-[var(--fg)]">
            <ArrowLeft size={13} /> All boards
          </Link>
          <h1 className="mt-1 text-2xl font-semibold tracking-tight">{board?.name ?? 'Board'}</h1>
          <p className="text-sm text-[var(--muted)]">
            {board?.description ?? 'The team’s own work.'}{' '}
            {board && (board.memberCount === 0
              ? 'Everyone on the team can see and take these tickets.'
              : `${board.memberCount} people work this board.`)}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <label className="inline-flex items-center gap-2 text-sm text-[var(--muted)]">
            <input type="checkbox" checked={mineOnly} onChange={(e) => setMineOnly(e.target.checked)} />
            Only mine
          </label>
          <button onClick={() => setRaising((r) => !r)}
            className="inline-flex items-center gap-2 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
            <Plus size={15} /> New ticket
          </button>
        </div>
      </div>

      {raising && (
        <RaiseForm boardId={id} members={members?.map((m) => ({ id: m.appUserId, name: m.displayName })) ?? null}
          onClose={() => setRaising(false)}
          onRaised={() => { setRaising(false); qc.invalidateQueries({ queryKey: ['board-tickets', id] }); qc.invalidateQueries({ queryKey: ['boards'] }); }} />
      )}

      {isLoading && <div className="h-40 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}

      {!isLoading && rows.length === 0 && (
        <div className="flex flex-col items-center rounded-xl border border-dashed border-[var(--border)] px-6 py-12 text-center">
          <Users className="mb-3 text-[var(--faint)]" size={26} />
          <p className="text-sm text-[var(--muted)]">Nothing on this board yet. Raise the first ticket.</p>
        </div>
      )}

      <TicketTable title={`Open (${open.length})`} rows={open} />
      {done.length > 0 && <TicketTable title={`Closed (${done.length})`} rows={done} muted />}
    </div>
  );
}

function TicketTable({ title, rows, muted }: { title: string; rows: TicketListItem[]; muted?: boolean }) {
  if (rows.length === 0) return null;
  return (
    <section className={`rounded-xl border border-[var(--border)] bg-[var(--surface)] ${muted ? 'opacity-80' : ''}`}>
      <h2 className="border-b border-[var(--border)] px-5 py-3 text-sm font-semibold">{title}</h2>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
            <tr className="border-b border-[var(--border)]">
              <th className="px-5 py-2.5 font-medium">Number</th>
              <th className="px-2 py-2.5 font-medium">Title</th>
              <th className="px-2 py-2.5 font-medium">Status</th>
              <th className="px-2 py-2.5 font-medium">Priority</th>
              <th className="px-2 py-2.5 font-medium">With</th>
              <th className="px-2 py-2.5 font-medium">Client</th>
              <th className="px-5 py-2.5 font-medium">Hours</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((t) => (
              <tr key={t.id} className="border-b border-[var(--border)] last:border-0">
                <td className="px-5 py-3 font-mono text-xs text-[var(--muted)]">{t.number ?? '—'}</td>
                <td className="px-2 py-3">
                  <Link href={`/dashboard/tickets/${t.id}`} className="font-medium hover:underline">{t.title}</Link>
                </td>
                <td className="px-2 py-3">
                  <span className="rounded-full bg-[var(--bg)] px-2 py-0.5 text-xs">{t.portalStatus}</span>
                </td>
                <td className="px-2 py-3 text-xs text-[var(--muted)]">{t.portalPriority}</td>
                <td className="px-2 py-3">
                  {t.assignedToName
                    ? <span className="inline-flex items-center gap-1.5 text-xs"><UserCircle2 size={13} className="text-[var(--faint)]" />{t.assignedToName}</span>
                    : <span className="text-xs text-[var(--faint)]">Unclaimed</span>}
                </td>
                <td className="px-2 py-3 text-xs text-[var(--muted)]">{t.customerName ?? '—'}</td>
                <td className="px-5 py-3 tabular-nums text-xs text-[var(--muted)]">{t.timeWorkedHours ?? 0}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function RaiseForm({ boardId, members, onClose, onRaised }: {
  boardId: string; members: { id: string; name: string }[] | null;
  onClose: () => void; onRaised: () => void;
}) {
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [priority, setPriority] = useState('NORMAL');
  const [assignee, setAssignee] = useState('');
  const [client, setClient] = useState('');

  const { data: staff } = useQuery({
    queryKey: ['staff-users-min'],
    queryFn: () => api.staffUsers({ pageSize: 200 }),
    staleTime: 5 * 60_000,
    retry: false,
  });
  const { data: clients } = useQuery({ queryKey: ['report-clients'], queryFn: api.reportClients, staleTime: 5 * 60_000, retry: false });

  // Board members when the board names them, otherwise everyone: the same rule the board itself uses.
  const people = useMemo(() => members?.length
    ? members
    : (staff?.users ?? []).filter((u) => u.isActive).map((u) => ({ id: u.id, name: u.displayName })),
    [members, staff]);

  const raise = useMutation({
    mutationFn: () => api.createInternalTicket({
      boardId,
      title,
      description: description || null,
      priority,
      clientCompanyId: client || null,
      assignedAppUserId: assignee || null,
    }),
    onSuccess: onRaised,
  });
  const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand';

  return (
    <form className="grid gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 sm:grid-cols-2"
      onSubmit={(e) => { e.preventDefault(); raise.mutate(); }}>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
        What needs doing
        <input required value={title} onChange={(e) => setTitle(e.target.value)}
          placeholder="Rebuild the spare laptop" className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
        Detail
        <textarea rows={3} value={description} onChange={(e) => setDescription(e.target.value)}
          placeholder="Anything the next person needs to know before they start." className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Assign to
        <select value={assignee} onChange={(e) => setAssignee(e.target.value)} className={field}>
          <option value="">Leave for anyone to pick up</option>
          {people.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Priority
        <select value={priority} onChange={(e) => setPriority(e.target.value)} className={field}>
          {['LOW', 'NORMAL', 'HIGH', 'URGENT'].map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
        For a client (optional)
        <select value={client} onChange={(e) => setClient(e.target.value)} className={field}>
          <option value="">No client — our own work</option>
          {(clients ?? []).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
        <span className="block text-[11px] font-normal text-[var(--faint)]">
          Naming a client records who the work was for. It stays on this board and the client never sees it.
        </span>
      </label>
      {raise.isError && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400 sm:col-span-2">{(raise.error as Error).message}</p>
      )}
      <div className="flex justify-end gap-2 sm:col-span-2">
        <button type="button" onClick={onClose}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
        <button type="submit" disabled={raise.isPending}
          className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
          {raise.isPending ? 'Raising…' : 'Raise ticket'}
        </button>
      </div>
    </form>
  );
}
