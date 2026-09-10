'use client';

import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Clock, CheckCircle2, Users, Gauge, Info } from 'lucide-react';
import { api } from '@/lib/api';
import type { TechnicianDay } from '@/lib/types';
import { BarChart, LineChart } from '@/components/charts';

/**
 * Hours and output per technician.
 *
 * Separate from the productivity overview because it answers a different question. The overview
 * asks how the desk is doing; this asks who did what, over a window someone chooses. It is also the
 * first view in the product that works for a technician with no PSA account — every figure here is
 * attributed through the portal identity, which is the only one most of this team has.
 */

const PRESETS = [
  { label: 'Last 7 days', days: 7 },
  { label: 'Last 30 days', days: 30 },
  { label: 'Last 90 days', days: 90 },
] as const;

function isoDay(d: Date) {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

function shortDate(iso: string) {
  const d = new Date(iso);
  return `${d.getDate()}/${d.getMonth() + 1}`;
}

/** Hours to one decimal. Two decimals of an hour is a precision nobody logs at. */
const hrs = (n: number) => (Math.round(n * 10) / 10).toLocaleString();

type PerTech = {
  key: string;
  name: string;
  hours: number;
  billableHours: number;
  resolved: number;
  touched: number;
  days: number;
};

export default function TechnicianProductivityPage() {
  const [days, setDays] = useState<number>(30);
  const [custom, setCustom] = useState<{ from: string; to: string } | null>(null);

  const range = useMemo(() => {
    if (custom) return { from: `${custom.from}T00:00:00Z`, to: `${custom.to}T23:59:59Z` };
    const to = new Date();
    const from = new Date(Date.now() - days * 86400_000);
    return { from: from.toISOString(), to: to.toISOString() };
  }, [custom, days]);

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['daily', range.from, range.to],
    queryFn: () => api.dailyMetrics(range.from, range.to),
  });

  const rows: TechnicianDay[] = useMemo(() => data ?? [], [data]);

  // Per technician, across the whole window.
  const perTech = useMemo(() => {
    const map = new Map<string, PerTech>();
    for (const r of rows) {
      const key = r.appUserId ?? r.technicianExternalId ?? r.name;
      const cur = map.get(key) ?? {
        key, name: r.name, hours: 0, billableHours: 0, resolved: 0, touched: 0, days: 0,
      };
      cur.hours += r.hours;
      cur.billableHours += r.billableHours;
      cur.resolved += r.resolved;
      cur.touched += r.ticketsTouched;
      cur.days += 1;
      map.set(key, cur);
    }
    return [...map.values()].sort((a, b) => b.hours - a.hours);
  }, [rows]);

  // Team totals per calendar day, for the two charts.
  const byDay = useMemo(() => {
    const map = new Map<string, { hours: number; resolved: number }>();
    for (const r of rows) {
      const cur = map.get(r.date) ?? { hours: 0, resolved: 0 };
      map.set(r.date, { hours: cur.hours + r.hours, resolved: cur.resolved + r.resolved });
    }
    return [...map.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [rows]);

  const totals = useMemo(() => {
    const hours = perTech.reduce((a, t) => a + t.hours, 0);
    const billable = perTech.reduce((a, t) => a + t.billableHours, 0);
    const resolved = perTech.reduce((a, t) => a + t.resolved, 0);
    const touched = perTech.reduce((a, t) => a + t.touched, 0);
    return {
      hours, billable, resolved, touched,
      billablePct: hours > 0 ? Math.round((billable / hours) * 100) : 0,
      perTicket: touched > 0 ? hours / touched : 0,
      people: perTech.length,
    };
  }, [perTech]);

  // Thin the x labels so a 90-day window does not print 90 of them on top of each other.
  const step = Math.max(1, Math.ceil(byDay.length / 8));
  const labels = byDay.map(([d], i) => (i % step === 0 ? shortDate(d) : ''));

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Technician productivity</h1>
          <p className="text-sm text-[var(--muted)]">
            Hours logged and tickets resolved, per person, for the window you choose.
          </p>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <select
            value={custom ? 'custom' : String(days)}
            onChange={(e) => {
              if (e.target.value === 'custom') {
                const to = new Date();
                const from = new Date(Date.now() - 30 * 86400_000);
                setCustom({ from: isoDay(from), to: isoDay(to) });
              } else {
                setCustom(null);
                setDays(Number(e.target.value));
              }
            }}
            aria-label="Date range"
            className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm outline-none focus:border-brand"
          >
            {PRESETS.map((p) => <option key={p.days} value={p.days}>{p.label}</option>)}
            <option value="custom">Custom range…</option>
          </select>

          {custom && (
            <span className="flex items-center gap-2">
              <input
                type="date" value={custom.from} max={custom.to}
                onChange={(e) => setCustom({ ...custom, from: e.target.value })}
                aria-label="From date"
                className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm outline-none focus:border-brand"
              />
              <span className="text-sm text-[var(--muted)]">to</span>
              <input
                type="date" value={custom.to} min={custom.from} max={isoDay(new Date())}
                onChange={(e) => setCustom({ ...custom, to: e.target.value })}
                aria-label="To date"
                className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm outline-none focus:border-brand"
              />
            </span>
          )}
        </div>
      </div>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Tile icon={Clock} label="Hours logged" value={hrs(totals.hours)}
              sub={`${totals.billablePct}% billable`} />
        <Tile icon={CheckCircle2} label="Tickets resolved" value={totals.resolved.toLocaleString()}
              sub={`${totals.touched.toLocaleString()} tickets touched`} />
        <Tile icon={Gauge} label="Hours per ticket" value={hrs(totals.perTicket)}
              sub="across every ticket worked in the range" />
        <Tile icon={Users} label="People with activity" value={String(totals.people)}
              sub="anyone who logged time or resolved work" />
      </div>

      {isError && (
        <p className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 text-sm text-red-600 dark:text-red-400">
          {error instanceof Error ? error.message : 'Could not load productivity figures.'}
        </p>
      )}

      <div className="grid gap-4 lg:grid-cols-2">
        <Panel title="Hours per day" hint="Summed across everyone who logged time that day.">
          {byDay.length > 0
            ? <LineChart labels={labels} values={byDay.map(([, v]) => Math.round(v.hours * 10) / 10)} color="#3b82f6" unit="h" />
            : <Empty loading={isLoading} />}
        </Panel>
        <Panel title="Tickets resolved per day" hint="Counted on the day the resolution landed.">
          {byDay.length > 0
            ? <BarChart labels={labels} values={byDay.map(([, v]) => v.resolved)} color="#22c55e" />
            : <Empty loading={isLoading} />}
        </Panel>
      </div>

      <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <div className="flex items-center justify-between border-b border-[var(--border)] px-4 py-3">
          <h2 className="text-sm font-semibold">By technician</h2>
          <span className="text-xs text-[var(--muted)]">Sorted by hours logged</span>
        </div>

        {perTech.length === 0 ? (
          <Empty loading={isLoading} />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-left text-sm">
              <thead className="text-xs text-[var(--muted)]">
                <tr className="border-b border-[var(--border)]">
                  <th className="px-4 py-2 font-medium">Technician</th>
                  <th className="px-2 py-2 text-right font-medium">Hours</th>
                  <th className="px-2 py-2 text-right font-medium">Billable</th>
                  <th className="px-2 py-2 text-right font-medium">Resolved</th>
                  <th className="px-2 py-2 text-right font-medium">Tickets</th>
                  <th className="px-2 py-2 text-right font-medium">Hrs / ticket</th>
                  <th className="px-4 py-2 text-right font-medium">Active days</th>
                </tr>
              </thead>
              <tbody>
                {perTech.map((t) => (
                  <tr key={t.key} className="border-b border-[var(--border)] last:border-0">
                    <td className="px-4 py-2.5">
                      <span className="flex items-center gap-2">
                        <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-[var(--bg)] text-[9px] font-semibold">
                          {t.name.split(' ').map((n) => n[0]).slice(0, 2).join('')}
                        </span>
                        {t.name}
                      </span>
                    </td>
                    <td className="px-2 py-2.5 text-right tabular-nums">{hrs(t.hours)}</td>
                    <td className="px-2 py-2.5 text-right tabular-nums text-[var(--muted)]">{hrs(t.billableHours)}</td>
                    <td className="px-2 py-2.5 text-right tabular-nums">{t.resolved}</td>
                    <td className="px-2 py-2.5 text-right tabular-nums text-[var(--muted)]">{t.touched}</td>
                    <td className="px-2 py-2.5 text-right tabular-nums">{t.touched > 0 ? hrs(t.hours / t.touched) : '—'}</td>
                    <td className="px-4 py-2.5 text-right tabular-nums text-[var(--muted)]">{t.days}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Deliberate, and placed under the table rather than hidden behind a tooltip. These columns
          invite a ranking, and a ranking read without them is how a fast worker on simple tickets
          outranks the person carrying the hard ones. */}
      <p className="flex items-start gap-2 rounded-xl border border-[var(--border)] bg-[var(--bg)] px-4 py-3 text-xs leading-relaxed text-[var(--muted)]">
        <Info size={14} className="mt-0.5 shrink-0" aria-hidden="true" />
        <span>
          Read these together, never one alone. Hours reward whoever is slowest; resolved counts
          reward whoever takes the easy tickets; neither knows how hard the work was. They also
          cover only what happened <strong>in this portal</strong> — anything done directly in the
          PSA is invisible here, so a low row may mean someone worked elsewhere rather than less.
        </span>
      </p>
    </div>
  );
}

function Tile({ icon: Icon, label, value, sub }: {
  icon: typeof Clock; label: string; value: string; sub: string;
}) {
  return (
    <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
      <div className="flex items-center gap-2 text-xs font-medium text-[var(--muted)]">
        <Icon size={14} aria-hidden="true" /> {label}
      </div>
      <p className="mt-1.5 text-2xl font-semibold tabular-nums">{value}</p>
      <p className="mt-0.5 text-xs text-[var(--muted)]">{sub}</p>
    </div>
  );
}

function Panel({ title, hint, children }: { title: string; hint: string; children: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
      <div className="mb-3">
        <h2 className="text-sm font-semibold">{title}</h2>
        <p className="text-xs text-[var(--muted)]">{hint}</p>
      </div>
      {children}
    </div>
  );
}

function Empty({ loading }: { loading: boolean }) {
  return (
    <div className="py-10 text-center text-sm text-[var(--muted)]">
      {loading ? 'Loading…' : 'No time logged and nothing resolved in this range.'}
    </div>
  );
}
