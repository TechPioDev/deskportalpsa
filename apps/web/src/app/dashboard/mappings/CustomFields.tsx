'use client';

import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Eye, EyeOff, Lock } from 'lucide-react';
import { api } from '@/lib/api';
import type { CustomField } from '@/lib/types';

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

type Draft = { import: boolean; portalLabel: string; clientVisible: boolean };
const draftOf = (f: CustomField): Draft => ({ import: f.import, portalLabel: f.portalLabel, clientVisible: f.clientVisible });
const KINDS: Record<string, string> = { text: 'Text', number: 'Number', date: 'Date', boolean: 'Yes / no', list: 'List' };

/**
 * Which of a connection's PSA custom fields the portal brings in, what each is called here, and
 * whether the client may see it.
 *
 * The fields are the PSA account's own; none is known to the portal by name. Nothing of a field is
 * kept until it is chosen here. A chosen field is shown to staff only; showing it to the client is
 * a second, separate choice, and saving one asks to be confirmed in so many words. Every field is
 * read-only: its value is changed in the PSA.
 */
export function CustomFields({ connectionId }: { connectionId: string }) {
  const qc = useQueryClient();
  const queryKey = useMemo(() => ['custom-fields', connectionId], [connectionId]);
  const settings = useQuery({ queryKey, queryFn: () => api.customFields(connectionId), retry: false, staleTime: 0 });
  const [drafts, setDrafts] = useState<Record<string, Draft>>({});
  const [confirmed, setConfirmed] = useState(false);
  const fields = settings.data?.fields;
  useEffect(() => { if (fields) { setDrafts(Object.fromEntries(fields.map((f) => [f.key, draftOf(f)]))); setConfirmed(false); } }, [fields]);

  const changed = (fields ?? []).filter((f) => {
    const d = drafts[f.key];
    return d && (d.import !== f.import || (d.import && (d.portalLabel.trim() !== f.portalLabel || d.clientVisible !== f.clientVisible)));
  });
  // What would newly be put in front of clients by this save. Said back before it is saved.
  const toClients = changed.filter((f) => drafts[f.key].import && drafts[f.key].clientVisible && !f.clientVisible);

  const save = useMutation({
    mutationFn: () => api.saveCustomFields(connectionId, changed.map((f) => ({
      key: f.key, import: drafts[f.key].import,
      portalLabel: drafts[f.key].portalLabel.trim() || null, clientVisible: drafts[f.key].import && drafts[f.key].clientVisible,
    }))),
    onSuccess: (data) => qc.setQueryData(queryKey, data),
  });
  const set = (key: string, patch: Partial<Draft>) => {
    setDrafts((d) => ({ ...d, [key]: { ...d[key], ...patch } }));
    setConfirmed(false);
    save.reset();
  };

  if (settings.isLoading) return <p className="text-sm text-[var(--muted)]">Asking the PSA for its custom fields…</p>;
  if (settings.isError || !settings.data) {
    return <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(settings.error, 'The custom fields could not be read.')}</p>;
  }
  const data = settings.data;

  return (
    <section aria-label="Custom fields" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      <header className="border-b border-[var(--border)] px-4 py-3">
        <h2 className="text-sm font-semibold">The PSA&apos;s custom fields</h2>
        <p className="mt-0.5 text-xs text-[var(--muted)]">
          {data.fields.length === 0
            ? (data.supported ? 'This PSA lists no custom fields for tickets.' : 'Custom fields are not read from this PSA.')
            : `${data.imported} of ${data.fields.length} brought in · ${data.clientVisible} shown to clients.`}
        </p>
        <p className="mt-1.5 max-w-3xl text-xs text-[var(--muted)]">
          These are this PSA account&apos;s own fields. Nothing of a field is kept until it is ticked here. A field that is brought in is shown
          to staff only; whether the client sees it on their own tickets is chosen separately, field by field. Every field is read-only
          here: its value is changed in the PSA. A ticket shows a newly chosen field once it has next been read from the PSA.
        </p>
        {data.notes.map((n) => <p key={n} className="mt-1.5 text-xs text-amber-700 dark:text-amber-300">{n}</p>)}
      </header>

      {data.fields.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full min-w-[760px] text-left text-sm">
            <thead className="text-xs text-[var(--muted)]">
              <tr>
                <th className="px-4 py-2 font-medium">Bring in</th>
                <th className="px-2 py-2 font-medium">In the PSA</th>
                <th className="px-2 py-2 font-medium">Kind</th>
                <th className="px-2 py-2 font-medium">Called in the portal</th>
                <th className="px-2 py-2 font-medium">Who sees it</th>
                <th className="px-4 py-2 font-medium">Changing it</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-[var(--border)]">
              {data.fields.map((f) => {
                const d = drafts[f.key] ?? draftOf(f);
                const dirty = changed.some((c) => c.key === f.key);
                return (
                  <tr key={f.key} className={dirty ? 'bg-amber-50/60 dark:bg-amber-950/20' : undefined}>
                    <td className="px-4 py-1.5">
                      <input type="checkbox" checked={d.import} aria-label={`Bring in ${f.label}`}
                        onChange={(e) => set(f.key, { import: e.target.checked, clientVisible: e.target.checked ? d.clientVisible : false })} />
                    </td>
                    <td className="px-2 py-1.5">
                      {f.label}
                      {!f.listedByPsa && <span className="ml-1.5 text-[11px] text-[var(--faint)]">not listed by the PSA now</span>}
                    </td>
                    <td className="px-2 py-1.5 text-[var(--muted)]">{KINDS[f.dataType] ?? 'Text'}</td>
                    <td className="px-2 py-1.5">
                      <input value={d.portalLabel} disabled={!d.import} maxLength={200} aria-label={`What ${f.label} is called in the portal`}
                        onChange={(e) => set(f.key, { portalLabel: e.target.value })}
                        className="w-full min-w-[10rem] rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1 text-sm outline-none focus:border-brand disabled:opacity-50" />
                    </td>
                    <td className="px-2 py-1.5">
                      <select value={d.import && d.clientVisible ? 'client' : 'staff'} disabled={!d.import} aria-label={`Who sees ${f.label}`}
                        onChange={(e) => set(f.key, { clientVisible: e.target.value === 'client' })}
                        className={'rounded-lg border bg-[var(--bg)] px-2 py-1 text-sm disabled:opacity-50 ' + (d.import && d.clientVisible ? 'border-amber-400' : 'border-[var(--border)]')}>
                        <option value="staff">Staff only</option>
                        <option value="client">Staff and the client</option>
                      </select>
                      {d.import && (d.clientVisible
                        ? <Eye size={13} className="ml-1.5 inline text-amber-600" aria-hidden="true" />
                        : <EyeOff size={13} className="ml-1.5 inline text-[var(--faint)]" aria-hidden="true" />)}
                    </td>
                    <td className="px-4 py-1.5 text-xs text-[var(--muted)]" title={f.editableReason}>
                      <Lock size={12} className="mr-1 inline" aria-hidden="true" />Read only
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {changed.length > 0 && (
        <div className="border-t border-[var(--border)] bg-amber-50/60 px-4 py-3 dark:bg-amber-950/20">
          <p className="text-sm font-medium">{changed.length} {changed.length === 1 ? 'change' : 'changes'} not saved yet</p>
          {toClients.length > 0 && (
            <label className="mt-2 flex items-start gap-2 text-sm">
              <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} className="mt-0.5" />
              <span>
                Clients will see <span className="font-medium">{toClients.map((f) => drafts[f.key].portalLabel.trim() || f.label).join(', ')}</span> on
                their own tickets, with whatever the PSA holds in {toClients.length === 1 ? 'that field' : 'those fields'}. I have checked that is fit for them to read.
              </span>
            </label>
          )}
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <button type="button" onClick={() => save.mutate()} disabled={save.isPending || (toClients.length > 0 && !confirmed)}
              className="rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
              {save.isPending ? 'Saving…' : 'Save'}
            </button>
            <button type="button" disabled={save.isPending}
              onClick={() => { setDrafts(Object.fromEntries((fields ?? []).map((f) => [f.key, draftOf(f)]))); setConfirmed(false); save.reset(); }}
              className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--bg)]">Discard</button>
            {save.isError && <span role="alert" className="basis-full text-xs text-rose-600 dark:text-rose-400">{message(save.error, 'The changes could not be saved.')}</span>}
          </div>
        </div>
      )}
      {changed.length === 0 && save.isSuccess && (
        <p className="border-t border-[var(--border)] px-4 py-3 text-xs text-emerald-600 dark:text-emerald-400">
          Saved. What a ticket shows, and to whom, follows this at once; a newly chosen field appears on a ticket once it has next been read from the PSA.
        </p>
      )}
    </section>
  );
}
