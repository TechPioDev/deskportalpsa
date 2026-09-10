'use client';

import { useMemo, useState } from 'react';
import { useIsFetching, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Calendar, ChevronDown, Gauge, ClipboardList, CheckCircle2, FolderOpen, ShieldCheck, Sparkles,
  TrendingUp, RefreshCw, Users, Info, LayoutGrid,
} from 'lucide-react';
import { MiniSpark, TrendChart, BarChart, Donut } from '@/components/charts';
import { api } from '@/lib/api';
import { isResolvedStatus } from '@/lib/status';

const RANGES = [7, 30, 90] as const;
const PRIORITY_META: Record<string, { color: string; order: number }> = {
  CRITICAL: { color: '#ef4444', order: 0 }, HIGH: { color: '#f97316', order: 1 },
  NORMAL: { color: '#3b82f6', order: 2 }, LOW: { color: '#94a3b8', order: 3 },
};
const QUEUE_COLORS = ['#3b82f6', '#8b5cf6', '#22c55e', '#f97316', '#ef4444'];
const first = (name: string) => name.split(' ')[0];
const shortDate = (iso: string) => new Date(iso + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
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
function Head({ title, right, hint }: { title: string; right?: React.ReactNode; hint?: boolean }) {
  return (
    <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3.5">
      <h2 className="flex items-center gap-1.5 text-sm font-semibold">{title}{hint && <Info size={13} className="text-[var(--faint)]" />}</h2>{right}
    </div>
  );
}
const tone: Record<string, string> = {
  blue: 'bg-blue-50 text-blue-600 dark:bg-blue-950/50 dark:text-blue-300',
  green: 'bg-green-50 text-green-600 dark:bg-green-950/50 dark:text-green-300',
  orange: 'bg-orange-50 text-orange-600 dark:bg-orange-950/50 dark:text-orange-300',
  violet: 'bg-violet-50 text-violet-600 dark:bg-violet-950/50 dark:text-violet-300',
};

export default function Analytics() {
  const qc = useQueryClient();
  const fetching = useIsFetching();
  const [days, setDays] = useState<number>(7);
  const fromIso = useMemo(() => new Date(Date.now() - days * 86400_000).toISOString(), [days]);

  const { data: team } = useQuery({ queryKey: ['team', days], queryFn: () => api.teamMetrics(fromIso) });
  const { data: trend } = useQuery({ queryKey: ['trend', days], queryFn: () => api.trend(fromIso) });
  const { data: tickets } = useQuery({ queryKey: ['tickets'], queryFn: api.listTickets });
  const { data: activity } = useQuery({ queryKey: ['notifications'], queryFn: api.notifications });

  const rows = [...(team?.team ?? [])].sort((a, b) => (b.score ?? 0) - (a.score ?? 0));
  // The ticket list isn't range-filtered server-side; apply the window client-side so every KPI
  // reflects the selected range. Resolved is classified by status (tolerating raw PSA values),
  // never inferred as "not open" — unmapped statuses count as open.
  const cutoff = Date.now() - days * 86400_000;
  const ts = (tickets ?? []).filter((t) => new Date(t.createdAt).getTime() >= cutoff);
  const assigned = ts.length;
  const resolvedCount = ts.filter((t) => isResolvedStatus(t.portalStatus)).length;
  const open = assigned - resolvedCount;
  const totalResolved = rows.reduce((a, r) => a + r.resolved, 0);
  const slaPct = totalResolved > 0 ? rows.reduce((a, r) => a + r.slaCompliancePct * r.resolved, 0) / totalResolved : 0;
  const score = rows.length ? rows.reduce((a, r) => a + (r.score ?? 0), 0) / rows.length : 0;
  const scoreLabel = score >= 90 ? 'Excellent' : score >= 75 ? 'Good' : score >= 60 ? 'Fair' : 'Needs attention';

  const trendRows = (trend ?? []).slice(-days);
  const labelStep = Math.max(1, Math.ceil(trendRows.length / 8)); // thin x labels on long ranges
  const trendLabels = trendRows.map((p, i) => (i % labelStep === 0 ? shortDate(p.date) : ''));
  const created = trendRows.map((p) => p.created);
  const resolvedSeries = trendRows.map((p) => p.resolved);

  const byPriority = Object.entries(
    ts.reduce<Record<string, number>>((m, t) => { const k = t.portalPriority.toUpperCase(); m[k] = (m[k] ?? 0) + 1; return m; }, {}),
  ).map(([label, value]) => ({ label, value, color: PRIORITY_META[label]?.color ?? '#94a3b8', order: PRIORITY_META[label]?.order ?? 9 }))
   .sort((a, b) => a.order - b.order);

  const byQueue = Object.entries(
    ts.reduce<Record<string, number>>((m, t) => { const k = t.queueOrBoard ?? 'Unassigned'; m[k] = (m[k] ?? 0) + 1; return m; }, {}),
  ).map(([label, value], i) => ({ label, value, color: QUEUE_COLORS[i % QUEUE_COLORS.length] }))
   .sort((a, b) => b.value - a.value);
  const maxQueue = Math.max(1, ...byQueue.map((q) => q.value));

  const maxResolved = Math.max(1, ...rows.map((r) => r.resolved));
  const workload = rows.map((r) => ({ name: r.technicianExternalId, pct: Math.round((r.resolved / maxResolved) * 100) }))
    .sort((a, b) => b.pct - a.pct);

  const top = rows[0];
  const insights = [
    totalResolved > 0
      ? { icon: ShieldCheck, color: '#22c55e', title: `SLA compliance at ${slaPct.toFixed(1)}%`, sub: 'Weighted across resolved tickets' }
      : { icon: ShieldCheck, color: '#94a3b8', title: 'No resolved tickets yet', sub: 'SLA compliance appears after resolutions' },
    { icon: FolderOpen, color: '#f59e0b', title: `${open} open tickets`, sub: `${resolvedCount} resolved of ${assigned} total` },
    top ? { icon: Users, color: '#3b82f6', title: `Top performer: ${top.technicianExternalId}`, sub: `Score ${top.score?.toFixed(1) ?? '—'} · ${top.resolved} resolved` } : null,
    { icon: TrendingUp, color: '#8b5cf6', title: `${created.reduce((a, b) => a + b, 0)} created in this range`, sub: `${resolvedSeries.reduce((a, b) => a + b, 0)} resolved in the same period` },
  ].filter(Boolean) as { icon: React.ElementType; color: string; title: string; sub: string }[];

  const kpis = [
    { label: 'Assigned Tickets', value: assigned.toLocaleString(), icon: ClipboardList, tone: 'blue', spark: created, color: '#3b82f6' },
    { label: 'Resolved Tickets', value: resolvedCount.toLocaleString(), icon: CheckCircle2, tone: 'green', spark: resolvedSeries, color: '#22c55e' },
    { label: 'Open Tickets', value: open.toLocaleString(), icon: FolderOpen, tone: 'orange', spark: null, color: '#f97316' },
    { label: 'SLA Compliance', value: `${slaPct.toFixed(1)}%`, icon: ShieldCheck, tone: 'violet', spark: null, color: '#8b5cf6' },
  ];

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Productivity Analytics</h1>
          <p className="text-sm text-[var(--muted)]">Technician and team performance across connected systems.</p>
        </div>
        <div className="flex items-center gap-2">
          <label className="relative inline-flex items-center">
            <Calendar size={15} className="pointer-events-none absolute left-3 text-[var(--muted)]" />
            <select value={days} onChange={(e) => setDays(Number(e.target.value))} aria-label="Date range"
              className="cursor-pointer appearance-none rounded-lg border border-[var(--border)] bg-[var(--surface)] py-2 pl-9 pr-8 text-sm outline-none focus:border-brand">
              {RANGES.map((d) => <option key={d} value={d}>Last {d} days</option>)}
            </select>
            <ChevronDown size={14} className="pointer-events-none absolute right-2.5 text-[var(--faint)]" />
          </label>
          <button onClick={() => qc.invalidateQueries()} disabled={fetching > 0}
            className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-60">
            <RefreshCw size={15} className={fetching > 0 ? 'animate-spin' : ''} /> {fetching > 0 ? 'Refreshing…' : 'Refresh'}
          </button>
        </div>
      </div>

      {/* Score + KPIs + Insights */}
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-4">
        <div className="xl:col-span-3">
          <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 xl:grid-cols-5">
            <Card className="p-4">
              <div className="flex items-center gap-2 text-sm text-[var(--muted)]"><Gauge size={16} className="text-brand" /> Productivity Score</div>
              <div className="mt-1 text-3xl font-semibold text-brand">{score.toFixed(0)}%</div>
              <div className="mt-3 h-1.5 rounded-full bg-[var(--bg)]"><div className="h-full rounded-full bg-brand" style={{ width: `${Math.min(100, score)}%` }} /></div>
              <div className="mt-2 text-xs text-[var(--muted)]">{scoreLabel} · team average</div>
            </Card>
            {kpis.map((k) => {
              const Icon = k.icon;
              return (
                <Card key={k.label} className="p-4">
                  <div className={`inline-flex h-9 w-9 items-center justify-center rounded-lg ${tone[k.tone]}`}><Icon size={17} /></div>
                  <div className="mt-2 text-sm text-[var(--muted)]">{k.label}</div>
                  <div className="text-2xl font-semibold tabular-nums">{k.value}</div>
                  {k.spark && k.spark.length > 1 ? <div className="mt-2"><MiniSpark points={k.spark} color={k.color} width={140} height={28} /></div> : <div className="mt-2 h-7" />}
                </Card>
              );
            })}
          </div>
          <p className="mt-2 text-[11px] text-[var(--faint)]">
            Productivity scores are operational indicators only and must not be used as the sole basis for employee performance decisions.
          </p>
        </div>

        <Card className="p-4 xl:col-span-1">
          <div className="mb-3 flex items-center gap-2 text-sm font-semibold"><Sparkles size={16} className="text-brand" /> Insights</div>
          <ul className="space-y-3">
            {insights.map((it, i) => {
              const Icon = it.icon;
              return (
                <li key={i} className="flex gap-2.5">
                  <span className="mt-0.5"><Icon size={16} style={{ color: it.color }} /></span>
                  <div><div className="text-sm font-medium leading-tight">{it.title}</div><div className="text-xs text-[var(--muted)]">{it.sub}</div></div>
                </li>
              );
            })}
          </ul>
        </Card>
      </div>

      {/* Charts + Recent activities */}
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-4">
        <div className="grid grid-cols-1 gap-4 md:grid-cols-3 xl:col-span-3">
          <Card>
            <Head title="Created vs Resolved" right={<span className="text-xs text-[var(--muted)]">Last {days} days</span>} />
            <div className="px-4 pb-2 pt-3">
              <div className="mb-1 flex gap-4 text-xs text-[var(--muted)]">
                <span className="flex items-center gap-1.5"><i className="h-2 w-2 rounded-full bg-[#3b82f6]" /> Created</span>
                <span className="flex items-center gap-1.5"><i className="h-2 w-2 rounded-full bg-[#22c55e]" /> Resolved</span>
              </div>
              {trendRows.length > 0 ? <TrendChart labels={trendLabels} created={created} resolved={resolvedSeries} height={200} /> : <div className="py-12 text-center text-sm text-[var(--muted)]">No data.</div>}
            </div>
          </Card>
          <Card>
            <Head title="Resolved per Day" hint right={<span className="text-xs text-[var(--muted)]">Last {days} days</span>} />
            <div className="px-4 pb-2 pt-3">
              <div className="text-2xl font-semibold">{resolvedSeries.reduce((a, b) => a + b, 0)}</div>
              <div className="mb-2 text-xs text-[var(--muted)]">tickets resolved in this range</div>
              {trendRows.length > 0 ? <BarChart values={resolvedSeries} labels={trendLabels} color="#22c55e" height={170} /> : <div className="py-10 text-center text-sm text-[var(--muted)]">No data.</div>}
            </div>
          </Card>
          <Card>
            <Head title="SLA by Technician" hint right={<span className="text-xs text-[var(--muted)]">%</span>} />
            <div className="px-4 pb-2 pt-3">
              <div className="text-2xl font-semibold">{slaPct.toFixed(1)}%</div>
              <div className="mb-2 text-xs text-[var(--muted)]">weighted team compliance</div>
              {rows.length > 0 ? <BarChart values={rows.map((r) => Math.round(r.slaCompliancePct))} labels={rows.map((r) => first(r.technicianName ?? r.technicianExternalId))} color="#3b82f6" height={170} unit="%" /> : <div className="py-10 text-center text-sm text-[var(--muted)]">No data.</div>}
            </div>
          </Card>
        </div>

        <Card className="xl:col-span-1">
          <Head title="Recent Activities" right={<a href="/dashboard/notifications" className="text-xs font-medium text-brand hover:underline">View all</a>} />
          <ul className="px-5 py-3">
            {(activity ?? []).slice(0, 6).map((a) => (
              <li key={a.ticketId} className="flex gap-3 pb-4 last:pb-1">
                <CheckCircle2 size={15} className="mt-0.5 shrink-0 text-brand" />
                <div className="min-w-0">
                  <div className="truncate text-sm font-medium leading-tight">{a.title}</div>
                  <div className="text-xs text-[var(--muted)]">{a.summary} · {ago(a.at)}</div>
                </div>
              </li>
            ))}
            {(!activity || activity.length === 0) && <li className="py-4 text-sm text-[var(--muted)]">No recent activity.</li>}
          </ul>
        </Card>
      </div>

      {/* Bottom row */}
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4">
        <Card>
          <Head title="Technician Performance" />
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
                <tr className="border-b border-[var(--border)]"><th className="px-4 py-2 font-medium">Technician</th><th className="px-2 py-2 font-medium">Resolved</th><th className="px-2 py-2 font-medium">SLA</th><th className="px-4 py-2 font-medium">Score</th></tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.technicianExternalId} className="border-b border-[var(--border)] last:border-0">
                    <td className="px-4 py-2.5"><span className="flex items-center gap-2"><span className="flex h-6 w-6 items-center justify-center rounded-full bg-[var(--bg)] text-[9px] font-semibold">{(r.technicianName ?? r.technicianExternalId).split(' ').map((n) => n[0]).slice(0, 2).join('')}</span>{r.technicianName ?? r.technicianExternalId}</span></td>
                    <td className="px-2 py-2.5 tabular-nums">{r.resolved}</td>
                    <td className="px-2 py-2.5 tabular-nums text-[var(--muted)]">{r.slaCompliancePct.toFixed(0)}%</td>
                    <td className="px-4 py-2.5"><span className="tabular-nums font-medium">{r.score?.toFixed(1) ?? '—'}</span></td>
                  </tr>
                ))}
                {rows.length === 0 && <tr><td colSpan={4} className="px-4 py-6 text-center text-sm text-[var(--muted)]">No data.</td></tr>}
              </tbody>
            </table>
          </div>
        </Card>

        <Card>
          <Head title="Ticket Distribution" />
          <div className="flex items-center gap-3 px-5 py-4">
            {byPriority.length > 0 ? <Donut segments={byPriority} total={assigned} size={150} /> : <div className="py-8 text-sm text-[var(--muted)]">No tickets.</div>}
            <ul className="space-y-1.5 text-xs">
              {byPriority.map((d) => (
                <li key={d.label} className="flex items-center gap-2"><i className="h-2.5 w-2.5 rounded-sm" style={{ background: d.color }} /><span className="capitalize">{d.label.toLowerCase()}</span><span className="ml-auto tabular-nums text-[var(--muted)]">{d.value} ({((d.value / (assigned || 1)) * 100).toFixed(1)}%)</span></li>
              ))}
            </ul>
          </div>
        </Card>

        <Card>
          <Head title="Technician Workload" />
          <div className="space-y-3 px-5 py-4">
            {workload.map((w) => (
              <div key={w.name}>
                <div className="mb-1 flex items-center justify-between text-sm">
                  <span className="flex items-center gap-2"><span className="flex h-6 w-6 items-center justify-center rounded-full bg-[var(--bg)] text-[9px] font-semibold">{w.name.split(' ').map((n) => n[0]).join('')}</span>{w.name}</span>
                  <span className="tabular-nums text-[var(--muted)]">{w.pct}%</span>
                </div>
                <div className="h-1.5 rounded-full bg-[var(--bg)]"><div className="h-full rounded-full" style={{ width: `${w.pct}%`, background: w.pct >= 85 ? '#ef4444' : w.pct >= 60 ? '#f59e0b' : '#22c55e' }} /></div>
              </div>
            ))}
            {workload.length === 0 && <div className="py-4 text-sm text-[var(--muted)]">No data.</div>}
          </div>
        </Card>

        <Card>
          <Head title="Tickets by Queue" right={<LayoutGrid size={14} className="text-[var(--faint)]" />} />
          <div className="space-y-3 px-5 py-4">
            {byQueue.map((q) => (
              <div key={q.label}>
                <div className="mb-1 flex items-center justify-between text-sm"><span className="truncate">{q.label}</span><span className="tabular-nums text-[var(--muted)]">{q.value}</span></div>
                <div className="h-1.5 rounded-full bg-[var(--bg)]"><div className="h-full rounded-full" style={{ width: `${(q.value / maxQueue) * 100}%`, background: q.color }} /></div>
              </div>
            ))}
            {byQueue.length === 0 && <div className="py-4 text-sm text-[var(--muted)]">No tickets.</div>}
          </div>
        </Card>
      </div>
    </div>
  );
}
