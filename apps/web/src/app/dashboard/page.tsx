'use client';

import Link from 'next/link';
import { MyApprovalsBanner } from '@/components/MyApprovalsBanner';
import { useMemo } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  RefreshCw, Plus, Calendar, TrendingUp, Clock, ShieldCheck, Plug, Ticket as TicketIcon,
  CheckCircle2, Inbox, ArrowUpRight,
  AlarmClock,
} from 'lucide-react';
import { MiniSpark, TrendChart, Donut } from '@/components/charts';
import { StatusBadge, PriorityBadge } from '@/components/badges';
import { api } from '@/lib/api';
import { isStaffPermissions } from '@/lib/staff';
const PRIORITY_META: Record<string, { color: string; order: number }> = {
  CRITICAL: { color: '#ef4444', order: 0 }, HIGH: { color: '#f97316', order: 1 },
  NORMAL: { color: '#3b82f6', order: 2 }, LOW: { color: '#94a3b8', order: 3 },
};
const shortDate = (iso: string) =>
  new Date(iso + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
function ago(iso: string): string {
  const s = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} hr ago`;
  return `${Math.floor(s / 86400)}d ago`;
}

function Card({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`rounded-xl border border-[var(--border)] bg-[var(--surface)] ${className}`}>{children}</div>;
}
function Head({ title, right }: { title: string; right?: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3.5">
      <h2 className="text-sm font-semibold">{title}</h2>{right}
    </div>
  );
}

export default function Overview() {
  const qc = useQueryClient();
  // Counted on the server. This page used to load every ticket the desk holds to count them, which
  // slows with every ticket ever synced.
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const isStaff = me ? isStaffPermissions(me.permissions) : false;
  const { data: breakdown } = useQuery({ queryKey: ['tickets', 'breakdown', 'all'], queryFn: () => api.ticketBreakdown() });
  const { data: recent } = useQuery({ queryKey: ['tickets', 'page', 'recent'], queryFn: () => api.ticketPage({ take: 5 }) });
  // Due dates are staff information; a client's list never carried them, so a client has no banners.
  const { data: summary } = useQuery({ queryKey: ['tickets', 'summary', false], queryFn: () => api.ticketSummary(false), enabled: isStaff });
  // A real 7-day window behind the "Last 7 days" this page shows. Both queries were unwindowed, so
  // SLA, the resolved count and the chart all quietly covered every ticket ever held.
  const fromIso = useMemo(() => new Date(Date.now() - 7 * 86400_000).toISOString(), []);
  const { data: team } = useQuery({ queryKey: ['team', 'last-7-days'], queryFn: () => api.teamMetrics(fromIso) });
  const { data: trend } = useQuery({ queryKey: ['trend', 'last-7-days'], queryFn: () => api.trend(fromIso) });
  const { data: health } = useQuery({ queryKey: ['health'], queryFn: api.health });
  const { data: activity } = useQuery({ queryKey: ['notifications'], queryFn: api.notifications });

  const recentRows = recent?.items ?? [];
  const totalTickets = breakdown?.total ?? 0;
  // Open is a state, not an event: every ticket open right now, whenever it was raised. Classified by
  // status on the server with the same rule the list's Open view uses.
  const open = breakdown?.open ?? 0;
  const teamRows = team?.team ?? [];
  const totalResolved = teamRows.reduce((a, r) => a + r.resolved, 0) || 1;
  const slaPct = teamRows.length
    ? teamRows.reduce((a, r) => a + r.slaCompliancePct * r.resolved, 0) / totalResolved
    : 0;
  const connections = health ?? [];

  // The last 7 DAYS, not the last 7 data points - the trend only carries days that had activity.
  const cutoffDay = fromIso.slice(0, 10);
  const trendRows = (trend ?? []).filter((p) => p.date >= cutoffDay);
  const trendLabels = trendRows.map((p) => shortDate(p.date));
  const created = trendRows.map((p) => p.created);
  const resolvedSeries = trendRows.map((p) => p.resolved);
  // Resolutions in the last 7 days, by resolution date - including tickets raised before the window.
  const resolved = resolvedSeries.reduce((a, b) => a + b, 0);

  const byPriority = (breakdown?.byPriority ?? [])
    .map(({ label, count }) => ({ label, value: count, color: PRIORITY_META[label]?.color ?? '#94a3b8', order: PRIORITY_META[label]?.order ?? 9 }))
    .sort((a, b) => a.order - b.order);

  // SLA at a glance, counted with exactly the rules the linked lists use (the ticket list's Overdue
  // and Due soon views), so the number on the banner is the number of rows the click shows. A
  // client's list carries no due dates, so for a client this is always nothing and never shows.
  const pastSla = summary?.overdue ?? 0;
  const dueSoon = summary?.dueSoon ?? 0;

  const stats = [
    { label: 'Open Tickets', value: open, sub: `${totalTickets} total`, icon: Inbox, tone: 'blue', spark: created, color: '#3b82f6', href: '/dashboard/tickets?view=open' },
    { label: 'Resolved', value: resolved, sub: 'last 7 days', icon: CheckCircle2, tone: 'green', spark: resolvedSeries, color: '#22c55e', href: '/dashboard/tickets?view=resolved' },
    { label: 'SLA Compliance', value: `${slaPct.toFixed(1)}%`, sub: 'weighted across techs', icon: ShieldCheck, tone: 'violet', spark: null, color: '#8b5cf6', href: '/dashboard/analytics' },
    { label: 'Active Connections', value: connections.length, sub: 'monitored', icon: Plug, tone: 'orange', spark: null, color: '#f97316', href: '/dashboard/connections' },
  ];
  const toneBg: Record<string, string> = {
    blue: 'bg-blue-50 text-blue-600 dark:bg-blue-950/50 dark:text-blue-300',
    green: 'bg-green-50 text-green-600 dark:bg-green-950/50 dark:text-green-300',
    violet: 'bg-violet-50 text-violet-600 dark:bg-violet-950/50 dark:text-violet-300',
    orange: 'bg-orange-50 text-orange-600 dark:bg-orange-950/50 dark:text-orange-300',
  };

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Overview</h1>
          <p className="text-sm text-[var(--muted)]">Ticket operations across all connected PSA systems.</p>
        </div>
        <div className="flex items-center gap-2">
          <span className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm text-[var(--muted)]"><Calendar size={15} /> Last 7 days</span>
          <button onClick={() => qc.invalidateQueries()} className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]"><RefreshCw size={15} /> Refresh</button>
          <Link href="/dashboard/tickets/new" className="inline-flex items-center gap-2 rounded-lg bg-brand px-3.5 py-2 text-sm font-medium text-brand-fg hover:opacity-90"><Plus size={16} /> New Ticket</Link>
        </div>
      </div>

      <MyApprovalsBanner />

      {(pastSla > 0 || dueSoon > 0) && (
        <div role="status" className="flex flex-wrap items-center gap-x-4 gap-y-1 rounded-xl border border-red-200 bg-red-50 px-4 py-2.5 text-sm dark:border-red-900/60 dark:bg-red-950/30">
          <AlarmClock size={16} className="text-red-600 dark:text-red-400" aria-hidden="true" />
          {pastSla > 0 && (
            <Link href="/dashboard/tickets?overdue=1" className="font-medium text-red-700 hover:underline dark:text-red-300">
              {pastSla} open ticket{pastSla === 1 ? '' : 's'} past {pastSla === 1 ? 'its' : 'their'} SLA
            </Link>
          )}
          {dueSoon > 0 && (
            <Link href="/dashboard/tickets?due=soon" className="font-medium text-amber-800 hover:underline dark:text-amber-300">
              {dueSoon} due within 8 hours
            </Link>
          )}
          <span className="text-xs text-[var(--muted)]">Tickets whose SLA is paused while waiting are not counted.</span>
        </div>
      )}

      {/* Stat cards */}
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {stats.map((s) => {
          const Icon = s.icon;
          return (
            <Link key={s.label} href={s.href} className="group block rounded-xl focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand">
              <Card className="p-4 transition-colors group-hover:border-brand">
                <div className="flex items-start justify-between">
                  <div>
                    <div className="flex items-center gap-1 text-sm text-[var(--muted)]">
                      {s.label}
                      <ArrowUpRight size={13} className="opacity-0 transition-opacity group-hover:opacity-100" aria-hidden="true" />
                    </div>
                    <div className="mt-1 text-2xl font-semibold tabular-nums">{s.value}</div>
                    <div className="text-xs text-[var(--faint)]">{s.sub}</div>
                  </div>
                  <span className={`inline-flex h-9 w-9 items-center justify-center rounded-lg ${toneBg[s.tone]}`}><Icon size={17} /></span>
                </div>
                {s.spark && s.spark.length > 1 && <div className="mt-2"><MiniSpark points={s.spark} color={s.color} width={150} height={30} /></div>}
              </Card>
            </Link>
          );
        })}
      </div>

      {/* Trend + recent + health */}
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-12">
        <Card className="xl:col-span-5">
          <Head title="Created vs Resolved" right={<span className="text-xs text-[var(--muted)]">Last 7 days</span>} />
          <div className="px-4 pb-2 pt-3">
            <div className="mb-1 flex gap-4 text-xs text-[var(--muted)]">
              <span className="flex items-center gap-1.5"><i className="h-2 w-2 rounded-full bg-[#3b82f6]" /> Created</span>
              <span className="flex items-center gap-1.5"><i className="h-2 w-2 rounded-full bg-[#22c55e]" /> Resolved</span>
            </div>
            {trendRows.length > 0
              ? <TrendChart labels={trendLabels} created={created} resolved={resolvedSeries} height={210} />
              : <div className="py-12 text-center text-sm text-[var(--muted)]">No trend data.</div>}
          </div>
        </Card>

        <Card className="xl:col-span-4">
          <Head title="Recent Tickets" right={<Link href="/dashboard/tickets" className="text-xs font-medium text-brand hover:underline">View all</Link>} />
          <ul className="divide-y divide-[var(--border)]">
            {recentRows.map((t) => (
              <li key={t.id} className="px-5 py-2.5">
                <Link href={`/dashboard/tickets/${t.id}`} className="flex items-center justify-between gap-2">
                  <span className="min-w-0">
                    <span className="block truncate text-sm font-medium">{t.title}</span>
                    <span className="text-xs text-[var(--muted)]">{t.externalTicketId ? `#${t.externalTicketId} · ` : ''}{t.queueOrBoard ?? '—'}</span>
                  </span>
                  <PriorityBadge priority={t.portalPriority} />
                </Link>
              </li>
            ))}
            {recent && recentRows.length === 0 && <li className="px-5 py-6 text-center text-sm text-[var(--muted)]">No tickets.</li>}
          </ul>
        </Card>

        <Card className="xl:col-span-3">
          <Head title="Integration Health" right={<Link href="/dashboard/health" className="text-xs font-medium text-brand hover:underline">Details</Link>} />
          <ul className="divide-y divide-[var(--border)]">
            {connections.map((h) => (
              <li key={h.connectionId} className="flex items-center justify-between px-5 py-3 text-sm">
                <span className="flex items-center gap-2"><Plug size={14} className="text-[var(--muted)]" />{h.name}</span>
                <span className={`inline-flex items-center gap-1 text-xs font-medium ${Number(h.status) === 2 ? 'text-green-600 dark:text-green-400' : 'text-amber-600 dark:text-amber-400'}`}>
                  <span className={`h-1.5 w-1.5 rounded-full ${Number(h.status) === 2 ? 'bg-green-500' : 'bg-amber-500'}`} />
                  {Number(h.status) === 2 ? 'Healthy' : 'Degraded'}
                </span>
              </li>
            ))}
            {connections.length === 0 && <li className="px-5 py-6 text-center text-sm text-[var(--muted)]">No connections.</li>}
          </ul>
        </Card>
      </div>

      {/* Technician performance + priority + activity */}
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
        <Card>
          <Head title="Technician Performance" right={<Link href="/dashboard/analytics" className="text-xs font-medium text-brand hover:underline">Analytics</Link>} />
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
                <tr className="border-b border-[var(--border)]"><th className="px-5 py-2 font-medium">Technician</th><th className="px-2 py-2 font-medium">Resolved</th><th className="px-5 py-2 font-medium">SLA</th></tr>
              </thead>
              <tbody>
                {teamRows.slice(0, 5).map((r) => {
                  const who = r.technicianName ?? r.technicianExternalId;
                  return (
                  <tr key={r.technicianExternalId} className="border-b border-[var(--border)] last:border-0 hover:bg-[var(--bg)]">
                    <td className="px-5 py-2.5">
                      <Link href="/dashboard/analytics/technicians" className="flex items-center gap-2 hover:text-brand">
                        <span className="flex h-6 w-6 items-center justify-center rounded-full bg-[var(--bg)] text-[9px] font-semibold">{who.split(' ').map((n) => n[0]).slice(0, 2).join('')}</span>{who}
                      </Link>
                    </td>
                    <td className="px-2 py-2.5 tabular-nums">{r.resolved}</td>
                    <td className="px-5 py-2.5"><span className="flex items-center gap-1.5"><span className="tabular-nums text-xs">{r.slaCompliancePct.toFixed(0)}%</span><span className="h-1.5 w-10 overflow-hidden rounded-full bg-[var(--bg)]"><span className="block h-full rounded-full bg-green-500" style={{ width: `${r.slaCompliancePct}%` }} /></span></span></td>
                  </tr>
                  );
                })}
                {teamRows.length === 0 && <tr><td colSpan={3} className="px-5 py-6 text-center text-sm text-[var(--muted)]">No data.</td></tr>}
              </tbody>
            </table>
          </div>
        </Card>

        <Card>
          <Head title="Tickets by Priority" />
          <div className="flex items-center gap-3 px-5 py-4">
            {byPriority.length > 0 ? <Donut segments={byPriority} total={totalTickets} size={150} /> : <div className="py-8 text-sm text-[var(--muted)]">No tickets.</div>}
            <ul className="space-y-1.5 text-xs">
              {byPriority.map((d) => (
                <li key={d.label}>
                  <Link href={`/dashboard/tickets?priority=${encodeURIComponent(d.label)}`}
                    className="flex items-center gap-2 rounded px-1 py-0.5 hover:bg-[var(--bg)] hover:text-brand">
                    <i className="h-2.5 w-2.5 rounded-sm" style={{ background: d.color }} />
                    <span className="capitalize">{d.label.toLowerCase()}</span>
                    <span className="ml-auto tabular-nums text-[var(--muted)]">{d.value}</span>
                  </Link>
                </li>
              ))}
            </ul>
          </div>
        </Card>

        <Card>
          <Head title="Recent Activity" right={<Link href="/dashboard/notifications" className="text-xs font-medium text-brand hover:underline">View all</Link>} />
          <ul className="px-5 py-3">
            {(activity ?? []).slice(0, 5).map((a) => (
              <li key={a.ticketId} className="flex gap-3 pb-3.5 last:pb-1">
                <TicketIcon size={15} className="mt-0.5 shrink-0 text-brand" />
                <div className="min-w-0">
                  <div className="truncate text-sm font-medium">{a.title}</div>
                  <div className="text-xs text-[var(--muted)]">{a.summary} · {ago(a.at)}</div>
                </div>
              </li>
            ))}
            {(!activity || activity.length === 0) && <li className="py-4 text-sm text-[var(--muted)]">No recent activity.</li>}
          </ul>
        </Card>
      </div>
    </div>
  );
}
