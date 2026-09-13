'use client';

import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ArrowLeftRight, Link2, AlertTriangle, RefreshCw, ShieldCheck, CheckCircle2, ChevronDown,
  FileText, Plus, Pencil, Trash2, Info, ListChecks, Flag, LayoutGrid, FolderClosed, Clock, History,
} from 'lucide-react';
import { api } from '@/lib/api';
import type { MappingRule } from '@/lib/types';

const SCOPE_CONNECTION = 2;      // MappingScope.ConnectionOverride
const DIRECTION_BIDIRECTIONAL = 3; // MappingDirection.Bidirectional

const TABS = [
  { key: 'status', label: 'Status', icon: ListChecks },
  { key: 'priority', label: 'Priority', icon: Flag },
  { key: 'queue', label: 'Queue / Board', icon: LayoutGrid },
  { key: 'category', label: 'Category', icon: FolderClosed },
  { key: 'workType', label: 'Work Type', icon: Clock },
] as const;
type TabKey = (typeof TABS)[number]['key'];

// Curated PSA option lists. Live discovery needs the PSA API; these are unioned with whatever
// external values already exist in the saved rules so nothing on screen is ever missing.
const CURATED: Record<TabKey, string[]> = {
  status: ['New (Not Responded)', 'In Progress', 'Waiting on Customer', 'On Hold', 'Resolved', 'Closed', 'Scheduled', 'Escalated'],
  priority: ['Priority 1 - Emergency', 'Priority 2 - High', 'Priority 3 - Medium', 'Priority 4 - Low', 'No SLA'],
  queue: ['Service Desk', 'Network Operations', 'Professional Services', 'Triage', 'Onboarding'],
  category: ['Hardware', 'Software', 'Network', 'Account / Access', 'Email', 'Security'],
  workType: [], // discovered live from the connection (see options memo)
};

// Fixed portal-neutral values shown as rows to map even before any rule exists (status/priority are
// closed enums). Open fields (queue/category/workType) get their rows from existing rules + Add Custom.
const PORTAL_VALUES: Partial<Record<TabKey, string[]>> = {
  status: ['NEW', 'IN_PROGRESS', 'WAITING_CUSTOMER', 'ON_HOLD', 'RESOLVED', 'CLOSED'],
  priority: ['CRITICAL', 'HIGH', 'NORMAL', 'LOW'],
};

type DisplayRow = { id?: string; portalValue: string; externalValue: string | null; fixed: boolean };

const badgeTone: Record<string, string> = {
  NEW: 'bg-blue-50 text-blue-700 dark:bg-blue-950/60 dark:text-blue-300',
  IN_PROGRESS: 'bg-amber-50 text-amber-700 dark:bg-amber-950/60 dark:text-amber-300',
  WAITING_CUSTOMER: 'bg-violet-50 text-violet-700 dark:bg-violet-950/60 dark:text-violet-300',
  ON_HOLD: 'bg-orange-50 text-orange-700 dark:bg-orange-950/60 dark:text-orange-300',
  RESOLVED: 'bg-green-50 text-green-700 dark:bg-green-950/60 dark:text-green-300',
  CLOSED: 'bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300',
  CRITICAL: 'bg-red-50 text-red-700 dark:bg-red-950/60 dark:text-red-300',
  HIGH: 'bg-orange-50 text-orange-700 dark:bg-orange-950/60 dark:text-orange-300',
  NORMAL: 'bg-blue-50 text-blue-700 dark:bg-blue-950/60 dark:text-blue-300',
  LOW: 'bg-green-50 text-green-700 dark:bg-green-950/60 dark:text-green-300',
};
const toneFor = (v: string) => badgeTone[v.toUpperCase()] ?? 'bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300';

function StatCard({ icon: Icon, iconTone, label, value, sub, subTone, action }: {
  icon: React.ElementType; iconTone: string; label: string; value: React.ReactNode; sub: string; subTone?: string;
  action?: React.ReactNode;
}) {
  return (
    <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
      <div className="flex items-start gap-3">
        <span className={`inline-flex h-10 w-10 items-center justify-center rounded-lg ${iconTone}`}><Icon size={18} /></span>
        <div>
          <div className="text-xs text-[var(--muted)]">{label}</div>
          <div className="text-2xl font-semibold leading-tight">{value}</div>
          <div className={`mt-0.5 text-xs ${subTone ?? 'text-[var(--muted)]'}`}>{sub}</div>
          {action}
        </div>
      </div>
    </div>
  );
}

type Draft = { portal: string; external: string };

export default function MappingsPage() {
  const qc = useQueryClient();
  const { data: connections } = useQuery({ queryKey: ['connections'], queryFn: api.connections });
  const [connId, setConnId] = useState<string | null>(null);
  const [tab, setTab] = useState<TabKey>('status');
  const [editingId, setEditingId] = useState<string | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);

  const conn = connections?.find((c) => c.id === connId) ?? connections?.[0];
  const provider = conn ? Number(conn.provider) : null;
  const tabLabel = TABS.find((t) => t.key === tab)!.label;

  const { data: mappings, isLoading } = useQuery({
    queryKey: ['mappings', provider], queryFn: () => api.listMappings(provider!), enabled: provider != null,
  });
  // Live field discovery — used for the Work Type tab's PSA options (needs a reachable connection).
  const { data: fields } = useQuery({
    queryKey: ['fields', conn?.id], queryFn: () => api.connectionFields(conn!.id), enabled: !!conn, retry: false,
  });

  // Snapshots cover every field of this connection, not just the open tab. Rules saved here snapshot
  // themselves; this catches the ones changed any other way, which a rollback would otherwise delete.
  const snapshotKey = ['mapping-snapshot', provider, conn?.id];
  const { data: snapshot } = useQuery({
    queryKey: snapshotKey, queryFn: () => api.mappingSnapshotStatus(provider!, conn!.id), enabled: provider != null && !!conn,
  });
  const saveSnapshot = useMutation({
    mutationFn: () => api.saveMappingSnapshot(provider!, conn!.id),
    onSuccess: (s) => qc.setQueryData(snapshotKey, s),
  });

  const del = useMutation({
    mutationFn: (ruleId: string) => api.deleteMapping(ruleId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['mappings', provider] }); qc.invalidateQueries({ queryKey: snapshotKey }); },
  });

  const upsert = useMutation({
    mutationFn: (body: Parameters<typeof api.upsertMapping>[0]) => api.upsertMapping(body, `map ${tab}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['mappings', provider] }); qc.invalidateQueries({ queryKey: snapshotKey }); setDraft(null);
    },
  });

  const rows = useMemo<DisplayRow[]>(() => {
    const rules = (mappings ?? []).filter((m) => m.portalField === tab && m.psaConnectionId === (conn?.id ?? ''));
    const fixedVals = PORTAL_VALUES[tab];
    if (fixedVals) {
      // Several rules can share a portal value (e.g. inbound-only aliases for per-board PSA names);
      // display the bidirectional rule as the canonical row.
      const byPortal = new Map<string, MappingRule>();
      for (const r of rules) {
        const key = r.portalValue ?? '';
        if (!byPortal.has(key) || Number(r.direction) === 3) byPortal.set(key, r);
      }
      const canonical: DisplayRow[] = fixedVals.map((pv) => {
        const rule = byPortal.get(pv);
        return { id: rule?.id, portalValue: pv, externalValue: rule?.externalValue ?? null, fixed: true };
      });
      const extra: DisplayRow[] = rules
        .filter((r) => !fixedVals.includes(r.portalValue ?? ''))
        .map((r) => ({ id: r.id, portalValue: r.portalValue ?? '', externalValue: r.externalValue, fixed: false }));
      return [...canonical, ...extra];
    }
    return rules.map((r) => ({ id: r.id, portalValue: r.portalValue ?? '', externalValue: r.externalValue, fixed: false }));
  }, [mappings, tab, conn]);

  const mapped = rows.filter((r) => r.externalValue).length;
  // A rule is compared against the value a synced ticket CARRIES, so that — syncValue — is what we
  // save, showing the human label. Saving the provider id instead is what left every ConnectWise
  // ticket and every Autotask priority unmapped: the id was compared against a name it could never
  // equal, the raw provider value fell through, and it looked exactly like a working mapping.
  //
  // The id is still the right thing for the import filter and for reassigning a ticket's queue, and
  // those pickers still send it. One list, two representations, each caller taking the one it needs.
  const options = useMemo<{ value: string; label: string }[]>(() => {
    const discovered = ({
      status: fields?.statuses, priority: fields?.priorities, queue: fields?.queuesOrBoards,
      category: fields?.categories, workType: fields?.workTypes,
    }[tab] ?? []).map((o) => ({ value: o.syncValue ?? o.value, label: o.label }));
    const base = discovered.length ? discovered : CURATED[tab].map((c) => ({ value: c, label: c }));

    // Keep any value already stored by a rule selectable, even if discovery no longer returns it
    // (or the rule predates id-based saving and holds a label).
    const known = new Set(base.flatMap((o) => [o.value, o.label]));
    const legacy = (mappings ?? [])
      .filter((m) => m.portalField === tab && m.externalValue && !known.has(m.externalValue))
      .map((m) => ({ value: m.externalValue as string, label: m.externalValue as string }));
    return [...base, ...Array.from(new Map(legacy.map((l) => [l.value, l])).values())];
  }, [mappings, tab, fields]);

  /// Values the PSA currently ACCEPTS. A rule pointing outside this set still saves and still
  /// reads "Mapped", but every write using it is rejected — which is how a status mapped to a
  /// retired PSA value ("Waiting on Customer" when the PSA now says "Waiting Customer") goes
  /// unnoticed until someone changes a ticket's status.
  const liveValues = useMemo(() => {
    const discovered = ({
      status: fields?.statuses, priority: fields?.priorities, queue: fields?.queuesOrBoards,
      category: fields?.categories, workType: fields?.workTypes,
    }[tab] ?? []);
    // Every form a rule might legitimately hold: the sync value it should have, the label, and the
    // provider id a rule saved before this counted as live too — those still push correctly.
    return new Set(discovered.flatMap((o) => [o.syncValue ?? o.value, o.value, o.label]));
  }, [fields, tab]);

  /// A rule may store the provider id (current) or a label (legacy) — match either so the select
  /// shows the real state instead of falsely reading "not mapped".
  const selectedValue = (external: string | null) => {
    if (!external) return '';
    return options.find((o) => o.value === external)?.value
        ?? options.find((o) => o.label === external)?.value
        ?? external;
  };

  function save(row: DisplayRow, externalValue: string | null, portalValue?: string) {
    if (!conn || provider == null) return;
    upsert.mutate({
      id: row.id, provider, scope: SCOPE_CONNECTION, psaConnectionId: conn.id,
      portalField: tab, portalValue: portalValue ?? row.portalValue, externalField: tab,
      externalValue: externalValue ?? '', direction: DIRECTION_BIDIRECTIONAL, isRequired: false, fallbackValue: null,
    });
  }
  function saveDraft(external: string) {
    if (!conn || provider == null || !draft || !draft.portal.trim()) return;
    upsert.mutate({
      provider, scope: SCOPE_CONNECTION, psaConnectionId: conn.id,
      portalField: tab, portalValue: draft.portal.trim(), externalField: tab,
      externalValue: external, direction: DIRECTION_BIDIRECTIONAL, isRequired: false, fallbackValue: null,
    });
  }

  return (
    <div className="space-y-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Field Mapping</h1>
          <p className="text-sm text-[var(--muted)]">Map the portal&apos;s neutral values to each PSA&apos;s real values — how your portal talks to the PSA.</p>
        </div>
        <a href="/user-guide.pdf" target="_blank" rel="noopener noreferrer"
          className="inline-flex shrink-0 items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">
          <FileText size={15} /> Mapping Guide
        </a>
      </div>

      <div className="flex items-start gap-2 rounded-lg border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-800 dark:border-blue-900 dark:bg-blue-950/40 dark:text-blue-200">
        <Info size={16} className="mt-0.5 shrink-0" />
        <p>Field mapping ensures data consistency between Desk Portal and your connected PSA. <strong>Changes are saved automatically.</strong></p>
      </div>

      {/* Connection + tabs */}
      <div className="flex flex-wrap items-center gap-3">
        <div className="text-xs font-medium uppercase tracking-wide text-[var(--faint)]">Connection</div>
        <div className="relative">
          <select value={conn?.id ?? ''} onChange={(e) => setConnId(e.target.value)}
            className="appearance-none rounded-lg border border-[var(--border)] bg-[var(--surface)] py-2 pl-3 pr-9 text-sm">
            {connections?.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            {(!connections || connections.length === 0) && <option value="">No connections</option>}
          </select>
          <ChevronDown size={14} className="pointer-events-none absolute right-2.5 top-1/2 -translate-y-1/2 text-[var(--faint)]" />
        </div>
        {conn && (
          <span className="inline-flex items-center gap-1 rounded-full bg-green-100 px-2 py-0.5 text-xs font-medium text-green-700 dark:bg-green-950 dark:text-green-300"><CheckCircle2 size={11} /> Connected</span>
        )}
        <div className="ml-auto inline-flex flex-wrap rounded-lg border border-[var(--border)] bg-[var(--surface)] p-0.5">
          {TABS.map((t) => {
            const Icon = t.icon;
            return (
              <button key={t.key} onClick={() => { setTab(t.key); setEditingId(null); setDraft(null); }}
                className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium ${tab === t.key ? 'bg-brand text-brand-fg' : 'text-[var(--muted)] hover:text-[var(--fg)]'}`}>
                <Icon size={14} /> {t.label}
              </button>
            );
          })}
        </div>
      </div>

      {/* Stat cards */}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard icon={Link2} iconTone="bg-blue-50 text-blue-600 dark:bg-blue-950/50 dark:text-blue-300"
          label="Mapped Fields" value={<span>{mapped} <span className="text-[var(--faint)]">/ {rows.length}</span></span>}
          sub={rows.length > 0 && mapped === rows.length ? `All ${tabLabel.toLowerCase()} fields mapped ✓` : `${rows.length - mapped} left to map`}
          subTone={rows.length > 0 && mapped === rows.length ? 'text-green-600 dark:text-green-400' : 'text-amber-600 dark:text-amber-400'} />
        <StatCard icon={AlertTriangle} iconTone="bg-amber-50 text-amber-600 dark:bg-amber-950/50 dark:text-amber-300"
          label="Unmapped Fields" value={rows.length - mapped}
          sub={rows.length - mapped === 0 ? 'Everything is mapped ✓' : 'Needs attention'}
          subTone={rows.length - mapped === 0 ? 'text-green-600 dark:text-green-400' : 'text-amber-600 dark:text-amber-400'} />
        <StatCard icon={History} iconTone="bg-emerald-50 text-emerald-600 dark:bg-emerald-950/50 dark:text-emerald-300"
          label="Saved Snapshot"
          value={<span className="text-lg">{snapshot?.latestVersion != null ? `v${snapshot.latestVersion}` : snapshot ? 'None' : '…'}</span>}
          sub={!snapshot ? 'Checking…'
            : snapshot.matchesLiveRules
              ? `Matches all ${snapshot.liveRules} live rules${snapshot.takenAt ? ` · ${new Date(snapshot.takenAt).toLocaleDateString()}` : ''}`
              : `Out of date — holds ${snapshot.snapshotRules} of ${snapshot.liveRules} live rules`}
          subTone={snapshot && !snapshot.matchesLiveRules ? 'text-amber-600 dark:text-amber-400' : undefined}
          action={snapshot && (!snapshot.matchesLiveRules || saveSnapshot.isError) && (
            <button onClick={() => saveSnapshot.mutate()} disabled={saveSnapshot.isPending}
              className="mt-2 inline-flex items-center gap-1.5 rounded-lg bg-brand px-2.5 py-1 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
              <History size={12} /> {saveSnapshot.isPending ? 'Saving…' : saveSnapshot.isError ? 'Save failed — try again' : 'Save snapshot'}
            </button>
          )} />
        <StatCard icon={ShieldCheck} iconTone="bg-violet-50 text-violet-600 dark:bg-violet-950/50 dark:text-violet-300"
          label="Mapping Status" value={<span className="text-lg text-green-600 dark:text-green-400">{rows.length > 0 && mapped === rows.length ? 'Healthy' : 'Review'}</span>} sub={rows.length > 0 && mapped === rows.length ? 'No issues detected' : 'Unmapped values remain'} />
      </div>

      {/* Table */}
      <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <div className="flex flex-wrap items-center justify-between gap-2 border-b border-[var(--border)] px-5 py-3.5">
          <h2 className="text-sm font-semibold">{tabLabel} Field Mapping</h2>
          <div className="flex items-center gap-2">
            {upsert.isPending && <span className="text-xs text-[var(--muted)]">Saving…</span>}
            {upsert.isError && (
              <span className="inline-flex items-center gap-1 rounded-md bg-red-50 px-2 py-1 text-xs font-medium text-red-600 dark:bg-red-950/50 dark:text-red-300">
                <AlertTriangle size={12} /> Save failed — is the API reachable?
              </span>
            )}
            {upsert.isSuccess && !upsert.isPending && (
              <span className="inline-flex items-center gap-1 text-xs font-medium text-green-600 dark:text-green-400">
                <CheckCircle2 size={12} /> Saved
              </span>
            )}
            <button onClick={() => setDraft({ portal: '', external: '' })} className="inline-flex items-center gap-1.5 text-sm font-medium text-brand hover:underline">
              <Plus size={15} /> Add Custom Mapping
            </button>
            <button onClick={() => qc.invalidateQueries({ queryKey: ['mappings', provider] })} className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-sm font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]">
              <RefreshCw size={14} /> Refresh
            </button>
          </div>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
              <tr className="border-b border-[var(--border)]">
                <th className="px-5 py-2.5 font-medium">Portal {tabLabel} (Neutral)</th>
                <th className="px-2 py-2.5 text-center font-medium"><ArrowLeftRight size={13} className="mx-auto" /></th>
                <th className="px-5 py-2.5 font-medium">PSA {tabLabel}{conn ? ` (${conn.name})` : ''}</th>
                <th className="px-5 py-2.5 font-medium">Status</th>
                <th className="px-5 py-2.5 text-right font-medium">Actions</th>
              </tr>
            </thead>
            <tbody>
              {isLoading && <tr><td colSpan={5} className="px-5 py-8 text-center text-sm text-[var(--muted)]">Loading mappings…</td></tr>}

              {!isLoading && rows.map((r) => (
                <tr key={r.id ?? r.portalValue} className="border-b border-[var(--border)] last:border-0">
                  <td className="px-5 py-3">
                    {!r.fixed && editingId === r.id ? (
                      <input autoFocus defaultValue={r.portalValue}
                        onBlur={(e) => { const v = e.target.value.trim(); if (v && v !== r.portalValue) save(r, r.externalValue, v); setEditingId(null); }}
                        onKeyDown={(e) => e.key === 'Enter' && (e.target as HTMLInputElement).blur()}
                        className="w-44 rounded-md border border-brand bg-[var(--bg)] px-2 py-1 text-sm outline-none" />
                    ) : (
                      <span className={`inline-flex rounded-md px-2.5 py-1 text-xs font-medium ${toneFor(r.portalValue)}`}>{r.portalValue.replace(/_/g, ' ')}</span>
                    )}
                  </td>
                  <td className="px-2 py-3 text-center"><ArrowLeftRight size={14} className="mx-auto text-[var(--faint)]" /></td>
                  <td className="px-5 py-3">
                    <div className="relative max-w-xs">
                      <select value={selectedValue(r.externalValue)} onChange={(e) => save(r, e.target.value || null)} disabled={upsert.isPending}
                        className="w-full appearance-none rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 pr-8 text-sm outline-none focus:border-brand disabled:opacity-60">
                        <option value="">— not mapped —</option>
                        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                      </select>
                      <ChevronDown size={14} className="pointer-events-none absolute right-2.5 top-1/2 -translate-y-1/2 text-[var(--faint)]" />
                    </div>
                  </td>
                  <td className="px-5 py-3">
                    {!r.externalValue
                      ? <span className="inline-flex items-center gap-1.5 text-sm font-medium text-amber-600 dark:text-amber-400"><AlertTriangle size={15} /> Unmapped</span>
                      : liveValues.size > 0 && !liveValues.has(r.externalValue)
                        // Saved, but the PSA no longer offers it — the write will fail, so this
                        // must not wear the same green tick as a mapping that works.
                        ? <span title={`"${r.externalValue}" is not a value this PSA currently accepts. Pick a current one.`}
                            className="inline-flex items-center gap-1.5 text-sm font-medium text-red-600 dark:text-red-400"><AlertTriangle size={15} /> Not accepted by PSA</span>
                        : <span className="inline-flex items-center gap-1.5 text-sm font-medium text-green-600 dark:text-green-400"><CheckCircle2 size={15} /> Mapped</span>}
                  </td>
                  <td className="px-5 py-3">
                    <div className="flex items-center justify-end gap-1">
                      {!r.fixed && (
                        <button onClick={() => setEditingId(r.id ?? null)} aria-label="Rename portal value"
                          className="rounded-md p-1.5 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-brand"><Pencil size={15} /></button>
                      )}
                      <button
                        onClick={() => {
                          if (r.fixed) { save(r, null); return; }   // canonical value: clear, keep the row
                          if (r.id && window.confirm(`Remove the "${r.portalValue}" mapping?`)) del.mutate(r.id);
                        }}
                        disabled={(r.fixed && !r.externalValue) || upsert.isPending || del.isPending}
                        aria-label={r.fixed ? 'Clear mapping' : 'Delete mapping'}
                        title={r.fixed ? 'Clear this mapping' : 'Delete this mapping'}
                        className="rounded-md p-1.5 text-[var(--muted)] hover:bg-red-50 hover:text-red-600 disabled:opacity-40 dark:hover:bg-red-950/50"><Trash2 size={15} /></button>
                    </div>
                  </td>
                </tr>
              ))}

              {draft && (
                <tr className="border-b border-[var(--border)] bg-[var(--bg)]/40 last:border-0">
                  <td className="px-5 py-3">
                    <input autoFocus value={draft.portal} onChange={(e) => setDraft({ ...draft, portal: e.target.value })} placeholder="New portal value"
                      className="w-44 rounded-md border border-brand bg-[var(--bg)] px-2 py-1 text-sm outline-none" />
                  </td>
                  <td className="px-2 py-3 text-center"><ArrowLeftRight size={14} className="mx-auto text-[var(--faint)]" /></td>
                  <td className="px-5 py-3">
                    <div className="relative max-w-xs">
                      <select value={draft.external} onChange={(e) => { setDraft({ ...draft, external: e.target.value }); if (draft.portal.trim() && e.target.value) saveDraft(e.target.value); }}
                        disabled={!draft.portal.trim() || upsert.isPending}
                        className="w-full appearance-none rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 pr-8 text-sm outline-none focus:border-brand disabled:opacity-60">
                        <option value="">— select PSA value —</option>
                        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                      </select>
                      <ChevronDown size={14} className="pointer-events-none absolute right-2.5 top-1/2 -translate-y-1/2 text-[var(--faint)]" />
                    </div>
                  </td>
                  <td className="px-5 py-3"><span className="text-sm text-[var(--faint)]">Draft</span></td>
                  <td className="px-5 py-3 text-right">
                    <button onClick={() => setDraft(null)} className="rounded-md p-1.5 text-[var(--muted)] hover:bg-[var(--bg)]"><Trash2 size={15} /></button>
                  </td>
                </tr>
              )}

              {!isLoading && rows.length === 0 && !draft && (
                <tr><td colSpan={5} className="px-5 py-8 text-center text-sm text-[var(--muted)]">No {tabLabel.toLowerCase()} mappings for this connection — use “Add Custom Mapping”.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
