'use client';

import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  RefreshCw, ChevronDown, Filter, Clock, Mail, AlertOctagon, CheckCircle2, Layers,
  Plus, ArrowRight, AlertTriangle, Activity, RotateCw, Check,
} from 'lucide-react';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { api, type UnsyncedTicket } from '@/lib/api';
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
          <button onClick={() => { qc.invalidateQueries({ queryKey: ['health'] }); qc.invalidateQueries({ queryKey: ['audit'] }); }}
            className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">
            <RefreshCw size={15} /> Refresh
          </button>
          <button className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">
            <Filter size={15} /> All Connections <ChevronDown size={14} className="text-[var(--faint)]" />
          </button>
        </div>
      </div>

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
 * Outbound email: whether the server has a mail account, and a one-click proof that it works.
 * The account itself is set on the server, not here, so it is shown but never editable.
 */
function EmailDeliveryCard() {
  const { data, isError } = useQuery({ queryKey: ['email-status'], queryFn: api.emailStatus, retry: false });
  const [to, setTo] = useState('');
  const test = useMutation({ mutationFn: () => api.sendTestEmail(to.trim() || undefined) });
  if (isError || !data) return null;

  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3">
      <span className={`inline-flex h-9 w-9 items-center justify-center rounded-lg ${data.configured
        ? 'bg-green-50 text-green-600 dark:bg-green-950/50 dark:text-green-300'
        : 'bg-amber-50 text-amber-600 dark:bg-amber-950/50 dark:text-amber-300'}`}>
        <Mail size={17} aria-hidden="true" />
      </span>
      <div className="min-w-0 flex-1">
        <div className="text-sm font-semibold">Email delivery</div>
        <div className="text-xs text-[var(--muted)]">
          {data.configured
            ? <>Scheduled reports are emailed from <span className="font-medium text-[var(--fg)]">{data.from}</span>.</>
            : 'Not set up — scheduled reports are saved in the portal but not emailed. The mail account is added on the server.'}
        </div>
      </div>
      {data.configured && (
        <form className="flex flex-wrap items-center gap-2" onSubmit={(e) => { e.preventDefault(); test.mutate(); }}>
          <input type="email" value={to} onChange={(e) => setTo(e.target.value)} placeholder="Your address"
            aria-label="Send test email to"
            className="w-52 rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2.5 py-1.5 text-sm outline-none focus:border-brand" />
          <button type="submit" disabled={test.isPending}
            className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
            {test.isPending ? 'Sending…' : 'Send test email'}
          </button>
          {test.data && (
            <span role="status" className={`text-xs font-medium ${test.data.sent ? 'text-green-600 dark:text-green-400' : 'text-red-600 dark:text-red-400'}`}>
              {test.data.message}
            </span>
          )}
          {test.isError && <span role="status" className="text-xs font-medium text-red-600 dark:text-red-400">Could not reach the server.</span>}
        </form>
      )}
    </div>
  );
}
