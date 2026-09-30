'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { History, Pencil } from 'lucide-react';
import { api, type BoardTicketEdit } from '@/lib/api';
import type { TicketDetail } from '@/lib/types';

const PRIORITIES = ['LOW', 'NORMAL', 'HIGH', 'URGENT'];
const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand';

/** Whether a status finishes the ticket - the same test the API applies. */
export const finishes = (status: string) => /RESOLV|CLOSED/i.test(status);

/** "2026-09-30T10:00:00Z" to the value a date-time input wants, in the viewer's own time. */
function toLocalInput(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/**
 * The details of a ticket on the team's own board, changed after it was raised. Sent whole: what the
 * form shows is what is saved. A PSA ticket never gets this form - its details are the provider's.
 */
export function EditBoardTicketForm({ ticket, onClose }: { ticket: TicketDetail; onClose: () => void }) {
  const qc = useQueryClient();
  const d = ticket.boardDetails!;
  const [v, setV] = useState<BoardTicketEdit>({
    title: ticket.title,
    description: ticket.description,
    priority: ticket.portalPriority.toUpperCase(),
    dueAt: ticket.slaDueAt ?? null,
    boardTopicId: d.boardTopicId,
    category: ticket.portalCategory,
    departmentId: d.departmentId,
    clientCompanyId: d.clientCompanyId,
  });
  const { data: topics } = useQuery({ queryKey: ['board-topics', d.boardId], queryFn: () => api.boardTopics(d.boardId), retry: false });
  const { data: departments } = useQuery({ queryKey: ['board-departments'], queryFn: api.boardDepartments, staleTime: 5 * 60_000, retry: false });
  const { data: clients } = useQuery({ queryKey: ['report-clients'], queryFn: api.reportClients, staleTime: 5 * 60_000, retry: false });
  const save = useMutation({
    mutationFn: () => api.editBoardTicket(ticket.id, v),
    onSuccess: () => {
      [['ticket', ticket.id], ['ticket-history', ticket.id], ['tickets']].forEach((k) => qc.invalidateQueries({ queryKey: k }));
      onClose();
    },
  });

  return (
    <form aria-label="Edit ticket details" className="mt-3 grid gap-3 border-t border-[var(--border)] pt-3"
      onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Title
        <input required maxLength={500} value={v.title} onChange={(e) => setV({ ...v, title: e.target.value })} className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Description
        <textarea rows={3} value={v.description ?? ''} onChange={(e) => setV({ ...v, description: e.target.value || null })} className={field} />
      </label>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-1">
        <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
          Priority
          <select value={v.priority} onChange={(e) => setV({ ...v, priority: e.target.value })} className={field}>
            {!PRIORITIES.includes(v.priority) && <option value={v.priority}>{v.priority}</option>}
            {PRIORITIES.map((p) => <option key={p} value={p}>{p}</option>)}
          </select>
        </label>
        <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
          Due
          <input type="datetime-local" value={toLocalInput(v.dueAt)}
            onChange={(e) => setV({ ...v, dueAt: e.target.value ? new Date(e.target.value).toISOString() : null })} className={field} />
        </label>
        <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
          Topic
          <select value={v.boardTopicId ?? ''} onChange={(e) => setV({ ...v, boardTopicId: e.target.value || null })} className={field}>
            <option value="">None</option>
            {(topics ?? []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
        </label>
        <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
          Category
          <input maxLength={200} value={v.category ?? ''} onChange={(e) => setV({ ...v, category: e.target.value || null })} className={field} />
        </label>
        <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
          Department
          <select value={v.departmentId ?? ''} onChange={(e) => setV({ ...v, departmentId: e.target.value || null })} className={field}>
            <option value="">None</option>
            {(departments ?? []).map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
          </select>
        </label>
        <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
          For a client
          <select value={v.clientCompanyId ?? ''} onChange={(e) => setV({ ...v, clientCompanyId: e.target.value || null })} className={field}>
            <option value="">No client — our own work</option>
            {(clients ?? []).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
      </div>
      <p className="text-[11px] text-[var(--faint)]">Naming a client records who the work was for. The client never sees this ticket.</p>
      {save.isError && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(save.error as Error).message}</p>}
      <div className="flex justify-end gap-2">
        <button type="button" onClick={onClose} className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
        <button type="submit" disabled={save.isPending} className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
          {save.isPending ? 'Saving…' : 'Save details'}
        </button>
      </div>
    </form>
  );
}

/** The button that opens the form above. */
export function EditDetailsButton({ onClick }: { onClick: () => void }) {
  return (
    <button type="button" onClick={onClick}
      className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-brand">
      <Pencil size={12} /> Edit details
    </button>
  );
}

/**
 * Asked before a board ticket is resolved: what fixed it. Required only where the board says so;
 * otherwise it can be left empty. Prefilled with the last resolution, so a reopened ticket shows
 * what was tried last time.
 */
export function ResolutionPrompt({ status, current, required, pending, error, onSave, onCancel }: {
  status: string; current: string | null; required: boolean; pending: boolean; error: string | null;
  onSave: (resolution: string) => void; onCancel: () => void;
}) {
  const [text, setText] = useState(current ?? '');
  return (
    <form aria-label="Resolution" className="mt-3 space-y-2 rounded-lg border border-[var(--border)] bg-[var(--bg)] p-3"
      onSubmit={(e) => { e.preventDefault(); onSave(text.trim()); }}>
      <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
        What fixed it?{required ? '' : ' (optional)'}
        <textarea autoFocus rows={3} maxLength={4000} required={required} value={text} onChange={(e) => setText(e.target.value)}
          placeholder="Replaced the HDMI cable behind the panel; the screen has been stable since."
          className="w-full rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm text-[var(--fg)] outline-none focus:border-brand" />
      </label>
      {error && <p role="alert" className="text-xs text-red-600 dark:text-red-400">{error}</p>}
      <div className="flex justify-end gap-2">
        <button type="button" onClick={onCancel} className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--surface)]">Cancel</button>
        <button type="submit" disabled={pending} className="rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
          {pending ? 'Saving…' : `Mark ${status.replace(/_/g, ' ').toLowerCase()}`}
        </button>
      </div>
    </form>
  );
}

const KIND_DOT: Record<string, string> = {
  created: 'bg-brand', status: 'bg-sky-500', reopened: 'bg-amber-500', edited: 'bg-violet-500',
  assigned: 'bg-emerald-500', team: 'bg-emerald-500', time: 'bg-slate-400', other: 'bg-slate-300',
};

/**
 * What happened to the ticket and who did it, newest first. Collapsed to the latest few; the rest is
 * one click away. Staff only - it names people and the team's own steps.
 */
export function TicketHistoryPanel({ ticketId }: { ticketId: string }) {
  const { data, isLoading, isError } = useQuery({ queryKey: ['ticket-history', ticketId], queryFn: () => api.ticketHistory(ticketId), retry: false });
  const [all, setAll] = useState(false);
  const rows = data ?? [];
  const shown = all ? rows : rows.slice(0, 6);

  return (
    <section aria-labelledby="history-heading" className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
      <h2 id="history-heading" className="flex items-center gap-2 text-sm font-semibold"><History size={15} aria-hidden="true" /> History</h2>
      {isLoading && <div className="mt-3 h-12 animate-pulse rounded-lg bg-[var(--bg)]" />}
      {isError && <p className="mt-2 text-xs text-[var(--muted)]">The history could not be loaded.</p>}
      {!isLoading && !isError && rows.length === 0 && <p className="mt-2 text-xs text-[var(--muted)]">Nothing recorded yet.</p>}
      {shown.length > 0 && (
        <ol className="mt-3 space-y-3">
          {shown.map((e, i) => (
            <li key={`${e.at}-${i}`} className="flex gap-2.5 text-xs">
              <span className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${KIND_DOT[e.kind] ?? KIND_DOT.other}`} aria-hidden="true" />
              <div className="min-w-0">
                <p className="text-[var(--fg)]">{e.summary}</p>
                {e.note && <p className="mt-0.5 italic text-[var(--muted)]">&ldquo;{e.note}&rdquo;</p>}
                <p className="mt-0.5 text-[var(--faint)]">
                  {e.who ? `${e.who} · ` : ''}{new Date(e.at).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}
                </p>
              </div>
            </li>
          ))}
        </ol>
      )}
      {rows.length > 6 && (
        <button type="button" onClick={() => setAll(!all)} className="mt-3 text-xs font-medium text-brand hover:underline">
          {all ? 'Show less' : `Show all ${rows.length}`}
        </button>
      )}
    </section>
  );
}
