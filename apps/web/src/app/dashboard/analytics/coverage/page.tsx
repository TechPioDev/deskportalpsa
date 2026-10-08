'use client';

import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Info, Clock, CheckCircle2 } from 'lucide-react';
import { api } from '@/lib/api';

/**
 * Portal coverage — how much of the work the PSA recorded is visible in this portal.
 *
 * Deliberately NOT "PSA hours versus portal hours". Two numbers of the same unit side by side, one
 * smaller, get subtracted by every reader, and the difference is then read as time wasted — which
 * the data cannot support and which no footnote survives. This is a percentage of work that is
 * visible here, not a deficit, and the copy says so where the number is rather than underneath it.
 *
 * Low coverage almost always means the portal is not where that person works yet. That is a finding
 * about rollout, not about people, and the page states it in those words.
 */
const RANGES = [
  { key: '7', label: 'Last 7 days', days: 7 },
  { key: '30', label: 'Last 30 days', days: 30 },
  { key: 'all', label: 'Everything recorded', days: null },
] as const;

export default function CoveragePage() {
  const [range, setRange] = useState<typeof RANGES[number]['key']>('30');
  const days = RANGES.find((r) => r.key === range)!.days;
  const from = days ? new Date(Date.now() - days * 86_400_000).toISOString() : undefined;

  const { data, isLoading, error } = useQuery({
    queryKey: ['portal-coverage', range],
    queryFn: () => api.portalCoverage({ from }),
    retry: false,
  });

  const since = data?.activityRecordedSince
    ? new Date(data.activityRecordedSince).toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })
    : null;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Portal coverage</h1>
          <p className="max-w-prose text-sm text-[var(--muted)]">
            How much of the work your PSA recorded also shows activity in this portal. A measure of
            where work is happening &mdash; not of how hard anyone is working.
          </p>
        </div>
        <div className="flex overflow-hidden rounded-lg border border-[var(--border)] text-xs font-medium">
          {RANGES.map((r) => (
            <button key={r.key} type="button" onClick={() => setRange(r.key)}
              className={`px-3 py-1.5 ${range === r.key
                ? 'bg-brand text-brand-fg'
                : 'bg-[var(--surface)] text-[var(--muted)] hover:text-[var(--fg)]'}`}>
              {r.label}
            </button>
          ))}
        </div>
      </div>

      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {error && <p className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>}

      {data && (
        <>
          {/* The caveat that decides whether the number means anything, ABOVE the number rather
              than below it. A range starting before the log existed is missing evidence, not a
              low score, and the two must never look the same. */}
          {data.rangeStartsBeforeRecording && (
            <div className="flex gap-2 rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm dark:border-amber-900/60 dark:bg-amber-950/30">
              <Info size={15} className="mt-0.5 shrink-0 text-amber-700 dark:text-amber-300" />
              <p className="leading-relaxed text-amber-900 dark:text-amber-200">
                {since
                  ? <>Portal activity has only been recorded since <strong>{since}</strong>. Work
                      before then shows as uncovered because nothing was watching, not because it
                      did not happen &mdash; choose a shorter range for a figure that means something.</>
                  : <>No portal activity has been recorded yet, so there is nothing to compare
                      against. This becomes meaningful once technicians start working in the portal.</>}
              </p>
            </div>
          )}

          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <Stat icon={Clock} label="Hours the PSA recorded" value={`${data.totalPsaHours.toFixed(1)}h`} />
            <Stat icon={CheckCircle2} label="Time entries" value={data.totalPsaEntries.toString()} />
            <Stat icon={CheckCircle2} label="With portal activity" value={data.totalCorroborated.toString()} />
            <Stat icon={Info} label="Portal coverage"
              value={data.overallCoveragePct === null ? '—' : `${data.overallCoveragePct}%`} />
          </div>

          <p className="max-w-prose text-xs leading-relaxed text-[var(--muted)]">
            An entry counts as covered when something happened in the portal on the same ticket on the
            same day. That says the work is visible here &mdash; not that the same person did it in
            both systems, which this data cannot establish.
          </p>

          {data.technicians.length === 0 ? (
            <p className="rounded-xl border border-dashed border-[var(--border)] p-6 text-center text-sm text-[var(--muted)]">
              No time was recorded in the PSA for this range.
            </p>
          ) : (
            <div className="overflow-x-auto rounded-xl border border-[var(--border)]">
              <table className="w-full min-w-[40rem] border-collapse bg-[var(--surface)] text-sm">
                <thead>
                  <tr className="bg-[var(--bg)] text-left text-[11px] uppercase tracking-wide text-[var(--faint)]">
                    <th className="px-4 py-2.5 font-medium">Technician</th>
                    <th className="px-4 py-2.5 text-right font-medium">PSA hours</th>
                    <th className="px-4 py-2.5 text-right font-medium">Entries</th>
                    <th className="px-4 py-2.5 text-right font-medium">Covered</th>
                    <th className="px-4 py-2.5 text-right font-medium">Coverage</th>
                    <th className="px-4 py-2.5 text-right font-medium">Portal actions</th>
                  </tr>
                </thead>
                <tbody>
                  {data.technicians.map((t) => (
                    <tr key={`${t.psaConnectionId ?? ''}:${t.technicianExternalId}`} className="border-t border-[var(--border)]">
                      <td className="px-4 py-2.5 font-medium">
                        {t.technicianName ?? (
                          <span className="text-[var(--muted)]">
                            Unmapped ({t.technicianExternalId})
                          </span>
                        )}
                      </td>
                      <td className="px-4 py-2.5 text-right tabular-nums">{t.psaHours.toFixed(1)}h</td>
                      <td className="px-4 py-2.5 text-right tabular-nums">{t.psaEntries}</td>
                      <td className="px-4 py-2.5 text-right tabular-nums">{t.corroboratedEntries}</td>
                      <td className="px-4 py-2.5 text-right tabular-nums">
                        {t.coveragePct === null ? <span className="text-[var(--faint)]">—</span> : `${t.coveragePct}%`}
                      </td>
                      <td className="px-4 py-2.5 text-right tabular-nums">{t.portalEvents}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          <p className="max-w-prose text-xs leading-relaxed text-[var(--muted)]">
            Low coverage usually means the portal is not where that person works yet &mdash; they may
            be replying from the PSA, email or a remote session. Treat it as a question about
            rollout, not about effort.
          </p>
        </>
      )}
    </div>
  );
}

function Stat({ icon: Icon, label, value }: { icon: typeof Clock; label: string; value: string }) {
  return (
    <div className="flex items-center gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3">
      <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-brand/10 text-brand">
        <Icon size={16} />
      </span>
      <span className="min-w-0">
        <span className="block text-lg font-semibold leading-tight tabular-nums">{value}</span>
        <span className="block truncate text-xs text-[var(--muted)]">{label}</span>
      </span>
    </div>
  );
}
