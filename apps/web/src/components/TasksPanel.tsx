'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ListChecks, Plus, ArrowUp, ArrowDown, Trash2, UserCircle2 } from 'lucide-react';
import { api, type TicketTask } from '@/lib/api';

/**
 * The steps inside a ticket. Staff only; a client never sees the list and nothing here is sent to
 * a PSA. The ticket cannot be closed from the portal while any step is open — which is why the
 * header says how many are left rather than how many exist.
 */
export function TasksPanel({ ticketId, canUpdate, people, onNeedPeople }: {
  ticketId: string;
  canUpdate: boolean;
  /** Who a step can be given to. Empty until the staff list has loaded; then it is offered. */
  people: { id: string; name: string }[];
  /** Asks the page for the staff list — only once somebody starts writing a step, since it can
   *  cost a PSA round trip that most visits to a ticket never need. */
  onNeedPeople: () => void;
}) {
  const qc = useQueryClient();
  const [title, setTitle] = useState('');
  const [assignee, setAssignee] = useState('');
  const { data: tasks } = useQuery({ queryKey: ['ticket-tasks', ticketId], queryFn: () => api.ticketTasks(ticketId), retry: false });

  // Every change answers with the whole list, so the answer replaces the cache rather than
  // triggering a refetch; the list and board rows show progress, so those are refreshed too.
  const settle = (rows: TicketTask[]) => {
    qc.setQueryData(['ticket-tasks', ticketId], rows);
    qc.invalidateQueries({ queryKey: ['tickets'] });
    qc.invalidateQueries({ queryKey: ['board-tickets'] });
  };
  // The box is cleared the moment a step is submitted, not when the save returns: clearing on
  // success wiped whatever the next step was, typed while the first one was still saving. A failed
  // save puts its own text back.
  const add = useMutation({
    mutationFn: (v: { title: string; assignee: string }) => api.addTicketTask(ticketId, v.title, v.assignee || null),
    onMutate: () => { setTitle(''); setAssignee(''); },
    onError: (_e, v) => { setTitle((now) => now || v.title); setAssignee((now) => now || v.assignee); },
    onSuccess: settle,
  });
  const change = useMutation({
    mutationFn: (op: { kind: 'done'; task: TicketTask } | { kind: 'move'; task: TicketTask; by: -1 | 1 } | { kind: 'delete'; task: TicketTask }) =>
      op.kind === 'done' ? api.setTicketTaskDone(op.task.id, !op.task.isDone)
        : op.kind === 'move' ? api.moveTicketTask(op.task.id, op.by)
          : api.deleteTicketTask(op.task.id),
    // A tick shows at once. A checkbox that waits for the server before it changes reads as a
    // click that missed, and gets clicked again — which would untick it.
    // The optimistic change itself is made in the click handler (tick below), synchronously: made
    // here it lands a tick later, after React has already put the controlled checkbox back.
    //
    // Each answer is the whole list as of THAT save. Tick a second step while the first is still
    // saving (a slow connection to the live site) and the first answer can arrive last, carrying a
    // list from before the second tick: applied, it unticked the second step on screen while the
    // server held it done. So no answer is applied while another save is running (the screen keeps
    // the ticks as clicked), and once the last save finishes the list is reloaded from the server.
    mutationKey: ['ticket-task-change', ticketId],
    onError: () => qc.invalidateQueries({ queryKey: ['ticket-tasks', ticketId] }),
    onSuccess: () => {
      if (qc.isMutating({ mutationKey: ['ticket-task-change', ticketId] }) > 1) return;
      for (const key of [['ticket-tasks', ticketId], ['tickets'], ['board-tickets']]) qc.invalidateQueries({ queryKey: key });
    },
  });

  const rows = tasks ?? [];
  const open = rows.filter((t) => !t.isDone).length;
  // A ticket nobody has broken into steps shows the panel only to someone who can add the first.
  if (rows.length === 0 && !canUpdate) return null;

  return (
    <section className="rounded-xl border border-[var(--border)] bg-[var(--surface)]" aria-labelledby="tasks-heading">
      <div className="flex items-center gap-2 border-b border-[var(--border)] px-5 py-3">
        <h2 id="tasks-heading" className="flex items-center gap-2 text-sm font-semibold">
          <ListChecks size={14} /> Tasks
        </h2>
        {rows.length > 0 && (
          <span className={`rounded-full px-2 py-0.5 text-xs ${open === 0
            ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300'
            : 'bg-[var(--bg)] text-[var(--muted)]'}`}>
            {open === 0 ? 'All done' : `${open} of ${rows.length} left`}
          </span>
        )}
        {open > 0 && (
          <span className="ml-auto text-[11px] text-[var(--faint)]">The ticket closes once these are done.</span>
        )}
      </div>

      {rows.length > 0 && (
        <ul className="divide-y divide-[var(--border)]">
          {rows.map((t, i) => (
            <li key={t.id} className="flex items-start gap-3 px-5 py-2.5">
              <input type="checkbox" checked={t.isDone} disabled={!canUpdate}
                onChange={() => {
                  qc.setQueryData<TicketTask[]>(['ticket-tasks', ticketId],
                    (rows) => rows?.map((x) => (x.id === t.id ? { ...x, isDone: !x.isDone } : x)));
                  change.mutate({ kind: 'done', task: t });
                }}
                // Named by the task alone: the checked state already says whether it is done, and a
                // name that flips between "Tick off" and "Reopen" moves under a screen reader.
                aria-label={t.title}
                className="mt-1 h-4 w-4 shrink-0 accent-[color:var(--brand,#14532D)]" />
              <div className="min-w-0 flex-1">
                <p className={`text-sm ${t.isDone ? 'text-[var(--muted)] line-through' : ''}`}>{t.title}</p>
                <p className="text-[11px] text-[var(--faint)]">
                  {t.assignedName && <span className="mr-2 inline-flex items-center gap-1"><UserCircle2 size={11} /> {t.assignedName}</span>}
                  {t.isDone && t.doneAt && `Done by ${t.doneByName ?? 'someone'}, ${new Date(t.doneAt).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}`}
                </p>
              </div>
              {canUpdate && (
                <span className="flex shrink-0 items-center gap-0.5 text-[var(--faint)]">
                  <button type="button" disabled={i === 0 || change.isPending} onClick={() => change.mutate({ kind: 'move', task: t, by: -1 })}
                    aria-label={`Move “${t.title}” up`} className="rounded p-1 hover:bg-[var(--bg)] hover:text-[var(--fg)] disabled:opacity-30"><ArrowUp size={12} /></button>
                  <button type="button" disabled={i === rows.length - 1 || change.isPending} onClick={() => change.mutate({ kind: 'move', task: t, by: 1 })}
                    aria-label={`Move “${t.title}” down`} className="rounded p-1 hover:bg-[var(--bg)] hover:text-[var(--fg)] disabled:opacity-30"><ArrowDown size={12} /></button>
                  <button type="button" disabled={change.isPending} onClick={() => change.mutate({ kind: 'delete', task: t })}
                    aria-label={`Remove “${t.title}”`} className="rounded p-1 hover:bg-[var(--bg)] hover:text-red-600"><Trash2 size={12} /></button>
                </span>
              )}
            </li>
          ))}
        </ul>
      )}

      {canUpdate && (
        <form className="flex flex-wrap items-center gap-2 border-t border-[var(--border)] px-5 py-3"
          onSubmit={(e) => { e.preventDefault(); if (title.trim()) add.mutate({ title: title.trim(), assignee }); }}>
          <input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={300} onFocus={onNeedPeople}
            placeholder={rows.length === 0 ? 'Break this ticket into steps — first step…' : 'Add a step…'}
            aria-label="New task"
            className="min-w-0 flex-1 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-1.5 text-sm outline-none focus:border-brand" />
          {people.length > 0 && (
            <select value={assignee} onChange={(e) => setAssignee(e.target.value)} aria-label="Who this step is for"
              className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-xs outline-none focus:border-brand">
              <option value="">Anyone</option>
              {people.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          )}
          <button type="submit" disabled={!title.trim()}
            className="inline-flex items-center gap-1 rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
            <Plus size={13} /> Add
          </button>
        </form>
      )}
      {(add.isError || change.isError) && (
        <p role="alert" className="px-5 pb-3 text-xs text-red-600 dark:text-red-400">
          {((add.error ?? change.error) as Error).message}
        </p>
      )}
    </section>
  );
}
