'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Copy, KeyRound, Plus, RefreshCw, Radio, Check, AlertTriangle } from 'lucide-react';
import { api, type AlertSource, type AlertSourceInput } from '@/lib/api';

const VENDORS = [
  { id: 0, label: 'Other tool' },
  { id: 1, label: 'NinjaOne' },
  { id: 2, label: 'Datto RMM' },
];
const vendorName = (id: number) => VENDORS.find((v) => v.id === id)?.label ?? 'Other tool';

/**
 * Monitoring tools allowed to open tickets. The key is shown once, when it is issued, because only
 * a hash of it is kept — which is what stops a copy of the database becoming a way to raise tickets.
 */
export default function AlertSourcesPage() {
  const qc = useQueryClient();
  const [adding, setAdding] = useState(false);
  const [issued, setIssued] = useState<{ name: string; key: string } | null>(null);
  const { data, isLoading, isError, error } = useQuery({ queryKey: ['alert-sources'], queryFn: api.alertSources, retry: false });
  const { data: boards } = useQuery({ queryKey: ['boards', true], queryFn: () => api.boards(true) });

  const monitoringBoards = (boards ?? []).filter((b) => b.kind === 1 && b.isActive);
  const setActive = useMutation({
    mutationFn: ({ id, active }: { id: string; active: boolean }) => api.setAlertSourceActive(id, active),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['alert-sources'] }),
  });
  const regenerate = useMutation({
    mutationFn: (s: AlertSource) => api.regenerateAlertSourceKey(s.id),
    onSuccess: (r) => { setIssued({ name: r.source.name, key: r.key }); qc.invalidateQueries({ queryKey: ['alert-sources'] }); },
  });

  // Say what actually went wrong. Reporting every failure as "you lack permission" sends whoever
  // reads it looking at roles when the server had something else to say.
  if (isError) {
    const message = error instanceof Error ? error.message : '';
    return (
      <p className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-5 py-4 text-sm text-[var(--muted)]">
        {/403|forbidden|permission/i.test(message)
          ? 'You do not manage boards, so this page is not for you.'
          : `Couldn’t load the monitoring tools: ${message || 'the server did not answer.'}`}
      </p>
    );
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <Link href="/dashboard/boards" className="inline-flex items-center gap-1 text-xs text-[var(--muted)] hover:text-[var(--fg)]">
            <ArrowLeft size={13} /> All boards
          </Link>
          <h1 className="mt-1 text-2xl font-semibold tracking-tight">Monitoring tools</h1>
          <p className="text-sm text-[var(--muted)]">
            Tools that open tickets here on their own. Each gets its own key, and an alert that clears
            closes its ticket rather than leaving it to be tidied up by hand.
          </p>
        </div>
        <button onClick={() => setAdding(true)} disabled={monitoringBoards.length === 0}
          className="inline-flex items-center gap-2 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
          <Plus size={15} /> Connect a tool
        </button>
      </div>

      {monitoringBoards.length === 0 && (
        <p className="rounded-xl border border-dashed border-[var(--border)] px-5 py-6 text-sm text-[var(--muted)]">
          Alerts land on a monitoring board, and there is not one yet.{' '}
          <Link href="/dashboard/boards" className="font-medium text-brand hover:underline">Create one first.</Link>
        </p>
      )}

      {issued && <IssuedKey name={issued.name} value={issued.key} onDone={() => setIssued(null)} />}

      {adding && (
        <SourceForm boards={monitoringBoards.map((b) => ({ id: b.id, name: b.name }))}
          onClose={() => setAdding(false)}
          onCreated={(r) => { setAdding(false); setIssued({ name: r.name, key: r.key }); qc.invalidateQueries({ queryKey: ['alert-sources'] }); }} />
      )}

      {isLoading && <div className="h-24 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}

      {(data ?? []).length > 0 && (
        <ul className="space-y-3">
          {(data ?? []).map((s) => (
            <li key={s.id} className={`rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 ${s.isActive ? '' : 'opacity-70'}`}>
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <Radio size={15} className={s.isActive ? 'text-green-600 dark:text-green-400' : 'text-[var(--faint)]'} />
                    <span className="font-medium">{s.name}</span>
                    <span className="rounded bg-[var(--bg)] px-1.5 py-0.5 text-[11px] text-[var(--muted)]">{vendorName(s.vendor)}</span>
                  </div>
                  <div className="mt-1 text-xs text-[var(--muted)]">
                    Opens tickets on <span className="font-medium text-[var(--fg)]">{s.boardName}</span>
                    {' · '}key <span className="font-mono">{s.keyHint}…</span>
                    {' · '}{s.receivedCount} received
                    {s.lastReceivedAt ? ` · last ${new Date(s.lastReceivedAt).toLocaleString()}` : ' · nothing yet'}
                    {s.closeOnClear ? ' · closes on clear' : ' · stays open on clear'}
                  </div>
                  {s.lastError && (
                    <p className="mt-1 inline-flex items-center gap-1 text-xs text-amber-700 dark:text-amber-400">
                      <AlertTriangle size={12} /> Last delivery refused: {s.lastError}
                    </p>
                  )}
                </div>
                <div className="flex shrink-0 items-center gap-3 text-xs font-medium">
                  <button onClick={() => regenerate.mutate(s)} disabled={regenerate.isPending}
                    className="inline-flex items-center gap-1 text-[var(--muted)] hover:text-[var(--fg)]">
                    <RefreshCw size={12} /> New key
                  </button>
                  <button onClick={() => setActive.mutate({ id: s.id, active: !s.isActive })}
                    className="text-[var(--muted)] hover:text-[var(--fg)]">
                    {s.isActive ? 'Switch off' : 'Switch on'}
                  </button>
                </div>
              </div>
            </li>
          ))}
        </ul>
      )}

      <Setup />
    </div>
  );
}

function IssuedKey({ name, value, onDone }: { name: string; value: string; onDone: () => void }) {
  const [copied, setCopied] = useState(false);
  return (
    <div role="alert" className="rounded-xl border border-brand bg-brand-tint p-4 dark:bg-brand/10">
      <h2 className="flex items-center gap-2 text-sm font-semibold"><KeyRound size={15} /> The key for {name}</h2>
      <p className="mt-1 text-xs text-[var(--muted)]">
        Copy it into the tool now. It is not stored in a form anyone can read back, so this is the only
        time it is shown. If it is lost, issue a new one.
      </p>
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <code className="min-w-0 flex-1 truncate rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 font-mono text-xs">{value}</code>
        <button onClick={() => { navigator.clipboard?.writeText(value); setCopied(true); }}
          className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">
          {copied ? <><Check size={14} /> Copied</> : <><Copy size={14} /> Copy</>}
        </button>
        <button onClick={onDone} className="rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
          Done
        </button>
      </div>
    </div>
  );
}

function SourceForm({ boards, onClose, onCreated }: {
  boards: { id: string; name: string }[];
  onClose: () => void;
  onCreated: (r: { name: string; key: string }) => void;
}) {
  const [v, setV] = useState<AlertSourceInput>({ name: '', boardId: boards[0]?.id ?? '', vendor: 1, closeOnClear: true });
  const create = useMutation({
    mutationFn: () => api.createAlertSource(v),
    onSuccess: (r) => onCreated({ name: r.source.name, key: r.key }),
  });
  const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand';

  return (
    <form className="grid gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 sm:grid-cols-2"
      onSubmit={(e) => { e.preventDefault(); create.mutate(); }}>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Name it
        <input required value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })}
          placeholder="NinjaOne production" className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Which tool
        <select value={v.vendor} onChange={(e) => setV({ ...v, vendor: Number(e.target.value) })} className={field}>
          {VENDORS.map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
        </select>
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Alerts land on
        <select value={v.boardId} onChange={(e) => setV({ ...v, boardId: e.target.value })} className={field}>
          {boards.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
        </select>
      </label>
      <label className="flex items-start gap-2 self-end pb-2 text-xs text-[var(--muted)]">
        <input type="checkbox" checked={!!v.closeOnClear} className="mt-0.5"
          onChange={(e) => setV({ ...v, closeOnClear: e.target.checked })} />
        <span>Close the ticket when the tool says the alert cleared.</span>
      </label>
      {create.isError && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400 sm:col-span-2">{(create.error as Error).message}</p>
      )}
      <div className="flex justify-end gap-2 sm:col-span-2">
        <button type="button" onClick={onClose}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
        <button type="submit" disabled={create.isPending}
          className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
          {create.isPending ? 'Connecting…' : 'Connect and show the key'}
        </button>
      </div>
    </form>
  );
}

/** What to paste into the tool. Written so it can be followed without reading anything else. */
function Setup() {
  const url = typeof window === 'undefined' ? '' : `${window.location.origin}/api/bff/api/intake/alerts`;
  return (
    <section className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
      <h2 className="text-sm font-semibold">Setting a tool up</h2>
      <ol className="mt-2 list-decimal space-y-2 pl-5 text-sm text-[var(--muted)]">
        <li>In the tool, add a webhook or notification channel that posts JSON.</li>
        <li>
          Address: <code className="rounded bg-[var(--bg)] px-1.5 py-0.5 font-mono text-xs">{url}</code>
        </li>
        <li>
          Add the header <code className="rounded bg-[var(--bg)] px-1.5 py-0.5 font-mono text-xs">X-Desk-Alert-Key</code>{' '}
          with the key issued above.
        </li>
        <li>Send a body like this. The names most tools use are understood too, so their own example usually works unchanged.</li>
      </ol>
      <pre className="mt-3 overflow-x-auto rounded-lg border border-[var(--border)] bg-[var(--bg)] p-3 text-xs">{`{
  "alertId": "the tool's own id for the condition",
  "title": "Disk C: is 95% full",
  "description": "Free space 4 GB of 100 GB",
  "severity": "critical",
  "device": "ACME-SRV01",
  "client": "Acme Dental",
  "status": "raised"
}`}</pre>
      <p className="mt-2 text-xs text-[var(--muted)]">
        Send the same alert id again with <span className="font-mono">&quot;status&quot;: &quot;cleared&quot;</span> when the
        condition resolves. Repeats of the same id update one ticket rather than opening more, and the
        client name is matched against your own customer list.
      </p>
    </section>
  );
}
