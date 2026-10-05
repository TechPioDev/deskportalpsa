'use client';

import { useMemo, useState } from 'react';
import Link from 'next/link';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { AlertCircle, AlertTriangle, Eye, FileSpreadsheet, Info, RefreshCw, X } from 'lucide-react';
import {
  api, type AnalyticsFilterOptions, type Comparison, type ConnectionInsight, type EstimateVarianceRow, type Forecast, type ForecastFigures, type ForecastGroup,
  type ForecastPerson, type ForecastWorkList, type GroupComparison, type Insight, type InsightsQuery, type QualitySignal, type SkillCapacity, type Trends,
} from '@/lib/api';
import { fmtDay } from '@/components/WorkforceCapacity';
import {
  Alert, btn, card, COLORS, dur, field, Figure, FigureTable, fmtAt, GroupedBars, isDate, mins, minToHours, Panel, pct, th, toHours, useDialogFocus, type Col,
} from '@/components/WorkforceAnalytics';

/**
 * Management insights (Phase 8): what is ahead (capacity against confirmed, tentative and estimated
 * unscheduled demand), what changed against the period before, what the optional quality data
 * supports, and what needs attention. Deterministic sums over records, defined in
 * docs/workforce-scheduling/PHASE8_MANAGEMENT_INSIGHTS_DESIGN.md: nothing is predicted
 * statistically, every statement shows its rule and its numbers, and nobody is scored or ranked.
 */

// ---- filters in the query string ---------------------------------------------------------------------------

export type InsightsFilters = {
  window: string; from: string; to: string; compare: string; teamId: string; departmentId: string; appUserId: string; clientId: string; source: string; priority: string;
};
const EMPTY: InsightsFilters = { window: 'next-7', from: '', to: '', compare: 'last-30', teamId: '', departmentId: '', appUserId: '', clientId: '', source: '', priority: '' };
export const WINDOWS: [string, string][] = [
  ['next-7', 'Next 7 days'], ['next-14', 'Next 14 days'], ['next-30', 'Next 30 days'], ['this-week', 'Rest of this week'], ['next-week', 'Next week'],
  ['this-month', 'Rest of this month'], ['custom', 'Custom range'],
];
export const COMPARISONS: [string, string][] = [
  ['last-30', 'Last 30 days against the 30 before'], ['last-7', 'Last 7 days against the 7 before'], ['last-week', 'Last week against the week before'], ['last-month', 'Last month against the month before'],
];
const TABS = [['forecast', 'Forecast'], ['history', 'What changed'], ['quality', 'Quality signals'], ['health', 'Data and integrations']] as const;
type Tab = (typeof TABS)[number][0];

/** The filters and the open tab, read from and written to the query string: a bookmarked view is the same view. */
export function useInsightsFilters(): [InsightsFilters, (patch: Partial<InsightsFilters>) => void, Tab, (tab: Tab) => void] {
  const sp = useSearchParams();
  const router = useRouter();
  const pathname = usePathname();
  const filters = useMemo<InsightsFilters>(() => ({
    window: sp.get('window') ?? (isDate(sp.get('from') ?? '') ? 'custom' : 'next-7'),
    from: isDate(sp.get('from') ?? '') ? sp.get('from')! : '', to: isDate(sp.get('to') ?? '') ? sp.get('to')! : '',
    compare: sp.get('compare') ?? 'last-30',
    teamId: sp.get('team') ?? '', departmentId: sp.get('department') ?? '', appUserId: sp.get('person') ?? '', clientId: sp.get('client') ?? '',
    source: sp.get('source') ?? '', priority: sp.get('priority') ?? '',
  }), [sp]);
  const tab = (TABS.find(([k]) => k === sp.get('view'))?.[0] ?? 'forecast') as Tab;
  const write = (next: InsightsFilters, view: Tab) => {
    const qs = new URLSearchParams();
    if (view !== 'forecast') qs.set('view', view);
    if (next.window && next.window !== 'next-7') qs.set('window', next.window);
    if (next.window === 'custom') { if (next.from) qs.set('from', next.from); if (next.to) qs.set('to', next.to); }
    if (next.compare && next.compare !== 'last-30') qs.set('compare', next.compare);
    for (const [k, v] of [['team', next.teamId], ['department', next.departmentId], ['person', next.appUserId], ['client', next.clientId], ['source', next.source], ['priority', next.priority]] as const)
      if (v) qs.set(k, v);
    const s = qs.toString();
    router.replace(s ? `${pathname}?${s}` : pathname, { scroll: false });
  };
  return [filters, (patch) => write({ ...filters, ...patch }, tab), tab, (view) => write(filters, view)];
}

const workQuery = (f: InsightsFilters) => ({ teamId: f.teamId, departmentId: f.departmentId, appUserId: f.appUserId, clientId: f.clientId, source: f.source, priority: f.priority });
const forecastQuery = (f: InsightsFilters): InsightsQuery => ({
  window: f.window === 'custom' && !(f.from && f.to) ? 'next-7' : f.window, from: f.window === 'custom' ? f.from : null, to: f.window === 'custom' ? f.to : null, ...workQuery(f),
});
const trendsQuery = (f: InsightsFilters): InsightsQuery => ({ compare: f.compare, ...workQuery(f) });

/** Team, department, technician, client, source and priority: the lists the viewer is offered, as on the analytics dashboard. */
export function WorkFilterSelects({ value, onChange, options, allow }: {
  value: { teamId: string; departmentId: string; appUserId: string; clientId: string; source: string; priority: string };
  onChange: (patch: Partial<{ teamId: string; departmentId: string; appUserId: string; clientId: string; source: string; priority: string }>) => void;
  options?: AnalyticsFilterOptions; allow?: string[];
}) {
  const on = (k: string) => !allow || allow.includes(k);
  return (
    <>
      {options?.seesOthers && on('team') && (
        <select value={value.teamId} onChange={(e) => onChange({ teamId: e.target.value })} aria-label="Team" className={field}>
          <option value="">All teams</option>
          {options.teams.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
        </select>
      )}
      {options?.seesOthers && on('department') && options.departments.length > 0 && (
        <select value={value.departmentId} onChange={(e) => onChange({ departmentId: e.target.value })} aria-label="Department" className={field}>
          <option value="">All departments</option>
          {options.departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
      )}
      {options?.seesOthers && on('person') && (
        <select value={value.appUserId} onChange={(e) => onChange({ appUserId: e.target.value })} aria-label="Technician" className={field}>
          <option value="">All technicians</option>
          {options.people.map((p) => <option key={p.key} value={p.key}>{p.name}</option>)}
        </select>
      )}
      {options && on('client') && options.clients.length > 0 && (
        <select value={value.clientId} onChange={(e) => onChange({ clientId: e.target.value })} aria-label="Client" className={field}>
          <option value="">All clients</option>
          {options.clients.map((c) => <option key={c.key} value={c.key}>{c.name}</option>)}
        </select>
      )}
      {on('source') && (
        <select value={value.source} onChange={(e) => onChange({ source: e.target.value })} aria-label="Source" className={field}>
          <option value="">All sources</option>
          {(options?.sources ?? []).map((s) => <option key={s.key} value={s.key}>{s.name}</option>)}
        </select>
      )}
      {options && on('priority') && options.priorities.length > 0 && (
        <select value={value.priority} onChange={(e) => onChange({ priority: e.target.value })} aria-label="Priority" className={field}>
          <option value="">All priorities</option>
          {options.priorities.map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
      )}
    </>
  );
}

// ---- formatting -----------------------------------------------------------------------------------------------

/** A capacity gap in words: what is left, or how much is short. Neither is a judgement. */
const gapText = (m: number | null | undefined) => (m == null ? 'N/A' : m < 0 ? `${mins(-m)} short` : `${mins(m)} left`);
const signedNumber = (n: number, unit = '') => (n === 0 ? `0${unit}` : `${n > 0 ? '+' : '−'}${Math.round(Math.abs(n) * 10) / 10}${unit}`);
const items = (n: number, one = 'work item', many = 'work items') => `${n} ${n === 1 ? one : many}`;

function comparisonValue(unit: string, v: number | null): string {
  if (v == null) return 'N/A';
  return unit === 'seconds' ? dur(v) : unit === 'minutes' ? mins(v) : unit === 'percent' ? pct(v) : String(v);
}
function comparisonChange(c: Comparison): string {
  if (c.change == null) return 'N/A';
  if (c.unit === 'percent') return `${signedNumber(c.change)} points`;
  if (c.change === 0) return 'no change';
  const size = Math.abs(c.change);
  return `${c.change > 0 ? '+' : '−'}${c.unit === 'seconds' ? dur(size) : c.unit === 'minutes' ? mins(size) : String(size)}`;
}
const changePercent = (v: number | null) => (v == null ? 'N/A' : v === 0 ? '0%' : `${v > 0 ? '+' : '−'}${pct(Math.abs(v))}`);

// ---- attention ------------------------------------------------------------------------------------------------

const SEVERITY: Record<number, { label: string; cls: string; Icon: typeof Info }> = {
  1: { label: 'Info', cls: 'border-slate-300 bg-slate-50 text-slate-700 dark:border-slate-700 dark:bg-slate-900/60 dark:text-slate-200', Icon: Info },
  2: { label: 'Watch', cls: 'border-amber-300 bg-amber-50 text-amber-900 dark:border-amber-800 dark:bg-amber-950/40 dark:text-amber-200', Icon: Eye },
  3: { label: 'Attention', cls: 'border-orange-300 bg-orange-50 text-orange-900 dark:border-orange-800 dark:bg-orange-950/40 dark:text-orange-200', Icon: AlertTriangle },
  4: { label: 'Critical', cls: 'border-red-300 bg-red-50 text-red-900 dark:border-red-900 dark:bg-red-950/40 dark:text-red-200', Icon: AlertCircle },
};

/** A severity as a word with an icon: never by colour alone. */
function SeverityBadge({ severity }: { severity: number }) {
  const s = SEVERITY[severity] ?? SEVERITY[1];
  return <span className={`inline-flex shrink-0 items-center gap-1 rounded-full border px-2 py-0.5 text-[11px] font-medium ${s.cls}`}><s.Icon size={12} aria-hidden="true" />{s.label}</span>;
}

/**
 * What needs attention: each statement comes from one fixed rule, and says which and from what
 * numbers. The reader can open the records behind it. Nothing here is a prediction or a verdict.
 */
function AttentionList({ list, action }: { list: Insight[]; action: (i: Insight) => { label: string; run: () => void } | null }) {
  if (list.length === 0) return <p className="text-sm text-[var(--muted)]">Nothing needs attention under these filters.</p>;
  return (
    <ul className="divide-y divide-[var(--border)]">
      {list.map((i) => {
        const act = action(i);
        return (
          <li key={i.key} className="py-2.5 first:pt-0 last:pb-0">
            <div className="flex flex-wrap items-start gap-2">
              <SeverityBadge severity={i.severity} />
              <p className="min-w-0 flex-1 text-sm font-medium">{i.title}</p>
              {act && <button type="button" onClick={(e) => { e.currentTarget.focus(); act.run(); }} className={`${btn} py-1 text-xs`}>{act.label}</button>}
            </div>
            <details className="mt-1 text-xs text-[var(--muted)]">
              <summary className="cursor-pointer hover:underline">Why this is shown</summary>
              <p className="mt-1 max-w-prose">{i.rule}</p>
              <dl className="mt-1 grid grid-cols-1 gap-x-4 gap-y-0.5 sm:grid-cols-2 lg:grid-cols-3">
                {i.facts.map((f) => <div key={f.label} className="flex justify-between gap-3 border-b border-dashed border-[var(--border)] py-0.5"><dt>{f.label}</dt><dd className="text-right tabular-nums text-[var(--fg)]">{f.value}</dd></div>)}
              </dl>
            </details>
          </li>
        );
      })}
    </ul>
  );
}

// ---- forecast drill-down ----------------------------------------------------------------------------------------

const LIST_LABEL: Record<ForecastWorkList, string> = {
  confirmed: 'Confirmed work ahead', tentative: 'Tentative work ahead', unscheduled: 'Estimated work with no time allocated', unestimated: 'Open work with no estimate and no plan',
  'at-risk': 'Work without enough free time before its due date', overdue: 'Open work past its due date', skill: 'Work that asks for this skill',
};

type Drill = { list: ForecastWorkList; skillId?: string; skillName?: string };

/** The records behind a forecast figure, paged on the server; the list's total is the figure. */
function ForecastDrillDown({ drill, query, timeZone, onClose }: { drill: Drill; query: InsightsQuery; timeZone: string; onClose: () => void }) {
  const [skip, setSkip] = useState(0);
  const take = 50;
  const { data, isLoading, error } = useQuery({
    queryKey: ['insights-forecast-work', drill.list, drill.skillId ?? '', query, skip], queryFn: () => api.insightsForecastWork(query, drill.list, drill.skillId ?? null, skip, take),
    retry: false, placeholderData: (prev) => prev,
  });
  const box = useDialogFocus(onClose);
  const allocations = drill.list === 'confirmed' || drill.list === 'tentative';
  const title = drill.list === 'skill' && drill.skillName ? `Work that asks for ${drill.skillName}` : LIST_LABEL[drill.list];
  const total = data ? `${allocations ? items(data.total, 'piece of work', 'pieces of work') : items(data.total)}${drill.list === 'unestimated' ? '' : ` · ${mins(data.totalMinutes)}`}` : '';
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-black/40 p-0 sm:items-center sm:p-6" onClick={onClose}>
      <div ref={box} role="dialog" aria-modal="true" aria-labelledby="forecast-drill-title" onClick={(e) => e.stopPropagation()} className={`${card} flex max-h-[90vh] w-full max-w-5xl flex-col shadow-xl`}>
        <div className="flex items-start justify-between gap-3 border-b border-[var(--border)] px-4 py-3">
          <div><h2 id="forecast-drill-title" className="text-base font-semibold">{title}</h2><p className="text-xs text-[var(--muted)]" aria-live="polite">{data ? `${data.window.label} · ${total}` : 'Loading…'}</p></div>
          <button type="button" onClick={onClose} aria-label="Close" className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><X size={14} /></button>
        </div>
        <div className="overflow-auto px-4 py-3">
          {error && <Alert error={error} />}
          {isLoading && !data && <div aria-busy="true" className="h-40 animate-pulse rounded-xl bg-[var(--bg)]" />}
          {data && data.rows.length === 0 && <p className="py-6 text-center text-sm text-[var(--muted)]">Nothing here.</p>}
          {data && data.rows.length > 0 && (
            <table className="w-full min-w-[640px] text-sm">
              <caption className="sr-only">{title}</caption>
              <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
                <tr>
                  <th scope="col" className={th}>{allocations ? 'When' : 'Due'}</th>
                  <th scope="col" className={th}>{allocations ? 'Technician' : 'Held by'}</th>
                  <th scope="col" className={th}>Work</th><th scope="col" className={th}>Client</th><th scope="col" className={th}>Source</th>
                  <th scope="col" className={`${th} text-right`}>{allocations ? 'Time ahead' : 'Not yet allocated'}</th>
                  <th scope="col" className={th}>{allocations ? 'Status' : 'Detail'}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-[var(--border)]">
                {data.rows.map((r) => (
                  <tr key={`${r.kind}-${r.id ?? r.ticketId}`}>
                    <td className="px-3 py-2 whitespace-nowrap tabular-nums">{r.at ? fmtAt(r.at, timeZone) : r.dueAt ? fmtAt(r.dueAt, timeZone) : '—'}</td>
                    <td className="px-3 py-2">{r.personName ?? (r.teamName ? <span>{r.teamName}<span className="block text-[11px] text-[var(--muted)]">not yet assigned</span></span> : '—')}</td>
                    <td className="px-3 py-2">
                      {r.ticketVisible ? <Link href={`/dashboard/tickets/${r.ticketId}`} className="hover:underline"><span className="font-mono text-xs text-[var(--muted)]">{r.reference}</span> {r.title}</Link> : <span className="text-[var(--muted)]">{r.reference}</span>}
                    </td>
                    <td className="px-3 py-2">{r.clientName ?? (r.ticketVisible ? '—' : '')}</td>
                    <td className="px-3 py-2">{r.source}{r.priority ? <span className="block text-[11px] text-[var(--muted)]">{r.priority}</span> : null}</td>
                    <td className="px-3 py-2 text-right tabular-nums">
                      {allocations ? mins(r.minutes) : r.requiredMinutes == null ? <span className="text-[var(--muted)]">No estimate</span> : <>{mins(r.remainingMinutes)}<span className="block text-[11px] text-[var(--muted)]">of {mins(r.requiredMinutes)} estimated</span></>}
                    </td>
                    <td className="px-3 py-2">
                      {r.risk ? <>{r.risk}{r.freeBeforeDueMinutes != null && <span className="block text-[11px] text-[var(--muted)]">{mins(r.freeBeforeDueMinutes)} free before it is due</span>}</> : r.status}
                      {r.skillName && <span className="block text-[11px] text-[var(--muted)]">Needs {r.skillName}</span>}
                    </td>
                  </tr>
                ))}
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

// ---- forecast tables ---------------------------------------------------------------------------------------------

const figureCols = <T,>(of: (row: T) => ForecastFigures, withCapacity: boolean): Col<T>[] => [
  ...(withCapacity ? [{ key: 'cap', label: 'Capacity', value: (r: T) => mins(of(r).capacityMinutes), sort: (r: T) => of(r).capacityMinutes ?? -1, right: true, title: 'Usable working time ahead in the window; today counts from now' } as Col<T>] : []),
  { key: 'confirmed', label: 'Confirmed', value: (r) => mins(of(r).confirmedMinutes), sort: (r) => of(r).confirmedMinutes, right: true },
  { key: 'tentative', label: 'Tentative', value: (r) => mins(of(r).tentativeMinutes), sort: (r) => of(r).tentativeMinutes, right: true },
  { key: 'unscheduled', label: 'Est. unscheduled', value: (r) => (of(r).unscheduledItems ? `${mins(of(r).unscheduledMinutes)} (${of(r).unscheduledItems})` : '0m'), sort: (r) => of(r).unscheduledMinutes, right: true, title: 'Estimated effort with no time allocated, and the number of work items it is on' },
  { key: 'unestimated', label: 'Unestimated', value: (r) => String(of(r).unestimatedItems), sort: (r) => of(r).unestimatedItems, right: true, title: 'Open work items with no estimate and no plan: a count, never hours' },
  { key: 'projected', label: 'Projected', value: (r) => mins(of(r).projectedMinutes), sort: (r) => of(r).projectedMinutes, right: true, title: 'Confirmed + tentative + estimated unscheduled' },
  ...(withCapacity ? [
    { key: 'gap', label: 'Gap', value: (r: T) => gapText(of(r).gapMinutes), sort: (r: T) => of(r).gapMinutes ?? 0, right: true, title: 'Capacity − projected demand' } as Col<T>,
    { key: 'load', label: 'Projected load', value: (r: T) => pct(of(r).projectedPercent), sort: (r: T) => of(r).projectedPercent ?? -1, right: true, title: 'Projected demand ÷ capacity. A scheduling condition, not a judgement' } as Col<T>,
  ] : []),
];

const personCols: Col<ForecastPerson>[] = [
  { key: 'name', label: 'Technician', value: (p) => p.displayName, sort: (p) => p.displayName },
  ...figureCols<ForecastPerson>((p) => p.figures, true).map((c) => (c.key === 'cap' ? { ...c, value: (p: ForecastPerson) => (p.isSchedulable ? mins(p.figures.capacityMinutes) : 'not offered') } : c)),
];
const groupCols = (label: string, withCapacity: boolean): Col<ForecastGroup>[] => [
  { key: 'name', label, value: (g) => g.name, sort: (g) => g.name },
  ...figureCols<ForecastGroup>((g) => g.figures, withCapacity),
  { key: 'people', label: 'People', value: (g) => String(g.people), sort: (g) => g.people, right: true },
];

function SkillTable({ skills, onOpen }: { skills: SkillCapacity[]; onOpen: (s: SkillCapacity) => void }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[560px] text-sm">
        <caption className="sr-only">Skill capacity</caption>
        <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
          <tr><th scope="col" className={th}>Skill</th><th scope="col" className={`${th} text-right`}>Demand</th><th scope="col" className={`${th} text-right`}>People holding it</th><th scope="col" className={`${th} text-right`}>Their free time</th><th scope="col" className={`${th} text-right`}>Gap</th></tr>
        </thead>
        <tbody className="divide-y divide-[var(--border)]">
          {skills.map((s) => (
            <tr key={s.skillId}>
              <th scope="row" className="px-3 py-2 text-left font-medium">{s.name}</th>
              <td className="px-3 py-2 text-right tabular-nums"><button type="button" onClick={(e) => { e.currentTarget.focus(); onOpen(s); }} aria-label={`${s.name}: ${mins(s.demandMinutes)} on ${items(s.demandItems)}. Show the records`} className="underline decoration-dotted underline-offset-4 hover:decoration-solid">{mins(s.demandMinutes)}</button><span className="block text-[11px] text-[var(--muted)]">{items(s.demandItems)}</span></td>
              <td className="px-3 py-2 text-right tabular-nums" title={s.people.join(', ')}>{s.skilledPeople}</td>
              <td className="px-3 py-2 text-right tabular-nums">{mins(s.capacityMinutes)}</td>
              <td className={`px-3 py-2 text-right tabular-nums ${s.gapMinutes < 0 ? 'font-semibold' : ''}`}>{gapText(s.gapMinutes)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ---- history -----------------------------------------------------------------------------------------------------

function ComparisonTable({ rows, current, previous }: { rows: Comparison[]; current: string; previous: string }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[520px] text-sm">
        <caption className="sr-only">This period against the one before</caption>
        <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
          <tr><th scope="col" className={th}>Figure</th><th scope="col" className={`${th} text-right`} title={current}>This period</th><th scope="col" className={`${th} text-right`} title={previous}>Before</th><th scope="col" className={`${th} text-right`}>Change</th><th scope="col" className={`${th} text-right`} title="N/A when the earlier value is 0: both values are always shown">Change %</th></tr>
        </thead>
        <tbody className="divide-y divide-[var(--border)]">
          {rows.map((c) => (
            <tr key={c.key}>
              <th scope="row" className="px-3 py-2 text-left font-medium">{c.label}</th>
              <td className="px-3 py-2 text-right tabular-nums">{comparisonValue(c.unit, c.current)}</td>
              <td className="px-3 py-2 text-right tabular-nums text-[var(--muted)]">{comparisonValue(c.unit, c.previous)}</td>
              <td className="px-3 py-2 text-right tabular-nums">{comparisonChange(c)}</td>
              <td className="px-3 py-2 text-right tabular-nums">{c.unit === 'percent' ? '—' : changePercent(c.changePercent)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const groupComparisonCols = (label: string): Col<GroupComparison>[] => [
  { key: 'name', label, value: (g) => g.name, sort: (g) => g.name },
  { key: 'current', label: 'Actual', value: (g) => dur(g.currentSeconds), sort: (g) => g.currentSeconds, right: true },
  { key: 'previous', label: 'Before', value: (g) => dur(g.previousSeconds), sort: (g) => g.previousSeconds, right: true },
  { key: 'change', label: 'Change', value: (g) => (g.changeSeconds === 0 ? 'no change' : `${g.changeSeconds > 0 ? '+' : '−'}${dur(Math.abs(g.changeSeconds))}`), sort: (g) => g.changeSeconds, right: true },
  { key: 'pct', label: 'Change %', value: (g) => changePercent(g.changePercent), sort: (g) => g.changePercent ?? 0, right: true, title: 'N/A when there was nothing before' },
  { key: 'reactive', label: 'Reactive', value: (g) => dur(g.currentReactiveSeconds), sort: (g) => g.currentReactiveSeconds, right: true },
  { key: 'completed', label: 'Completed', value: (g) => `${g.currentCompleted} (before ${g.previousCompleted})`, sort: (g) => g.currentCompleted, right: true },
];

const varianceCols = (label: string): Col<EstimateVarianceRow>[] => [
  { key: 'name', label, value: (r) => r.name, sort: (r) => r.name },
  { key: 'planned', label: 'Planned', value: (r) => mins(r.plannedMinutes), sort: (r) => r.plannedMinutes, right: true },
  { key: 'actual', label: 'Recorded on it', value: (r) => mins(r.actualMinutes), sort: (r) => r.actualMinutes, right: true, title: 'Time recorded on that planned work, on the days it was planned' },
  { key: 'variance', label: 'Variance', value: (r) => (r.varianceMinutes === 0 ? '0m' : `${r.varianceMinutes > 0 ? '+' : '−'}${mins(Math.abs(r.varianceMinutes))}`), sort: (r) => r.varianceMinutes, right: true, title: 'Recorded − planned. Neither sign is good or bad' },
  { key: 'pct', label: 'Variance %', value: (r) => changePercent(r.variancePercent), sort: (r) => r.variancePercent ?? 0, right: true },
  { key: 'estimate', label: 'Estimate variance', value: (r) => pct(r.estimateVariancePercent), sort: (r) => r.estimateVariancePercent ?? -1, right: true, title: 'Σ |recorded − planned| ÷ Σ planned, over the planned ticket-days' },
  { key: 'days', label: 'Ticket-days', value: (r) => String(r.ticketDays), sort: (r) => r.ticketDays, right: true },
];

function HistoryTab({ trends, timeZone }: { trends: Trends; timeZone: string }) {
  const [by, setBy] = useState<'category' | 'client' | 'source'>('category');
  const weeks = trends.weeks;
  const variance = by === 'client' ? trends.byClient : by === 'source' ? trends.bySource : trends.byCategory;
  return (
    <>
      <p className="text-xs text-[var(--muted)]" aria-live="polite">
        <span className="font-medium text-[var(--fg)]">{trends.current.label}</span> against {trends.previous.label} ({trends.current.timeZone}) · as of {fmtAt(trends.generatedAt, timeZone)}
        {trends.sync.length > 0 && <> · PSA synced {trends.sync.map((s) => `${s.connection}: ${s.lastSuccessfulSyncAt ? fmtAt(s.lastSuccessfulSyncAt, timeZone) : 'never'}`).join(', ')}</>}
      </p>
      <Panel title="This period against the one before" hint="The same definitions as the analytics dashboard. A percentage is N/A when the earlier value is 0, and both values are always shown.">
        <ComparisonTable rows={trends.totals} current={trends.current.label} previous={trends.previous.label} />
      </Panel>
      <Panel title="Planned and reactive work by week" hint="The last eight weeks, Monday to Sunday. Reactive work is time on work that had no plan that day: a fact about demand, not a fault.">
        <GroupedBars labels={weeks.map((w) => `${fmtDay(w.from, { day: 'numeric', month: 'short' })}${w.partial ? '*' : ''}`)} unit="h" ariaLabel="Planned actual and reactive hours by week"
          series={[{ name: 'Planned actual', values: weeks.map((w) => toHours(w.plannedActualSeconds)), color: COLORS.actual }, { name: 'Reactive', values: weeks.map((w) => toHours(w.reactiveSeconds)), color: COLORS.reactive }]} />
        <div className="mt-3 overflow-x-auto">
          <table className="w-full min-w-[560px] text-xs">
            <caption className="sr-only">Work by week</caption>
            <thead className="border-b border-[var(--border)] text-left uppercase tracking-wide text-[var(--muted)]">
              <tr><th scope="col" className="px-2 py-1 font-medium">Week</th><th scope="col" className="px-2 py-1 text-right font-medium">Actual</th><th scope="col" className="px-2 py-1 text-right font-medium">Planned actual</th><th scope="col" className="px-2 py-1 text-right font-medium">Reactive</th><th scope="col" className="px-2 py-1 text-right font-medium">Reactive share</th><th scope="col" className="px-2 py-1 text-right font-medium">Completed</th></tr>
            </thead>
            <tbody className="divide-y divide-[var(--border)]">
              {weeks.map((w) => (
                <tr key={w.from}>
                  <th scope="row" className="px-2 py-1 text-left font-normal">{fmtDay(w.from, { day: 'numeric', month: 'short' })} – {fmtDay(w.to, { day: 'numeric', month: 'short' })}{w.partial && <span className="ml-1 text-[var(--muted)]">(in progress)</span>}</th>
                  <td className="px-2 py-1 text-right tabular-nums">{dur(w.actualSeconds)}</td><td className="px-2 py-1 text-right tabular-nums">{dur(w.plannedActualSeconds)}</td>
                  <td className="px-2 py-1 text-right tabular-nums">{dur(w.reactiveSeconds)}</td><td className="px-2 py-1 text-right tabular-nums">{pct(w.reactiveSharePercent)}</td><td className="px-2 py-1 text-right tabular-nums">{w.completed}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="mt-1 text-[11px] text-[var(--muted)]">* the week in progress.</p>
      </Panel>
      <div className="grid gap-3 lg:grid-cols-2">
        <Panel title="Client demand" hint="Recorded work per client, this period against the one before. More work for a client is a fact about demand, not a problem.">
          <FigureTable caption="Client demand" rows={trends.clients} cols={groupComparisonCols('Client')} keyOf={(g) => g.key} initialSort="name" narrowTitle={(g) => <span className="font-medium">{g.name}</span>} />
        </Panel>
        <Panel title="Work sources" hint="Each PSA connection, the team's own boards, monitoring.">
          <FigureTable caption="Work sources" rows={trends.sources} cols={groupComparisonCols('Source')} keyOf={(g) => g.key} initialSort="name" narrowTitle={(g) => <span className="font-medium">{g.name}</span>} />
        </Panel>
      </div>
      <Panel title="Estimate variance" hint="Planned time against the time recorded on that planned work, in this period. It describes estimates per kind of work; it is never attributed to a person."
        right={
          <div role="group" aria-label="Group estimate variance by" className="flex gap-1 text-xs">
            {(['category', 'client', 'source'] as const).map((k) => (
              <button key={k} type="button" aria-pressed={by === k} onClick={() => setBy(k)} className={`rounded-full border px-2.5 py-0.5 capitalize ${by === k ? 'border-brand bg-brand text-brand-fg' : 'border-[var(--border)]'}`}>By {k}</button>
            ))}
          </div>
        }>
        <FigureTable caption={`Estimate variance by ${by}`} rows={variance} cols={varianceCols(by === 'client' ? 'Client' : by === 'source' ? 'Source' : 'Category')} keyOf={(r) => r.key} initialSort="name" narrowTitle={(r) => <span className="font-medium">{r.name}</span>} />
      </Panel>
    </>
  );
}

// ---- quality signals ----------------------------------------------------------------------------------------------

const QUALITY: Record<number, { label: string; cls: string }> = {
  0: { label: 'Not available', cls: 'border-[var(--border)] text-[var(--muted)]' },
  1: { label: 'Partial data', cls: 'border-amber-300 bg-amber-50 text-amber-900 dark:border-amber-800 dark:bg-amber-950/40 dark:text-amber-200' },
  2: { label: 'High data quality', cls: 'border-emerald-300 bg-emerald-50 text-emerald-900 dark:border-emerald-800 dark:bg-emerald-950/40 dark:text-emerald-200' },
};

function QualityTab({ trends }: { trends: Trends }) {
  const population = trends.quality[0]?.population ?? 0;
  return (
    <>
      <p className="text-xs text-[var(--muted)]">
        <span className="font-medium text-[var(--fg)]">{trends.current.label}</span> against {trends.previous.label} · {items(population, 'completed work item', 'completed work items')}, the same set as the analytics dashboard&rsquo;s Completed card
      </p>
      <ul aria-label="Quality signals" className="grid gap-3 md:grid-cols-2">
        {trends.quality.map((s: QualitySignal) => {
          const q = QUALITY[s.quality] ?? QUALITY[0];
          return (
            <li key={s.key} className={`${card} p-4`}>
              <div className="flex flex-wrap items-start justify-between gap-2">
                <h3 className="text-sm font-semibold">{s.name}</h3>
                <span className={`rounded-full border px-2 py-0.5 text-[11px] font-medium ${q.cls}`}>{q.label}</span>
              </div>
              {s.quality === 0
                ? <p className="mt-2 text-xl font-semibold text-[var(--muted)]">N/A</p>
                : (
                  <p className="mt-2 flex flex-wrap items-baseline gap-x-2">
                    <span className="text-xl font-semibold tabular-nums">{pct(s.percent)}</span>
                    <span className="text-xs text-[var(--muted)] tabular-nums">{s.met} of {s.eligible}</span>
                    <span className="text-xs text-[var(--muted)] tabular-nums">· before: {pct(s.previousPercent)} ({s.previousMet} of {s.previousEligible})</span>
                  </p>
                )}
              <p className="mt-1 text-xs text-[var(--muted)]">{s.definition}</p>
              <p className="mt-1 text-xs"><span className="font-medium">What it rests on: </span><span className="text-[var(--muted)]">{s.qualityReason}</span></p>
            </li>
          );
        })}
      </ul>
      <p className="text-xs text-[var(--muted)]">Signals describe completed work and the data behind it. There is no per-technician quality table, by design, and a signal with no reliable source is not estimated from something else.</p>
    </>
  );
}

// ---- data and integrations ------------------------------------------------------------------------------------------

const coverage = (m: { mapped: number; total: number; percent: number | null }) => (m.total === 0 ? '—' : `${pct(m.percent)} (${m.mapped} of ${m.total})`);

function HealthTab({ forecast, canSeeHealth, timeZone }: { forecast: Forecast; canSeeHealth: boolean; timeZone: string }) {
  const { data: health, error } = useQuery({ queryKey: ['insights-health'], queryFn: api.insightsHealth, retry: false, enabled: canSeeHealth, staleTime: 60_000 });
  const d = forecast.dataQuality;
  const c = forecast.coverage;
  return (
    <>
      <Panel title="Planning data" hint="What the forecast rests on. A forecast is only as complete as the estimates, schedules and holders behind it.">
        <dl className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-4">
          <Figure label="Open work held" value={String(d.openHeldItems)} sub={d.openWithoutHolder != null ? `${d.openWithoutHolder} more with no holder in the portal` : 'by the people in scope'} title="Open work held by the people in scope. Work nobody holds in the portal is in nobody's forecast" />
          <Figure label="Estimated" value={`${c.estimatedItems} of ${c.openItems}`} sub={`${c.unestimatedOpenItems} without an estimate`} title="Open work items that carry an estimate. Work without one has demand of unknown size" />
          <Figure label="With a working schedule" value={`${d.people - d.peopleWithoutSchedule} of ${d.people}`} sub={d.peopleNotOffered ? `${d.peopleNotOffered} not offered for planned work` : 'people in scope'} title="People without a working schedule have a capacity of 0" />
          <Figure label="Estimates naming a skill" value={String(d.estimatedWithSkill)} sub={d.finishedWithoutDate ? `${d.finishedWithoutDate} finished items have no completion date` : 'of the estimated work'} title="Skill capacity is computed only for work whose estimate names a required skill" />
        </dl>
      </Panel>
      {!canSeeHealth && <p className="text-sm text-[var(--muted)]">Mapping and integration health is shown to people who hold the integration health permission.</p>}
      {error && <Alert error={error} />}
      {health && (
        <Panel title="Mapping and integration health" hint="Per PSA connection, from the records as they stand. No credential, address or error text is shown here; the error text is on the Integration health page.">
          {health.connections.length === 0 ? <p className="text-sm text-[var(--muted)]">No PSA connection is configured.</p> : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[860px] text-sm">
                <caption className="sr-only">Mapping and integration health</caption>
                <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
                  <tr>
                    <th scope="col" className={th}>Connection</th><th scope="col" className={th}>State</th><th scope="col" className={th}>Last successful sync</th><th scope="col" className={`${th} text-right`}>Tickets</th>
                    <th scope="col" className={`${th} text-right`}>Statuses mapped</th><th scope="col" className={`${th} text-right`}>Priorities mapped</th><th scope="col" className={`${th} text-right`}>PSA logins linked</th><th scope="col" className={`${th} text-right`}>Failed records</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-[var(--border)]">
                  {health.connections.map((x: ConnectionInsight) => (
                    <tr key={x.connectionId}>
                      <th scope="row" className="px-3 py-2 text-left font-medium">{x.name}<span className="block text-[11px] font-normal text-[var(--muted)]">{x.provider}{x.isEnabled ? '' : ' · disabled'}</span></th>
                      <td className="px-3 py-2">{x.status}{x.stale && <span className="block text-[11px] font-medium">Stale or failing</span>}{x.hasError && <span className="block text-[11px] text-[var(--muted)]">an error is recorded</span>}</td>
                      <td className="px-3 py-2 whitespace-nowrap tabular-nums">{x.lastSuccessfulSyncAt ? fmtAt(x.lastSuccessfulSyncAt, timeZone) : 'never'}</td>
                      <td className="px-3 py-2 text-right tabular-nums">{x.tickets}{x.placeholderClientTickets > 0 && <span className="block text-[11px] text-[var(--muted)]">{x.placeholderClientTickets} on a placeholder client</span>}</td>
                      <td className="px-3 py-2 text-right tabular-nums">{coverage(x.statusMapping)}{x.statusMapping.unmapped.length > 0 && <span className="block text-[11px] text-[var(--muted)]">unmapped: {x.statusMapping.unmapped.join(', ')}</span>}</td>
                      <td className="px-3 py-2 text-right tabular-nums">{coverage(x.priorityMapping)}{x.priorityMapping.unmapped.length > 0 && <span className="block text-[11px] text-[var(--muted)]">unmapped: {x.priorityMapping.unmapped.join(', ')}</span>}</td>
                      <td className="px-3 py-2 text-right tabular-nums">{coverage(x.technicianLinks)}</td>
                      <td className="px-3 py-2 text-right tabular-nums">{x.ticketsInSyncError} tickets<span className="block text-[11px] text-[var(--muted)]">{x.timeEntriesFailed} time entries failed · {x.timeEntriesPending} pending</span></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <p className="mt-2 text-xs text-[var(--muted)]">Mapping rules are under <Link href="/dashboard/mappings" className="underline">Mappings</Link>, sync errors under <Link href="/dashboard/health" className="underline">Integration health</Link>, and a PSA login is linked to a person under <Link href="/dashboard/users" className="underline">Users</Link>.</p>
        </Panel>
      )}
    </>
  );
}

// ---- the page ------------------------------------------------------------------------------------------------------

export function WorkforceInsightsView() {
  const [filters, setFilters, tab, setTab] = useInsightsFilters();
  const { data: options } = useQuery({ queryKey: ['analytics-filters'], queryFn: api.analyticsFilters, staleTime: 5 * 60_000, retry: false });
  const fq = useMemo(() => forecastQuery(filters), [filters]);
  const tq = useMemo(() => trendsQuery(filters), [filters]);
  const ready = filters.window !== 'custom' || !!(filters.from && filters.to);
  const forecast = useQuery({ queryKey: ['insights-forecast', fq], queryFn: () => api.insightsForecast(fq), retry: false, placeholderData: (prev) => prev, refetchInterval: 120_000, enabled: ready });
  const trends = useQuery({ queryKey: ['insights-trends', tq], queryFn: () => api.insightsTrends(tq), retry: false, placeholderData: (prev) => prev });
  const canSeeHealth = forecast.data?.canSeeHealth ?? false;
  const health = useQuery({ queryKey: ['insights-health'], queryFn: api.insightsHealth, retry: false, enabled: canSeeHealth, staleTime: 60_000 });
  const [drill, setDrill] = useState<Drill | null>(null);
  const data = forecast.data;
  const t = data?.totals;
  const zone = data?.window.timeZone ?? options?.timeZone ?? 'UTC';

  // One list: the forecast's statements, the comparison's, and the integrations' (which the forecast already carries for sync).
  const attention = useMemo(() => {
    const seen = new Set<string>();
    return [...(data?.attention ?? []), ...(trends.data?.attention ?? []), ...(health.data?.attention ?? [])]
      .filter((i) => (seen.has(i.key) ? false : (seen.add(i.key), true)))
      .sort((a, b) => b.severity - a.severity);
  }, [data, trends.data, health.data]);
  const action = (i: Insight) => {
    if (i.list === 'skill' && i.targetId) return { label: 'Show the work', run: () => setDrill({ list: 'skill', skillId: i.targetId!, skillName: data?.skills.find((s) => s.skillId === i.targetId)?.name }) };
    if (i.list) return { label: 'Show the work', run: () => setDrill({ list: i.list as ForecastWorkList }) };
    if (i.targetKind === 'team' && i.targetId && filters.teamId !== i.targetId) return { label: 'Filter to this team', run: () => setFilters({ teamId: i.targetId! }) };
    if (i.targetKind === 'health' || i.key.startsWith('mapping:')) return tab === 'health' ? null : { label: 'Open data and integrations', run: () => setTab('health') };
    if (i.key === 'reactive-share') return tab === 'history' ? null : { label: 'See what changed', run: () => setTab('history') };
    return null;
  };

  const daily = useMemo(() => {
    if (!data) return null;
    const long = data.daily.length > 10;
    return {
      labels: data.daily.map((d) => fmtDay(d.date, long ? { day: 'numeric', month: 'short' } : { weekday: 'short', day: 'numeric' })),
      capacity: data.daily.map((d) => minToHours(d.capacityMinutes ?? 0)), confirmed: data.daily.map((d) => minToHours(d.confirmedMinutes)),
      tentative: data.daily.map((d) => minToHours(d.tentativeMinutes)), due: data.daily.map((d) => minToHours(d.unscheduledDueMinutes)),
    };
  }, [data]);
  const balance = useMemo(() => {
    if (!data) return null;
    const rows = [...data.people].filter((p) => (p.figures.capacityMinutes ?? 0) > 0 || p.figures.projectedMinutes > 0).sort((a, b) => a.displayName.localeCompare(b.displayName)).slice(0, 24);
    return { labels: rows.map((p) => p.displayName.split(' ')[0]), capacity: rows.map((p) => minToHours(p.figures.capacityMinutes ?? 0)), projected: rows.map((p) => minToHours(p.figures.projectedMinutes)), count: data.people.length };
  }, [data]);
  const active = Object.entries(filters).some(([k, v]) => v && v !== (EMPTY as Record<string, string>)[k]);

  return (
    <div className="space-y-3">
      <section aria-label="Filters" className={`${card} flex flex-wrap items-center gap-2 p-3`}>
        <select value={filters.window} onChange={(e) => setFilters({ window: e.target.value })} aria-label="Forecast window" className={field}>
          {WINDOWS.map(([k, label]) => <option key={k} value={k}>{label}</option>)}
        </select>
        {filters.window === 'custom' && (
          <>
            <input type="date" value={filters.from} onChange={(e) => setFilters({ from: e.target.value })} aria-label="First day" className={field} />
            <input type="date" value={filters.to} onChange={(e) => setFilters({ to: e.target.value })} aria-label="Last day" className={field} />
          </>
        )}
        <WorkFilterSelects value={filters} onChange={setFilters} options={options} />
        <select value={filters.compare} onChange={(e) => setFilters({ compare: e.target.value })} aria-label="Comparison" className={field} title="The history shown under What changed and Quality signals">
          {COMPARISONS.map(([k, label]) => <option key={k} value={k}>{label}</option>)}
        </select>
        {active && <button type="button" onClick={() => setFilters({ ...EMPTY })} className={btn}>Reset</button>}
        <div className="ml-auto flex items-center gap-2">
          <Link href="/dashboard/workforce/reports" className={`${btn} py-1 text-xs`}><FileSpreadsheet size={14} aria-hidden="true" /> Reports</Link>
          <button type="button" onClick={() => { forecast.refetch(); trends.refetch(); if (canSeeHealth) health.refetch(); }} aria-label="Refresh" className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><RefreshCw size={14} className={forecast.isFetching || trends.isFetching ? 'animate-spin' : ''} /></button>
        </div>
      </section>
      {!ready && <p className="text-sm text-[var(--muted)]">Choose the first and last day of the custom range (today or later, at most 62 days).</p>}
      {forecast.error && <Alert error={forecast.error} />}
      {forecast.isLoading && !data && <div aria-busy="true" className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}

      {data && t && (
        <>
          <p className="text-xs text-[var(--muted)]" aria-live="polite">
            <span className="font-medium text-[var(--fg)]">{data.window.label}</span> ({data.window.timeZone}) · {data.people.length} {data.people.length === 1 ? 'person' : 'people'} · as of {fmtAt(data.generatedAt, zone)}
          </p>

          <Panel title="Needs attention" hint="Each statement comes from one fixed rule over the figures on this page, and shows the rule and the numbers. Rules, not predictions.">
            <AttentionList list={attention} action={action} />
          </Panel>

          <dl className={`${card} grid grid-cols-2 gap-3 px-4 py-3 sm:grid-cols-4 lg:grid-cols-8`} aria-label="Forecast summary">
            <Figure label="Capacity ahead" value={mins(t.capacityMinutes)} sub={`${data.people.filter((p) => p.isSchedulable && p.hasSchedule).length} with a schedule`} title="Usable working time of the people offered for planned work in the window; today counts from now" />
            <Figure label="Confirmed" value={mins(t.confirmedMinutes)} sub={`${pct(t.confirmedPercent)} of capacity`} title="Planned allocations in the window; today, the part still ahead" onOpen={() => setDrill({ list: 'confirmed' })} />
            <Figure label="Tentative" value={mins(t.tentativeMinutes)} sub="pencilled in" title="Tentative allocations in the window. Never merged with confirmed work" onOpen={() => setDrill({ list: 'tentative' })} />
            <Figure label="Est. unscheduled" value={mins(t.unscheduledMinutes)} sub={items(t.unscheduledItems)} title="Estimated effort of open work with no time allocated to it yet" onOpen={() => setDrill({ list: 'unscheduled' })} />
            <Figure label="Unestimated" value={String(t.unestimatedItems)} sub="demand of unknown size" title="Open work items with no estimate and no plan. A count: never turned into hours" onOpen={() => setDrill({ list: 'unestimated' })} />
            <Figure label="Projected demand" value={mins(t.projectedMinutes)} sub="confirmed + tentative + unscheduled" title="Confirmed + tentative + estimated unscheduled demand. Unestimated work is not in it" />
            <Figure label="Capacity gap" value={gapText(t.gapMinutes)} sub={`${mins(t.confirmedRemainingMinutes)} left after confirmed`} title="Capacity − projected demand. Left is potential remaining capacity; short is a potential shortage" />
            <Figure label="Projected load" value={pct(t.projectedPercent)} sub="projected ÷ capacity" title="How much of the capacity the projected demand takes. N/A when there is no capacity" />
          </dl>

          <div role="tablist" aria-label="Insights" className="flex flex-wrap gap-1 border-b border-[var(--border)]">
            {TABS.map(([k, label]) => (
              <button key={k} type="button" role="tab" id={`insights-tab-${k}`} aria-selected={tab === k} aria-controls="insights-panel" onClick={() => setTab(k)}
                className={`border-b-2 px-3 py-2 text-sm font-medium ${tab === k ? 'border-brand text-[var(--fg)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--fg)]'}`}>{label}</button>
            ))}
          </div>

          <div id="insights-panel" role="tabpanel" aria-labelledby={`insights-tab-${tab}`} className="space-y-3">
            {tab === 'forecast' && (
              <>
                {daily && (
                  <Panel title="Capacity and demand by day" hint={`Unscheduled effort has no day of its own: it is shown on the day its work is due and never spread. ${mins(data.unscheduledOverdueMinutes)} is already past its due date; ${mins(data.unscheduledNoDateMinutes)} has no due date in this window.`}>
                    <GroupedBars labels={daily.labels} unit="h" ariaLabel="Capacity, confirmed, tentative and unscheduled hours by day"
                      series={[{ name: 'Capacity', values: daily.capacity, color: COLORS.capacity }, { name: 'Confirmed', values: daily.confirmed, color: COLORS.planned }, { name: 'Tentative', values: daily.tentative, color: COLORS.tentative }, { name: 'Unscheduled, due that day', values: daily.due, color: COLORS.reactive }]} />
                  </Panel>
                )}
                <div className="grid gap-3 lg:grid-cols-3">
                  <Panel title="Schedule coverage" hint="How much of the estimated open work has time allocated to it.">
                    <dl className="grid grid-cols-2 gap-2 text-sm">
                      <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Coverage</dt><dd className="font-semibold tabular-nums">{pct(data.coverage.coveragePercent)}</dd></div>
                      <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Estimated work</dt><dd className="font-semibold tabular-nums">{mins(data.coverage.estimatedMinutes)}</dd><dd className="text-[11px] text-[var(--muted)]">{data.coverage.estimatedItems} of {items(data.coverage.openItems, 'open item', 'open items')}</dd></div>
                      <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Allocated</dt><dd className="font-semibold tabular-nums">{mins(data.coverage.scheduledMinutes)}</dd></div>
                      <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Not yet allocated</dt><dd className="font-semibold tabular-nums">{mins(data.coverage.unscheduledMinutes)}</dd></div>
                    </dl>
                  </Panel>
                  <Panel title="Work at risk" hint="Due work that is late, or that cannot fit in its holder's free time before it is due.">
                    <dl className="grid grid-cols-2 gap-2 text-sm">
                      <Figure label="Past its due date" value={String(data.atRisk.overdue)} title="Open work whose due date has passed; paused work is not counted" onOpen={data.atRisk.overdue > 0 ? () => setDrill({ list: 'overdue' }) : undefined} />
                      <Figure label="Not enough free time" value={String(data.atRisk.capacityShortfall)} title="Unallocated effort greater than the holder's free time on the days up to the due date" onOpen={data.atRisk.capacityShortfall > 0 ? () => setDrill({ list: 'at-risk' }) : undefined} />
                      <Figure label="Due in this window" value={String(data.atRisk.dueInWindow)} title="Open work that falls due within the window" />
                    </dl>
                  </Panel>
                  <Panel title="Recurring work" hint="Recurring tickets scheduled to be raised in the window. Counts only: an occurrence carries no estimate, and one is skipped while the previous is still open.">
                    {!data.recurring ? <p className="text-sm text-[var(--muted)]">Shown to people who manage boards.</p>
                      : data.recurring.items.length === 0 ? <p className="text-sm text-[var(--muted)]">None in this window.</p>
                        : (
                          <ul className="space-y-1 text-sm">
                            {data.recurring.items.slice(0, 6).map((r) => <li key={r.id} className="flex justify-between gap-3"><span className="min-w-0 truncate" title={`${r.schedule}${r.assigneeName ? ` · ${r.assigneeName}` : ''}`}>{r.title}</span><span className="shrink-0 tabular-nums text-[var(--muted)]">{r.occurrences}×</span></li>)}
                            {data.recurring.items.length > 6 && <li className="text-xs text-[var(--muted)]">and {data.recurring.items.length - 6} more: {data.recurring.occurrences} occurrences in all</li>}
                          </ul>
                        )}
                  </Panel>
                </div>
                {data.seesOthers && balance && balance.labels.length > 1 && (
                  <Panel title="Workload balance" hint={`Capacity against projected demand per person, in name order${balance.count > balance.labels.length ? ` (first ${balance.labels.length}; the table below has everyone)` : ''}. Over 100% is a scheduling condition, not a judgement of the person.`}>
                    <GroupedBars labels={balance.labels} unit="h" ariaLabel="Capacity and projected demand by technician" series={[{ name: 'Capacity', values: balance.capacity, color: COLORS.capacity }, { name: 'Projected demand', values: balance.projected, color: COLORS.planned }]} />
                  </Panel>
                )}
                <Panel title={data.seesOthers ? 'Forecast by technician' : 'Your forecast'} hint="Sort any column yourself; nothing is pre-ranked.">
                  <FigureTable caption="Forecast by technician" rows={data.people} cols={personCols} keyOf={(p) => p.appUserId} initialSort="name"
                    narrowTitle={(p) => (
                      <span>
                        <span className="font-medium">{p.displayName}</span>
                        {p.teams.length > 0 && <span className="block text-[11px] text-[var(--muted)]">{p.teams.join(', ')}</span>}
                        {!p.hasSchedule && <span className="block text-[11px] text-[var(--muted)]">No working schedule</span>}
                      </span>
                    )} />
                  {data.unassigned && (
                    <p className="mt-2 text-xs text-[var(--muted)]">
                      Not yet assigned: <button type="button" onClick={(e) => { e.currentTarget.focus(); setDrill({ list: 'unscheduled' }); }} className="underline decoration-dotted underline-offset-4 hover:decoration-solid">{mins(data.unassigned.unscheduledMinutes)} of estimated work on {items(data.unassigned.unscheduledItems)}</button>
                      {data.unassigned.unestimatedItems > 0 && <> and {items(data.unassigned.unestimatedItems, 'unestimated item', 'unestimated items')}</>} routed to a team and held by nobody. Counted in the team and the totals, in no person&rsquo;s row.
                    </p>
                  )}
                </Panel>
                {data.seesOthers && data.teams.length > 0 && (
                  <Panel title="Forecast by team" hint="Members' capacity against the demand on them and on work routed to the team. A person in two teams appears under both; the totals count them once.">
                    <FigureTable caption="Forecast by team" rows={data.teams} cols={groupCols('Team', true)} keyOf={(g) => g.key} initialSort="name" narrowTitle={(g) => <span className="font-medium">{g.name}</span>} />
                  </Panel>
                )}
                {data.skills.length > 0 && (
                  <Panel title="Skill capacity" hint="Only for skills that some estimated open work asks for. A person holding two skills counts under both, so the rows do not add up; anyone holding a skill counts, at any level.">
                    <SkillTable skills={data.skills} onOpen={(s) => setDrill({ list: 'skill', skillId: s.skillId, skillName: s.name })} />
                  </Panel>
                )}
                <div className="grid gap-3 lg:grid-cols-2">
                  <Panel title="Demand by client" hint="Work ahead per client. Work you cannot open is one row.">
                    <FigureTable caption="Demand by client" rows={data.clients} cols={groupCols('Client', false)} keyOf={(g) => g.key} initialSort="name" narrowTitle={(g) => <span className="font-medium">{g.name}</span>} />
                  </Panel>
                  <Panel title="Demand by source" hint="Each PSA connection, the team's own boards, monitoring.">
                    <FigureTable caption="Demand by source" rows={data.sources} cols={groupCols('Source', false)} keyOf={(g) => g.key} initialSort="name" narrowTitle={(g) => <span className="font-medium">{g.name}</span>} />
                  </Panel>
                </div>
              </>
            )}
            {(tab === 'history' || tab === 'quality') && trends.error && <Alert error={trends.error} />}
            {(tab === 'history' || tab === 'quality') && trends.isLoading && !trends.data && <div aria-busy="true" className="h-40 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}
            {tab === 'history' && trends.data && <HistoryTab trends={trends.data} timeZone={zone} />}
            {tab === 'quality' && trends.data && <QualityTab trends={trends.data} />}
            {tab === 'health' && <HealthTab forecast={data} canSeeHealth={canSeeHealth} timeZone={zone} />}
          </div>

          <Panel title="What these numbers do and do not cover">
            <ul className="list-disc space-y-1 pl-5 text-xs text-[var(--muted)]">
              {((tab === 'history' || tab === 'quality') && trends.data ? trends.data.notes : data.notes).map((n) => <li key={n}>{n}</li>)}
              <li>Operational facts for planning. Not attendance, not presence, and not a performance score: nobody is scored or ranked, and no statement here is a verdict on a person.</li>
            </ul>
          </Panel>
        </>
      )}
      {drill && data && <ForecastDrillDown drill={drill} query={fq} timeZone={zone} onClose={() => setDrill(null)} />}
    </div>
  );
}
