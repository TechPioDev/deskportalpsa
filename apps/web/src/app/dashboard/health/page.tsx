'use client';

import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  RefreshCw, ChevronDown, Filter, Clock, Mail, AlertOctagon, CheckCircle2, Layers,
  Plus, ArrowRight, AlertTriangle, Activity, RotateCw, Check, BellRing, ShieldCheck,
} from 'lucide-react';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { api, type AttentionItem, type EmailSettingsInput, type UnsyncedTicket } from '@/lib/api';
import type { Health } from '@/lib/types';

const PROVIDER: Record<number, { name: string; abbr: string; color: string }> = {
  1: { name: 'ConnectWise', abbr: 'CW', color: 'bg-blue-100 text-blue-700 dark:bg-blue-950 dark:text-blue-300' },
  2: { name: 'Autotask', abbr: 'AT', color: 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300' },
  10: { name: 'HaloPSA', abbr: 'H', color: 'bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300' },
  20: { name: 'ServiceNow', abbr: 'SN', color: 'bg-slate-200 text-slate-700 dark:bg-slate-700 dark:text-slate-200' },
};
const STATUS: Record<number, { label: string; tone: string; pct: number }> = {
  0: { label: 'Disabled', tone: 'bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300', pct: 0 },
  1: { label: 'Pending', tone: 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300', pct: 60 },
  2: { label: 'Healthy', tone: 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300', pct: 100 },
  3: { label: 'Degraded', tone: 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300', pct: 85 },
  4: { label: 'Failed', tone: 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300', pct: 40 },
};
const healthBar = (h: number) => (h >= 95 ? 'bg-green-500' : h >= 70 ? 'bg-amber-500' : 'bg-red-500');

function ago(iso: string | null): string {
  if (!iso) return 'never';
  const s = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} hr ago`;
  return `${Math.floor(s / 86400)}d ago`;
}
const meta = (h: Health) => {
  const p = PROVIDER[Number(h.provider)] ?? { name: 'PSA', abbr: 'P', color: 'bg-slate-200 text-slate-700' };
  const s = STATUS[Number(h.status)] ?? STATUS[1];
  const pct = s.pct - Math.min(20, h.failedSyncEvents * 5);
  return { ...p, s, pct: Math.max(0, pct) };
};

function Avatar({ abbr, color, size = 'md' }: { abbr: string; color: string; size?: 'md' | 'lg' }) {
  const s = size === 'lg' ? 'h-12 w-12 text-sm' : 'h-8 w-8 text-[11px]';
  return <span className={`inline-flex items-center justify-center rounded-full font-bold ${s} ${color}`}>{abbr}</span>;
}
function BigStat({ icon: Icon, iconTone, value, label, sub, valueTone }: {
  icon: React.ElementType; iconTone: string; value: React.ReactNode; label: string; sub: string; valueTone?: string;
}) {
  return (
    <div className="flex items-start gap-3">
      <span className={`inline-flex h-10 w-10 items-center justify-center rounded-full ${iconTone}`}><Icon size={18} /></span>
      <div>
        <div className={`text-2xl font-semibold leading-tight ${valueTone ?? ''}`}>{value}</div>
        <div className="text-sm font-medium">{label}</div>
        <div className="text-xs text-[var(--muted)]">{sub}</div>
      </div>
    </div>
  );
}

export default function HealthPage() {
  const qc = useQueryClient();
  const { data, isLoading, isError } = useQuery({ queryKey: ['health'], queryFn: api.health });
  const { data: audit } = useQuery({ queryKey: ['audit'], queryFn: api.audit });

  const rows = data ?? [];
  const featured = rows[0];
  const overall = rows.length ? Math.round(rows.reduce((a, h) => a + meta(h).pct, 0) / rows.length) : 0;
  const totalPending = rows.reduce((a, h) => a + h.pendingJobs, 0);
  const totalDead = rows.reduce((a, h) => a + h.deadLetterJobs, 0);
  const totalFailed = rows.reduce((a, h) => a + h.failedSyncEvents, 0);

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Integration Health</h1>
          <p className="text-sm text-[var(--muted)]">Live status of each PSA connection.</p>
        </div>
        <div className="flex items-center gap-2">
          <button onClick={() => { ['health', 'audit', 'attention', 'unsynced-tickets'].forEach((k) => qc.invalidateQueries({ queryKey: [k] })); }}
            className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">
            <RefreshCw size={15} /> Refresh
          </button>
          <button className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">
            <Filter size={15} /> All Connections <ChevronDown size={14} className="text-[var(--faint)]" />
          </button>
        </div>
      </div>

      <AttentionPanel />

      <EmailDeliveryCard />

      {isLoading && <div className="h-40 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}

      {(isError || (data && rows.length === 0)) && (
        <div className="flex flex-col items-center rounded-xl border border-dashed border-[var(--border)] px-6 py-12 text-center">
          <Activity className="mb-3 text-[var(--faint)]" size={26} />
          <p className="text-sm text-[var(--muted)]">No connections to monitor yet.</p>
        </div>
      )}

      {featured && (() => {
        const m = meta(featured);
        return (
          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="flex items-center gap-3">
                <Avatar abbr={m.abbr} color={m.color} size="lg" />
                <div>
                  <div className="text-lg font-semibold">{featured.name}</div>
                  <div className="mt-0.5 flex items-center gap-2 text-sm text-[var(--muted)]">
                    <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium ${m.s.tone}`}><CheckCircle2 size={11} /> {m.s.label}</span>
                    Last synced: {ago(featured.lastSuccessfulSyncAt)}
                  </div>
                </div>
              </div>
              <span className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-1.5 text-sm">
                <Layers size={15} className="text-[var(--muted)]" /> <strong>{rows.length}</strong> Monitor{rows.length === 1 ? '' : 's'}
              </span>
            </div>
            <div className="mt-5 grid grid-cols-2 gap-4 lg:grid-cols-4">
              <BigStat icon={Clock} iconTone="bg-blue-50 text-blue-600 dark:bg-blue-950/50 dark:text-blue-300" value={totalPending} label="Pending" sub="Requires attention" />
              <BigStat icon={Mail} iconTone="bg-amber-50 text-amber-600 dark:bg-amber-950/50 dark:text-amber-300" value={totalDead} label="Dead-letter" sub="Failed after retries" />
              <BigStat icon={AlertOctagon} iconTone="bg-red-50 text-red-600 dark:bg-red-950/50 dark:text-red-300" value={totalFailed} label="Failed events" sub="Sync errors" />
              <BigStat icon={CheckCircle2} iconTone="bg-green-50 text-green-600 dark:bg-green-950/50 dark:text-green-300" value={`${overall}%`} valueTone={overall >= 95 ? 'text-green-600 dark:text-green-400' : 'text-amber-600 dark:text-amber-400'} label="Overall health" sub={overall >= 95 ? 'All systems operational' : 'Degradation detected'} />
            </div>
          </div>
        );
      })()}

      {rows.length > 0 && (
        <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] xl:col-span-2">
            <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3.5">
              <h2 className="text-sm font-semibold">All PSA Connections</h2>
              <a href="/dashboard/connections" className="inline-flex items-center gap-1.5 text-sm font-medium text-brand hover:underline"><Plus size={15} /> Add Connection</a>
            </div>
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
                  <tr className="border-b border-[var(--border)]">
                    <th className="px-5 py-2.5 font-medium">Connection</th>
                    <th className="px-2 py-2.5 font-medium">Status</th>
                    <th className="px-2 py-2.5 font-medium">Last Sync</th>
                    <th className="px-2 py-2.5 font-medium">Pending</th>
                    <th className="px-2 py-2.5 font-medium">Failed</th>
                    <th className="px-5 py-2.5 font-medium">Health</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((h) => {
                    const m = meta(h);
                    return (
                      <tr key={h.connectionId} className="border-b border-[var(--border)] last:border-0">
                        <td className="px-5 py-3"><span className="flex items-center gap-2.5"><Avatar abbr={m.abbr} color={m.color} /><span className="font-medium">{h.name}</span></span></td>
                        <td className="px-2 py-3"><span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-medium ${m.s.tone}`}>{m.s.label}</span></td>
                        <td className="px-2 py-3 text-[var(--muted)]">{ago(h.lastSuccessfulSyncAt)}</td>
                        <td className="px-2 py-3"><span className={h.pendingJobs > 0 ? 'font-medium text-amber-600 dark:text-amber-400' : 'text-[var(--muted)]'}>{h.pendingJobs}</span></td>
                        <td className="px-2 py-3"><span className={h.failedSyncEvents > 0 ? 'font-medium text-red-600 dark:text-red-400' : 'text-[var(--muted)]'}>{h.failedSyncEvents}</span></td>
                        <td className="px-5 py-3">
                          <span className="flex items-center gap-2">
                            <span className="h-1.5 w-16 overflow-hidden rounded-full bg-[var(--bg)]"><span className={`block h-full rounded-full ${healthBar(m.pct)}`} style={{ width: `${m.pct}%` }} /></span>
                            <span className="tabular-nums text-xs text-[var(--muted)]">{m.pct}%</span>
                          </span>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>

          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
            <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3.5">
              <h2 className="text-sm font-semibold">Recent Activity</h2>
              <a href="/dashboard/audit" className="text-sm font-medium text-brand hover:underline">View all</a>
            </div>
            <ul className="px-5 py-3">
              {(audit ?? []).slice(0, 6).map((a) => (
                <li key={a.id} className="flex gap-3 pb-4 last:pb-1">
                  <CheckCircle2 size={17} className="mt-0.5 shrink-0 text-green-500" />
                  <div className="min-w-0">
                    <div className="flex items-baseline justify-between gap-2">
                      <span className="truncate text-sm font-medium">{a.action}</span>
                      <span className="shrink-0 text-xs text-[var(--faint)]">{ago(a.createdAt)}</span>
                    </div>
                    <div className="text-xs text-[var(--muted)]">{a.entityType}{a.actorDisplayName ? ` · ${a.actorDisplayName}` : ''}</div>
                  </div>
                </li>
              ))}
              {(!audit || audit.length === 0) && <li className="py-4 text-sm text-[var(--muted)]">No recent activity.</li>}
            </ul>
          </div>
        </div>
      )}

      <UnsyncedPanel />

      <div className="flex items-center gap-2 rounded-lg border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-800 dark:border-blue-900 dark:bg-blue-950/40 dark:text-blue-200">
        <span className="flex h-5 w-5 items-center justify-center rounded-full bg-blue-500 text-white text-[11px] font-bold">i</span>
        Integration health is calculated from synchronization status, error rates, and pending job counts.
      </div>
    </div>
  );
}

/**
 * Everything that is quietly going wrong, most urgent first: connections that failed or stalled,
 * pushes that never reached the PSA, closed tickets that reports will skip, reports nobody received.
 * The same list is emailed once a day to the digest recipients when it is not empty.
 */
function AttentionPanel() {
  const { data, isLoading, isError } = useQuery({ queryKey: ['attention'], queryFn: api.attention, retry: false, refetchInterval: 5 * 60_000 });
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const canManage = !!me?.permissions?.includes('org.manage');
  const [editing, setEditing] = useState(false);

  if (isLoading) return <div className="h-20 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />;
  if (isError || !data) return null;

  const items = data.items;
  const critical = items.filter((i) => i.severity === 'critical').length;
  const digestTo = data.digest.recipients;

  return (
    <section aria-labelledby="attention-heading" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-[var(--border)] px-5 py-3">
        <h2 id="attention-heading" className="flex items-center gap-2 text-sm font-semibold">
          {items.length === 0
            ? <ShieldCheck size={16} className="text-green-600 dark:text-green-400" aria-hidden="true" />
            : <AlertTriangle size={16} className={critical ? 'text-red-600 dark:text-red-400' : 'text-amber-600 dark:text-amber-400'} aria-hidden="true" />}
          Needs attention
          <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${items.length === 0
            ? 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300'
            : critical
              ? 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300'
              : 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300'}`}>
            {items.length}
          </span>
        </h2>
        <span className="flex min-w-0 basis-full items-center gap-2 text-xs text-[var(--muted)] sm:flex-1 sm:basis-auto sm:justify-end">
          <BellRing size={13} aria-hidden="true" />
          <span className="truncate">
            {digestTo ? <>Daily digest to <span className="font-medium text-[var(--fg)]">{digestTo}</span></> : 'Daily digest is off'}
          </span>
          {canManage && !editing && (
            <button onClick={() => setEditing(true)} className="shrink-0 font-medium text-brand hover:underline">
              {digestTo ? 'Change' : 'Set up'}
            </button>
          )}
        </span>
      </div>

      {editing && <DigestForm current={digestTo ?? ''} onClose={() => setEditing(false)} />}

      {items.length === 0 ? (
        <p className="px-5 py-4 text-sm text-[var(--muted)]">
          Nothing needs attention. Connections are syncing, tickets are reaching the PSA and reports are going out.
        </p>
      ) : (
        <ul className="divide-y divide-[var(--border)]">
          {items.map((i) => <AttentionRow key={i.kind + i.title} item={i} />)}
        </ul>
      )}
    </section>
  );
}

function AttentionRow({ item }: { item: AttentionItem }) {
  const critical = item.severity === 'critical';
  return (
    <li className="flex gap-3 px-5 py-3">
      <span className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${critical ? 'bg-red-500' : 'bg-amber-500'}`} aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-baseline gap-x-2">
          <span className="text-sm font-medium">{item.title}</span>
          <span className={`rounded px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${critical
            ? 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300'
            : 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300'}`}>
            {critical ? 'Critical' : 'Warning'}
          </span>
        </div>
        <p className="mt-0.5 break-words text-xs text-[var(--muted)]">{item.detail}</p>
      </div>
      {item.link && (
        <a href={item.link} className="inline-flex shrink-0 items-center gap-1 self-center text-xs font-medium text-brand hover:underline">
          Open <ArrowRight size={13} aria-hidden="true" />
        </a>
      )}
    </li>
  );
}

function DigestForm({ current, onClose }: { current: string; onClose: () => void }) {
  const qc = useQueryClient();
  const [value, setValue] = useState(current);
  const [invalid, setInvalid] = useState<string[]>([]);
  const save = useMutation({
    mutationFn: () => api.saveAttentionDigest(value),
    onSuccess: (r) => {
      qc.invalidateQueries({ queryKey: ['attention'] });
      setInvalid(r.invalid);
      if (r.invalid.length === 0) onClose();
    },
  });
  const send = useMutation({ mutationFn: api.sendAttentionDigest });

  return (
    <form className="space-y-2 border-b border-[var(--border)] bg-[var(--bg)] px-5 py-4"
      onSubmit={(e) => { e.preventDefault(); send.reset(); save.mutate(); }}>
      <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
        Email the list every morning at 07:30 (organization time) to
        <input value={value} onChange={(e) => setValue(e.target.value)} placeholder="ops@yourmsp.com, lead@yourmsp.com"
          className="w-full rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm text-[var(--fg)] outline-none focus:border-brand" />
      </label>
      <p className="text-xs text-[var(--faint)]">
        Sent only on days something needs attention. Leave blank to switch the digest off.
      </p>
      {invalid.length > 0 && (
        <p role="alert" className="text-xs text-red-600 dark:text-red-400">
          Saved the valid addresses. Not an email address: {invalid.join(', ')}
        </p>
      )}
      {save.isError && <p role="alert" className="text-xs text-red-600 dark:text-red-400">{(save.error as Error).message}</p>}
      {(send.data || send.isError) && (
        <p role="status" className={`text-xs font-medium ${send.data?.sent ? 'text-green-600 dark:text-green-400' : 'text-red-600 dark:text-red-400'}`}>
          {send.data?.message ?? 'Could not reach the server.'}
        </p>
      )}
      <div className="flex flex-wrap items-center gap-2">
        {current && (
          <button type="button" disabled={send.isPending} onClick={() => send.mutate()}
            className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--surface)] disabled:opacity-60">
            {send.isPending ? 'Sending…' : 'Send digest now'}
          </button>
        )}
        <span className="ml-auto flex gap-2">
          <button type="button" onClick={onClose} className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--surface)]">Cancel</button>
          <button type="submit" disabled={save.isPending} className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
        </span>
      </div>
    </form>
  );
}

/**
 * Tickets the portal holds that never reached the PSA. Before this existed a rejected create threw
 * away the customer's ticket entirely, so there was nothing to count and nothing to retry.
 *
 * Resync is per ticket on purpose: each retry hits the provider and can fail for its own reason,
 * and a single bulk button would bury which ones did.
 */
function UnsyncedPanel() {
  const qc = useQueryClient();
  const [done, setDone] = useState<Record<string, string>>({});
  const { data, isLoading } = useQuery({
    queryKey: ['unsynced-tickets'],
    queryFn: () => api.unsyncedTickets(),
    retry: false,
  });

  const resync = useMutation({
    mutationFn: (ticketId: string) => api.resyncTicket(ticketId),
    onSuccess: (r) => {
      setDone((d) => ({ ...d, [r.ticketId]: r.success ? `Synced as ${r.externalTicketId}` : (r.error ?? 'Rejected again') }));
      if (r.success) {
        ['unsynced-tickets', 'tickets', 'health'].forEach((k) => qc.invalidateQueries({ queryKey: [k] }));
      }
    },
  });

  if (isLoading) return null;
  const tickets: UnsyncedTicket[] = data?.tickets ?? [];

  return (
    <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3">
        <h2 className="flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-[var(--faint)]">
          <AlertOctagon size={15} className={tickets.length ? 'text-red-600 dark:text-red-400' : 'text-green-600 dark:text-green-400'} />
          Not synced to the PSA
          <span className={`rounded-full px-2 py-0.5 text-xs font-semibold ${tickets.length
            ? 'bg-red-100 text-red-700 dark:bg-red-950 dark:text-red-300'
            : 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300'}`}>
            {data?.count ?? 0}
          </span>
        </h2>
        <span className="text-xs text-[var(--muted)]">Resync applies this connection&apos;s current mappings and board defaults.</span>
      </div>

      {tickets.length === 0 ? (
        <p className="px-5 py-4 text-sm text-[var(--muted)]">
          Every ticket the portal holds exists in its PSA.
        </p>
      ) : (
        <ul className="divide-y divide-[var(--border)]">
          {tickets.map((t) => (
            <li key={t.ticketId} className="px-5 py-3">
              <div className="flex flex-wrap items-center gap-3">
                <span className="font-mono text-xs text-[var(--faint)]" title="Desk Portal ticket ID">{t.ticketId.slice(0, 8)}</span>
                <span className="min-w-0 flex-1 truncate text-sm font-medium">{t.title}</span>
                {t.customerName && <span className="hidden shrink-0 text-xs text-[var(--muted)] sm:inline">{t.customerName}</span>}
                <span className="shrink-0 rounded bg-[var(--bg)] px-1.5 py-0.5 text-[11px] text-[var(--muted)]">{t.connectionName}</span>
                <span className="shrink-0 rounded bg-red-100 px-1.5 py-0.5 text-[11px] font-medium text-red-700 dark:bg-red-950 dark:text-red-300">{t.syncStatus}</span>
                <span className="shrink-0 text-xs text-[var(--faint)]">{ago(t.createdAt)}</span>
                <button
                  onClick={() => resync.mutate(t.ticketId)}
                  disabled={resync.isPending}
                  className="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50">
                  <RotateCw size={13} /> Resync
                </button>
              </div>
              {t.syncError && (
                <p className="mt-1 text-xs text-red-600 dark:text-red-400">{t.syncError}</p>
              )}
              {done[t.ticketId] && (
                <p className="mt-1 inline-flex items-center gap-1 text-xs text-green-700 dark:text-green-400">
                  <Check size={12} /> {done[t.ticketId]}
                </p>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/**
 * Outbound email: whether it works, the mail account itself for organization admins, and a
 * one-click proof. The password is write-only - the form never receives it back.
 */
function EmailDeliveryCard() {
  const qc = useQueryClient();
  const { data, isError } = useQuery({ queryKey: ['email-status'], queryFn: api.emailStatus, retry: false });
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const canManage = !!me?.permissions?.includes('org.manage');
  const [editing, setEditing] = useState(false);
  const [to, setTo] = useState('');
  const test = useMutation({ mutationFn: () => api.sendTestEmail(to.trim() || undefined) });
  if (isError || !data) return null;

  const statusText = data.configured
    ? <>Scheduled reports are emailed from <span className="font-medium text-[var(--fg)]">{data.from}</span>
        {data.source === 'server' ? ' (the server default account)' : ''}.</>
    : canManage
      ? 'Not set up — scheduled reports are saved in the portal but not emailed. Add your mail account to start sending.'
      : 'Not set up — scheduled reports are saved in the portal but not emailed. An organization admin can add the mail account here.';

  return (
    <section aria-labelledby="email-delivery-heading" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-3 px-4 py-3">
        <span className={`inline-flex h-9 w-9 items-center justify-center rounded-lg ${data.configured
          ? 'bg-green-50 text-green-600 dark:bg-green-950/50 dark:text-green-300'
          : 'bg-amber-50 text-amber-600 dark:bg-amber-950/50 dark:text-amber-300'}`}>
          <Mail size={17} aria-hidden="true" />
        </span>
        <div className="min-w-0 flex-1">
          <h2 id="email-delivery-heading" className="text-sm font-semibold">Email delivery</h2>
          <div className="text-xs text-[var(--muted)]">{statusText}</div>
        </div>
        {data.configured && canManage && !editing && (
          <form className="flex flex-wrap items-center gap-2" onSubmit={(e) => { e.preventDefault(); test.mutate(); }}>
            <input type="email" value={to} onChange={(e) => setTo(e.target.value)} placeholder="Your address"
              aria-label="Send test email to"
              className="w-52 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2.5 py-1.5 text-sm outline-none focus:border-brand" />
            <button type="submit" disabled={test.isPending}
              className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-60">
              {test.isPending ? 'Sending…' : 'Send test email'}
            </button>
          </form>
        )}
        {canManage && !editing && (
          <button onClick={() => setEditing(true)}
            className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90">
            {data.source === 'organization' ? 'Edit mail settings' : 'Set up email'}
          </button>
        )}
        {(test.data || test.isError) && !editing && (
          <span role="status" className={`w-full text-xs font-medium ${test.data?.sent ? 'text-green-600 dark:text-green-400' : 'text-red-600 dark:text-red-400'}`}>
            {test.data?.message ?? 'Could not reach the server.'}
          </span>
        )}
      </div>
      {editing && (
        <EmailSettingsForm
          onClose={() => setEditing(false)}
          onSaved={() => { setEditing(false); test.reset(); qc.invalidateQueries({ queryKey: ['email-status'] }); }} />
      )}
    </section>
  );
}

const MAIL_PRESETS: { label: string; method: 'Smtp' | 'Graph'; host: string; port: number; security: string; hint: string }[] = [
  { label: 'Microsoft 365', method: 'Graph', host: '', port: 443, security: 'SslOnConnect',
    hint: 'Recommended for Microsoft 365. Sends through Microsoft Graph from a real mailbox, so reports do not land in Junk and no mailbox password is needed. In Microsoft Entra admin center → App registrations → New registration, then: API permissions → Add → Microsoft Graph → Application permissions → Mail.Send → Grant admin consent; Certificates & secrets → New client secret (copy its Value). Enter the Directory (tenant) ID and Application (client) ID from the app’s Overview page.' },
  { label: 'Microsoft 365 (SMTP)', method: 'Smtp', host: 'smtp.office365.com', port: 587, security: 'StartTls',
    hint: 'Password sign-in over SMTP. Microsoft is switching this off for many tenants; if sign-in is refused, use Microsoft 365 above instead.' },
  { label: 'Google Workspace', method: 'Smtp', host: 'smtp.gmail.com', port: 587, security: 'StartTls',
    hint: 'Use the mailbox address as the username and a Google app password (needs 2-Step Verification), not the normal password.' },
  { label: 'SendGrid', method: 'Smtp', host: 'smtp.sendgrid.net', port: 587, security: 'StartTls',
    hint: 'The username is the word apikey and the password is your SendGrid API key. The From address must be a verified sender.' },
  { label: 'Other', method: 'Smtp', host: '', port: 587, security: 'StartTls', hint: 'Your provider SMTP server name, port and login.' },
];

function EmailSettingsForm({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const { data: current, isLoading } = useQuery({ queryKey: ['email-settings'], queryFn: api.emailSettings, retry: false });
  const qc = useQueryClient();
  const [v, setV] = useState<EmailSettingsInput | null>(null);
  const [presetChoice, setPreset] = useState<number | null>(null);
  const savedGraph = current?.method === 'Graph';
  // With nothing chosen yet, open on what is saved: Graph for a Graph account, else the SMTP preset.
  const preset = presetChoice ?? (savedGraph || !current?.hasOwnAccount ? 0 : 1);
  const graph = MAIL_PRESETS[preset].method === 'Graph';
  const form: EmailSettingsInput = v ?? {
    host: current?.hasOwnAccount && !savedGraph ? current.host ?? '' : MAIL_PRESETS[1].host,
    port: current?.hasOwnAccount && !savedGraph ? current.port : 587,
    security: current?.hasOwnAccount && !savedGraph ? current.security : 'StartTls',
    username: savedGraph ? '' : current?.username ?? '', password: null,
    fromAddress: current?.fromAddress ?? '', fromName: current?.fromName ?? 'Desk Portal',
    graphTenantId: current?.graphTenantId ?? '', graphClientId: current?.graphClientId ?? '',
  };
  const set = (patch: Partial<EmailSettingsInput>) => setV({ ...form, ...patch });
  const save = useMutation({
    mutationFn: () => api.saveEmailSettings({
      ...form,
      method: graph ? 'Graph' : 'Smtp',
      username: graph ? null : form.username?.trim() || null,
      password: form.password || null,
    }),
    onSuccess: (s) => { qc.setQueryData(['email-settings'], s); onSaved(); },
  });
  const remove = useMutation({
    mutationFn: api.removeEmailSettings,
    onSuccess: (s) => { qc.setQueryData(['email-settings'], s); onSaved(); },
  });
  const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand';
  // A stored secret only carries over within the same method: an SMTP password is not a client secret.
  const secretSaved = !!current?.hasPassword && savedGraph === graph;

  if (isLoading) return <p className="border-t border-[var(--border)] px-4 py-4 text-sm text-[var(--muted)]">Loading…</p>;

  return (
    <form className="grid gap-3 border-t border-[var(--border)] px-4 py-4 sm:grid-cols-2"
      onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
      <div className="flex flex-wrap gap-1.5 sm:col-span-2" role="group" aria-label="Mail provider">
        {MAIL_PRESETS.map((p, i) => (
          <button key={p.label} type="button" aria-pressed={preset === i}
            onClick={() => {
              setPreset(i);
              // A client secret typed for Graph must not travel into the SMTP password box, or back.
              const clear = p.method !== MAIL_PRESETS[preset].method ? { password: null } : {};
              set({ ...clear, ...(p.method === 'Smtp' && p.host ? { host: p.host, port: p.port, security: p.security } : {}) });
            }}
            className={`rounded-lg border px-3 py-1.5 text-xs font-medium ${preset === i ? 'border-brand bg-brand-tint text-brand dark:bg-brand/20' : 'border-[var(--border)] text-[var(--muted)] hover:bg-[var(--bg)]'}`}>
            {p.label}
          </button>
        ))}
      </div>
      <p className="text-xs leading-relaxed text-[var(--muted)] sm:col-span-2">{MAIL_PRESETS[preset].hint}</p>

      {graph ? (
        <>
          <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
            Directory (tenant) ID
            <input required value={form.graphTenantId ?? ''} onChange={(e) => set({ graphTenantId: e.target.value })}
              placeholder="00000000-0000-0000-0000-000000000000" autoComplete="off" spellCheck={false} className={`${field} font-mono`} />
          </label>
          <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
            Application (client) ID
            <input required value={form.graphClientId ?? ''} onChange={(e) => set({ graphClientId: e.target.value })}
              placeholder="00000000-0000-0000-0000-000000000000" autoComplete="off" spellCheck={false} className={`${field} font-mono`} />
          </label>
          <label className="space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
            Client secret (Value)
            <input type="password" value={form.password ?? ''} onChange={(e) => set({ password: e.target.value })}
              placeholder={secretSaved ? 'Saved — leave blank to keep it' : 'The secret’s Value, not its Secret ID'}
              autoComplete="new-password" className={field} />
          </label>
        </>
      ) : (
        <>
          <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
            Mail server (SMTP)
            <input required value={form.host} onChange={(e) => set({ host: e.target.value })} placeholder="smtp.office365.com" className={field} />
          </label>
          <div className="grid grid-cols-2 gap-3">
            <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
              Port
              <input required type="number" min={1} max={65535} value={form.port} onChange={(e) => set({ port: Number(e.target.value) })} className={field} />
            </label>
            <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
              Security
              <select value={form.security} onChange={(e) => set({ security: e.target.value })} className={field}>
                <option value="StartTls">STARTTLS (587)</option>
                <option value="SslOnConnect">SSL/TLS (465)</option>
                <option value="None">None (local relay only)</option>
              </select>
            </label>
          </div>
          <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
            Username
            <input value={form.username ?? ''} onChange={(e) => set({ username: e.target.value })} placeholder="reports@yourmsp.com" autoComplete="off" className={field} />
          </label>
          <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
            Password
            <input type="password" value={form.password ?? ''} onChange={(e) => set({ password: e.target.value })}
              placeholder={secretSaved ? 'Saved — leave blank to keep it' : 'Password or API key'} autoComplete="new-password" className={field} />
          </label>
        </>
      )}

      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        {graph ? 'Send from (mailbox)' : 'Send from (address)'}
        <input required type="email" value={form.fromAddress} onChange={(e) => set({ fromAddress: e.target.value })} placeholder="reports@yourmsp.com" className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Send from (name)
        <input value={form.fromName ?? ''} onChange={(e) => set({ fromName: e.target.value })} placeholder="Desk Portal" className={field} />
      </label>

      <p className="text-xs text-[var(--faint)] sm:col-span-2">
        {graph
          ? 'The mailbox must exist in that tenant (a shared mailbox works). The secret is stored encrypted and never shown again. After saving, use Send test email to confirm it works.'
          : 'The password is stored encrypted and is never shown again. After saving, use Send test email to confirm it works.'}
      </p>
      {(save.isError || remove.isError) && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400 sm:col-span-2">
          {((save.error ?? remove.error) as Error).message}
        </p>
      )}
      <div className="flex flex-wrap items-center gap-2 sm:col-span-2">
        {current?.hasOwnAccount && (
          <button type="button" disabled={remove.isPending}
            onClick={() => { if (window.confirm('Remove this mail account? Reports will stop being emailed unless the server has a default account.')) remove.mutate(); }}
            className="rounded-lg px-3 py-1.5 text-sm font-medium text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-950/40">
            Remove account
          </button>
        )}
        <span className="ml-auto flex gap-2">
          <button type="button" onClick={onClose} className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
          <button type="submit" disabled={save.isPending} className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
        </span>
      </div>
    </form>
  );
}
