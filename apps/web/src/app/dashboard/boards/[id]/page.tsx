'use client';

import { use, useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Plus, UserCircle2, Users, CalendarClock, Settings2 } from 'lucide-react';
import { api } from '@/lib/api';
import type { TicketListItem } from '@/lib/types';

const isOpen = (status: string) => !/(CLOSED|RESOLV)/i.test(status);

const PRIORITY_TONE: Record<string, string> = {
  URGENT: 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300',
  HIGH: 'bg-orange-100 text-orange-700 dark:bg-orange-950 dark:text-orange-300',
  NORMAL: 'bg-[var(--bg)] text-[var(--muted)]',
  LOW: 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300',
};

/** "3 days ago", the way a queue reads its last-update column. */
function ago(iso: string | null): string {
  if (!iso) return '—';
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
  if (seconds < 60) return 'just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} hr ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

function dueLabel(iso: string | null): { text: string; tone: string } {
  if (!iso) return { text: '—', tone: 'text-[var(--faint)]' };
  const due = new Date(iso).getTime();
  const hours = (due - Date.now()) / 3_600_000;
  const when = new Date(iso).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
  if (hours < 0) return { text: `${when} · overdue`, tone: 'text-red-600 dark:text-red-400 font-medium' };
  if (hours < 8) return { text: `${when} · soon`, tone: 'text-amber-700 dark:text-amber-400' };
  return { text: when, tone: 'text-[var(--muted)]' };
}

/**
 * One board: what is on it, who holds each ticket, when it is due, and the form to raise another.
 * The columns follow the desk's own habits — last update, department, who — because this is the
 * list the team lives in all day.
 */
export default function BoardPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const qc = useQueryClient();
  const [raising, setRaising] = useState(false);
  const [mineOnly, setMineOnly] = useState(false);
  const [department, setDepartment] = useState('');

  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const { data: boards } = useQuery({ queryKey: ['boards', true], queryFn: () => api.boards(true) });
  const { data: tickets, isLoading } = useQuery({ queryKey: ['board-tickets', id], queryFn: () => api.tickets(id) });
  const canManage = !!me?.permissions?.includes('boards.manage');
  const { data: members } = useQuery({
    queryKey: ['board-members', id], queryFn: () => api.boardMembers(id), retry: false, enabled: canManage,
  });

  const board = boards?.find((b) => b.id === id);
  const rows = tickets ?? [];
  const departments = useMemo(
    () => [...new Set(rows.map((t) => t.departmentName).filter(Boolean) as string[])].sort(),
    [rows]);

  const shown = rows.filter((t) =>
    (!mineOnly || (!!t.assignedToName && t.assignedToName === me?.displayName))
    && (!department || t.departmentName === department));
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
        <div className="flex flex-wrap items-center gap-2">
          {departments.length > 0 && (
            <select value={department} onChange={(e) => setDepartment(e.target.value)}
              aria-label="Filter by department"
              className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2.5 py-2 text-sm outline-none focus:border-brand">
              <option value="">All departments</option>
              {departments.map((d) => <option key={d} value={d}>{d}</option>)}
            </select>
          )}
          <label className="inline-flex items-center gap-2 text-sm text-[var(--muted)]">
            <input type="checkbox" checked={mineOnly} onChange={(e) => setMineOnly(e.target.checked)} />
            Only mine
          </label>
          {canManage && (
            <Link href={`/dashboard/boards/${id}/topics`}
              className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">
              <Settings2 size={15} /> Topics
            </Link>
          )}
          <button onClick={() => setRaising((r) => !r)}
            className="inline-flex items-center gap-2 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
            <Plus size={15} /> New ticket
          </button>
        </div>
      </div>

      {raising && (
        <RaiseForm boardId={id} members={members?.map((m) => ({ id: m.appUserId, name: m.displayName })) ?? null}
          onClose={() => setRaising(false)}
          onRaised={() => {
            setRaising(false);
            qc.invalidateQueries({ queryKey: ['board-tickets', id] });
            qc.invalidateQueries({ queryKey: ['boards'] });
          }} />
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
  // Oldest activity last: a queue is read newest-first, like every desk tool the team already uses.
  const sorted = [...rows].sort((a, b) =>
    new Date(b.lastActivityAt ?? b.createdAt).getTime() - new Date(a.lastActivityAt ?? a.createdAt).getTime());
  return (
    <section className={`rounded-xl border border-[var(--border)] bg-[var(--surface)] ${muted ? 'opacity-80' : ''}`}>
      <h2 className="border-b border-[var(--border)] px-5 py-3 text-sm font-semibold">{title}</h2>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
            <tr className="border-b border-[var(--border)]">
              <th className="px-5 py-2.5 font-medium">Ticket</th>
              <th className="px-2 py-2.5 font-medium">Last update</th>
              <th className="px-2 py-2.5 font-medium">Subject</th>
              <th className="px-2 py-2.5 font-medium">With</th>
              <th className="px-2 py-2.5 font-medium">Department</th>
              <th className="px-2 py-2.5 font-medium">Priority</th>
              <th className="px-2 py-2.5 font-medium">Due</th>
              <th className="px-5 py-2.5 font-medium">Hours</th>
            </tr>
          </thead>
          <tbody>
            {sorted.map((t) => {
              const due = dueLabel(t.dueAt);
              return (
                <tr key={t.id} className="border-b border-[var(--border)] last:border-0">
                  <td className="px-5 py-3 font-mono text-xs text-[var(--muted)]">{t.number ?? '—'}</td>
                  <td className="px-2 py-3 text-xs text-[var(--muted)]">{ago(t.lastActivityAt ?? t.createdAt)}</td>
                  <td className="px-2 py-3">
                    <Link href={`/dashboard/tickets/${t.id}`} className="font-medium hover:underline">{t.title}</Link>
                    <span className="ml-2 inline-flex items-center gap-2 align-middle text-[11px] text-[var(--faint)]">
                      {t.replyCount > 0 && <span title="Updates on this ticket">{t.replyCount}</span>}
                      {t.topic && <span className="rounded bg-[var(--bg)] px-1.5 py-0.5">{t.topic}</span>}
                      {t.source && <span>{t.source}</span>}
                      {t.customerName && <span>· {t.customerName}</span>}
                    </span>
                  </td>
                  <td className="px-2 py-3">
                    {t.assignedToName
                      ? <span className="inline-flex items-center gap-1.5 text-xs"><UserCircle2 size={13} className="text-[var(--faint)]" />{t.assignedToName}</span>
                      : <span className="text-xs text-[var(--faint)]">Unclaimed</span>}
                  </td>
                  <td className="px-2 py-3 text-xs text-[var(--muted)]">{t.departmentName ?? '—'}</td>
                  <td className="px-2 py-3">
                    <span className={`rounded-full px-2 py-0.5 text-[11px] font-medium ${PRIORITY_TONE[t.portalPriority.toUpperCase()] ?? PRIORITY_TONE.NORMAL}`}>
                      {t.portalPriority.toUpperCase()}
                    </span>
                  </td>
                  <td className={`px-2 py-3 text-xs ${due.tone}`}>{due.text}</td>
                  <td className="px-5 py-3 tabular-nums text-xs text-[var(--muted)]">{t.timeWorkedHours ?? 0}</td>
                </tr>
              );
            })}
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
  const [topicId, setTopicId] = useState('');
  const [departmentId, setDepartmentId] = useState('');
  const [source, setSource] = useState('');
  const [dueAt, setDueAt] = useState('');

  const { data: topics } = useQuery({ queryKey: ['board-topics', boardId], queryFn: () => api.boardTopics(boardId), retry: false });
  const { data: sources } = useQuery({ queryKey: ['ticket-sources'], queryFn: api.ticketSources, staleTime: 60 * 60_000, retry: false });
  const { data: departments } = useQuery({ queryKey: ['board-departments'], queryFn: api.boardDepartments, staleTime: 5 * 60_000, retry: false });
  const { data: staff } = useQuery({
    queryKey: ['staff-users-min'], queryFn: () => api.staffUsers({ pageSize: 200 }), staleTime: 5 * 60_000, retry: false,
  });
  const { data: clients } = useQuery({ queryKey: ['report-clients'], queryFn: api.reportClients, staleTime: 5 * 60_000, retry: false });

  const people = useMemo(() => members?.length
    ? members
    : (staff?.users ?? []).filter((u) => u.isActive).map((u) => ({ id: u.id, name: u.displayName })),
    [members, staff]);

  // Choosing a topic fills in what usually follows from it. Everything stays editable: the topic is
  // a shortcut, and the person raising the ticket knows when this one is different.
  const topic = topics?.find((t) => t.id === topicId);
  useEffect(() => {
    if (!topic) return;
    if (topic.defaultDepartmentId) setDepartmentId(topic.defaultDepartmentId);
    if (topic.defaultPriority) setPriority(topic.defaultPriority);
    if (topic.defaultAssigneeUserId) setAssignee(topic.defaultAssigneeUserId);
    if (topic.dueInHours) {
      const when = new Date(Date.now() + topic.dueInHours * 3_600_000);
      when.setMinutes(when.getMinutes() - when.getTimezoneOffset());
      setDueAt(when.toISOString().slice(0, 16));
    }
  }, [topic]);

  const raise = useMutation({
    mutationFn: () => api.createInternalTicket({
      boardId,
      title,
      description: description || null,
      priority,
      clientCompanyId: client || null,
      assignedAppUserId: assignee || null,
      boardTopicId: topicId || null,
      departmentId: departmentId || null,
      source: source || null,
      dueAt: dueAt ? new Date(dueAt).toISOString() : null,
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

      {(topics ?? []).length > 0 && (
        <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
          What it is about
          <select value={topicId} onChange={(e) => setTopicId(e.target.value)} className={field}>
            <option value="">Not specified</option>
            {(topics ?? []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
          <span className="block text-[11px] font-normal text-[var(--faint)]">
            Fills in the department, priority and due date this kind of work usually has.
          </span>
        </label>
      )}

      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Department
        <select value={departmentId} onChange={(e) => setDepartmentId(e.target.value)} className={field}>
          <option value="">Not assigned to a department</option>
          {(departments ?? []).filter((d) => d.isActive).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
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

      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Due by
        <input type="datetime-local" value={dueAt} onChange={(e) => setDueAt(e.target.value)} className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        How it reached us
        <select value={source} onChange={(e) => setSource(e.target.value)} className={field}>
          <option value="">Not recorded</option>
          {(sources ?? []).map((s) => <option key={s} value={s}>{s}</option>)}
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
