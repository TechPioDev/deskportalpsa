'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Plus, Repeat, Play, Pause, RotateCcw, Trash2 } from 'lucide-react';
import { api, type RecurringTicket, type RecurringTicketInput } from '@/lib/api';

const FREQUENCIES = [
  { value: 0, label: 'Every day' },
  { value: 1, label: 'Every weekday (Mon–Fri)' },
  { value: 2, label: 'Every week' },
  { value: 3, label: 'Every month' },
];
const WEEKDAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const PRIORITIES = ['LOW', 'NORMAL', 'HIGH', 'URGENT'];

function when(iso: string | null) {
  if (!iso) return '—';
  return new Date(iso).toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
}

/**
 * Work that comes round on a schedule. The worker raises each ticket when it falls due, in the name
 * of whoever last saved the schedule, on the board and topic chosen here — so it arrives with that
 * topic's department, assignee and SLA exactly as if it had been raised by hand.
 */
export default function RecurringTicketsPage() {
  const qc = useQueryClient();
  const [editing, setEditing] = useState<RecurringTicket | 'new' | null>(null);
  const [lastRun, setLastRun] = useState<{ id: string; text: string } | null>(null);
  const { data: items, isLoading, isError, error } = useQuery({
    queryKey: ['recurring'], queryFn: () => api.recurringTickets(true), retry: false,
  });
  const refresh = () => qc.invalidateQueries({ queryKey: ['recurring'] });
  const setActive = useMutation({
    mutationFn: ({ id, active }: { id: string; active: boolean }) => api.setRecurringTicketActive(id, active),
    onSuccess: refresh,
  });
  const remove = useMutation({ mutationFn: (id: string) => api.deleteRecurringTicket(id), onSuccess: refresh });
  const run = useMutation({
    mutationFn: (id: string) => api.runRecurringTicket(id),
    onSuccess: (r, id) => { setLastRun({ id, text: r.outcome }); refresh(); qc.invalidateQueries({ queryKey: ['board-tickets'] }); },
  });

  if (isError) {
    const message = error instanceof Error ? error.message : '';
    return (
      <p className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-5 py-4 text-sm text-[var(--muted)]">
        {/403|forbidden|permission/i.test(message)
          ? 'Recurring tickets are set up by leads and administrators.'
          : `Couldn’t load the recurring tickets: ${message || 'the server did not answer.'}`}
      </p>
    );
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="max-w-2xl">
          <Link href="/dashboard/boards" className="inline-flex items-center gap-1 text-xs text-[var(--muted)] hover:text-[var(--fg)]">
            <ArrowLeft size={13} /> All boards
          </Link>
          <h1 className="mt-1 text-2xl font-semibold tracking-tight">Recurring tickets</h1>
          <p className="text-sm text-[var(--muted)]">
            Work that comes round on a schedule — the Monday patch review, the monthly backup test. Each
            one raises a ticket on its board at the time you choose, with its checklist as tasks. While
            the last one is still open, the next is skipped rather than piled up.
          </p>
        </div>
        <button onClick={() => setEditing('new')}
          className="inline-flex items-center gap-2 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
          <Plus size={15} /> New schedule
        </button>
      </div>

      {editing && (
        <ScheduleForm item={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); refresh(); }} />
      )}

      {isLoading && <div className="h-24 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}

      {!isLoading && (items ?? []).length === 0 && (
        <div className="flex flex-col items-center rounded-xl border border-dashed border-[var(--border)] px-6 py-10 text-center">
          <Repeat className="mb-3 text-[var(--faint)]" size={26} />
          <p className="max-w-md text-sm text-[var(--muted)]">
            Nothing scheduled yet. Start with the job your team forgets most often.
          </p>
        </div>
      )}

      <ul className="space-y-3">
        {(items ?? []).map((r) => (
          <li key={r.id} className={`rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 ${r.isActive ? '' : 'opacity-60'}`}>
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0">
                <h2 className="font-medium">{r.title}</h2>
                <p className="text-xs text-[var(--muted)]">
                  {r.schedule} · {r.boardName}
                  {r.topicName && ` · ${r.topicName}`}
                  {r.assignedName && ` · to ${r.assignedName}`}
                  {!r.isActive && ' · paused'}
                </p>
              </div>
              <span className="flex shrink-0 flex-wrap gap-3 text-xs font-medium">
                <button onClick={() => run.mutate(r.id)} disabled={run.isPending}
                  title="Raise the ticket now, to check it is what you meant. The schedule is not changed."
                  className="inline-flex items-center gap-1 text-[var(--muted)] hover:text-[var(--fg)] disabled:opacity-50">
                  <Play size={12} /> Raise now
                </button>
                <button onClick={() => setEditing(r)} className="text-[var(--muted)] hover:text-[var(--fg)]">Edit</button>
                <button onClick={() => setActive.mutate({ id: r.id, active: !r.isActive })}
                  className="inline-flex items-center gap-1 text-[var(--muted)] hover:text-[var(--fg)]">
                  {r.isActive ? <><Pause size={12} /> Pause</> : <><RotateCcw size={12} /> Resume</>}
                </button>
                <button onClick={() => { if (confirm(`Delete the schedule “${r.title}”? Tickets it already raised are kept.`)) remove.mutate(r.id); }}
                  className="inline-flex items-center gap-1 text-[var(--muted)] hover:text-red-600">
                  <Trash2 size={12} /> Delete
                </button>
              </span>
            </div>
            <dl className="mt-3 grid gap-x-6 gap-y-1 text-xs sm:grid-cols-3">
              <div><dt className="text-[var(--faint)]">Next</dt><dd>{r.isActive ? when(r.nextRunAt) : 'Paused'}</dd></div>
              <div><dt className="text-[var(--faint)]">Last run</dt><dd>{when(r.lastRunAt)}</dd></div>
              <div>
                <dt className="text-[var(--faint)]">What happened</dt>
                <dd>
                  {r.lastTicketId && r.lastOutcome?.startsWith('Raised')
                    ? <Link href={`/dashboard/tickets/${r.lastTicketId}`} className="text-brand hover:underline">{r.lastOutcome}</Link>
                    : r.lastOutcome ?? 'Not run yet'}
                </dd>
              </div>
            </dl>
            {lastRun?.id === r.id && (
              <p role="status" className="mt-2 text-xs text-[var(--muted)]">{lastRun.text}</p>
            )}
            {r.checklist && (
              <p className="mt-2 text-[11px] text-[var(--faint)]">
                Checklist: {r.checklist.split('\n').length} step{r.checklist.split('\n').length === 1 ? '' : 's'}
              </p>
            )}
          </li>
        ))}
      </ul>
      {run.isError && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(run.error as Error).message}</p>}
    </div>
  );
}

function ScheduleForm({ item, onClose, onSaved }: { item: RecurringTicket | null; onClose: () => void; onSaved: () => void }) {
  const [v, setV] = useState<RecurringTicketInput>({
    boardId: item?.boardId ?? '',
    title: item?.title ?? '',
    description: item?.description ?? '',
    boardTopicId: item?.boardTopicId ?? null,
    priority: item?.priority ?? null,
    assignedAppUserId: item?.assignedAppUserId ?? null,
    checklist: item?.checklist ?? '',
    frequency: item?.frequency ?? 2,
    dayOfWeek: item?.dayOfWeek ?? 1,
    dayOfMonth: item?.dayOfMonth ?? 1,
    hour: item?.hour ?? 9,
    skipIfOpen: item?.skipIfOpen ?? true,
  });
  const { data: boards } = useQuery({ queryKey: ['boards', false], queryFn: () => api.boards(false), retry: false });
  const { data: topics } = useQuery({
    queryKey: ['board-topics', v.boardId, false], queryFn: () => api.boardTopics(v.boardId, false),
    enabled: !!v.boardId, retry: false,
  });
  const { data: staff } = useQuery({
    queryKey: ['staff-users-min'], queryFn: () => api.staffUsers({ pageSize: 200 }), staleTime: 5 * 60_000, retry: false,
  });
  const save = useMutation({
    mutationFn: () => api.saveRecurringTicket(item?.id ?? null, {
      ...v,
      description: v.description?.trim() || null,
      checklist: v.checklist?.trim() || null,
    }),
    onSuccess: onSaved,
  });
  const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand';
  const label = 'space-y-1 text-xs font-medium text-[var(--muted)]';

  return (
    <form className="grid gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 sm:grid-cols-2 lg:grid-cols-4"
      onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
      <label className={`${label} sm:col-span-2`}>
        Ticket title
        <input required maxLength={500} value={v.title} onChange={(e) => setV({ ...v, title: e.target.value })}
          placeholder="Monthly backup restore test" className={field} />
      </label>
      <label className={label}>
        Board
        <select required value={v.boardId} onChange={(e) => setV({ ...v, boardId: e.target.value, boardTopicId: null })} className={field}>
          <option value="" disabled>Choose a board</option>
          {(boards ?? []).map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
        </select>
      </label>
      <label className={label}>
        Topic
        <select value={v.boardTopicId ?? ''} onChange={(e) => setV({ ...v, boardTopicId: e.target.value || null })}
          disabled={!v.boardId} className={field}>
          <option value="">None</option>
          {(topics ?? []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
        </select>
      </label>

      <label className={label}>
        Repeats
        <select value={v.frequency} onChange={(e) => setV({ ...v, frequency: Number(e.target.value) })} className={field}>
          {FREQUENCIES.map((f) => <option key={f.value} value={f.value}>{f.label}</option>)}
        </select>
      </label>
      {v.frequency === 2 && (
        <label className={label}>
          On
          <select value={v.dayOfWeek} onChange={(e) => setV({ ...v, dayOfWeek: Number(e.target.value) })} className={field}>
            {WEEKDAYS.map((d, i) => <option key={d} value={i}>{d}</option>)}
          </select>
        </label>
      )}
      {v.frequency === 3 && (
        <label className={label}>
          On day
          <select value={v.dayOfMonth} onChange={(e) => setV({ ...v, dayOfMonth: Number(e.target.value) })} className={field}>
            {Array.from({ length: 28 }, (_, i) => i + 1).map((d) => <option key={d} value={d}>{d}</option>)}
            <option value={0}>Last day of the month</option>
          </select>
        </label>
      )}
      <label className={label}>
        At
        <select value={v.hour} onChange={(e) => setV({ ...v, hour: Number(e.target.value) })} className={field}>
          {Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
        </select>
      </label>
      <label className={label}>
        Priority
        <select value={v.priority ?? ''} onChange={(e) => setV({ ...v, priority: e.target.value || null })} className={field}>
          <option value="">From the topic, else Normal</option>
          {PRIORITIES.map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
      </label>
      <label className={label}>
        Assign to
        <select value={v.assignedAppUserId ?? ''} onChange={(e) => setV({ ...v, assignedAppUserId: e.target.value || null })} className={field}>
          <option value="">From the topic, else unclaimed</option>
          {(staff?.users ?? []).filter((u) => u.isActive).map((u) => <option key={u.id} value={u.id}>{u.displayName}</option>)}
        </select>
      </label>

      <label className={`${label} sm:col-span-2`}>
        Details
        <textarea rows={4} maxLength={4000} value={v.description ?? ''} onChange={(e) => setV({ ...v, description: e.target.value })}
          placeholder="What this is and where the instructions live" className={field} />
      </label>
      <label className={`${label} sm:col-span-2`}>
        Checklist — one step per line
        <textarea rows={4} maxLength={6000} value={v.checklist ?? ''} onChange={(e) => setV({ ...v, checklist: e.target.value })}
          placeholder={'Pick a random file\nRestore it to a scratch folder\nRecord the result'} className={`${field} font-mono text-[13px]`} />
      </label>

      <label className="flex items-start gap-2 text-xs text-[var(--muted)] sm:col-span-2 lg:col-span-4">
        <input type="checkbox" className="mt-0.5" checked={v.skipIfOpen} onChange={(e) => setV({ ...v, skipIfOpen: e.target.checked })} />
        <span>Skip a run while the ticket from last time is still open, rather than raising another beside it.</span>
      </label>
      <p className="text-[11px] text-[var(--faint)] sm:col-span-2 lg:col-span-4">
        Times are in your organization&apos;s time zone. Tickets are raised in the name of whoever saves this schedule.
      </p>
      {save.isError && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400 sm:col-span-2 lg:col-span-4">{(save.error as Error).message}</p>
      )}
      <div className="flex justify-end gap-2 sm:col-span-2 lg:col-span-4">
        <button type="button" onClick={onClose}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
        <button type="submit" disabled={save.isPending}
          className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
          {save.isPending ? 'Saving…' : item ? 'Save schedule' : 'Add schedule'}
        </button>
      </div>
    </form>
  );
}
