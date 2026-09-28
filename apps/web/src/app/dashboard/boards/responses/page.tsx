'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Plus, Archive, RotateCcw, MessageSquareText } from 'lucide-react';
import { api, type CannedResponse, type CannedResponseInput } from '@/lib/api';
import { PLACEHOLDERS } from '@/lib/canned';
import { NoteBody } from '@/components/NoteBody';

/**
 * Canned responses: the replies the desk writes often enough to keep. Offered in every reply box —
 * PSA tickets included — and filled in from the ticket there, so one response serves them all.
 */
export default function CannedResponsesPage() {
  const qc = useQueryClient();
  const [editing, setEditing] = useState<CannedResponse | 'new' | null>(null);
  const { data: responses, isLoading, isError, error } = useQuery({
    queryKey: ['canned', true], queryFn: () => api.cannedResponses(true), retry: false,
  });
  const setActive = useMutation({
    mutationFn: ({ id, active }: { id: string; active: boolean }) => api.setCannedResponseActive(id, active),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['canned'] }),
  });

  if (isError) {
    const message = error instanceof Error ? error.message : '';
    return (
      <p className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-5 py-4 text-sm text-[var(--muted)]">
        {/403|forbidden|permission/i.test(message)
          ? 'Canned responses are managed by leads and administrators. Everyone can use them from the reply box.'
          : `Couldn’t load the responses: ${message || 'the server did not answer.'}`}
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
          <h1 className="mt-1 text-2xl font-semibold tracking-tight">Canned responses</h1>
          <p className="text-sm text-[var(--muted)]">
            Replies your team writes again and again. Anyone can insert one from the reply box on any
            ticket, including PSA tickets, and edit it before sending.
          </p>
        </div>
        <button onClick={() => setEditing('new')}
          className="inline-flex items-center gap-2 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
          <Plus size={15} /> New response
        </button>
      </div>

      {editing && (
        <ResponseForm response={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); qc.invalidateQueries({ queryKey: ['canned'] }); }} />
      )}

      {isLoading && <div className="h-24 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}

      {!isLoading && (responses ?? []).length === 0 && (
        <div className="flex flex-col items-center rounded-xl border border-dashed border-[var(--border)] px-6 py-10 text-center">
          <MessageSquareText className="mb-3 text-[var(--faint)]" size={26} />
          <p className="max-w-md text-sm text-[var(--muted)]">
            No responses yet. Start with the ones you type most — an acknowledgement, a &ldquo;please
            restart and let us know&rdquo;, the steps for a password reset.
          </p>
        </div>
      )}

      <ul className="grid gap-3 lg:grid-cols-2">
        {(responses ?? []).map((r) => (
          <li key={r.id} className={`rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 ${r.isActive ? '' : 'opacity-60'}`}>
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <h2 className="font-medium">{r.name}</h2>
                <p className="text-xs text-[var(--muted)]">
                  {r.boardName ? `${r.boardName} only` : 'Every ticket'}
                  {!r.isActive && ' · retired'}
                </p>
              </div>
              <span className="flex shrink-0 gap-3 text-xs font-medium">
                <button onClick={() => setEditing(r)} className="text-[var(--muted)] hover:text-[var(--fg)]">Edit</button>
                <button onClick={() => setActive.mutate({ id: r.id, active: !r.isActive })}
                  className="inline-flex items-center gap-1 text-[var(--muted)] hover:text-[var(--fg)]">
                  {r.isActive ? <><Archive size={12} /> Retire</> : <><RotateCcw size={12} /> Restore</>}
                </button>
              </span>
            </div>
            <div className="mt-2 border-t border-[var(--border)] pt-2 text-[var(--muted)]">
              <NoteBody body={r.body} />
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}

function ResponseForm({ response, onClose, onSaved }: {
  response: CannedResponse | null; onClose: () => void; onSaved: () => void;
}) {
  const [v, setV] = useState<CannedResponseInput>({
    name: response?.name ?? '',
    body: response?.body ?? '',
    boardId: response?.boardId ?? null,
    sortOrder: response?.sortOrder ?? 0,
  });
  const { data: boards } = useQuery({ queryKey: ['boards', false], queryFn: () => api.boards(false), retry: false });
  const save = useMutation({ mutationFn: () => api.saveCannedResponse(response?.id ?? null, v), onSuccess: onSaved });
  const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand';

  return (
    <form className="grid gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 sm:grid-cols-2"
      onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Name
        <input required maxLength={80} value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })}
          placeholder="Received — we are on it" className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Offered on
        <select value={v.boardId ?? ''} onChange={(e) => setV({ ...v, boardId: e.target.value || null })} className={field}>
          <option value="">Every ticket, PSA ones included</option>
          {(boards ?? []).map((b) => <option key={b.id} value={b.id}>{b.name} only</option>)}
        </select>
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
        Text
        <textarea required rows={6} maxLength={10000} value={v.body} onChange={(e) => setV({ ...v, body: e.target.value })}
          placeholder={'Hi {requester},\n\nThanks — we have logged this as {ticket.number} and {assignee} is looking at it.\n\n{me}'}
          className={`${field} font-mono text-[13px]`} />
      </label>
      <div className="text-xs text-[var(--muted)] sm:col-span-2">
        <p className="mb-1 font-medium">Filled in from the ticket when inserted:</p>
        <ul className="flex flex-wrap gap-x-4 gap-y-1">
          {PLACEHOLDERS.map((p) => (
            <li key={p.token}>
              <button type="button" onClick={() => setV({ ...v, body: `${v.body}${p.token}` })}
                title={`Add ${p.token} — ${p.meaning}`}
                className="font-mono text-brand hover:underline">{p.token}</button>
              <span className="text-[var(--faint)]"> {p.meaning}</span>
            </li>
          ))}
        </ul>
      </div>
      {save.isError && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400 sm:col-span-2">{(save.error as Error).message}</p>
      )}
      <div className="flex justify-end gap-2 sm:col-span-2">
        <button type="button" onClick={onClose}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
        <button type="submit" disabled={save.isPending}
          className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
          {save.isPending ? 'Saving…' : response ? 'Save response' : 'Add response'}
        </button>
      </div>
    </form>
  );
}
