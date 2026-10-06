'use client';

import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plug, Plus, ShieldCheck, ChevronDown, Globe, Copy, Ticket, Users, Contact, RefreshCw, Activity, Upload, Image as ImageIcon, type LucideIcon } from 'lucide-react';
import { api, ApiError } from '@/lib/api';
import type { ConnectionSummary, ConnectionFields } from '@/lib/types';
import { SyncSettings } from './SyncSettings';
import { SyncActivity } from './SyncActivity';
import { ConnectionWizard } from './ConnectionWizard';
import { MappingHealth } from './MappingHealth';

// ConnectionStatus enum: 0 Disabled, 1 Pending, 2 Healthy, 3 Degraded, 4 Failed
const STATUS_LABEL: Record<number, string> = { 0: 'Disabled', 1: 'Pending', 2: 'Healthy', 3: 'Degraded', 4: 'Failed' };

const TONE = {
  quiet: { dot: 'bg-slate-400', text: 'text-[var(--muted)]' },
  good: { dot: 'bg-emerald-500', text: 'text-emerald-600 dark:text-emerald-400' },
  busy: { dot: 'bg-sky-500', text: 'text-sky-600 dark:text-sky-400' },
  warn: { dot: 'bg-amber-500', text: 'text-amber-600 dark:text-amber-400' },
  bad: { dot: 'bg-rose-500', text: 'text-rose-600 dark:text-rose-400' },
} as const;

// ConnectionState enum. The server works it out from everything it knows about the connection
// (set up or not, switched on, paused, rejected by the PSA, syncing now), so this page never has
// to guess it from the parts.
const SETUP = 0;
const AUTH_REQUIRED = 5;
const STATE: Record<number, { label: string; tone: keyof typeof TONE }> = {
  0: { label: 'Setup', tone: 'warn' },
  1: { label: 'Connected', tone: 'good' },
  2: { label: 'Syncing', tone: 'busy' },
  3: { label: 'Healthy', tone: 'good' },
  4: { label: 'Degraded', tone: 'warn' },
  5: { label: 'Credentials rejected', tone: 'bad' },
  6: { label: 'Sync paused', tone: 'quiet' },
  7: { label: 'Error', tone: 'bad' },
  8: { label: 'Disabled', tone: 'quiet' },
  9: { label: 'Archived', tone: 'quiet' },
};

type LifecycleAction = 'pause' | 'resume' | 'enable' | 'disable' | 'archive' | 'restore';
const LIFECYCLE_DONE: Record<LifecycleAction, string> = {
  pause: 'Sync paused. Nothing is read from the PSA until it is resumed.',
  resume: 'Sync resumed. It carries on from where it stopped.',
  enable: 'Enabled.',
  disable: 'Disabled.',
  archive: 'Archived.',
  restore: 'Restored, and still switched off. Switch it on when it should work again.',
};

export default function ConnectionsPage() {
  const qc = useQueryClient();
  const { data, isError } = useQuery({ queryKey: ['connections'], queryFn: api.connections });

  // What can be connected comes from the server: each connector says what it needs, so a field
  // added to one appears here without this page changing. The PSAs with no connector yet are
  // named too, and cannot be chosen.
  const catalog = useQuery({ queryKey: ['connection-providers'], queryFn: api.connectionProviders, retry: false, staleTime: 10 * 60_000 });
  const archived = useQuery({ queryKey: ['connections', 'archived'], queryFn: api.archivedConnections, retry: false });
  const available = (catalog.data ?? []).filter((p) => p.available);
  const planned = (catalog.data ?? []).filter((p) => !p.available);

  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<ConnectionSummary | null>(null);
  const editingId = editing?.id ?? null;
  const [chosenProvider, setChosenProvider] = useState<number | null>(null);
  const provider = chosenProvider ?? (available[0] ? Number(available[0].provider) : null);
  const [form, setForm] = useState<Record<string, string>>({ name: '', apiEndpoint: '', tenantIdentifier: '', logoUrl: '' });
  // Which credential FIELDS hold a stored value for the connection being edited (names only — the
  // values are write-only). null = unknown, [] = the server said nothing is stored.
  const [storedKeys, setStoredKeys] = useState<string[] | null>(null);
  const providerDef = available.find((p) => Number(p.provider) === provider) ?? null;
  const credFields = providerDef?.credentials ?? [];
  const credKeys = credFields.map((f) => f.key);

  // Adding a connection, or finishing one left in setup, goes through the wizard. The form below
  // is for changing a connection that exists.
  const [wizard, setWizard] = useState<{ resume: ConnectionSummary | null } | null>(null);
  function openAdd() {
    setOpen(false);
    setWizard({ resume: null });
  }
  function openEdit(c: ConnectionSummary) {
    setWizard(null);
    setEditing(c);
    setChosenProvider(Number(c.provider));
    setForm({ name: c.name, apiEndpoint: c.apiEndpoint, tenantIdentifier: c.tenantIdentifier ?? '', logoUrl: c.logoUrl ?? '' });
    setStoredKeys(c.storedCredentialKeys);
    setOpen(true);
  }

  const save = useMutation({
    mutationFn: async () => {
      if (!providerDef || !editing) throw new Error('Open a connection to change it.');
      const entered = Object.fromEntries(credKeys.map((k) => [k, form[k] ?? '']).filter(([, v]) => v !== ''));
      const credentialsChanged = Object.keys(entered).length > 0;
      {
        const updated = await api.updateConnection(editing.id, {
          name: form.name,
          apiEndpoint: form.apiEndpoint,
          tenantIdentifier: form.tenantIdentifier || undefined,
          // Saving its details is not a decision about whether it is switched on. This sent true
          // every time, so editing the name of a disabled connection switched it on.
          isEnabled: editing.isEnabled,
          logoUrl: form.logoUrl,
          credentials: credentialsChanged ? entered : undefined,
        });
        return { id: updated.id, inSetup: Number(updated.state) === SETUP, credentialsChanged };
      }
    },
    onSuccess: ({ id, inSetup, credentialsChanged }) => {
      setOpen(false);
      qc.invalidateQueries({ queryKey: ['connections'] });
      // A connection that has never been live is saved switched off. It is tested straight away
      // and switched on only if the PSA accepts it; if not, it stays in setup with the reason on
      // its card.
      if (inSetup) switchOn.mutate(id);
      // New credentials on a live connection were tried by the server before it kept them (a
      // refusal leaves this form open with the reason). The test here puts the result on the card.
      else if (credentialsChanged) test.mutate(id);
    },
  });

  const [results, setResults] = useState<Record<string, { ok: boolean; msg: string }>>({});
  const test = useMutation({
    mutationFn: (id: string) => api.testConnection(id),
    onSuccess: (r, id) => {
      setResults((m) => ({ ...m, [id]: { ok: r.success, msg: r.success ? `Healthy · ${Math.round(r.latencyMs)}ms` : r.message ?? 'Failed' } }));
      qc.invalidateQueries({ queryKey: ['connections'] });
    },
    onError: (_e, id) => setResults((m) => ({ ...m, [id]: { ok: false, msg: 'Request failed' } })),
  });

  // Test, and switch on if the test passes. The server refuses to switch on a connection the PSA
  // has not accepted, so there is no order of clicks that gets round the test.
  const switchOn = useMutation({
    mutationFn: async (id: string) => {
      const tested = await api.testConnection(id);
      if (!tested.success) return { on: false as const, why: tested.message ?? 'The PSA did not accept the connection.' };
      await api.activateConnection(id);
      return { on: true as const, ms: tested.latencyMs };
    },
    onSuccess: (r, id) => {
      setResults((m) => ({
        ...m,
        [id]: r.on
          ? { ok: true, msg: `Tested and switched on · ${Math.round(r.ms)}ms. The first sync runs on the next cycle, or press Sync now.` }
          : { ok: false, msg: `Not switched on — ${r.why} Choose Edit to correct the details, then test again.` },
      }));
      qc.invalidateQueries({ queryKey: ['connections'] });
    },
    onError: (e, id) => {
      setResults((m) => ({ ...m, [id]: { ok: false, msg: e instanceof Error ? `Not switched on — ${e.message}` : 'Not switched on — the request failed.' } }));
      qc.invalidateQueries({ queryKey: ['connections'] });
    },
  });

  const lifecycle = useMutation({
    mutationFn: async (v: { id: string; action: LifecycleAction }) => {
      switch (v.action) {
        case 'pause': await api.pauseConnectionSync(v.id); break;
        case 'resume': await api.resumeConnectionSync(v.id); break;
        case 'enable': await api.setConnectionEnabled(v.id, true); break;
        case 'disable': await api.setConnectionEnabled(v.id, false); break;
        case 'archive': await api.archiveConnection(v.id); break;
        case 'restore': await api.restoreConnection(v.id); break;
      }
    },
    onSuccess: (_r, v) => {
      setResults((m) => ({ ...m, [v.id]: { ok: true, msg: LIFECYCLE_DONE[v.action] } }));
      ['connections', 'health', 'connection-sync-state'].forEach((k) => qc.invalidateQueries({ queryKey: [k] }));
      // Switched back on: find out now whether the PSA still accepts it, rather than at the next sync.
      if (v.action === 'enable') test.mutate(v.id);
    },
    onError: (e, v) => setResults((m) => ({
      ...m, [v.id]: { ok: false, msg: e instanceof Error ? e.message : 'That could not be done.' },
    })),
  });
  const sync = useMutation({
    mutationFn: (v: { id: string; full: boolean }) => api.syncConnection(v.id, v.full),
    onSuccess: (r, v) => {
      const id = v.id;
      const parts = [`${v.full ? 'Re-synced all' : 'Synced'} · ${r.created} new, ${r.updated} updated (${r.fetched} fetched)`];
      const failed = r.failed ?? 0;
      const recovered = r.recovered ?? 0;
      if (r.moreToRead) parts.push('more to read — the sync carries on by itself');
      if (failed > 0) parts.push(`${failed} ${failed === 1 ? 'record' : 'records'} could not be read and will be tried again`);
      if (recovered > 0) parts.push(`${recovered} earlier ${recovered === 1 ? 'failure' : 'failures'} went through`);
      setResults((m) => ({ ...m, [id]: { ok: failed === 0, msg: parts.join(' · ') } }));
      // Refresh every page that reads synced data.
      ['connections', 'tickets', 'team', 'trend', 'health', 'notifications', 'audit', 'jobs'].forEach((k) =>
        qc.invalidateQueries({ queryKey: [k] }));
    },
    onError: (e, v) => setResults((m) => ({
      ...m,
      // 409: a run already has this connection. That is not a failure, and pressing again will
      // not start a second one - it says so instead of "Sync failed".
      [v.id]: e instanceof ApiError && e.status === 409
        ? { ok: true, msg: e.message }
        : { ok: false, msg: e instanceof Error ? `Sync failed: ${e.message}` : 'Sync failed' },
    })),
  });

  const refreshFields = useMutation({
    mutationFn: (id: string) => api.refreshConnectionFields(id),
    onSuccess: (f, id) => {
      setFieldsById((m) => ({ ...m, [id]: f }));
      setExpandedId(id);
      setResults((m) => ({ ...m, [id]: { ok: true, msg: `Fields refreshed · ${f.statuses.length} statuses, ${f.workTypes.length} work types` } }));
      qc.invalidateQueries({ queryKey: ['connections'] });
    },
    onError: (_e, id) => setResults((m) => ({ ...m, [id]: { ok: false, msg: 'Field refresh failed' } })),
  });

  const [settingsId, setSettingsId] = useState<string | null>(null);
  const [activityId, setActivityId] = useState<string | null>(null);
  const [mappingId, setMappingId] = useState<string | null>(null);
  const [manageId, setManageId] = useState<string | null>(null);

  // Live field discovery (boards/queues, statuses, priorities, categories)
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [fieldsById, setFieldsById] = useState<Record<string, ConnectionFields | 'loading' | 'error'>>({});
  async function toggleFields(id: string) {
    if (expandedId === id) { setExpandedId(null); return; }
    setExpandedId(id);
    if (!fieldsById[id] || fieldsById[id] === 'error') {
      setFieldsById((m) => ({ ...m, [id]: 'loading' }));
      try {
        const discovered = await api.connectionFields(id);
        setFieldsById((m) => ({ ...m, [id]: discovered }));
      } catch {
        setFieldsById((m) => ({ ...m, [id]: 'error' }));
      }
    }
  }

  const isEdit = editing !== null;

  // Blank means "keep the stored key" — so when a save would rotate credentials (anything typed),
  // every field must be either typed or already stored. Otherwise a partial entry saves fine and
  // then fails at sync time with "credential missing from the secret store".
  const isStored = (key: string) => isEdit && (storedKeys?.includes(key) ?? true);
  const enteredAny = credKeys.some((k) => (form[k] ?? '').trim() !== '');
  const missingCreds = isEdit && enteredAny
    ? credFields.filter((f) => !(form[f.key] ?? '').trim() && !isStored(f.key)).map((f) => f.label)
    : [];
  const nothingStored = isEdit && storedKeys !== null && storedKeys.length === 0;

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">PSA Connections</h1>
          <p className="text-sm text-[var(--muted)]">Connect, manage and synchronize your PSA platforms.</p>
        </div>
        <button onClick={openAdd} className="inline-flex items-center gap-2 rounded-lg bg-brand px-3.5 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
          <Plus size={16} /> Add connection
        </button>
      </div>

      {open && (
        <form
          onSubmit={(e) => { e.preventDefault(); save.mutate(); }}
          className="space-y-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5"
        >
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold">{isEdit ? 'Edit connection' : 'New connection'}</h2>
          </div>
          {nothingStored ? (
            <div className="flex items-center gap-2 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700 dark:bg-amber-950/50 dark:text-amber-300">
              <ShieldCheck size={14} /> No credentials are currently stored for this connection — fill in every credential field.
            </div>
          ) : (
            <div className="flex items-center gap-2 rounded-lg bg-[var(--bg)] px-3 py-2 text-xs text-[var(--muted)]">
              <ShieldCheck size={14} /> Credentials are stored in your secret vault — never in the database or shown again.
              {isEdit && ' Leave the credential fields blank to keep the existing keys.'}
            </div>
          )}
          <div className="grid gap-3 sm:grid-cols-2">
            <Input label="Name" value={form.name} onChange={(v) => setForm({ ...form, name: v })} />
            <label className="block">
              <span className="mb-1.5 block text-sm font-medium">Provider</span>
              <select
                value={provider ?? ''}
                disabled={isEdit || available.length === 0}
                onChange={(e) => setChosenProvider(Number(e.target.value))}
                className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm disabled:opacity-60"
              >
                {available.map((p) => <option key={String(p.provider)} value={Number(p.provider)}>{p.name}</option>)}
                {/* A connection to a PSA this build has no connector for: named, so the box is not blank. */}
                {isEdit && !providerDef && provider !== null && <option value={provider}>No connector in this version</option>}
                {/* Named, and not selectable: there is no connector behind them yet. */}
                {!isEdit && planned.length > 0 && (
                  <optgroup label="Coming soon">
                    {planned.map((p) => <option key={String(p.provider)} value={Number(p.provider)} disabled>{p.name}</option>)}
                  </optgroup>
                )}
              </select>
              {catalog.isLoading && <span className="mt-1 block text-xs text-[var(--muted)]">Loading the PSAs that can be connected…</span>}
              {catalog.isError && (
                <span className="mt-1 block text-xs text-rose-600 dark:text-rose-400">
                  The list of PSAs could not be loaded{catalog.error instanceof Error ? ` (${catalog.error.message})` : ''}. Reload the page to try again.
                </span>
              )}
            </label>
            {isEdit && (
              <div className="sm:col-span-2">
                <span className="mb-1.5 block text-sm font-medium">Logo</span>
                <LogoPicker
                  connectionId={editingId!}
                  current={form.logoUrl}
                  onChanged={(url) => { setForm((f) => ({ ...f, logoUrl: url })); qc.invalidateQueries({ queryKey: ['connections'] }); }}
                />
              </div>
            )}
            <Input label="API endpoint" value={form.apiEndpoint} onChange={(v) => setForm({ ...form, apiEndpoint: v })}
              placeholder={providerDef?.endpointExample ?? ''}
              hint={providerDef?.endpointHint ?? undefined} />
            <Input label={`${providerDef?.tenantIdentifierLabel ?? 'Tenant identifier'} (optional)`} value={form.tenantIdentifier} onChange={(v) => setForm({ ...form, tenantIdentifier: v })}
              hint="A label for your own reference — not required, and not sent to the PSA." />
          </div>
          <div className="grid gap-3 sm:grid-cols-2">
            {credFields.map((f) => {
              // stored===null means an older response didn't say — keep the historical wording.
              const stored = !isEdit ? null : storedKeys === null ? null : storedKeys.includes(f.key);
              return (
                <Input key={f.key} label={f.label} type={f.secret ? 'password' : 'text'}
                  placeholder={!isEdit ? '' : stored === false ? 'nothing stored — enter a value' : 'unchanged'}
                  badge={stored === null ? undefined : stored
                    ? { label: 'Stored', tone: 'ok' }
                    : { label: 'Nothing stored', tone: 'warn' }}
                  hint={f.hint ?? undefined}
                  value={form[f.key] ?? ''} onChange={(v) => setForm({ ...form, [f.key]: v })} />
              );
            })}
          </div>
          {missingCreds.length > 0 && (
            <p className="text-xs text-amber-700 dark:text-amber-300">
              Entering credentials replaces the stored set — also fill in {missingCreds.join(', ')} (nothing is stored for {missingCreds.length === 1 ? 'it' : 'them'}).
            </p>
          )}
          <div className="flex flex-wrap items-center justify-end gap-2">
            {!isEdit && (
              <span className="mr-auto text-xs text-[var(--muted)]">
                It is tested as soon as it is saved, and switched on only if the PSA accepts it.
              </span>
            )}
            <button type="button" onClick={() => setOpen(false)} className="rounded-lg border border-[var(--border)] px-3.5 py-2 text-sm">Cancel</button>
            <button type="submit" disabled={save.isPending || missingCreds.length > 0 || !providerDef} className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg disabled:opacity-50">
              {save.isPending ? 'Saving…' : isEdit ? 'Save changes' : 'Save and test'}
            </button>
          </div>
          {save.isError && (
            <p className="text-sm text-red-500">
              {save.error instanceof Error ? save.error.message : 'Could not save the connection.'}
            </p>
          )}
        </form>
      )}

      {wizard && (catalog.data ? (
        <ConnectionWizard
          key={wizard.resume?.id ?? 'new'}
          providers={catalog.data}
          resume={wizard.resume}
          onClose={() => setWizard(null)}
          onEnabled={(id) => {
            setWizard(null);
            setResults((m) => ({ ...m, [id]: { ok: true, msg: 'Switched on. The first sync runs on the next cycle, or press Sync now.' } }));
          }}
        />
      ) : (
        <p className={'rounded-xl border border-[var(--border)] bg-[var(--surface)] px-5 py-4 text-sm ' + (catalog.isError ? 'text-rose-600 dark:text-rose-400' : 'text-[var(--muted)]')}>
          {catalog.isError
            ? `The list of PSAs could not be loaded${catalog.error instanceof Error ? ` (${catalog.error.message})` : ''}. Reload the page to try again.`
            : 'Loading the PSAs that can be connected…'}
        </p>
      ))}

      {(isError || (data && data.length === 0)) && !open && !wizard && (
        <div className="flex flex-col items-center rounded-xl border border-dashed border-[var(--border)] px-6 py-12 text-center">
          <Plug className="mb-3 text-[var(--faint)]" size={26} />
          <p className="text-sm text-[var(--muted)]">No connections yet. Add one to start syncing tickets.</p>
        </div>
      )}

      {data && data.length > 0 && (
        <div className="space-y-4">
          {data.map((c) => (
            <ConnectionCard
              key={c.id}
              c={c}
              // Initials, not a vendor's logo - the portal ships no brand asset it has no licence
              // for. They come with the catalog, so a new connector needs no entry on this page.
              mark={(catalog.data ?? []).find((p) => Number(p.provider) === Number(c.provider))?.mark || '?'}
              result={results[c.id]}
              expanded={expandedId === c.id}
              fields={fieldsById[c.id]}
              onTest={() => test.mutate(c.id)}
              testing={test.isPending && test.variables === c.id}
              onContinueSetup={() => { setOpen(false); setWizard({ resume: c }); }}
              onSwitchOn={() => switchOn.mutate(c.id)}
              switchingOn={switchOn.isPending && switchOn.variables === c.id}
              onSync={() => sync.mutate({ id: c.id, full: false })}
              onResyncAll={() => sync.mutate({ id: c.id, full: true })}
              syncing={sync.isPending && sync.variables?.id === c.id}
              settingsOpen={settingsId === c.id}
              onToggleSettings={() => setSettingsId(settingsId === c.id ? null : c.id)}
              mappingOpen={mappingId === c.id}
              onToggleMapping={() => setMappingId(mappingId === c.id ? null : c.id)}
              activityOpen={activityId === c.id}
              onToggleActivity={() => setActivityId(activityId === c.id ? null : c.id)}
              manageOpen={manageId === c.id}
              onToggleManage={() => setManageId(manageId === c.id ? null : c.id)}
              onLifecycle={(action) => lifecycle.mutate({ id: c.id, action })}
              lifecycleBusy={lifecycle.isPending && lifecycle.variables?.id === c.id}
              onRefreshFields={() => refreshFields.mutate(c.id)}
              refreshingFields={refreshFields.isPending && refreshFields.variables === c.id}
              onEdit={() => openEdit(c)}
              onToggleFields={() => toggleFields(c.id)}
            />
          ))}
        </div>
      )}
      {/* Put away, not gone. They are in no other list, so this is the only way back to one. */}
      {archived.data && archived.data.length > 0 && (
        <details className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <summary className="cursor-pointer px-5 py-3 text-sm font-medium">
            Archived connections <span className="font-normal text-[var(--muted)]">({archived.data.length})</span>
          </summary>
          <ul className="divide-y divide-[var(--border)] border-t border-[var(--border)]">
            {archived.data.map((c) => (
              <li key={c.id} className="flex flex-wrap items-center gap-3 px-5 py-3">
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium">{c.name}</p>
                  <p className="truncate text-xs text-[var(--muted)]">
                    {c.apiEndpoint} · {c.ticketCount.toLocaleString()} {c.ticketCount === 1 ? 'ticket' : 'tickets'} kept
                  </p>
                  {results[c.id] && !results[c.id].ok && (
                    <p className="mt-1 text-xs text-rose-600 dark:text-rose-400">{results[c.id].msg}</p>
                  )}
                </div>
                <ActionButton
                  onClick={() => lifecycle.mutate({ id: c.id, action: 'restore' })}
                  disabled={lifecycle.isPending && lifecycle.variables?.id === c.id}
                >
                  Restore
                </ActionButton>
              </li>
            ))}
          </ul>
        </details>
      )}
    </div>
  );
}


const HEALTH: Record<number, { dot: string; text: string }> = {
  0: { dot: 'bg-slate-400', text: 'text-[var(--muted)]' },
  1: { dot: 'bg-amber-500', text: 'text-amber-600 dark:text-amber-400' },
  2: { dot: 'bg-emerald-500', text: 'text-emerald-600 dark:text-emerald-400' },
  3: { dot: 'bg-amber-500', text: 'text-amber-600 dark:text-amber-400' },
  4: { dot: 'bg-rose-500', text: 'text-rose-600 dark:text-rose-400' },
};

function ago(iso: string | null) {
  if (!iso) return 'never';
  const mins = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
  if (mins < 1) return 'just now';
  if (mins < 60) return mins + ' min ago';
  const hrs = Math.round(mins / 60);
  if (hrs < 24) return hrs + ' h ago';
  return Math.round(hrs / 24) + ' d ago';
}

function Stat({ icon: Icon, label, value, tint }: { icon: LucideIcon; label: string; value: string; tint: string }) {
  return (
    <div className="flex items-center gap-2.5">
      <span className={'flex h-8 w-8 shrink-0 items-center justify-center rounded-lg ' + tint}>
        <Icon size={15} aria-hidden="true" />
      </span>
      <span className="min-w-0">
        <span className="block text-[11px] text-[var(--muted)]">{label}</span>
        <span className="block truncate text-sm font-semibold tabular-nums">{value}</span>
      </span>
    </div>
  );
}

/** What a state means for the person looking at it, where the one word is not enough. */
function stateNote(c: ConnectionSummary, state: number | null): string | null {
  switch (state) {
    case SETUP:
      return 'Not switched on yet. Continue setup to test it, choose what it brings in and switch it on.';
    case 1:
      return 'The PSA accepted the credentials. Nothing has been synced yet.';
    case AUTH_REQUIRED:
      return 'The PSA rejected the stored credentials. Automatic sync has stopped so that the API account is not locked out. Choose Edit and enter credentials the PSA accepts.';
    case 6:
      return `Sync ${c.syncPausedAt ? `was paused ${ago(c.syncPausedAt)}` : 'is paused'}. Nothing is read from the PSA; replies, time and status changes made here still go to it.`;
    case 8:
      return 'Switched off. Nothing is read from the PSA, and changes made here cannot be sent to it.';
    default:
      return null;
  }
}

function ManageRow({ text, action, onClick, disabled }: { text: string; action: string; onClick: () => void; disabled?: boolean }) {
  return (
    <div className="flex flex-wrap items-center gap-3">
      <p className="min-w-0 flex-1 text-sm text-[var(--muted)]">{text}</p>
      <ActionButton onClick={onClick} disabled={disabled}>{action}</ActionButton>
    </div>
  );
}

function ConnectionCard({
  c, mark, result, expanded, fields, onTest, testing, onContinueSetup, onSwitchOn, switchingOn, onSync, onResyncAll, syncing,
  settingsOpen, onToggleSettings, mappingOpen, onToggleMapping, activityOpen, onToggleActivity, manageOpen, onToggleManage, onLifecycle, lifecycleBusy,
  onRefreshFields, refreshingFields, onEdit, onToggleFields,
}: {
  c: ConnectionSummary;
  mark: string;
  result?: { ok: boolean; msg: string };
  expanded: boolean;
  fields?: ConnectionFields | 'loading' | 'error';
  onTest: () => void;
  testing: boolean;
  onContinueSetup: () => void;
  onSwitchOn: () => void;
  switchingOn: boolean;
  onSync: () => void;
  onResyncAll: () => void;
  syncing: boolean;
  settingsOpen: boolean;
  onToggleSettings: () => void;
  mappingOpen: boolean;
  onToggleMapping: () => void;
  activityOpen: boolean;
  onToggleActivity: () => void;
  manageOpen: boolean;
  onToggleManage: () => void;
  onLifecycle: (action: LifecycleAction) => void;
  lifecycleBusy: boolean;
  onRefreshFields: () => void;
  refreshingFields: boolean;
  onEdit: () => void;
  onToggleFields: () => void;
}) {
  const status = Number(c.status);
  // An older response carries no state; the health status is the nearest thing to it.
  const state = c.state === null ? null : Number(c.state);
  const known = state !== null ? STATE[state] : undefined;
  const view = known
    ? { label: known.label, ...TONE[known.tone] }
    : { label: STATUS_LABEL[status] ?? String(c.status), ...(HEALTH[status] ?? HEALTH[1]) };
  const inSetup = state === SETUP;
  const paused = c.syncPausedAt !== null;
  const note = stateNote(c, state);
  // Reading from the PSA needs the connection switched on and not paused. The server says the
  // same; saying it on the button saves a click that can only be refused.
  const whyNoSync = !c.isEnabled ? 'Enable the connection first' : paused ? 'Resume sync first' : undefined;

  return (
    <div className="overflow-hidden rounded-2xl border border-[var(--border)] bg-[var(--surface)]">
      <div className="flex flex-wrap items-start gap-4 p-5">
        <span className="flex h-14 w-14 shrink-0 items-center justify-center overflow-hidden rounded-xl border border-[var(--border)] bg-white">
          {c.logoUrl ? (
            // A logo the admin supplied. object-contain keeps a wordmark from being cropped, and a
            // broken URL falls back to the initials rather than leaving an empty tile.
            /* eslint-disable-next-line @next/next/no-img-element */
            <img
              src={c.logoUrl}
              alt=""
              className="h-full w-full object-contain p-1.5"
              onError={(e) => { e.currentTarget.style.display = 'none'; e.currentTarget.nextElementSibling?.classList.remove('hidden'); }}
            />
          ) : null}
          <span className={`text-base font-bold tracking-wide text-brand ${c.logoUrl ? 'hidden' : ''}`} aria-hidden="true">
            {mark}
          </span>
        </span>

        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="text-[17px] font-semibold">{c.name}</h2>
            {state === null && (
              <span
                className={
                  'rounded-full px-2 py-0.5 text-[11px] font-medium ' +
                  (c.isEnabled
                    ? 'bg-brand/10 text-brand dark:bg-brand/25 dark:text-brand-soft'
                    : 'bg-[var(--bg)] text-[var(--muted)]')
                }
              >
                {c.isEnabled ? 'Enabled' : 'Disabled'}
              </span>
            )}
          </div>
          <p className="mt-1 flex items-center gap-1.5 text-[13px] text-[var(--muted)]">
            <Globe size={12} aria-hidden="true" />
            <span className="truncate">{c.apiEndpoint}</span>
            <button
              onClick={() => navigator.clipboard?.writeText(c.apiEndpoint)}
              aria-label="Copy endpoint"
              className="shrink-0 rounded p-0.5 hover:bg-[var(--bg)]"
            >
              <Copy size={12} />
            </button>
          </p>
        </div>

        <div className="text-right">
          <p className={'flex items-center justify-end gap-1.5 text-sm font-medium ' + view.text}>
            <span className={'h-2 w-2 rounded-full ' + view.dot} aria-hidden="true" />
            {view.label}
          </p>
          <p className="mt-0.5 text-[12px] text-[var(--muted)]">Last checked {ago(c.lastHealthCheckAt)}</p>
        </div>

        {/* A row of its own, at every width: its basis is the whole row, so it cannot sit beside the
            name and squeeze it (on a phone, into a column a word wide). The line length is the span's. */}
        {note && (
          <p className={'basis-full text-xs ' + (state === AUTH_REQUIRED ? 'text-rose-600 dark:text-rose-400' : 'text-[var(--muted)]')}>
            <span className="block max-w-3xl">{note}</span>
          </p>
        )}
      </div>

      {/* Counts are what has actually synced into the portal, not the PSA's own totals. */}
      <div className="grid gap-4 border-t border-[var(--border)] px-5 py-4 sm:grid-cols-2 lg:grid-cols-4">
        <Stat icon={Ticket} label="Tickets" value={c.ticketCount.toLocaleString()} tint="bg-brand/10 text-brand dark:bg-brand/25 dark:text-brand-soft" />
        <Stat icon={Users} label="Customers" value={c.customerCount.toLocaleString()} tint="bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300" />
        <Stat icon={Contact} label="Contacts" value={c.contactCount.toLocaleString()} tint="bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300" />
        <Stat icon={RefreshCw} label="Last sync" value={ago(c.lastSuccessfulSyncAt)} tint="bg-violet-100 text-violet-700 dark:bg-violet-950 dark:text-violet-300" />
      </div>

      {(c.lastError || result) && (
        <div className="border-t border-[var(--border)] px-5 py-3">
          {result ? (
            <p className={'text-xs ' + (result.ok ? 'text-emerald-600 dark:text-emerald-400' : 'text-rose-600 dark:text-rose-400')}>
              {result.msg}
            </p>
          ) : (
            <p className="text-xs text-rose-600 dark:text-rose-400">{c.lastError}</p>
          )}
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2 border-t border-[var(--border)] bg-[var(--bg)] px-5 py-3">
        {inSetup ? (
          <>
            {/* The whole of it, a step at a time: test, what the PSA offers, mapping, scope, preview. */}
            <button
              onClick={onContinueSetup}
              className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90"
            >
              Continue setup
            </button>
            {/* The short way, for someone who knows the defaults are what they want. */}
            <ActionButton onClick={onSwitchOn} disabled={switchingOn}>
              <Activity size={13} /> {switchingOn ? 'Testing…' : 'Test and switch on'}
            </ActionButton>
          </>
        ) : (
          <>
            <ActionButton onClick={onTest} disabled={testing}>
              <Activity size={13} /> {testing ? 'Testing…' : 'Test connection'}
            </ActionButton>
            <button
              onClick={onSync}
              disabled={syncing || whyNoSync !== undefined}
              title={whyNoSync}
              className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-50"
            >
              <RefreshCw size={13} className={syncing ? 'animate-spin' : undefined} /> {syncing ? 'Syncing…' : 'Sync now'}
            </button>
          </>
        )}
        <span className="flex-1" />
        <ActionButton onClick={onEdit}>Edit</ActionButton>
        {!inSetup && (
          <>
            <ActionButton onClick={onResyncAll} disabled={syncing || whyNoSync !== undefined}>Re-sync all</ActionButton>
            <ActionButton onClick={onRefreshFields} disabled={refreshingFields || !c.isEnabled}>
              {refreshingFields ? 'Refreshing…' : 'Refresh fields'}
            </ActionButton>
            <ActionButton onClick={onToggleFields}>
              <ChevronDown size={13} className={expanded ? 'rotate-180 transition-transform' : 'transition-transform'} /> Boards
            </ActionButton>
          </>
        )}
        <ActionButton onClick={onToggleSettings}>
          <ChevronDown size={13} className={settingsOpen ? 'rotate-180 transition-transform' : 'transition-transform'} /> Sync settings
        </ActionButton>
        {!inSetup && (
          <ActionButton onClick={onToggleMapping}>
            <ChevronDown size={13} className={mappingOpen ? 'rotate-180 transition-transform' : 'transition-transform'} /> Mapping
          </ActionButton>
        )}
        <ActionButton onClick={onToggleActivity}>
          <ChevronDown size={13} className={activityOpen ? 'rotate-180 transition-transform' : 'transition-transform'} /> Sync activity
        </ActionButton>
        <ActionButton onClick={onToggleManage}>
          <ChevronDown size={13} className={manageOpen ? 'rotate-180 transition-transform' : 'transition-transform'} /> Manage
        </ActionButton>
      </div>

      {settingsOpen && (
        <div className="border-t border-[var(--border)] px-5 py-4">
          <SyncSettings connectionId={c.id} provider={Number(c.provider)} />
        </div>
      )}

      {mappingOpen && !inSetup && (
        <div className="border-t border-[var(--border)] px-5 py-4">
          <MappingHealth connectionId={c.id} />
        </div>
      )}

      {activityOpen && (
        <div className="border-t border-[var(--border)] px-5 py-4">
          <SyncActivity connectionId={c.id} />
        </div>
      )}

      {manageOpen && (
        <div className="space-y-3 border-t border-[var(--border)] px-5 py-4">
          {!inSetup && c.isEnabled && (paused ? (
            <ManageRow
              text="Sync is paused. Resuming carries on from where it stopped, so what changed in the PSA meanwhile is read."
              action="Resume sync" onClick={() => onLifecycle('resume')} disabled={lifecycleBusy} />
          ) : (
            <ManageRow
              text="Stop reading from the PSA for now. Replies, time and status changes made here still go to it."
              action="Pause sync" onClick={() => onLifecycle('pause')} disabled={lifecycleBusy} />
          ))}
          {!inSetup && (c.isEnabled ? (
            <ManageRow
              text="Switch the connection off. Nothing is read from the PSA, and changes made here cannot be sent to it, until it is enabled again."
              action="Disable" onClick={() => onLifecycle('disable')} disabled={lifecycleBusy} />
          ) : (
            <ManageRow
              text="Switch the connection back on. It is tested straight away."
              action="Enable" onClick={() => onLifecycle('enable')} disabled={lifecycleBusy} />
          ))}
          <ManageRow
            text="Take it off this list and stop it working. Every ticket, client and mapping it brought in stays, and it can be restored."
            action="Archive"
            onClick={() => {
              if (window.confirm(`Archive "${c.name}"? It stops syncing and leaves this list. What it imported stays, and it can be restored.`))
                onLifecycle('archive');
            }}
            disabled={lifecycleBusy} />
        </div>
      )}
      {expanded && (
        <div className="border-t border-[var(--border)] px-5 py-4">
          {fields === 'loading' && <p className="text-sm text-[var(--muted)]">Discovering fields from the PSA…</p>}
          {fields === 'error' && <p className="text-sm text-rose-500">Couldn&apos;t load fields (connection may be unreachable).</p>}
          {fields && fields !== 'loading' && fields !== 'error' && (
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
              <FieldList title="Service boards / queues" items={fields.queuesOrBoards} />
              <FieldList title="Statuses" items={fields.statuses} />
              <FieldList title="Priorities" items={fields.priorities} />
              <FieldList title="Categories" items={fields.categories} />
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Logo upload, in the shape people already know from a profile picture: current image, a button
 * that opens the file dialog, and a way to remove it.
 *
 * Only offered when editing, because the upload needs a connection to attach to — asking for a
 * logo before the connection exists would mean holding a file in limbo through a failed save.
 */
function LogoPicker({
  connectionId, current, onChanged,
}: { connectionId: string; current: string; onChanged: (url: string) => void }) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function pick(file: File | undefined) {
    if (!file) return;
    setError(null);
    setBusy(true);
    try {
      const updated = await api.uploadConnectionLogo(connectionId, file);
      onChanged(updated.logoUrl ?? '');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not upload the logo.');
    } finally {
      setBusy(false);
      if (inputRef.current) inputRef.current.value = ''; // let the same file be chosen again
    }
  }

  async function clear() {
    setBusy(true);
    try {
      await api.removeConnectionLogo(connectionId);
      onChanged('');
    } catch {
      setError('Could not remove the logo.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <div className="flex flex-wrap items-center gap-3">
        <span className="flex h-16 w-16 shrink-0 items-center justify-center overflow-hidden rounded-xl border border-[var(--border)] bg-white">
          {current ? (
            /* eslint-disable-next-line @next/next/no-img-element */
            <img src={current} alt="Current logo" className="h-full w-full object-contain p-1.5" />
          ) : (
            <ImageIcon size={20} className="text-[var(--faint)]" aria-hidden="true" />
          )}
        </span>

        <input
          ref={inputRef}
          type="file"
          accept="image/png,image/jpeg,image/webp,image/gif"
          className="sr-only"
          onChange={(e) => pick(e.target.files?.[0])}
        />
        <button
          type="button"
          disabled={busy}
          onClick={() => inputRef.current?.click()}
          className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-50"
        >
          <Upload size={14} /> {busy ? 'Uploading…' : current ? 'Change logo' : 'Upload logo'}
        </button>
        {current && (
          <button
            type="button"
            disabled={busy}
            onClick={clear}
            className="text-sm text-[var(--muted)] hover:text-rose-600 disabled:opacity-50"
          >
            Remove
          </button>
        )}
      </div>
      <p className="mt-2 text-xs text-[var(--muted)]">
        PNG, JPEG, WebP or GIF, up to 1 MB. A square mark suits the tile best. SVG is not accepted —
        it can carry script, and a logo never needs it.
      </p>
      {error && <p className="mt-1.5 text-xs text-rose-600 dark:text-rose-400">{error}</p>}
    </div>
  );
}

function ActionButton({ children, onClick, disabled }: { children: React.ReactNode; onClick: () => void; disabled?: boolean }) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--surface)] disabled:opacity-50"
    >
      {children}
    </button>
  );
}

function FieldList({ title, items }: { title: string; items: { value: string; label: string }[] }) {
  return (
    <div>
      <div className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">
        {title} <span className="text-[var(--muted)]">({items.length})</span>
      </div>
      {items.length === 0 ? (
        <p className="text-xs text-[var(--muted)]">—</p>
      ) : (
        <ul className="space-y-1">
          {items.slice(0, 12).map((o) => (
            <li key={o.value} className="text-sm">{o.label}</li>
          ))}
          {items.length > 12 && <li className="text-xs text-[var(--muted)]">+{items.length - 12} more</li>}
        </ul>
      )}
    </div>
  );
}

function Input({ label, value, onChange, type = 'text', placeholder, hint, badge }: {
  label: string; value: string; onChange: (v: string) => void; type?: string; placeholder?: string; hint?: string;
  /** Small status chip after the label — used to say whether a credential field has a stored value. */
  badge?: { label: string; tone: 'ok' | 'warn' };
}) {
  return (
    <label className="block">
      <span className="mb-1.5 flex items-center gap-2 text-sm font-medium">
        {label}
        {badge && (
          <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${badge.tone === 'ok'
            ? 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300'
            : 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300'}`}>
            {badge.label}
          </span>
        )}
      </span>
      <input
        type={type}
        value={value}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value)}
        className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand"
      />
      {hint && <span className="mt-1 block text-xs text-[var(--muted)]">{hint}</span>}
    </label>
  );
}
