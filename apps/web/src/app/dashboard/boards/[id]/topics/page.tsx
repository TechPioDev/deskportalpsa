'use client';

import { use, useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Plus, Archive, RotateCcw } from 'lucide-react';
import { api, type BoardTopic, type BoardTopicInput } from '@/lib/api';

const PRIORITIES = ['LOW', 'NORMAL', 'HIGH', 'URGENT'];

/**
 * What tickets on this board can be about, and what each kind fills in when it is chosen. A topic is
 * a shortcut for the person raising the ticket, never a rule: everything it sets stays editable.
 */
export default function BoardTopicsPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const qc = useQueryClient();
  const [editing, setEditing] = useState<BoardTopic | 'new' | null>(null);

  const { data: boards } = useQuery({ queryKey: ['boards', true], queryFn: () => api.boards(true) });
  const { data: topics, isLoading, isError, error } = useQuery({
    queryKey: ['board-topics', id, true],
    queryFn: () => api.boardTopics(id, true),
    retry: false,
  });
  const setActive = useMutation({
    mutationFn: ({ topicId, active }: { topicId: string; active: boolean }) => api.setBoardTopicActive(topicId, active),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['board-topics'] }),
  });

  const board = boards?.find((b) => b.id === id);

  if (isError) {
    const message = error instanceof Error ? error.message : '';
    return (
      <p className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-5 py-4 text-sm text-[var(--muted)]">
        {/403|forbidden|permission/i.test(message)
          ? 'Topics are set up by leads and administrators.'
          : `Couldn’t load the topics: ${message || 'the server did not answer.'}`}
      </p>
    );
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <Link href={`/dashboard/boards/${id}`} className="inline-flex items-center gap-1 text-xs text-[var(--muted)] hover:text-[var(--fg)]">
            <ArrowLeft size={13} /> Back to {board?.name ?? 'the board'}
          </Link>
          <h1 className="mt-1 text-2xl font-semibold tracking-tight">Topics</h1>
          <p className="text-sm text-[var(--muted)]">
            What tickets on this board are about. Choosing one fills in the department, priority,
            assignee and due date that kind of work usually has, and whoever raises the ticket can
            still change any of it.
          </p>
        </div>
        <button onClick={() => setEditing('new')}
          className="inline-flex items-center gap-2 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
          <Plus size={15} /> New topic
        </button>
      </div>

      {editing && (
        <TopicForm boardId={id} topic={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); qc.invalidateQueries({ queryKey: ['board-topics'] }); }} />
      )}

      {isLoading && <div className="h-24 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}

      {!isLoading && (topics ?? []).length === 0 && (
        <p className="rounded-xl border border-dashed border-[var(--border)] px-5 py-8 text-center text-sm text-[var(--muted)]">
          No topics yet. Add the kinds of work this board handles — patching, access requests, site
          visits — and raising a ticket becomes one choice instead of four.
        </p>
      )}

      {(topics ?? []).length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <table className="w-full text-sm">
            <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
              <tr className="border-b border-[var(--border)]">
                <th className="px-5 py-2.5 font-medium">Topic</th>
                <th className="px-2 py-2.5 font-medium">Department</th>
                <th className="px-2 py-2.5 font-medium">Priority</th>
                <th className="px-2 py-2.5 font-medium">Usually goes to</th>
                <th className="px-2 py-2.5 font-medium">Due within</th>
                <th className="px-2 py-2.5 font-medium">SLA plan</th>
                <th className="px-5 py-2.5 font-medium"></th>
              </tr>
            </thead>
            <tbody>
              {(topics ?? []).map((t) => (
                <tr key={t.id} className={`border-b border-[var(--border)] last:border-0 ${t.isActive ? '' : 'opacity-60'}`}>
                  <td className="px-5 py-3 font-medium">
                    {t.name}
                    {!t.isActive && <span className="ml-2 text-[11px] font-normal text-[var(--faint)]">retired</span>}
                  </td>
                  <td className="px-2 py-3 text-xs text-[var(--muted)]">{t.defaultDepartmentName ?? '—'}</td>
                  <td className="px-2 py-3 text-xs text-[var(--muted)]">{t.defaultPriority ?? '—'}</td>
                  <td className="px-2 py-3 text-xs text-[var(--muted)]">{t.defaultAssigneeName ?? 'Anyone'}</td>
                  <td className="px-2 py-3 text-xs text-[var(--muted)]">{t.dueInHours ? `${t.dueInHours}h` : '—'}</td>
                  <td className="px-2 py-3 text-xs text-[var(--muted)]">{t.slaPlanName ?? 'Board default'}</td>
                  <td className="px-5 py-3 text-right text-xs font-medium">
                    <button onClick={() => setEditing(t)} className="text-[var(--muted)] hover:text-[var(--fg)]">Edit</button>
                    <button onClick={() => setActive.mutate({ topicId: t.id, active: !t.isActive })}
                      className="ml-3 inline-flex items-center gap-1 text-[var(--muted)] hover:text-[var(--fg)]">
                      {t.isActive ? <><Archive size={12} /> Retire</> : <><RotateCcw size={12} /> Restore</>}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <p className="text-xs text-[var(--muted)]">
        Retiring a topic leaves every ticket raised under it exactly as it is; it only stops new ones
        choosing it.
      </p>
    </div>
  );
}

function TopicForm({ boardId, topic, onClose, onSaved }: {
  boardId: string; topic: BoardTopic | null; onClose: () => void; onSaved: () => void;
}) {
  const [v, setV] = useState<BoardTopicInput>({
    name: topic?.name ?? '',
    defaultDepartmentId: topic?.defaultDepartmentId ?? '',
    defaultPriority: topic?.defaultPriority ?? '',
    defaultAssigneeUserId: topic?.defaultAssigneeUserId ?? '',
    dueInHours: topic?.dueInHours ?? null,
    sortOrder: topic?.sortOrder ?? 0,
    slaPlanId: topic?.slaPlanId ?? null,
    requireReview: topic?.requireReview ?? false,
  });
  const { data: plans } = useQuery({ queryKey: ['sla-plans', false], queryFn: () => api.slaPlans(false), retry: false });
  const { data: departments } = useQuery({ queryKey: ['board-departments'], queryFn: api.boardDepartments, retry: false });
  const { data: staff } = useQuery({
    queryKey: ['staff-users-min'], queryFn: () => api.staffUsers({ pageSize: 200 }), staleTime: 5 * 60_000, retry: false,
  });
  const save = useMutation({
    mutationFn: () => {
      const input: BoardTopicInput = {
        ...v,
        defaultDepartmentId: v.defaultDepartmentId || null,
        defaultPriority: v.defaultPriority || null,
        defaultAssigneeUserId: v.defaultAssigneeUserId || null,
        dueInHours: v.dueInHours || null,
        slaPlanId: v.slaPlanId || null,
      };
      return topic ? api.updateBoardTopic(topic.id, input) : api.addBoardTopic(boardId, input);
    },
    onSuccess: onSaved,
  });
  const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand';

  return (
    <form className="grid gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 sm:grid-cols-2"
      onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
        Topic
        <input required value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })}
          placeholder="Patching" className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Goes to department
        <select value={v.defaultDepartmentId ?? ''} onChange={(e) => setV({ ...v, defaultDepartmentId: e.target.value })} className={field}>
          <option value="">Leave to the person raising it</option>
          {(departments ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Starts at priority
        <select value={v.defaultPriority ?? ''} onChange={(e) => setV({ ...v, defaultPriority: e.target.value })} className={field}>
          <option value="">Normal</option>
          {PRIORITIES.map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Usually goes to
        <select value={v.defaultAssigneeUserId ?? ''} onChange={(e) => setV({ ...v, defaultAssigneeUserId: e.target.value })} className={field}>
          <option value="">Anyone</option>
          {(staff?.users ?? []).filter((u) => u.isActive).map((u) => <option key={u.id} value={u.id}>{u.displayName}</option>)}
        </select>
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Due within (hours)
        <input type="number" min={1} max={8760} value={v.dueInHours ?? ''}
          onChange={(e) => setV({ ...v, dueInHours: e.target.value ? Number(e.target.value) : null })}
          placeholder="8" className={field} />
        <span className="block text-[11px] font-normal text-[var(--faint)]">
          Leave blank when this kind of work has no usual deadline. A due date nobody chose becomes an
          overdue ticket nobody meant.
        </span>
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
        SLA plan
        <select value={v.slaPlanId ?? ''} onChange={(e) => setV({ ...v, slaPlanId: e.target.value || null })} className={field}>
          <option value="">The board&apos;s default</option>
          {(plans ?? []).map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
        <span className="block text-[11px] font-normal text-[var(--faint)]">
          Sets when a reply is owed and when the ticket is due. A fixed &ldquo;Due within&rdquo; above wins for the due date.
        </span>
      </label>
      <label className="flex items-start gap-2 text-xs text-[var(--muted)] sm:col-span-2">
        <input type="checkbox" checked={!!v.requireReview} className="mt-0.5" aria-label="Review this kind of work"
          onChange={(e) => setV({ ...v, requireReview: e.target.checked })} />
        <span>Review this kind of work before it closes, even if the board does not review everything.</span>
      </label>
      {save.isError && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400 sm:col-span-2">{(save.error as Error).message}</p>
      )}
      <div className="flex justify-end gap-2 sm:col-span-2">
        <button type="button" onClick={onClose}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
        <button type="submit" disabled={save.isPending}
          className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
          {save.isPending ? 'Saving…' : topic ? 'Save topic' : 'Add topic'}
        </button>
      </div>
    </form>
  );
}
