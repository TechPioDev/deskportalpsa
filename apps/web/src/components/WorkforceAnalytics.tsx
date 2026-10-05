'use client';

import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import Link from 'next/link';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { Download, RefreshCw, X } from 'lucide-react';
import {
  api, ApiError, type AnalyticsDay, type AnalyticsExportReport, type AnalyticsFigures, type AnalyticsFilterOptions, type AnalyticsGroup, type AnalyticsOverview,
  type AnalyticsPerson, type AnalyticsQuery, type AnalyticsWorkKind, type AnalyticsWorkRow, type Heatmap as HeatmapData, type TechnicianAnalytics,
} from '@/lib/api';
import { hours } from '@/components/Workforce';
import { fmtDay } from '@/components/WorkforceCapacity';
import { fmtDuration } from '@/components/WorkTime';

/**
 * Workforce analytics (Phase 7): how work is distributed, planned, executed and completed over a
 * period, for the people the viewer may see. Every figure is a fact defined in
 * docs/workforce-scheduling/PHASE7_ANALYTICS_METRIC_SPEC.md; every card opens the records that make
 * it; nothing is a score and nobody is ranked. Filters live in the query string so a view can be
 * bookmarked and shared inside the team.
 */

const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm outline-none focus:border-brand disabled:opacity-60';
const btn = 'inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-50';
const card = 'rounded-xl border border-[var(--border)] bg-[var(--surface)]';
const th = 'px-3 py-2 font-medium';
const COLORS = { capacity: '#94a3b8', planned: '#3b82f6', actual: '#22c55e', reactive: '#f59e0b', tentative: '#a78bfa' };

// ---- formatting ----------------------------------------------------------------------------------------

/** A percentage as the screen says it: one decimal when it has one, "N/A" when there was nothing to divide by. */
export const pct = (v: number | null | undefined) => (v == null ? 'N/A' : `${Math.round(v * 10) / 10}%`);
/** Seconds as hours and minutes, "0m" for nothing. */
const dur = (s: number) => (s > 0 ? fmtDuration(s) : '0m');
/** Minutes as hours and minutes, "0m" for nothing. */
const mins = (m: number | null | undefined) => (m == null ? '—' : m > 0 ? hours(m) : '0m');
const signed = (m: number | null | undefined) => (m == null ? 'N/A' : m === 0 ? '0m' : `${m > 0 ? '+' : '−'}${hours(Math.abs(m))}`);
const toHours = (seconds: number) => Math.round((seconds / 3600) * 10) / 10;
const minToHours = (m: number) => Math.round((m / 60) * 10) / 10;
const fmtAt = (iso: string, timeZone: string) => {
  try { return new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hour12: false, timeZone }); } catch { return iso; }
};

// ---- filters in the query string -------------------------------------------------------------------------

export type AnalyticsFilters = {
  period: string; from: string; to: string; teamId: string; departmentId: string; appUserId: string; clientId: string; source: string; priority: string; kind: string;
};
const EMPTY: AnalyticsFilters = { period: 'this-week', from: '', to: '', teamId: '', departmentId: '', appUserId: '', clientId: '', source: '', priority: '', kind: '' };
export const PERIODS: [string, string][] = [
  ['this-week', 'This week'], ['last-week', 'Last week'], ['today', 'Today'], ['yesterday', 'Yesterday'],
  ['this-month', 'This month'], ['last-month', 'Last month'], ['7d', 'Last 7 days'], ['30d', 'Last 30 days'], ['custom', 'Custom range'],
];
const isDate = (s: string) => /^\d{4}-\d{2}-\d{2}$/.test(s);

/** The filters, read from and written to the query string: a bookmarked view is the same view. */
export function useAnalyticsFilters(): [AnalyticsFilters, (patch: Partial<AnalyticsFilters>) => void] {
  const sp = useSearchParams();
  const router = useRouter();
  const pathname = usePathname();
  const filters = useMemo<AnalyticsFilters>(() => ({
    period: sp.get('period') ?? (isDate(sp.get('from') ?? '') ? 'custom' : 'this-week'),
    from: isDate(sp.get('from') ?? '') ? sp.get('from')! : '', to: isDate(sp.get('to') ?? '') ? sp.get('to')! : '',
    teamId: sp.get('team') ?? '', departmentId: sp.get('department') ?? '', appUserId: sp.get('person') ?? '', clientId: sp.get('client') ?? '',
    source: sp.get('source') ?? '', priority: sp.get('priority') ?? '', kind: sp.get('kind') ?? '',
  }), [sp]);
  const set = (patch: Partial<AnalyticsFilters>) => {
    const next = { ...filters, ...patch };
    const qs = new URLSearchParams();
    if (next.period && next.period !== 'this-week') qs.set('period', next.period);
    if (next.period === 'custom') { if (next.from) qs.set('from', next.from); if (next.to) qs.set('to', next.to); }
    for (const [k, v] of [['team', next.teamId], ['department', next.departmentId], ['person', next.appUserId], ['client', next.clientId], ['source', next.source], ['priority', next.priority], ['kind', next.kind]] as const)
      if (v) qs.set(k, v);
    const s = qs.toString();
    router.replace(s ? `${pathname}?${s}` : pathname, { scroll: false });
  };
  return [filters, set];
}

export const toQuery = (f: AnalyticsFilters, appUserId?: string | null): AnalyticsQuery => ({
  period: f.period === 'custom' && !(f.from && f.to) ? 'this-week' : f.period, from: f.period === 'custom' ? f.from : null, to: f.period === 'custom' ? f.to : null,
  teamId: f.teamId, departmentId: f.departmentId, appUserId: appUserId ?? f.appUserId, clientId: f.clientId, source: f.source, priority: f.priority, kind: f.kind,
});

/** Period, team, technician, client, source, priority and kind; a reset; the people and teams offered are the viewer's own. */
export function AnalyticsFilterBar({ filters, onChange, options, hidePeople, right }: {
  filters: AnalyticsFilters; onChange: (patch: Partial<AnalyticsFilters>) => void; options?: AnalyticsFilterOptions; hidePeople?: boolean; right?: ReactNode;
}) {
  const active = Object.entries(filters).some(([k, v]) => v && k !== 'period' && k !== 'from' && k !== 'to') || filters.period !== 'this-week';
  return (
    <section aria-label="Filters" className={`${card} flex flex-wrap items-center gap-2 p-3`}>
      <select value={filters.period} onChange={(e) => onChange({ period: e.target.value })} aria-label="Period" className={field}>
        {PERIODS.map(([k, label]) => <option key={k} value={k}>{label}</option>)}
      </select>
      {filters.period === 'custom' && (
        <>
          <input type="date" value={filters.from} onChange={(e) => onChange({ from: e.target.value })} aria-label="First day" className={field} />
          <input type="date" value={filters.to} onChange={(e) => onChange({ to: e.target.value })} aria-label="Last day" className={field} />
        </>
      )}
      {!hidePeople && options?.seesOthers && (
        <>
          <select value={filters.teamId} onChange={(e) => onChange({ teamId: e.target.value })} aria-label="Team" className={field}>
            <option value="">All teams</option>
            {options.teams.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
          {options.departments.length > 0 && (
            <select value={filters.departmentId} onChange={(e) => onChange({ departmentId: e.target.value })} aria-label="Department" className={field}>
              <option value="">All departments</option>
              {options.departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          )}
          <select value={filters.appUserId} onChange={(e) => onChange({ appUserId: e.target.value })} aria-label="Technician" className={field}>
            <option value="">All technicians</option>
            {options.people.map((p) => <option key={p.key} value={p.key}>{p.name}</option>)}
          </select>
        </>
      )}
      {options && options.clients.length > 0 && (
        <select value={filters.clientId} onChange={(e) => onChange({ clientId: e.target.value })} aria-label="Client" className={field}>
          <option value="">All clients</option>
          {options.clients.map((c) => <option key={c.key} value={c.key}>{c.name}</option>)}
        </select>
      )}
      <select value={filters.source} onChange={(e) => onChange({ source: e.target.value })} aria-label="Source" className={field}>
        <option value="">All sources</option>
        {(options?.sources ?? []).map((s) => <option key={s.key} value={s.key}>{s.name}</option>)}
      </select>
      {options && options.priorities.length > 0 && (
        <select value={filters.priority} onChange={(e) => onChange({ priority: e.target.value })} aria-label="Priority" className={field}>
          <option value="">All priorities</option>
          {options.priorities.map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
      )}
      <select value={filters.kind} onChange={(e) => onChange({ kind: e.target.value })} aria-label="Kind of work" className={field} title="Narrows the recorded time to planned or reactive work; planned minutes and capacity are unchanged">
        <option value="">Planned and reactive</option>
        <option value="planned">Planned work only</option>
        <option value="reactive">Reactive work only</option>
      </select>
      {active && <button type="button" onClick={() => onChange({ ...EMPTY })} className={btn}>Reset</button>}
      <div className="ml-auto flex items-center gap-2">{right}</div>
    </section>
  );
}

// ---- small pieces --------------------------------------------------------------------------------------------

function Alert({ error }: { error: unknown }) {
  const message = error instanceof ApiError || error instanceof Error ? error.message : 'Something went wrong.';
  return <p role="alert" className="rounded-lg border border-red-300 bg-red-50 px-3 py-2 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-200">{message}</p>;
}

function Panel({ title, hint, children, right }: { title: string; hint?: string; children: ReactNode; right?: ReactNode }) {
  return (
    <section aria-label={title} className={`${card} p-4`}>
      <div className="mb-3 flex flex-wrap items-start justify-between gap-2">
        <div><h2 className="text-sm font-semibold">{title}</h2>{hint && <p className="text-xs text-[var(--muted)]">{hint}</p>}</div>
        {right}
      </div>
      {children}
    </section>
  );
}

/** One figure. Where a card opens its records, the value itself is the button. */
function Figure({ label, value, sub, title, onOpen }: { label: string; value: string; sub?: string; title: string; onOpen?: () => void }) {
  return (
    <div title={title}>
      <dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">{label}</dt>
      <dd className="text-xl font-semibold tabular-nums">
        {onOpen
          ? <button type="button" onClick={onOpen} aria-label={`${label}: ${value}. Show the records`} className="rounded text-left underline decoration-dotted underline-offset-4 hover:decoration-solid focus:outline-none focus-visible:ring-2 focus-visible:ring-brand">{value}</button>
          : value}
      </dd>
      {sub && <dd className="text-[11px] text-[var(--muted)]">{sub}</dd>}
    </div>
  );
}

/** The period and the work filters as a query string, for a link to one person's page (the people filters do not travel: the page is one person). */
function personQs(f: AnalyticsFilters): string {
  const qs = new URLSearchParams();
  if (f.period !== 'this-week') qs.set('period', f.period);
  if (f.period === 'custom') { if (f.from) qs.set('from', f.from); if (f.to) qs.set('to', f.to); }
  for (const [k, v] of [['client', f.clientId], ['source', f.source], ['priority', f.priority], ['kind', f.kind]] as const) if (v) qs.set(k, v);
  const s = qs.toString();
  return s ? `?${s}` : '';
}

/** Phones get cards, not a wide table. */
function useNarrow(): boolean {
  const [narrow, setNarrow] = useState(false);
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 767px)');
    const on = () => setNarrow(mq.matches);
    on();
    mq.addEventListener('change', on);
    return () => mq.removeEventListener('change', on);
  }, []);
  return narrow;
}

// ---- charts: inline SVG, each with a table alternative ----------------------------------------------------

type Series = { name: string; values: number[]; color: string };

/** Grouped bars per label (a day, a person), one bar per series, with a y-grid; the table beside it says the same numbers. */
function GroupedBars({ labels, series, unit, ariaLabel, height = 200 }: { labels: string[]; series: Series[]; unit: string; ariaLabel: string; height?: number }) {
  const width = 640;
  const padL = 36, padB = 22, padT = 8, padR = 6;
  const max = Math.max(1, ...series.flatMap((s) => s.values));
  const ticks = 4;
  const n = Math.max(1, labels.length);
  const group = (width - padL - padR) / n;
  const bw = Math.max(2, (group * 0.72) / Math.max(1, series.length));
  const y = (v: number) => padT + (1 - v / max) * (height - padT - padB);
  const every = n > 16 ? Math.ceil(n / 16) : 1;
  return (
    <figure>
      <svg viewBox={`0 0 ${width} ${height}`} className="w-full" role="img" aria-label={ariaLabel}>
        {Array.from({ length: ticks + 1 }, (_, i) => {
          const v = (max / ticks) * i;
          return (
            <g key={i}>
              <line x1={padL} x2={width - padR} y1={y(v)} y2={y(v)} stroke="var(--border)" strokeWidth="1" />
              <text x={padL - 5} y={y(v) + 3} textAnchor="end" fontSize="9" fill="var(--faint)">{Math.round(v * 10) / 10}{unit}</text>
            </g>
          );
        })}
        {labels.map((l, i) => (
          <g key={`${l}-${i}`}>
            {series.map((s, k) => {
              const v = s.values[i] ?? 0;
              const x = padL + i * group + (group - bw * series.length) / 2 + k * bw;
              return <rect key={s.name} x={x} y={y(v)} width={bw - 1} height={Math.max(0, height - padB - y(v))} rx="1.5" fill={s.color}><title>{`${l}: ${s.name} ${Math.round(v * 10) / 10}${unit}`}</title></rect>;
            })}
            {i % every === 0 && <text x={padL + i * group + group / 2} y={height - 6} textAnchor="middle" fontSize="9" fill="var(--faint)">{l}</text>}
          </g>
        ))}
      </svg>
      <figcaption className="mt-1 flex flex-wrap items-center gap-3 text-[11px] text-[var(--muted)]">
        {series.map((s) => <span key={s.name} className="inline-flex items-center gap-1"><span aria-hidden="true" className="inline-block h-2.5 w-2.5 rounded-sm" style={{ background: s.color }} />{s.name}</span>)}
        <details className="ml-auto">
          <summary className="cursor-pointer hover:underline">Show as table</summary>
          <div className="mt-2 max-h-64 overflow-auto rounded-lg border border-[var(--border)]">
            <table className="w-full text-xs">
              <caption className="sr-only">{ariaLabel}</caption>
              <thead className="text-left text-[var(--muted)]"><tr><th scope="col" className="px-2 py-1 font-medium">Label</th>{series.map((s) => <th key={s.name} scope="col" className="px-2 py-1 text-right font-medium">{s.name}</th>)}</tr></thead>
              <tbody>{labels.map((l, i) => <tr key={`${l}-${i}`} className="border-t border-[var(--border)]"><th scope="row" className="px-2 py-1 text-left font-normal">{l}</th>{series.map((s) => <td key={s.name} className="px-2 py-1 text-right tabular-nums">{Math.round((s.values[i] ?? 0) * 10) / 10}{unit}</td>)}</tr>)}</tbody>
            </table>
          </div>
        </details>
      </figcaption>
    </figure>
  );
}

/** Actual or planned against capacity, one cell per person-day; a neutral single hue by intensity, the three facts in every cell's title and in the table itself. */
function HeatmapGrid({ heatmap, metric, qs }: { heatmap: HeatmapData; metric: 'actual' | 'planned'; qs: string }) {
  // One hue by intensity: the brand green on a light surface, a lighter green on a dark one, so the steps stay visible in both themes.
  const shade = (p: number | null) => (p == null ? 'transparent' : `rgb(var(--heat) / ${Math.min(0.85, 0.08 + (Math.min(p, 120) / 120) * 0.77).toFixed(2)})`);
  return (
    <div className="overflow-x-auto [--heat:20_83_45] dark:[--heat:74_222_128]">
      <table className="text-xs">
        <caption className="sr-only">{metric === 'actual' ? 'Actual capacity utilization' : 'Scheduled capacity'} per person and day</caption>
        <thead>
          <tr><th scope="col" className="sticky left-0 bg-[var(--surface)] px-2 py-1 text-left font-medium text-[var(--muted)]">Person</th>{heatmap.dates.map((d) => <th key={d} scope="col" className="px-1 py-1 text-center font-medium text-[var(--muted)]">{fmtDay(d, { weekday: 'short', day: 'numeric' })}</th>)}</tr>
        </thead>
        <tbody>
          {heatmap.rows.map((r) => (
            <tr key={r.appUserId}>
              <th scope="row" className="sticky left-0 bg-[var(--surface)] px-2 py-1 text-left font-normal"><Link href={`/dashboard/workforce/analytics/${r.appUserId}${qs}`} className="hover:underline">{r.displayName}</Link></th>
              {r.cells.map((c, i) => {
                const used = metric === 'actual' ? Math.round(c.actualSeconds / 60) : c.plannedMinutes;
                const p = c.capacityMinutes && c.capacityMinutes > 0 ? Math.round((used / c.capacityMinutes) * 100) : null;
                const title = `${fmtDay(heatmap.dates[i])}: ${metric === 'actual' ? 'actual' : 'planned'} ${mins(used)} · capacity ${c.capacityMinutes == null ? 'not offered' : mins(c.capacityMinutes)} · ${p == null ? 'N/A' : `${p}%`}`;
                return <td key={i} title={title} className={`h-8 min-w-[44px] border border-[var(--surface)] text-center tabular-nums ${p != null && p > 105 ? 'text-white' : ''} ${p != null && p > 60 ? 'dark:text-slate-950' : ''}`} style={{ background: shade(p) }}>{p == null ? '—' : `${p}%`}</td>;
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ---- tables -----------------------------------------------------------------------------------------------------

type Col<T> = { key: string; label: string; value: (row: T) => string; sort: (row: T) => number | string; right?: boolean; title?: string };

function useSort<T>(initial: string) {
  const [sort, setSort] = useState<{ key: string; dir: 1 | -1 }>({ key: initial, dir: 1 });
  const apply = (rows: T[], cols: Col<T>[]) => {
    const col = cols.find((c) => c.key === sort.key) ?? cols[0];
    return [...rows].sort((a, b) => {
      const x = col.sort(a), y = col.sort(b);
      const r = typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), undefined, { sensitivity: 'base' });
      return r * sort.dir;
    });
  };
  const toggle = (key: string) => setSort((s) => (s.key === key ? { key, dir: s.dir === 1 ? -1 : 1 } : { key, dir: 1 }));
  return { sort, apply, toggle };
}

/** A sortable table of figures: the reader chooses the order; nothing is pre-ranked. */
function FigureTable<T>({ caption, rows, cols, keyOf, initialSort, narrowTitle }: { caption: string; rows: T[]; cols: Col<T>[]; keyOf: (row: T) => string; initialSort: string; narrowTitle: (row: T) => ReactNode }) {
  const { sort, apply, toggle } = useSort<T>(initialSort);
  const narrow = useNarrow();
  const sorted = apply(rows, cols);
  if (rows.length === 0) return <p className="py-6 text-center text-sm text-[var(--muted)]">Nothing in this period.</p>;
  if (narrow) {
    return (
      <ul aria-label={caption} className="space-y-2">
        {sorted.map((r) => (
          <li key={keyOf(r)} className="rounded-lg border border-[var(--border)] p-3 text-sm">
            <div className="font-medium">{narrowTitle(r)}</div>
            <dl className="mt-1 grid grid-cols-2 gap-x-3 gap-y-1 text-xs">
              {cols.slice(1).map((c) => <div key={c.key} className="flex justify-between gap-2"><dt className="text-[var(--muted)]">{c.label}</dt><dd className="tabular-nums">{c.value(r)}</dd></div>)}
            </dl>
          </li>
        ))}
      </ul>
    );
  }
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[720px] text-sm">
        <caption className="sr-only">{caption}</caption>
        <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
          <tr>
            {cols.map((c) => (
              <th key={c.key} scope="col" aria-sort={sort.key === c.key ? (sort.dir === 1 ? 'ascending' : 'descending') : 'none'} className={`${th} ${c.right ? 'text-right' : ''}`} title={c.title}>
                <button type="button" onClick={() => toggle(c.key)} className="inline-flex items-center gap-1 hover:text-[var(--fg)]">{c.label}{sort.key === c.key && <span aria-hidden="true">{sort.dir === 1 ? '▲' : '▼'}</span>}</button>
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-[var(--border)]">
          {sorted.map((r) => (
            <tr key={keyOf(r)}>
              {cols.map((c, i) => <td key={c.key} className={`px-3 py-2 ${c.right ? 'text-right tabular-nums' : ''}`}>{i === 0 ? narrowTitle(r) : c.value(r)}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const groupCols = (withCapacity: boolean): Col<AnalyticsGroup>[] => [
  { key: 'name', label: 'Name', value: (g) => g.name, sort: (g) => g.name },
  ...(withCapacity ? [{ key: 'cap', label: 'Capacity', value: (g: AnalyticsGroup) => mins(g.figures.capacityMinutes), sort: (g: AnalyticsGroup) => g.figures.capacityMinutes ?? -1, right: true } as Col<AnalyticsGroup>] : []),
  { key: 'planned', label: 'Planned', value: (g) => mins(g.figures.plannedMinutes), sort: (g) => g.figures.plannedMinutes, right: true },
  { key: 'actual', label: 'Actual', value: (g) => dur(g.figures.actualSeconds), sort: (g) => g.figures.actualSeconds, right: true },
  ...(withCapacity ? [{ key: 'util', label: 'Utilization', value: (g: AnalyticsGroup) => pct(g.figures.capacityUtilizationPercent), sort: (g: AnalyticsGroup) => g.figures.capacityUtilizationPercent ?? -1, right: true, title: 'Actual ÷ capacity' } as Col<AnalyticsGroup>] : []),
  { key: 'reactive', label: 'Reactive', value: (g) => `${dur(g.figures.reactiveActualSeconds)}${g.figures.reactiveSharePercent != null ? ` (${pct(g.figures.reactiveSharePercent)})` : ''}`, sort: (g) => g.figures.reactiveActualSeconds, right: true },
  { key: 'completed', label: 'Completed', value: (g) => String(g.figures.completedWork), sort: (g) => g.figures.completedWork, right: true },
  { key: 'items', label: 'Work items', value: (g) => String(g.figures.workItems), sort: (g) => g.figures.workItems, right: true },
  { key: 'people', label: 'People', value: (g) => String(g.people), sort: (g) => g.people, right: true },
];

function GroupTable({ caption, rows, withCapacity }: { caption: string; rows: AnalyticsGroup[]; withCapacity: boolean }) {
  return <FigureTable caption={caption} rows={rows} cols={groupCols(withCapacity)} keyOf={(g) => g.key} initialSort="name" narrowTitle={(g) => <span className="font-medium">{g.name}</span>} />;
}

const personCols: Col<AnalyticsPerson>[] = [
  { key: 'name', label: 'Technician', value: (p) => p.displayName, sort: (p) => p.displayName },
  { key: 'cap', label: 'Capacity', value: (p) => (p.isSchedulable ? mins(p.figures.capacityMinutes) : 'not offered'), sort: (p) => p.figures.capacityMinutes ?? -1, right: true, title: 'Usable working time: windows less breaks and time away' },
  { key: 'planned', label: 'Planned', value: (p) => `${mins(p.figures.plannedMinutes)}${p.figures.tentativeMinutes ? ` +${mins(p.figures.tentativeMinutes)} tentative` : ''}`, sort: (p) => p.figures.plannedMinutes, right: true },
  { key: 'actual', label: 'Actual', value: (p) => dur(p.figures.actualSeconds), sort: (p) => p.figures.actualSeconds, right: true },
  { key: 'sched', label: 'Scheduled', value: (p) => pct(p.figures.scheduledUtilizationPercent), sort: (p) => p.figures.scheduledUtilizationPercent ?? -1, right: true, title: 'Planned ÷ capacity' },
  { key: 'util', label: 'Utilization', value: (p) => pct(p.figures.capacityUtilizationPercent), sort: (p) => p.figures.capacityUtilizationPercent ?? -1, right: true, title: 'Actual ÷ capacity. An operational figure, not a performance score' },
  { key: 'variance', label: 'Variance', value: (p) => signed(p.figures.varianceMinutes), sort: (p) => p.figures.varianceMinutes ?? 0, right: true, title: 'Actual − planned' },
  { key: 'completed', label: 'Completed', value: (p) => String(p.figures.completedWork), sort: (p) => p.figures.completedWork, right: true },
  { key: 'reactive', label: 'Reactive', value: (p) => dur(p.figures.reactiveActualSeconds), sort: (p) => p.figures.reactiveActualSeconds, right: true },
  { key: 'estimate', label: 'Estimate variance', value: (p) => pct(p.figures.estimateVariancePercent), sort: (p) => p.figures.estimateVariancePercent ?? -1, right: true, title: 'Σ |actual − planned| ÷ Σ planned, over the days that had a plan' },
];

// ---- drill-down ------------------------------------------------------------------------------------------------

const KIND_LABEL: Record<AnalyticsWorkKind, string> = {
  actual: 'Recorded work', 'planned-actual': 'Recorded work that was planned', reactive: 'Reactive work', planned: 'Planned work', tentative: 'Tentative work',
  completed: 'Completed work', open: 'Open work right now', unscheduled: 'Unscheduled work right now', overdue: 'Overdue work right now',
};

/** The records behind a card, paged on the server; the list's total is the card's figure. */
export function DrillDown({ kind, query, timeZone, onClose, linkQs = '' }: { kind: AnalyticsWorkKind; query: AnalyticsQuery; timeZone: string; onClose: () => void; linkQs?: string }) {
  const [skip, setSkip] = useState(0);
  const take = 50;
  const { data, isLoading, error } = useQuery({ queryKey: ['analytics-work', kind, query, skip], queryFn: () => api.analyticsWork(query, kind, skip, take), retry: false, placeholderData: (prev) => prev });
  // A modal dialog: focus moves into it, Tab stays inside it, Escape closes it, and focus goes back to the card that opened it.
  const box = useRef<HTMLDivElement>(null);
  const close = useRef(onClose);
  close.current = onClose;
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null;
    box.current?.querySelector<HTMLElement>('button')?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { close.current(); return; }
      if (e.key !== 'Tab' || !box.current) return;
      const stops = [...box.current.querySelectorAll<HTMLElement>('a[href], button:not([disabled])')];
      if (stops.length === 0) return;
      const first = stops[0], last = stops[stops.length - 1];
      const outside = !box.current.contains(document.activeElement);
      if (e.shiftKey && (outside || document.activeElement === first)) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && (outside || document.activeElement === last)) { e.preventDefault(); first.focus(); }
    };
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('keydown', onKey); opener?.focus?.(); };
  }, []);
  const timeKind = kind === 'actual' || kind === 'planned-actual' || kind === 'reactive';
  const allocationKind = kind === 'planned' || kind === 'tentative';
  const total = data ? (timeKind ? `${data.total} records · ${dur(data.totalSeconds)}` : allocationKind ? `${data.total} pieces of work · ${mins(data.totalMinutes)}` : `${data.total} work items`) : '';
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-black/40 p-0 sm:items-center sm:p-6" onClick={onClose}>
      <div ref={box} role="dialog" aria-modal="true" aria-labelledby="drill-title" onClick={(e) => e.stopPropagation()} className={`${card} flex max-h-[90vh] w-full max-w-5xl flex-col shadow-xl`}>
        <div className="flex items-start justify-between gap-3 border-b border-[var(--border)] px-4 py-3">
          <div><h2 id="drill-title" className="text-base font-semibold">{KIND_LABEL[kind]}</h2><p className="text-xs text-[var(--muted)]" aria-live="polite">{data ? `${data.period.label} · ${total}` : 'Loading…'}</p></div>
          <button type="button" onClick={onClose} aria-label="Close" className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><X size={14} /></button>
        </div>
        <div className="overflow-auto px-4 py-3">
          {error && <Alert error={error} />}
          {isLoading && !data && <div aria-busy="true" className="h-40 animate-pulse rounded-xl bg-[var(--bg)]" />}
          {data && data.rows.length === 0 && <p className="py-6 text-center text-sm text-[var(--muted)]">Nothing here.</p>}
          {data && data.rows.length > 0 && (
            <table className="w-full min-w-[640px] text-sm">
              <caption className="sr-only">{KIND_LABEL[kind]}</caption>
              <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
                <tr>
                  <th scope="col" className={th}>{kind === 'completed' ? 'Finished' : timeKind || allocationKind ? 'When' : 'Due'}</th>
                  <th scope="col" className={th}>{kind === 'completed' ? 'Credited to' : kind === 'open' || kind === 'unscheduled' || kind === 'overdue' ? 'Held by' : 'Technician'}</th>
                  <th scope="col" className={th}>Work</th><th scope="col" className={th}>Client</th><th scope="col" className={th}>Source</th>
                  {(timeKind || allocationKind) && <th scope="col" className={`${th} text-right`}>Time</th>}
                  <th scope="col" className={th}>{timeKind ? 'Planned or reactive' : allocationKind ? 'Status' : 'Status'}</th>
                  {timeKind && <th scope="col" className={th}>State</th>}
                </tr>
              </thead>
              <tbody className="divide-y divide-[var(--border)]">
                {data.rows.map((r) => <WorkRow key={`${r.kind}-${r.id ?? r.ticketId}-${r.date}`} r={r} timeZone={timeZone} timeKind={timeKind} allocationKind={allocationKind} linkQs={linkQs} />)}
              </tbody>
            </table>
          )}
        </div>
        {data && data.total > take && (
          <nav aria-label="Pages" className="flex items-center justify-between gap-2 border-t border-[var(--border)] px-4 py-2 text-xs text-[var(--muted)]">
            <span aria-live="polite">{skip + 1}–{Math.min(skip + take, data.total)} of {data.total}</span>
            <span className="flex gap-2">
              <button type="button" onClick={() => setSkip(Math.max(0, skip - take))} disabled={skip === 0} className={btn}>Previous</button>
              <button type="button" onClick={() => setSkip(skip + take)} disabled={skip + take >= data.total} className={btn}>Next</button>
            </span>
          </nav>
        )}
      </div>
    </div>
  );
}

function WorkRow({ r, timeZone, timeKind, allocationKind, linkQs }: { r: AnalyticsWorkRow; timeZone: string; timeKind: boolean; allocationKind: boolean; linkQs: string }) {
  const when = r.kind === 'ticket' && !r.finishedAt ? (r.dueAt ? fmtAt(r.dueAt, timeZone) : '—') : r.at ? fmtAt(r.at, timeZone) : fmtDay(r.date);
  return (
    <tr>
      <td className="px-3 py-2 whitespace-nowrap tabular-nums">{when}</td>
      <td className="px-3 py-2">{r.appUserId ? <Link href={`/dashboard/workforce/analytics/${r.appUserId}${linkQs}`} className="hover:underline">{r.personName ?? 'Someone'}</Link> : '—'}</td>
      <td className="px-3 py-2">
        {r.ticketVisible ? <Link href={`/dashboard/tickets/${r.ticketId}`} className="hover:underline"><span className="font-mono text-xs text-[var(--muted)]">{r.reference}</span> {r.title}</Link> : <span className="text-[var(--muted)]">{r.reference}</span>}
      </td>
      <td className="px-3 py-2">{r.clientName ?? (r.ticketVisible ? '—' : '')}</td>
      <td className="px-3 py-2">{r.source}{r.priority ? <span className="block text-[11px] text-[var(--muted)]">{r.priority}</span> : null}</td>
      {(timeKind || allocationKind) && <td className="px-3 py-2 text-right tabular-nums">{r.seconds != null ? dur(r.seconds) : mins(r.minutes)}{r.billable === false && <span className="block text-[11px] text-[var(--muted)]">not billable</span>}</td>}
      <td className="px-3 py-2">{timeKind ? (r.plannedWork ? 'Planned' : 'Reactive') : allocationKind ? r.status : r.plannedWork === false ? `${r.status} · unscheduled` : r.status}</td>
      {timeKind && <td className="px-3 py-2 text-[var(--muted)]">{r.status}</td>}
    </tr>
  );
}

// ---- export --------------------------------------------------------------------------------------------------------

/** A CSV through the BFF, fetched as a blob so the session goes with it; the file name is the server's. */
async function downloadCsv(url: string, fallbackName: string): Promise<string | null> {
  const res = await fetch(url, { cache: 'no-store' });
  if (!res.ok) {
    try { const body = await res.json(); return body?.detail ?? body?.title ?? `Export failed (${res.status}).`; } catch { return `Export failed (${res.status}).`; }
  }
  const blob = await res.blob();
  const name = res.headers.get('content-disposition')?.match(/filename\*?=(?:UTF-8'')?"?([^";]+)"?/)?.[1] ?? fallbackName;
  const href = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = href; a.download = decodeURIComponent(name); document.body.appendChild(a); a.click();
  a.remove(); URL.revokeObjectURL(href);
  return null;
}

function ExportMenu({ query, reports }: { query: AnalyticsQuery; reports: [AnalyticsExportReport, string][] }) {
  const [busy, setBusy] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  return (
    <div className="flex flex-wrap items-center gap-1">
      <label className="inline-flex items-center gap-1 text-xs text-[var(--muted)]"><Download size={14} aria-hidden="true" /> Export CSV
        <select aria-label="Export" value="" disabled={!!busy} onChange={async (e) => {
          const report = e.target.value as AnalyticsExportReport;
          if (!report) return;
          setBusy(report); setProblem(null);
          setProblem(await downloadCsv(api.analyticsExportUrl(query, report), `workforce-analytics-${report}.csv`));
          setBusy(null);
        }} className={`${field} py-1`}>
          <option value="">{busy ? 'Preparing…' : 'Choose a table'}</option>
          {reports.map(([k, label]) => <option key={k} value={k}>{label}</option>)}
        </select>
      </label>
      {problem && <span role="alert" className="text-xs text-red-700 dark:text-red-300">{problem}</span>}
    </div>
  );
}

// ---- the management dashboard ------------------------------------------------------------------------------------------

function dailySeries(daily: AnalyticsDay[]) {
  return {
    labels: daily.map((d) => fmtDay(d.date, daily.length > 10 ? { day: 'numeric', month: 'short' } : { weekday: 'short', day: 'numeric' })),
    capacity: daily.map((d) => minToHours(d.figures.capacityMinutes ?? 0)),
    planned: daily.map((d) => minToHours(d.figures.plannedMinutes)),
    actual: daily.map((d) => toHours(d.figures.actualSeconds)),
    reactive: daily.map((d) => toHours(d.figures.reactiveActualSeconds)),
  };
}

export function WorkforceAnalyticsView() {
  const [filters, setFilters] = useAnalyticsFilters();
  const { data: options } = useQuery({ queryKey: ['analytics-filters'], queryFn: api.analyticsFilters, staleTime: 5 * 60_000, retry: false });
  const query = useMemo(() => toQuery(filters), [filters]);
  const { data, isLoading, error, refetch, isFetching } = useQuery({
    queryKey: ['analytics-overview', query], queryFn: () => api.analyticsOverview(query), retry: false, placeholderData: (prev) => prev, refetchInterval: 60_000,
    enabled: filters.period !== 'custom' || !!(filters.from && filters.to),
  });
  const [drill, setDrill] = useState<AnalyticsWorkKind | null>(null);
  const [metric, setMetric] = useState<'actual' | 'planned'>('actual');
  const t = data?.totals;
  const series = useMemo(() => (data ? dailySeries(data.daily) : null), [data]);
  const peopleSeries = useMemo(() => {
    if (!data) return null;
    const rows = [...data.people].sort((a, b) => a.displayName.localeCompare(b.displayName)).slice(0, 24);
    return { labels: rows.map((p) => p.displayName.split(' ')[0]), planned: rows.map((p) => minToHours(p.figures.plannedMinutes)), actual: rows.map((p) => toHours(p.figures.plannedActualSeconds)), reactive: rows.map((p) => toHours(p.figures.reactiveActualSeconds)), count: data.people.length };
  }, [data]);

  return (
    <div className="space-y-3">
      <AnalyticsFilterBar filters={filters} onChange={setFilters} options={options}
        right={
          <>
            {data?.canExport && <ExportMenu query={query} reports={[['technicians', 'Technicians'], ['teams', 'Teams'], ['clients', 'Clients'], ['sources', 'Sources'], ['daily', 'Planned against actual by day'], ['work', 'Recorded work']]} />}
            <button type="button" onClick={() => refetch()} aria-label="Refresh" className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><RefreshCw size={14} className={isFetching ? 'animate-spin' : ''} /></button>
          </>
        } />
      {filters.period === 'custom' && !(filters.from && filters.to) && <p className="text-sm text-[var(--muted)]">Choose the first and last day of the custom range.</p>}
      {error && <Alert error={error} />}
      {isLoading && !data && <div aria-busy="true" className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}
      {data && t && (
        <>
          <p className="text-xs text-[var(--muted)]" aria-live="polite">
            <span className="font-medium text-[var(--fg)]">{data.period.label}</span> ({data.period.timeZone}) · {data.people.length} {data.people.length === 1 ? 'person' : 'people'} · as of {fmtAt(data.generatedAt, data.period.timeZone)}
            {data.sync.length > 0 && <> · PSA synced {data.sync.map((s) => `${s.connection}: ${s.lastSuccessfulSyncAt ? fmtAt(s.lastSuccessfulSyncAt, data.period.timeZone) : 'never'}`).join(', ')}</>}
          </p>

          <dl className={`${card} grid grid-cols-2 gap-3 px-4 py-3 sm:grid-cols-4 lg:grid-cols-8`} aria-label="Summary">
            <Figure label="Work capacity" value={mins(t.capacityMinutes)} sub={`${data.people.filter((p) => p.isSchedulable && p.hasSchedule).length} with a schedule`} title="Usable working time of the people offered for planned work: windows less breaks and time away" />
            <Figure label="Planned work" value={mins(t.plannedMinutes)} sub={t.tentativeMinutes ? `+${mins(t.tentativeMinutes)} tentative` : 'confirmed'} title="Confirmed allocations starting in the period; tentative work is shown apart" onOpen={() => setDrill('planned')} />
            <Figure label="Actual work" value={dur(t.actualSeconds)} sub={t.liveSeconds ? `${dur(t.liveSeconds)} on running clocks` : `${t.workItems} work items`} title="Recorded time entries plus clocks still running, on the person's day" onOpen={() => setDrill('actual')} />
            <Figure label="Scheduled utilization" value={pct(t.scheduledUtilizationPercent)} sub="planned ÷ capacity" title="How much of the capacity was planned into. N/A when there is no capacity" />
            <Figure label="Capacity utilization" value={pct(t.capacityUtilizationPercent)} sub="actual ÷ capacity" title="How much of the capacity was spent on recorded work. An operational figure, not a performance score" />
            <Figure label="Completed work" value={String(t.completedWork)} sub="unique work items" title="Work items that reached a finished status in the period, credited to the people in scope; one ticket counts once however many entries it took" onOpen={() => setDrill('completed')} />
            <Figure label="Reactive work" value={dur(t.reactiveActualSeconds)} sub={`${pct(t.reactiveSharePercent)} of actual · ${t.reactiveWorkItems} items`} title="Recorded time on work the person had nothing planned on that day. A fact about planning, not a judgement: incidents and urgent support are reactive by nature" onOpen={() => setDrill('reactive')} />
            <Figure label="Open work now" value={String(data.now.open)} sub={`${data.now.unscheduled} unscheduled · ${data.now.overdue} overdue`} title="Open tickets held by the people in scope as of now; not period-bound" onOpen={() => setDrill('open')} />
          </dl>

          <div className="grid gap-3 lg:grid-cols-3">
            <Panel title="Capacity against demand" hint="For the selected period. Confirmed and tentative are never merged.">
              <dl className="grid grid-cols-2 gap-2 text-sm">
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Available</dt><dd className="font-semibold tabular-nums">{mins(data.demand.availableMinutes)}</dd></div>
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Confirmed demand</dt><dd className="font-semibold tabular-nums">{mins(data.demand.confirmedMinutes)}</dd></div>
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Tentative demand</dt><dd className="font-semibold tabular-nums">{mins(data.demand.tentativeMinutes)}</dd></div>
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Projected demand</dt><dd className="font-semibold tabular-nums">{mins(data.demand.projectedMinutes)}</dd></div>
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Remaining (confirmed)</dt><dd className="font-semibold tabular-nums">{mins(data.demand.remainingConfirmedMinutes)}</dd></div>
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Potential shortage</dt><dd className={`font-semibold tabular-nums ${data.demand.shortageMinutes > 0 ? 'text-red-700 dark:text-red-300' : ''}`}>{data.demand.shortageMinutes > 0 ? mins(data.demand.shortageMinutes) : 'none'}</dd></div>
              </dl>
              {t.overCapacityMinutes > 0 && <p className="mt-2 text-xs text-[var(--muted)]">{mins(t.overCapacityMinutes)} planned beyond capacity on {t.overCapacityPersonDays} person-day{t.overCapacityPersonDays === 1 ? '' : 's'} (an override put it there).</p>}
              <p className="mt-2 text-xs text-[var(--muted)]">Unscheduled work&rsquo;s estimated effort is on the <Link href="/dashboard/workforce/queue" className="underline">planning queue</Link>.</p>
            </Panel>
            <Panel title="Client, internal and monitoring work" hint="Recorded time by what kind of work the ticket is. Internal work is work.">
              <dl className="grid grid-cols-2 gap-2 text-sm">
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Client work</dt><dd className="font-semibold tabular-nums">{dur(t.clientSeconds)}</dd></div>
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Internal work</dt><dd className="font-semibold tabular-nums">{dur(t.internalSeconds)}</dd></div>
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Monitoring work</dt><dd className="font-semibold tabular-nums">{dur(t.monitoringSeconds)}</dd></div>
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Marked billable</dt><dd className="font-semibold tabular-nums">{dur(t.billableSeconds)}</dd><dd className="text-[11px] text-[var(--muted)]">as marked when logged; invoicing is the PSA&rsquo;s</dd></div>
              </dl>
            </Panel>
            <Panel title="Planned against actual" hint={`${data.period.endsInFuture ? `Recorded time against the ${mins(t.plannedToDateMinutes)} planned up to today (the period has not ended).` : 'Recorded time against what was planned in the period.'} Positive is more time than planned; neither sign is good or bad.`}>
              <dl className="grid grid-cols-2 gap-2 text-sm">
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Variance</dt><dd className="font-semibold tabular-nums">{signed(t.varianceMinutes)}{t.variancePercent != null && <span className="ml-1 text-xs text-[var(--muted)]">({t.variancePercent > 0 ? '+' : ''}{pct(t.variancePercent)})</span>}</dd></div>
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Estimate variance</dt><dd className="font-semibold tabular-nums">{pct(t.estimateVariancePercent)}</dd><dd className="text-[11px] text-[var(--muted)]">{mins(t.absoluteVarianceMinutes)} over {t.plannedItemsCompared} planned ticket-day{t.plannedItemsCompared === 1 ? '' : 's'}</dd></div>
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Planned actual</dt><dd className="font-semibold tabular-nums"><button type="button" onClick={() => setDrill('planned-actual')} aria-label={`Planned actual: ${dur(t.plannedActualSeconds)}. Show the records`} className="underline decoration-dotted underline-offset-4 hover:decoration-solid">{dur(t.plannedActualSeconds)}</button></dd></div>
                <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Reactive actual</dt><dd className="font-semibold tabular-nums"><button type="button" onClick={() => setDrill('reactive')} aria-label={`Reactive actual: ${dur(t.reactiveActualSeconds)}. Show the records`} className="underline decoration-dotted underline-offset-4 hover:decoration-solid">{dur(t.reactiveActualSeconds)}</button></dd></div>
              </dl>
            </Panel>
          </div>

          {series && (
            <Panel title="Daily trend" hint="Capacity, planned and actual hours per day across the people in scope. Completed work is dated in the organization's zone.">
              <GroupedBars labels={series.labels} unit="h" ariaLabel="Capacity, planned and actual hours by day"
                series={[{ name: 'Capacity', values: series.capacity, color: COLORS.capacity }, { name: 'Planned', values: series.planned, color: COLORS.planned }, { name: 'Actual', values: series.actual, color: COLORS.actual }, { name: 'of which reactive', values: series.reactive, color: COLORS.reactive }]} />
            </Panel>
          )}

          {data.seesOthers && peopleSeries && peopleSeries.labels.length > 1 && (
            <Panel title="Planned, planned actual and reactive by technician" hint={`Hours over the period, in name order${peopleSeries.count > peopleSeries.labels.length ? ` (first ${peopleSeries.labels.length} of ${peopleSeries.count}; the table below has everyone)` : ''}.`}>
              <GroupedBars labels={peopleSeries.labels} unit="h" ariaLabel="Planned, planned actual and reactive hours by technician"
                series={[{ name: 'Planned', values: peopleSeries.planned, color: COLORS.planned }, { name: 'Planned actual', values: peopleSeries.actual, color: COLORS.actual }, { name: 'Reactive', values: peopleSeries.reactive, color: COLORS.reactive }]} />
            </Panel>
          )}

          <Panel title={data.seesOthers ? 'Technicians' : 'Your figures'} hint="Sort any column yourself; nothing is pre-ranked. Open a name for the work behind the figures.">
            <FigureTable caption="Technicians" rows={data.people} cols={personCols} keyOf={(p) => p.appUserId} initialSort="name"
              narrowTitle={(p) => (
                <span>
                  <Link href={`/dashboard/workforce/analytics/${p.appUserId}${personQs(filters)}`} className="font-medium hover:underline">{p.displayName}</Link>
                  {p.teams.length > 0 && <span className="block text-[11px] text-[var(--muted)]">{p.teams.join(', ')}</span>}
                  {!p.hasSchedule && <span className="block text-[11px] text-[var(--muted)]">No working schedule</span>}
                </span>
              )} />
          </Panel>

          {data.seesOthers && data.teams.length > 0 && (
            <Panel title="Teams" hint="Members' capacity, planned and recorded work. A person in two teams appears under both.">
              <GroupTable caption="Teams" rows={data.teams} withCapacity />
            </Panel>
          )}

          <div className="grid gap-3 lg:grid-cols-2">
            <Panel title="Clients" hint="Where the recorded time went. Work you cannot open is one row.">
              <GroupTable caption="Clients" rows={data.clients} withCapacity={false} />
            </Panel>
            <Panel title="Work sources" hint="Each PSA connection, the team's own boards, monitoring.">
              <GroupTable caption="Work sources" rows={data.sources} withCapacity={false} />
            </Panel>
            <Panel title="Priorities" hint="The ticket's normalized priority.">
              <GroupTable caption="Priorities" rows={data.priorities} withCapacity={false} />
            </Panel>
            <Panel title="Work types" hint="The work type each hour was logged under (recorded time only).">
              <GroupTable caption="Work types" rows={data.workTypes} withCapacity={false} />
            </Panel>
          </div>

          <Panel title="Capacity heatmap" hint="One cell per person and day. 100% is not ideal and 50% is not poor: capacity conditions vary. Hover or read the table for the three facts."
            right={
              <div role="group" aria-label="Heatmap metric" className="flex gap-1 text-xs">
                <button type="button" aria-pressed={metric === 'actual'} onClick={() => setMetric('actual')} className={`rounded-full border px-2.5 py-0.5 ${metric === 'actual' ? 'border-brand bg-brand text-brand-fg' : 'border-[var(--border)]'}`}>Actual capacity utilization %</button>
                <button type="button" aria-pressed={metric === 'planned'} onClick={() => setMetric('planned')} className={`rounded-full border px-2.5 py-0.5 ${metric === 'planned' ? 'border-brand bg-brand text-brand-fg' : 'border-[var(--border)]'}`}>Scheduled capacity %</button>
              </div>
            }>
            {data.heatmap.unavailable ? <p className="text-sm text-[var(--muted)]">{data.heatmap.unavailable}</p> : <HeatmapGrid heatmap={data.heatmap} metric={metric} qs={personQs(filters)} />}
            {data.heatmap.truncated && <p className="mt-2 text-xs text-[var(--muted)]">Showing {data.heatmap.rows.length} of {data.heatmap.peopleTotal} people; filter by team to see the others.</p>}
          </Panel>

          <Panel title="What these numbers do and do not cover">
            <ul className="list-disc space-y-1 pl-5 text-xs text-[var(--muted)]">
              {data.notes.map((n) => <li key={n}>{n}</li>)}
              <li>Operational facts about planned and recorded work. Not attendance, not presence, and not a performance score: ticket counts say nothing about complexity, and a long task is a long task.</li>
            </ul>
          </Panel>
        </>
      )}
      {drill && data && <DrillDown kind={drill} query={query} timeZone={data.period.timeZone} onClose={() => setDrill(null)} linkQs={personQs(filters)} />}
    </div>
  );
}

/** Back to the dashboard with the period and the work filters this page was opened with. */
export function BackToAnalytics({ children }: { children: ReactNode }) {
  const sp = useSearchParams();
  const s = sp.toString();
  return <Link href={`/dashboard/workforce/analytics${s ? `?${s}` : ''}`} className="inline-flex items-center gap-1 text-sm text-[var(--muted)] hover:text-[var(--fg)]">{children}</Link>;
}

// ---- one person ------------------------------------------------------------------------------------------------------

const itemCols: Col<import('@/lib/api').AnalyticsWorkItem>[] = [
  { key: 'work', label: 'Work', value: (i) => i.reference, sort: (i) => i.reference },
  { key: 'planned', label: 'Planned', value: (i) => `${mins(i.plannedMinutes)}${i.tentativeMinutes ? ` +${mins(i.tentativeMinutes)} tentative` : ''}`, sort: (i) => i.plannedMinutes, right: true },
  { key: 'actual', label: 'Actual', value: (i) => dur(i.actualSeconds), sort: (i) => i.actualSeconds, right: true },
  { key: 'reactive', label: 'Reactive', value: (i) => dur(i.reactiveActualSeconds), sort: (i) => i.reactiveActualSeconds, right: true },
  { key: 'variance', label: 'Variance', value: (i) => `${signed(i.varianceMinutes)}${i.variancePercent != null ? ` (${i.variancePercent > 0 ? '+' : ''}${pct(i.variancePercent)})` : ''}`, sort: (i) => i.varianceMinutes ?? 0, right: true },
  { key: 'entries', label: 'Entries', value: (i) => String(i.entries), sort: (i) => i.entries, right: true },
  { key: 'days', label: 'Days', value: (i) => String(i.days), sort: (i) => i.days, right: true },
  { key: 'state', label: 'State', value: (i) => (!i.ticketVisible ? '—' : i.finished ? 'Finished' : 'Open'), sort: (i) => (!i.ticketVisible ? -1 : i.finished ? 1 : 0) },
];

export function TechnicianAnalyticsView({ appUserId, self }: { appUserId: string; self: boolean }) {
  const [filters, setFilters] = useAnalyticsFilters();
  const { data: options } = useQuery({ queryKey: ['analytics-filters'], queryFn: api.analyticsFilters, staleTime: 5 * 60_000, retry: false });
  const query = useMemo(() => toQuery(filters, appUserId), [filters, appUserId]);
  const { data, isLoading, error, refetch, isFetching } = useQuery({
    queryKey: ['analytics-technician', appUserId, query], queryFn: () => api.analyticsTechnician(appUserId, query), retry: false, placeholderData: (prev) => prev, refetchInterval: 60_000,
    enabled: filters.period !== 'custom' || !!(filters.from && filters.to),
  });
  const [drill, setDrill] = useState<AnalyticsWorkKind | null>(null);
  const [show, setShow] = useState<'all' | 'finished' | 'open' | 'reactive'>('all');
  const series = useMemo(() => (data ? dailySeries(data.daily) : null), [data]);
  const f = data?.person.figures;
  const items = useMemo(() => {
    if (!data) return [];
    return data.items.filter((i) => show === 'all' || (show === 'finished' ? i.ticketVisible && i.finished : show === 'open' ? i.ticketVisible && !i.finished : i.reactiveActualSeconds > 0));
  }, [data, show]);
  const title = data ? `${data.person.displayName}` : '';

  return (
    <div className="space-y-3">
      <AnalyticsFilterBar filters={filters} onChange={setFilters} options={options} hidePeople
        right={<button type="button" onClick={() => refetch()} aria-label="Refresh" className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><RefreshCw size={14} className={isFetching ? 'animate-spin' : ''} /></button>} />
      {error && <Alert error={error} />}
      {isLoading && !data && <div aria-busy="true" className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}
      {data && f && (
        <>
          <p className="text-xs text-[var(--muted)]" aria-live="polite">
            {!self && <><span className="font-medium text-[var(--fg)]">{title}</span>{data.person.teams.length > 0 && <> · {data.person.teams.join(', ')}</>} · </>}
            <span className="font-medium text-[var(--fg)]">{data.period.label}</span> ({data.person.timeZone}) · as of {fmtAt(data.generatedAt, data.period.timeZone)}
            {!data.person.hasSchedule && <> · no working schedule, so capacity is 0 and utilization is N/A</>}
            {!data.person.isSchedulable && <> · not offered for planned work, so capacity does not count</>}
          </p>
          <dl className={`${card} grid grid-cols-2 gap-3 px-4 py-3 sm:grid-cols-3 lg:grid-cols-6`} aria-label="Summary">
            <Figure label="Capacity" value={data.person.isSchedulable ? mins(f.capacityMinutes) : 'not offered'} title="Usable working time: windows less breaks and time away" />
            <Figure label="Planned" value={mins(f.plannedMinutes)} sub={f.tentativeMinutes ? `+${mins(f.tentativeMinutes)} tentative` : 'confirmed'} title="Confirmed allocations starting in the period" onOpen={() => setDrill('planned')} />
            <Figure label="Actual" value={dur(f.actualSeconds)} sub={f.liveSeconds ? `${dur(f.liveSeconds)} on a running clock` : `${f.workItems} work items`} title="Recorded time plus a running clock, on the day it started" onOpen={() => setDrill('actual')} />
            <Figure label="Scheduled utilization" value={pct(f.scheduledUtilizationPercent)} sub="planned ÷ capacity" title="How much of the capacity was planned into" />
            <Figure label="Capacity utilization" value={pct(f.capacityUtilizationPercent)} sub="actual ÷ capacity" title="How much of the capacity was spent on recorded work. Operational, not a score" />
            <Figure label="Completed" value={String(f.completedWork)} sub="unique work items" title="Work items finished in the period and credited to this person" onOpen={() => setDrill('completed')} />
            <Figure label="Reactive" value={dur(f.reactiveActualSeconds)} sub={`${pct(f.reactiveSharePercent)} of actual`} title="Time on work with nothing planned that day" onOpen={() => setDrill('reactive')} />
            <Figure label="Variance" value={signed(f.varianceMinutes)} sub={f.variancePercent != null ? `${f.variancePercent > 0 ? '+' : ''}${pct(f.variancePercent)} of ${mins(f.plannedToDateMinutes)} planned${data.period.endsInFuture ? ' so far' : ''}` : f.plannedMinutes > 0 ? 'nothing was due yet' : 'nothing planned'} title="Recorded time − what was planned up to today; neither sign is good or bad" />
            <Figure label="Estimate variance" value={pct(f.estimateVariancePercent)} sub={`${mins(f.absoluteVarianceMinutes)} over ${f.plannedItemsCompared} planned day${f.plannedItemsCompared === 1 ? '' : 's'}`} title="Σ |actual − planned| ÷ Σ planned, over the days that had a plan. Not a quality score" />
            <Figure label="Client work" value={dur(f.clientSeconds)} sub={`internal ${dur(f.internalSeconds)} · monitoring ${dur(f.monitoringSeconds)}`} title="Recorded time by kind of ticket" />
            <Figure label="Marked billable" value={dur(f.billableSeconds)} sub="as marked when logged" title="Entries marked billable; invoicing is the PSA's" />
            <Figure label="Over capacity" value={f.overCapacityMinutes > 0 ? mins(f.overCapacityMinutes) : 'none'} sub={f.overCapacityPersonDays > 0 ? `${f.overCapacityPersonDays} day${f.overCapacityPersonDays === 1 ? '' : 's'}` : undefined} title="Confirmed planned time beyond a day's capacity" />
          </dl>

          {series && (
            <Panel title="Daily trend" hint="Capacity, planned and actual hours per day.">
              <GroupedBars labels={series.labels} unit="h" ariaLabel="Capacity, planned and actual hours by day"
                series={[{ name: 'Capacity', values: series.capacity, color: COLORS.capacity }, { name: 'Planned', values: series.planned, color: COLORS.planned }, { name: 'Actual', values: series.actual, color: COLORS.actual }, { name: 'of which reactive', values: series.reactive, color: COLORS.reactive }]} />
            </Panel>
          )}

          <Panel title="Work in this period" hint={`Each ticket once, with its planned and recorded time${data.itemsTruncated ? ' (the first 200 by recorded time; open a card above for everything)' : ''}.`}
            right={
              <div role="group" aria-label="Show" className="flex flex-wrap gap-1 text-xs">
                {([['all', 'All'], ['finished', 'Finished'], ['open', 'Open'], ['reactive', 'Reactive']] as const).map(([k, label]) => (
                  <button key={k} type="button" aria-pressed={show === k} onClick={() => setShow(k)} className={`rounded-full border px-2.5 py-0.5 ${show === k ? 'border-brand bg-brand text-brand-fg' : 'border-[var(--border)]'}`}>{label}</button>
                ))}
              </div>
            }>
            <FigureTable caption="Work in this period" rows={items} cols={itemCols} keyOf={(i) => i.ticketId} initialSort="actual"
              narrowTitle={(i) => (
                i.ticketVisible
                  ? <Link href={`/dashboard/tickets/${i.ticketId}`} className="hover:underline"><span className="font-mono text-xs text-[var(--muted)]">{i.reference}</span> {i.title}<span className="block text-[11px] font-normal text-[var(--muted)]">{[i.clientName, i.source, i.priority].filter(Boolean).join(' · ')}</span></Link>
                  : <span className="text-[var(--muted)]">{i.reference}<span className="block text-[11px]">{i.source}</span></span>
              )} />
          </Panel>

          <div className="grid gap-3 lg:grid-cols-2">
            <Panel title="By client"><GroupTable caption="By client" rows={data.clients} withCapacity={false} /></Panel>
            <Panel title="By source"><GroupTable caption="By source" rows={data.sources} withCapacity={false} /></Panel>
            <Panel title="By work type" hint="Recorded time only."><GroupTable caption="By work type" rows={data.workTypes} withCapacity={false} /></Panel>
            <Panel title="By priority"><GroupTable caption="By priority" rows={data.priorities} withCapacity={false} /></Panel>
          </div>

          <Panel title="What these numbers do and do not cover">
            <ul className="list-disc space-y-1 pl-5 text-xs text-[var(--muted)]">
              {data.notes.map((n) => <li key={n}>{n}</li>)}
              <li>Facts about planned and recorded work; not attendance and not a performance score.</li>
            </ul>
          </Panel>
        </>
      )}
      {drill && data && <DrillDown kind={drill} query={query} timeZone={data.person.timeZone} onClose={() => setDrill(null)} linkQs={personQs(filters)} />}
    </div>
  );
}
