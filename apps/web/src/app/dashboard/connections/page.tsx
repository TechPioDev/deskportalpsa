'use client';

import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plug, Plus, ShieldCheck, ChevronDown, Globe, Copy, Ticket, Users, Contact, RefreshCw, Activity, Upload, Image as ImageIcon, type LucideIcon } from 'lucide-react';
import { api, ApiError } from '@/lib/api';
import type { ConnectionSummary, ConnectionFields } from '@/lib/types';
import { SyncSettings } from './SyncSettings';

const PROVIDERS = [
  { value: 2, label: 'Datto Autotask', creds: ['ApiIntegrationCode', 'UserName', 'Secret'] },
  { value: 1, label: 'ConnectWise Manage', creds: ['CompanyId', 'PublicKey', 'PrivateKey', 'ClientId'] },
];

// ConnectionStatus enum: 0 Disabled, 1 Pending, 2 Healthy, 3 Degraded, 4 Failed
const STATUS_LABEL: Record<number, string> = { 0: 'Disabled', 1: 'Pending', 2: 'Healthy', 3: 'Degraded', 4: 'Failed' };

export default function ConnectionsPage() {
  const qc = useQueryClient();
  const { data, isError } = useQuery({ queryKey: ['connections'], queryFn: api.connections });

  const [open, setOpen] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [provider, setProvider] = useState(2);
  const [form, setForm] = useState<Record<string, string>>({ name: '', apiEndpoint: '', tenantIdentifier: '', logoUrl: '' });
  // Which credential FIELDS hold a stored value for the connection being edited (names only — the
  // values are write-only). null = unknown, [] = the server said nothing is stored.
  const [storedKeys, setStoredKeys] = useState<string[] | null>(null);
  const providerDef = PROVIDERS.find((p) => p.value === provider)!;

  function openAdd() {
    setEditingId(null);
    setProvider(2);
    setForm({ name: '', apiEndpoint: '', tenantIdentifier: '', logoUrl: '' });
    setStoredKeys(null);
    setOpen(true);
  }
  function openEdit(c: ConnectionSummary) {
    setEditingId(c.id);
    setProvider(Number(c.provider));
    setForm({ name: c.name, apiEndpoint: c.apiEndpoint, tenantIdentifier: c.tenantIdentifier ?? '', logoUrl: c.logoUrl ?? '' });
    setStoredKeys(c.storedCredentialKeys);
    setOpen(true);
  }

  const save = useMutation({
    mutationFn: async () => {
      const entered = Object.fromEntries(providerDef.creds.map((c) => [c, form[c] ?? '']).filter(([, v]) => v !== ''));
      const credentialsChanged = Object.keys(entered).length > 0;
      if (editingId) {
        const updated = await api.updateConnection(editingId, {
          name: form.name,
          apiEndpoint: form.apiEndpoint,
          tenantIdentifier: form.tenantIdentifier || undefined,
          isEnabled: true,
          logoUrl: form.logoUrl,
          credentials: credentialsChanged ? entered : undefined,
        });
        return { id: updated.id, credentialsChanged };
      }
      const created = await api.createConnection({
        name: form.name,
        provider,
        apiEndpoint: form.apiEndpoint,
        tenantIdentifier: form.tenantIdentifier || undefined,
        logoUrl: form.logoUrl,
        credentials: Object.fromEntries(providerDef.creds.map((c) => [c, form[c] ?? ''])),
      });
      return { id: created.id, credentialsChanged: true };
    },
    onSuccess: ({ id, credentialsChanged }) => {
      setOpen(false);
      qc.invalidateQueries({ queryKey: ['connections'] });
      // New or rotated credentials get tested immediately — the whole reason someone re-enters keys
      // is to find out whether they work, and making them hunt for the Test button after saving is
      // an extra round-trip through a Degraded card. The result lands on the card like a manual test.
      if (credentialsChanged) test.mutate(id);
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

  const isEdit = editingId !== null;

  // Blank means "keep the stored key" — so when a save would rotate credentials (anything typed),
  // every field must be either typed or already stored. Otherwise a partial entry saves fine and
  // then fails at sync time with "credential missing from the secret store".
  const isStored = (c: string) => isEdit && (storedKeys?.includes(c) ?? true);
  const enteredAny = providerDef.creds.some((c) => (form[c] ?? '').trim() !== '');
  const missingCreds = isEdit && enteredAny
    ? providerDef.creds.filter((c) => !(form[c] ?? '').trim() && !isStored(c))
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
                value={provider}
                disabled={isEdit}
                onChange={(e) => setProvider(Number(e.target.value))}
                className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm disabled:opacity-60"
              >
                {PROVIDERS.map((p) => <option key={p.value} value={p.value}>{p.label}</option>)}
              </select>
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
              placeholder={provider === 2 ? 'https://webservices31.autotask.net/ATServicesRest/' : 'https://api-na.myconnectwise.net/v4_6_release/apis/3.0/'}
              hint={provider === 2
                ? 'Your Autotask zone URL: https, on autotask.net. The version segment is optional — /ATServicesRest/ and /ATServicesRest/v1.0/ both work.'
                : 'Your ConnectWise API base, starting with https:// and ending in /apis/3.0/.'} />
            <Input label="Tenant identifier (optional)" value={form.tenantIdentifier} onChange={(v) => setForm({ ...form, tenantIdentifier: v })}
              hint="A label for your own reference — not required, and not sent to the PSA." />
          </div>
          <div className="grid gap-3 sm:grid-cols-2">
            {providerDef.creds.map((c) => {
              // stored===null means an older response didn't say — keep the historical wording.
              const stored = !isEdit ? null : storedKeys === null ? null : storedKeys.includes(c);
              return (
                <Input key={c} label={c} type={/secret|key/i.test(c) ? 'password' : 'text'}
                  placeholder={!isEdit ? '' : stored === false ? 'nothing stored — enter a value' : 'unchanged'}
                  badge={stored === null ? undefined : stored
                    ? { label: 'Stored', tone: 'ok' }
                    : { label: 'Nothing stored', tone: 'warn' }}
                  value={form[c] ?? ''} onChange={(v) => setForm({ ...form, [c]: v })} />
              );
            })}
          </div>
          {missingCreds.length > 0 && (
            <p className="text-xs text-amber-700 dark:text-amber-300">
              Entering credentials replaces the stored set — also fill in {missingCreds.join(', ')} (nothing is stored for {missingCreds.length === 1 ? 'it' : 'them'}).
            </p>
          )}
          <div className="flex justify-end gap-2">
            <button type="button" onClick={() => setOpen(false)} className="rounded-lg border border-[var(--border)] px-3.5 py-2 text-sm">Cancel</button>
            <button type="submit" disabled={save.isPending || missingCreds.length > 0} className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg disabled:opacity-50">
              {save.isPending ? 'Saving…' : isEdit ? 'Save changes' : 'Save connection'}
            </button>
          </div>
          {save.isError && (
            <p className="text-sm text-red-500">
              {save.error instanceof Error ? save.error.message : 'Could not save the connection.'}
            </p>
          )}
        </form>
      )}

      {(isError || (data && data.length === 0)) && !open && (
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
              result={results[c.id]}
              expanded={expandedId === c.id}
              fields={fieldsById[c.id]}
              onTest={() => test.mutate(c.id)}
              testing={test.isPending && test.variables === c.id}
              onSync={() => sync.mutate({ id: c.id, full: false })}
              onResyncAll={() => sync.mutate({ id: c.id, full: true })}
              syncing={sync.isPending && sync.variables?.id === c.id}
              settingsOpen={settingsId === c.id}
              onToggleSettings={() => setSettingsId(settingsId === c.id ? null : c.id)}
              onRefreshFields={() => refreshFields.mutate(c.id)}
              refreshingFields={refreshFields.isPending && refreshFields.variables === c.id}
              onEdit={() => openEdit(c)}
              onToggleFields={() => toggleFields(c.id)}
            />
          ))}
        </div>
      )}
    </div>
  );
}

/** Provider mark. Initials, not vendor logos — we ship no brand assets we have no licence for. */
const PROVIDER_MARK: Record<number, string> = { 1: 'CW', 2: 'AT' };

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

function ConnectionCard({
  c, result, expanded, fields, onTest, testing, onSync, onResyncAll, syncing, settingsOpen,
  onToggleSettings, onRefreshFields, refreshingFields, onEdit, onToggleFields,
}: {
  c: ConnectionSummary;
  result?: { ok: boolean; msg: string };
  expanded: boolean;
  fields?: ConnectionFields | 'loading' | 'error';
  onTest: () => void;
  testing: boolean;
  onSync: () => void;
  onResyncAll: () => void;
  syncing: boolean;
  settingsOpen: boolean;
  onToggleSettings: () => void;
  onRefreshFields: () => void;
  refreshingFields: boolean;
  onEdit: () => void;
  onToggleFields: () => void;
}) {
  const status = Number(c.status);
  const health = HEALTH[status] ?? HEALTH[1];

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
            {PROVIDER_MARK[Number(c.provider)] ?? '?'}
          </span>
        </span>

        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="text-[17px] font-semibold">{c.name}</h2>
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
          <p className={'flex items-center justify-end gap-1.5 text-sm font-medium ' + health.text}>
            <span className={'h-2 w-2 rounded-full ' + health.dot} aria-hidden="true" />
            {STATUS_LABEL[status] ?? String(c.status)}
          </p>
          <p className="mt-0.5 text-[12px] text-[var(--muted)]">Last checked {ago(c.lastHealthCheckAt)}</p>
        </div>
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
        <ActionButton onClick={onTest} disabled={testing}>
          <Activity size={13} /> {testing ? 'Testing…' : 'Test connection'}
        </ActionButton>
        <button
          onClick={onSync}
          disabled={syncing}
          className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-50"
        >
          <RefreshCw size={13} className={syncing ? 'animate-spin' : undefined} /> {syncing ? 'Syncing…' : 'Sync now'}
        </button>
        <span className="flex-1" />
        <ActionButton onClick={onEdit}>Edit</ActionButton>
        <ActionButton onClick={onResyncAll} disabled={syncing}>Re-sync all</ActionButton>
        <ActionButton onClick={onRefreshFields} disabled={refreshingFields}>
          {refreshingFields ? 'Refreshing…' : 'Refresh fields'}
        </ActionButton>
        <ActionButton onClick={onToggleFields}>
          <ChevronDown size={13} className={expanded ? 'rotate-180 transition-transform' : 'transition-transform'} /> Boards
        </ActionButton>
        <ActionButton onClick={onToggleSettings}>
          <ChevronDown size={13} className={settingsOpen ? 'rotate-180 transition-transform' : 'transition-transform'} /> Sync settings
        </ActionButton>
      </div>

      {settingsOpen && (
        <div className="border-t border-[var(--border)] px-5 py-4">
          <SyncSettings connectionId={c.id} provider={Number(c.provider)} />
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
