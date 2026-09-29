'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Server, Building2, Plus, Pencil, Trash2, Check, X, CheckCircle2, Info, DownloadCloud, FileSignature, ListTree } from 'lucide-react';
import { api, type Device, type DeviceInput } from '@/lib/api';
import { CpHeader, AccessError, Field } from '../_ui';
import { warrantyState, WARRANTY_TONE } from '@/lib/devices';

export default function AccountsPage() {
  const qc = useQueryClient();
  const { data: account, error } = useQuery({ queryKey: ['cp-account'], queryFn: api.cpAccount, retry: false });
  const { data: devices, isLoading } = useQuery({ queryKey: ['cp-devices'], queryFn: api.cpDevices, retry: false, enabled: !error });
  // Live from the PSA on every visit — agreements are the provider's commercial record, not
  // something the portal should cache a stale copy of.
  const { data: psaView, isError: psaViewError } = useQuery({ queryKey: ['cp-psa-view'], queryFn: api.cpPsaView, retry: false, enabled: !error });
  const [draft, setDraft] = useState<DeviceInput | null>(null);

  const save = useMutation({
    mutationFn: (input: DeviceInput) => api.cpSaveDevice(input),
    onSuccess: () => { setDraft(null); qc.invalidateQueries({ queryKey: ['cp-devices'] }); },
  });
  const del = useMutation({
    mutationFn: (id: string) => api.cpDeleteDevice(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['cp-devices'] }),
  });
  // Pull the account's contacts + devices straight from the PSA so the panel reflects the
  // provider's records rather than only what was entered by hand.
  const importPsa = useMutation({
    mutationFn: () => api.cpImportFromPsa(),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['cp-devices'] });
      qc.invalidateQueries({ queryKey: ['cp-users'] });
    },
  });

  return (
    <div className="mx-auto max-w-4xl space-y-5">
      <CpHeader icon={Server} title="Accounts & Devices" subtitle="Your account details and devices, kept in step with your IT provider's PSA. Open a device to see every ticket raised about it." />

      {!error && (
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-4 py-3">
          <p className="text-xs text-[var(--muted)]">
            Import contacts and devices for this account directly from the connected PSA.
            {importPsa.isSuccess && importPsa.data && (
              <span className="ml-1 font-medium text-green-600 dark:text-green-400">
                Imported {importPsa.data.usersCreated} new / {importPsa.data.usersUpdated} updated contacts,
                {' '}{importPsa.data.devicesCreated} new / {importPsa.data.devicesUpdated} updated devices.
              </span>
            )}
            {importPsa.isError && (
              <span className="ml-1 text-red-600 dark:text-red-400">{(importPsa.error as Error)?.message ?? 'Import failed'}</span>
            )}
          </p>
          <button onClick={() => importPsa.mutate()} disabled={importPsa.isPending}
            className="inline-flex shrink-0 items-center gap-2 rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-50">
            <DownloadCloud size={15} /> {importPsa.isPending ? 'Importing…' : 'Import from PSA'}
          </button>
        </div>
      )}

      {error ? <AccessError label="Accounts & Devices" /> : (
        <>
          {/* Account card — read-only, from PSA sync */}
          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
            <div className="flex items-center gap-2.5">
              <span className="inline-flex h-10 w-10 items-center justify-center rounded-lg bg-brand/10 text-brand"><Building2 size={18} /></span>
              <div>
                <div className="text-sm font-semibold">{account?.name ?? '—'}</div>
                <div className="text-xs text-[var(--muted)]">
                  {account?.connectionName ? `${account.connectionName} · ` : ''}PSA id {account?.externalCompanyId ?? '—'}
                  {account && (account.isActive
                    ? <span className="ml-2 inline-flex items-center gap-1 text-green-600 dark:text-green-400"><CheckCircle2 size={11} /> Active</span>
                    : <span className="ml-2 text-[var(--faint)]">Inactive</span>)}
                </div>
              </div>
            </div>
            <p className="mt-3 flex items-center gap-1.5 text-xs text-[var(--faint)]"><Info size={12} /> Account details come from your PSA and are read-only here.</p>
          </div>

          {/* Agreements & contracts — the provider's commercial record, read live, never cached. */}
          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
            <div className="flex items-center gap-2 border-b border-[var(--border)] px-5 py-3.5">
              <FileSignature size={15} className="text-brand" />
              <h2 className="text-sm font-semibold">Agreements & contracts</h2>
            </div>
            {psaViewError || psaView?.agreementsUnavailable ? (
              <p className="px-5 py-4 text-sm text-[var(--muted)]">Couldn&apos;t reach the PSA right now — try again shortly.</p>
            ) : !psaView ? (
              <p className="px-5 py-4 text-sm text-[var(--muted)]">Loading from the PSA…</p>
            ) : !psaView.agreementsSupported ? (
              <p className="px-5 py-4 text-sm text-[var(--muted)]">Your provider does not expose agreements to this portal.</p>
            ) : psaView.agreements.length === 0 ? (
              <p className="px-5 py-4 text-sm text-[var(--muted)]">No agreements are recorded for this account in the PSA.</p>
            ) : (
              <ul className="divide-y divide-[var(--border)]">
                {psaView.agreements.map((a) => (
                  <li key={`${a.name}-${a.startDate ?? ''}`} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-5 py-3">
                    <span className="min-w-0 flex-1 text-sm font-medium">{a.name}</span>
                    {a.type && <span className="rounded bg-[var(--bg)] px-1.5 py-0.5 text-[11px] text-[var(--muted)]">{a.type}</span>}
                    {a.status && (
                      <span className={`rounded px-1.5 py-0.5 text-[11px] font-medium ${a.status.toLowerCase() === 'active'
                        ? 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300'
                        : 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300'}`}>{a.status}</span>
                    )}
                    <span className="text-xs text-[var(--faint)]">
                      {a.startDate ? new Date(a.startDate).toLocaleDateString() : '—'}
                      {' → '}
                      {a.endDate ? new Date(a.endDate).toLocaleDateString() : 'open-ended'}
                    </span>
                  </li>
                ))}
              </ul>
            )}
            <p className="flex items-center gap-1.5 border-t border-[var(--border)] px-5 py-2.5 text-xs text-[var(--faint)]">
              <Info size={12} /> Read live from your PSA. To change an agreement, contact your service provider.
            </p>
          </div>

          {/* Monitored queues — where this account's work actually flows. */}
          {psaView && psaView.monitoredQueues.length > 0 && (
            <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
              <div className="mb-2.5 flex items-center gap-2">
                <ListTree size={15} className="text-brand" />
                <h2 className="text-sm font-semibold">Monitored queues</h2>
              </div>
              <div className="flex flex-wrap gap-2">
                {psaView.monitoredQueues.map((q) => (
                  <span key={q} className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2.5 py-1 text-xs font-medium">{q}</span>
                ))}
              </div>
              <p className="mt-2.5 text-xs text-[var(--faint)]">The service queues and boards your tickets have flowed through.</p>
            </div>
          )}

          {/* Devices */}
          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
            <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3.5">
              <h2 className="text-sm font-semibold">Devices</h2>
              <button onClick={() => setDraft({ name: '', type: '', identifier: '', notes: '' })}
                className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90">
                <Plus size={15} /> Add device
              </button>
            </div>
            <div className="divide-y divide-[var(--border)]">
              {isLoading && <div className="px-5 py-8 text-center text-sm text-[var(--muted)]">Loading…</div>}
              {devices?.length === 0 && !draft && (
                <div className="px-5 py-8 text-center text-sm text-[var(--muted)]">
                  No devices yet. Devices your IT provider tracks in their PSA appear here automatically, once a day.
                </div>
              )}
              {devices?.map((d) => <Row key={d.id} device={d} onSave={(i) => save.mutate(i)} onDelete={() => del.mutate(d.id)} saving={save.isPending} />)}
              {draft && <Editor initial={draft} onCancel={() => setDraft(null)} onSave={(i) => save.mutate(i)} saving={save.isPending} />}
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function Row({ device, onSave, onDelete, saving }: { device: Device; onSave: (i: DeviceInput) => void; onDelete: () => void; saving: boolean }) {
  const [editing, setEditing] = useState(false);
  if (editing) return <Editor initial={device} onCancel={() => setEditing(false)} onSave={(i) => { onSave(i); setEditing(false); }} saving={saving} />;
  const warranty = warrantyState(device.warrantyExpiresAt);
  return (
    <div className={`flex items-center gap-3 px-5 py-3 ${device.isActive ? '' : 'opacity-60'}`}>
      <Server size={16} className="shrink-0 text-[var(--muted)]" />
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-1.5 font-medium">
          <Link href={`/control-panel/accounts/devices/${device.id}`} className="hover:text-brand hover:underline">{device.name}</Link>
          {device.type && <span className="rounded bg-[var(--bg)] px-1.5 py-0.5 text-[11px] font-medium text-[var(--muted)]">{device.type}</span>}
          {device.fromPsa && <span className="rounded bg-brand-tint px-1.5 py-0.5 text-[11px] font-medium text-brand dark:bg-brand/15">From your PSA</span>}
          {!device.isActive && <span className="rounded bg-[var(--bg)] px-1.5 py-0.5 text-[11px] font-medium text-[var(--muted)]">Retired</span>}
        </div>
        <div className="truncate text-xs text-[var(--muted)]">
          {[device.identifier, device.notes].filter(Boolean).join(' · ') || '—'}
          {warranty && <span className={warranty.tone === 'ok' ? '' : WARRANTY_TONE[warranty.tone]}> · {warranty.text}</span>}
        </div>
      </div>
      <Link href={`/control-panel/accounts/devices/${device.id}`}
        className="hidden shrink-0 text-right text-xs text-[var(--muted)] hover:text-brand sm:block">
        {device.totalTickets === 0 ? 'No tickets' : <>
          <span className="font-medium text-[var(--fg)]">{device.totalTickets}</span> ticket{device.totalTickets === 1 ? '' : 's'}
          {device.openTickets > 0 && <span className="block text-amber-700 dark:text-amber-400">{device.openTickets} open</span>}
        </>}
      </Link>
      <button onClick={() => setEditing(true)} aria-label={device.fromPsa ? `Edit notes for ${device.name}` : `Edit ${device.name}`}
        className="rounded-md p-1.5 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-brand"><Pencil size={15} /></button>
      {/* A synced device would come straight back from the PSA; it is retired there, not here. */}
      {!device.fromPsa && (
        <button onClick={() => { if (window.confirm(`Remove ${device.name}?`)) onDelete(); }} aria-label={`Delete ${device.name}`}
          className="rounded-md p-1.5 text-[var(--muted)] hover:bg-red-50 hover:text-red-600 dark:hover:bg-red-950/50"><Trash2 size={15} /></button>
      )}
    </div>
  );
}

function Editor({ initial, onCancel, onSave, saving }: { initial: Device | DeviceInput; onCancel: () => void; onSave: (i: DeviceInput) => void; saving: boolean }) {
  const [f, setF] = useState<DeviceInput>({
    id: 'id' in initial ? initial.id : undefined,
    name: initial.name, type: initial.type ?? '', identifier: initial.identifier ?? '', notes: initial.notes ?? '',
  });
  const synced = 'fromPsa' in initial && initial.fromPsa;
  if (synced) {
    // The PSA owns this device's details; the notes are the one thing that is the client's own.
    return (
      <div className="bg-[var(--bg)]/40 px-5 py-4">
        <p className="mb-3 text-xs text-[var(--muted)]">
          <strong className="text-[var(--fg)]">{initial.name}</strong> comes from your PSA, so its name, type and serial are changed there. Add your own notes here.
        </p>
        <Field label="Notes" value={f.notes ?? ''} onChange={(v) => setF({ ...f, notes: v })} placeholder="Comms room, rack 2" />
        <div className="mt-3 flex items-center justify-end gap-2">
          <button onClick={onCancel} className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm text-[var(--muted)] hover:bg-[var(--bg)]"><X size={14} /> Cancel</button>
          <button onClick={() => onSave(f)} disabled={saving} className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-40"><Check size={14} /> Save</button>
        </div>
      </div>
    );
  }
  return (
    <div className="bg-[var(--bg)]/40 px-5 py-4">
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <Field label="Name" value={f.name} onChange={(v) => setF({ ...f, name: v })} placeholder="Reception PC" />
        <Field label="Type" value={f.type ?? ''} onChange={(v) => setF({ ...f, type: v })} placeholder="Workstation / Server / Firewall" />
        <Field label="Identifier" value={f.identifier ?? ''} onChange={(v) => setF({ ...f, identifier: v })} placeholder="Asset tag / serial / hostname" />
        <Field label="Notes" value={f.notes ?? ''} onChange={(v) => setF({ ...f, notes: v })} placeholder="Ground floor, front desk" />
      </div>
      <div className="mt-3 flex items-center justify-end gap-2">
        <button onClick={onCancel} className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm text-[var(--muted)] hover:bg-[var(--bg)]"><X size={14} /> Cancel</button>
        <button onClick={() => f.name.trim() && onSave(f)} disabled={!f.name.trim() || saving} className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-40"><Check size={14} /> Save</button>
      </div>
    </div>
  );
}
