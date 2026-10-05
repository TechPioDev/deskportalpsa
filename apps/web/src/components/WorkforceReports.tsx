'use client';

import { useMemo, useState } from 'react';
import Link from 'next/link';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { ChevronLeft, Download, FileSpreadsheet, RefreshCw } from 'lucide-react';
import { api, type InsightsQuery, type WorkforceReport as Report, type WorkforceReportDefinition as ReportDefinition } from '@/lib/api';
import { Alert, btn, card, downloadCsv, field, fmtAt, isDate, PERIODS, th } from '@/components/WorkforceAnalytics';
import { COMPARISONS, WINDOWS, WorkFilterSelects } from '@/components/WorkforceInsights';

/**
 * The report center (Phase 8): reports defined in code, each previewed and exported (CSV or XLSX)
 * by the same code from the same filters, so a file's totals are the preview's. Nothing is stored
 * or scheduled from here. Facts from records; not a score.
 */

// ---- the catalogue -----------------------------------------------------------------------------------------------

const PERIOD_KIND: Record<string, string> = { history: 'Over a period', forecast: 'Looking ahead', compare: 'A period against the one before', none: 'As it stands' };

export function ReportCatalogue() {
  const { data, isLoading, error } = useQuery({ queryKey: ['workforce-reports'], queryFn: api.workforceReports, staleTime: 5 * 60_000, retry: false });
  const groups = useMemo(() => {
    const map = new Map<string, ReportDefinition[]>();
    for (const d of data ?? []) map.set(d.category, [...(map.get(d.category) ?? []), d]);
    return [...map.entries()];
  }, [data]);
  return (
    <div className="space-y-4">
      {error && <Alert error={error} />}
      {isLoading && <div aria-busy="true" className="h-40 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}
      {groups.map(([category, reports]) => (
        <section key={category} aria-label={category}>
          <h2 className="mb-2 text-xs font-semibold uppercase tracking-wide text-[var(--muted)]">{category}</h2>
          <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {reports.map((r) => (
              <li key={r.key}>
                <Link href={`/dashboard/workforce/reports/${r.key}`} className={`${card} flex h-full flex-col gap-1 p-4 hover:border-brand focus:outline-none focus-visible:ring-2 focus-visible:ring-brand`}>
                  <span className="flex items-center gap-2 text-sm font-semibold"><FileSpreadsheet size={16} className="shrink-0 text-brand" aria-hidden="true" />{r.title}</span>
                  <span className="text-xs text-[var(--muted)]">{r.description}</span>
                  <span className="mt-auto pt-2 text-[11px] uppercase tracking-wide text-[var(--faint)]">{PERIOD_KIND[r.periodKind] ?? ''}</span>
                </Link>
              </li>
            ))}
          </ul>
        </section>
      ))}
      {data && (
        <p className="max-w-prose text-xs text-[var(--muted)]">
          Each report is built when you open it, from the same definitions as the analytics dashboard and the management insights. An export needs the workforce export permission and is written to the audit log.
          Scheduled delivery by email is not offered here.
        </p>
      )}
    </div>
  );
}

// ---- one report ------------------------------------------------------------------------------------------------------

type ReportFilters = {
  period: string; window: string; compare: string; from: string; to: string; by: string;
  teamId: string; departmentId: string; appUserId: string; clientId: string; source: string; priority: string;
};
const DEFAULTS: ReportFilters = { period: 'last-week', window: 'next-7', compare: 'last-30', from: '', to: '', by: 'category', teamId: '', departmentId: '', appUserId: '', clientId: '', source: '', priority: '' };
const PARAMS: [keyof ReportFilters, string][] = [
  ['period', 'period'], ['window', 'window'], ['compare', 'compare'], ['from', 'from'], ['to', 'to'], ['by', 'by'],
  ['teamId', 'team'], ['departmentId', 'department'], ['appUserId', 'person'], ['clientId', 'client'], ['source', 'source'], ['priority', 'priority'],
];

function useReportFilters(): [ReportFilters, (patch: Partial<ReportFilters>) => void] {
  const sp = useSearchParams();
  const router = useRouter();
  const pathname = usePathname();
  const filters = useMemo<ReportFilters>(() => {
    const f = { ...DEFAULTS };
    for (const [k, param] of PARAMS) { const v = sp.get(param); if (v) f[k] = v; }
    if (!isDate(f.from)) f.from = '';
    if (!isDate(f.to)) f.to = '';
    return f;
  }, [sp]);
  const set = (patch: Partial<ReportFilters>) => {
    const next = { ...filters, ...patch };
    const qs = new URLSearchParams();
    for (const [k, param] of PARAMS) if (next[k] && next[k] !== DEFAULTS[k]) qs.set(param, next[k]);
    const s = qs.toString();
    router.replace(s ? `${pathname}?${s}` : pathname, { scroll: false });
  };
  return [filters, set];
}

/** The query a report takes: only the kind of period it uses, and only the filters it declares. */
function toReportQuery(d: ReportDefinition, f: ReportFilters): InsightsQuery {
  const q: InsightsQuery = {};
  const custom = (key: string) => key === 'custom' && !!(f.from && f.to);
  if (d.periodKind === 'history') { q.period = f.period === 'custom' && !custom(f.period) ? 'last-week' : f.period; if (custom(f.period)) { q.from = f.from; q.to = f.to; } }
  if (d.periodKind === 'forecast') { q.window = f.window === 'custom' && !custom(f.window) ? 'next-7' : f.window; if (custom(f.window)) { q.from = f.from; q.to = f.to; } }
  if (d.periodKind === 'compare') q.compare = f.compare;
  const has = (k: string) => d.filters.includes(k);
  if (has('team')) q.teamId = f.teamId;
  if (has('department')) q.departmentId = f.departmentId;
  if (has('person')) q.appUserId = f.appUserId;
  if (has('client')) q.clientId = f.clientId;
  if (has('source')) q.source = f.source;
  if (has('priority')) q.priority = f.priority;
  if (has('by')) q.by = f.by;
  return q;
}

/** A cell as the screen says it, by the column's kind; null is "not applicable", never a zero. */
function cell(kind: string, v: string | number | null): string {
  if (v == null) return kind === 'text' ? '' : 'N/A';
  if (typeof v === 'string') return v;
  if (kind === 'hours') return `${(Math.round(v * 100) / 100).toLocaleString('en-GB', { maximumFractionDigits: 2 })} h`;
  if (kind === 'percent') return `${Math.round(v * 10) / 10}%`;
  return v.toLocaleString('en-GB');
}

function ReportTable({ report }: { report: Report }) {
  const [sort, setSort] = useState<{ col: number; dir: 1 | -1 } | null>(null);
  const rows = useMemo(() => {
    if (!sort) return report.rows;
    return [...report.rows].sort((a, b) => {
      const x = a[sort.col], y = b[sort.col];
      if (x == null && y == null) return 0;
      if (x == null) return 1;
      if (y == null) return -1;
      const r = typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), undefined, { sensitivity: 'base', numeric: true });
      return r * sort.dir;
    });
  }, [report.rows, sort]);
  if (report.rows.length === 0) return <p className="py-6 text-center text-sm text-[var(--muted)]">Nothing to report under these filters.</p>;
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <caption className="sr-only">{report.definition.title}</caption>
        <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
          <tr>
            {report.columns.map((c, i) => (
              <th key={c.key} scope="col" aria-sort={sort?.col === i ? (sort.dir === 1 ? 'ascending' : 'descending') : 'none'} className={`${th} whitespace-nowrap ${c.kind === 'text' || c.kind === 'date' ? '' : 'text-right'}`}>
                <button type="button" onClick={() => setSort((s) => (s?.col === i ? { col: i, dir: s.dir === 1 ? -1 : 1 } : { col: i, dir: 1 }))} className="inline-flex items-center gap-1 hover:text-[var(--fg)]">
                  {c.label}{sort?.col === i && <span aria-hidden="true">{sort.dir === 1 ? '▲' : '▼'}</span>}
                </button>
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-[var(--border)]">
          {rows.map((r, n) => (
            <tr key={n}>
              {r.map((v, i) => {
                const kind = report.columns[i]?.kind ?? 'text';
                const text = cell(kind, v);
                return i === 0
                  ? <th key={i} scope="row" className="max-w-[28rem] px-3 py-2 text-left font-medium">{text}</th>
                  : <td key={i} className={`px-3 py-2 ${kind === 'text' ? 'max-w-[28rem] text-[var(--muted)]' : kind === 'date' ? 'whitespace-nowrap tabular-nums' : 'whitespace-nowrap text-right tabular-nums'}`}>{text}</td>;
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function ReportView({ reportKey }: { reportKey: string }) {
  const [filters, setFilters] = useReportFilters();
  const catalogue = useQuery({ queryKey: ['workforce-reports'], queryFn: api.workforceReports, staleTime: 5 * 60_000, retry: false });
  const { data: options } = useQuery({ queryKey: ['analytics-filters'], queryFn: api.analyticsFilters, staleTime: 5 * 60_000, retry: false });
  const definition = catalogue.data?.find((d) => d.key === reportKey.toLowerCase());
  const query = useMemo(() => (definition ? toReportQuery(definition, filters) : null), [definition, filters]);
  const needsDates = !!definition && ((definition.periodKind === 'history' && filters.period === 'custom') || (definition.periodKind === 'forecast' && filters.window === 'custom')) && !(filters.from && filters.to);
  const { data, isLoading, error, refetch, isFetching } = useQuery({
    queryKey: ['workforce-report', reportKey, query], queryFn: () => api.workforceReport(reportKey, query!), retry: false, placeholderData: (prev) => prev, enabled: !!query && !needsDates,
  });
  const [busy, setBusy] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const exportAs = async (format: 'csv' | 'xlsx') => {
    if (!query) return;
    setBusy(format); setProblem(null);
    setProblem(await downloadCsv(api.workforceReportExportUrl(reportKey, query, format), `${reportKey}.${format}`));
    setBusy(null);
  };
  const zone = data?.period?.timeZone ?? options?.timeZone ?? 'UTC';
  const active = PARAMS.some(([k]) => filters[k] !== DEFAULTS[k]);

  return (
    <div className="space-y-3">
      <Link href="/dashboard/workforce/reports" className="inline-flex items-center gap-1 text-sm text-[var(--muted)] hover:text-[var(--fg)]"><ChevronLeft size={14} aria-hidden="true" /> All reports</Link>
      {catalogue.error && <Alert error={catalogue.error} />}
      {catalogue.data && !definition && <p role="alert" className="text-sm text-[var(--muted)]">There is no such report, or it is not one you may open.</p>}
      {definition && (
        <>
          <div>
            <h2 className="text-lg font-semibold">{definition.title}</h2>
            <p className="max-w-prose text-sm text-[var(--muted)]">{definition.description}</p>
          </div>
          <section aria-label="Filters" className={`${card} flex flex-wrap items-center gap-2 p-3`}>
            {definition.periodKind === 'history' && (
              <select value={filters.period} onChange={(e) => setFilters({ period: e.target.value })} aria-label="Period" className={field}>
                {PERIODS.map(([k, label]) => <option key={k} value={k}>{label}</option>)}
              </select>
            )}
            {definition.periodKind === 'forecast' && (
              <select value={filters.window} onChange={(e) => setFilters({ window: e.target.value })} aria-label="Forecast window" className={field}>
                {WINDOWS.map(([k, label]) => <option key={k} value={k}>{label}</option>)}
              </select>
            )}
            {definition.periodKind === 'compare' && (
              <select value={filters.compare} onChange={(e) => setFilters({ compare: e.target.value })} aria-label="Comparison" className={field}>
                {COMPARISONS.map(([k, label]) => <option key={k} value={k}>{label}</option>)}
              </select>
            )}
            {((definition.periodKind === 'history' && filters.period === 'custom') || (definition.periodKind === 'forecast' && filters.window === 'custom')) && (
              <>
                <input type="date" value={filters.from} onChange={(e) => setFilters({ from: e.target.value })} aria-label="First day" className={field} />
                <input type="date" value={filters.to} onChange={(e) => setFilters({ to: e.target.value })} aria-label="Last day" className={field} />
              </>
            )}
            {definition.filters.includes('by') && (
              <select value={filters.by} onChange={(e) => setFilters({ by: e.target.value })} aria-label="Group by" className={field}>
                <option value="category">By category</option><option value="client">By client</option><option value="source">By source</option>
              </select>
            )}
            {definition.filters.length > 0 && <WorkFilterSelects value={filters} onChange={setFilters} options={options} allow={definition.filters} />}
            {active && <button type="button" onClick={() => setFilters({ ...DEFAULTS })} className={btn}>Reset</button>}
            <div className="ml-auto flex flex-wrap items-center gap-2">
              {data?.canExport && (
                <>
                  <button type="button" onClick={() => exportAs('csv')} disabled={!!busy || needsDates} className={`${btn} py-1 text-xs`}><Download size={14} aria-hidden="true" /> {busy === 'csv' ? 'Preparing…' : 'Export CSV'}</button>
                  <button type="button" onClick={() => exportAs('xlsx')} disabled={!!busy || needsDates} className={`${btn} py-1 text-xs`}><FileSpreadsheet size={14} aria-hidden="true" /> {busy === 'xlsx' ? 'Preparing…' : 'Export XLSX'}</button>
                </>
              )}
              <button type="button" onClick={() => refetch()} aria-label="Refresh" className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><RefreshCw size={14} className={isFetching ? 'animate-spin' : ''} /></button>
            </div>
          </section>
          {problem && <p role="alert" className="text-sm text-red-700 dark:text-red-300">{problem}</p>}
          {needsDates && <p className="text-sm text-[var(--muted)]">Choose the first and last day of the custom range.</p>}
          {error && <Alert error={error} />}
          {isLoading && !data && <div aria-busy="true" className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}
          {data && (
            <>
              <p className="text-xs text-[var(--muted)]" aria-live="polite">
                {data.period && <><span className="font-medium text-[var(--fg)]">{data.period.label}</span> ({data.period.timeZone}) · </>}
                {data.totalRows} {data.totalRows === 1 ? 'row' : 'rows'} · generated {fmtAt(data.generatedAt, zone)}
                {data.sync.length > 0 && <> · PSA synced {data.sync.map((s) => `${s.connection}: ${s.lastSuccessfulSyncAt ? fmtAt(s.lastSuccessfulSyncAt, zone) : 'never'}`).join(', ')}</>}
              </p>
              {data.applied.length > 0 && (
                <ul aria-label="Applied filters" className="flex flex-wrap gap-1.5 text-xs">
                  {data.applied.map((a) => <li key={a.label} className="rounded-full border border-[var(--border)] px-2.5 py-0.5"><span className="text-[var(--muted)]">{a.label}: </span>{a.value}</li>)}
                </ul>
              )}
              <dl aria-label="Summary" className={`${card} grid grid-cols-2 gap-3 px-4 py-3 sm:grid-cols-3 lg:grid-cols-4`}>
                {data.summary.map((s) => <div key={s.label}><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">{s.label}</dt><dd className="text-base font-semibold tabular-nums">{s.value}</dd></div>)}
              </dl>
              <section aria-label="Preview" className={`${card} p-4`}>
                <ReportTable report={data} />
                {data.truncated && <p className="mt-2 text-xs text-[var(--muted)]">Showing the first {data.rows.length} of {data.totalRows} rows. The export has all of them.</p>}
              </section>
              <section aria-label="What this report covers" className={`${card} p-4`}>
                <h3 className="mb-2 text-sm font-semibold">What this report does and does not cover</h3>
                <ul className="list-disc space-y-1 pl-5 text-xs text-[var(--muted)]">
                  {data.notes.map((n) => <li key={n}>{n}</li>)}
                  <li>Facts from records, by the definitions the dashboards use. Not attendance and not a performance score.</li>
                </ul>
              </section>
            </>
          )}
        </>
      )}
    </div>
  );
}
