'use client';

import Link from 'next/link';
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Building2, Clock, Users, AlertCircle } from 'lucide-react';
import { api } from '@/lib/api';

/**
 * Where the desk's capacity goes, by client — the question an owner actually asks before a renewal.
 *
 * Ranked by HOURS, not ticket count: twenty trivial tickets are not the same drain as three that
 * took a day each. Ranked bars rather than a pie, because the question is ordering and a pie answers
 * that worse at every count above about four.
 *
 * Every figure here is derived from what the PSA reported. Where one cannot be computed for a
 * ticket, that ticket is excluded and said so — an average over two of five is not wrong, but shown
 * alone it reads as the whole picture.
 */
const RANGES = [
  { key: '30', label: 'Last 30 days', days: 30 },
  { key: '90', label: 'Last 90 days', days: 90 },
  { key: 'all', label: 'Everything held', days: null },
] as const;

const fmtHours = (h: number) => h >= 10 ? `${Math.round(h)}h` : `${h.toFixed(1)}h`;
const fmtDuration = (hours: number) =>
  hours < 24 ? `${hours.toFixed(1)}h` : `${(hours / 24).toFixed(1)}d`;

export default function ClientAnalyticsPage() {
  const [range, setRange] = useState<typeof RANGES[number]['key']>('90');
  const days = RANGES.find((r) => r.key === range)!.days;
  const from = days ? new Date(Date.now() - days * 86_400_000).toISOString() : undefined;

  const { data, isLoading, error } = useQuery({
    queryKey: ['client-workload', range],
    queryFn: () => api.clientWorkload({ from }),
    retry: false,
  });

  const clients = data?.clients ?? [];
  const maxHours = Math.max(1, ...clients.map((c) => c.hoursWorked));
  const totalHours = clients.reduce((a, c) => a + c.hoursWorked, 0);
  const totalTickets = clients.reduce((a, c) => a + c.totalTickets, 0);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Client workload</h1>
          <p className="text-sm text-[var(--muted)]">
            Where your team&rsquo;s time is going, ranked by hours logged in the PSA.
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
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <Stat icon={Building2} label="Clients" value={clients.length.toString()} />
            <Stat icon={Clock} label="Hours logged" value={fmtHours(totalHours)} />
            <Stat icon={Users} label="Tickets" value={totalTickets.toString()} />
            <Stat icon={Clock} label="Still open"
              value={clients.reduce((a, c) => a + c.openTickets, 0).toString()} />
          </div>

          {/* What the numbers do NOT cover, stated beside them rather than in a footnote. A reader
              who does not know the import window will read these figures as the whole business. */}
          <div className="flex gap-2 rounded-xl border border-[var(--border)] bg-[var(--bg)] px-4 py-3 text-xs leading-relaxed text-[var(--muted)]">
            <AlertCircle size={14} className="mt-0.5 shrink-0" />
            <div className="space-y-1">
              <p>
                These figures cover only what the portal imports:{' '}
                {data.importWindows.map((w, i) => (
                  <span key={w.connectionName}>
                    {i > 0 && '; '}
                    <strong className="font-medium text-[var(--fg)]">{w.connectionName}</strong>{' '}
                    {w.importsClosedTickets ? 'open and closed' : 'open tickets only'}
                    {w.activeWithinDays ? `, active within ${w.activeWithinDays} days` : ''}
                    {' '}({w.ticketsHeld} held)
                  </span>
                ))}.
              </p>
              {data.ticketsWithoutClosure > 0 && (
                <p>
                  {data.ticketsWithoutClosure} ticket{data.ticketsWithoutClosure === 1 ? '' : 's'} in
                  range {data.ticketsWithoutClosure === 1 ? 'has' : 'have'} no closure date and
                  {' '}{data.ticketsWithoutClosure === 1 ? 'is' : 'are'} excluded from resolution times.
                </p>
              )}
            </div>
          </div>

          {clients.length === 0 ? (
            <p className="rounded-xl border border-dashed border-[var(--border)] p-6 text-center text-sm text-[var(--muted)]">
              No tickets in this range.
            </p>
          ) : (
            <>
              <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
                <h2 className="mb-4 text-sm font-semibold uppercase tracking-wide text-[var(--faint)]">
                  Hours by client
                </h2>
                <ul className="space-y-2.5">
                  {clients.map((c) => (
                    <li key={c.clientCompanyId} className="grid grid-cols-[minmax(6rem,10rem)_1fr_4rem] items-center gap-3">
                      <span className="truncate text-sm" title={c.clientName}>{c.clientName}</span>
                      <span className="h-3 rounded-full bg-[var(--bg)]">
                        <span className="block h-3 rounded-full bg-brand"
                          style={{ width: `${Math.max(2, (c.hoursWorked / maxHours) * 100)}%` }} />
                      </span>
                      <span className="text-right text-sm font-semibold tabular-nums">{fmtHours(c.hoursWorked)}</span>
                    </li>
                  ))}
                </ul>
              </div>

              <div className="overflow-x-auto rounded-xl border border-[var(--border)]">
                <table className="w-full min-w-[46rem] border-collapse bg-[var(--surface)] text-sm">
                  <thead>
                    <tr className="bg-[var(--bg)] text-left text-[11px] uppercase tracking-wide text-[var(--faint)]">
                      <th className="px-4 py-2.5 font-medium">Client</th>
                      <th className="px-4 py-2.5 text-right font-medium">Tickets</th>
                      <th className="px-4 py-2.5 text-right font-medium">Open</th>
                      <th className="px-4 py-2.5 text-right font-medium">Closed</th>
                      <th className="px-4 py-2.5 text-right font-medium">Hours</th>
                      <th className="px-4 py-2.5 text-right font-medium">Billable</th>
                      <th className="px-4 py-2.5 text-right font-medium">People</th>
                      <th className="px-4 py-2.5 text-right font-medium">Avg to close</th>
                      <th className="px-4 py-2.5 text-right font-medium">SLA met</th>
                    </tr>
                  </thead>
                  <tbody>
                    {clients.map((c) => (
                      <tr key={c.clientCompanyId} className="border-t border-[var(--border)]">
                        {/* Counts navigate; measures do not.
                            Tickets, Open and Closed are each a count OF A LIST, so there is
                            somewhere real to land. Hours, Billable, Avg to close and SLA met are
                            derived - there is no page listing 32 hours - and sending them somewhere
                            approximate would teach people the links are unreliable, which costs
                            more than the convenience buys. */}
                        <td className="px-4 py-2.5 font-medium">
                          <CountLink company={c.clientName} label={c.clientName} />
                        </td>
                        <td className="px-4 py-2.5 text-right tabular-nums">
                          <CountLink company={c.clientName} label={c.totalTickets} />
                        </td>
                        <td className="px-4 py-2.5 text-right tabular-nums">
                          <CountLink company={c.clientName} view="open" label={c.openTickets} />
                        </td>
                        <td className="px-4 py-2.5 text-right tabular-nums">
                          <CountLink company={c.clientName} view="resolved" label={c.closedTickets} />
                        </td>
                        <td className="px-4 py-2.5 text-right tabular-nums">{fmtHours(c.hoursWorked)}</td>
                        <td className="px-4 py-2.5 text-right tabular-nums text-green-700 dark:text-green-400">
                          {fmtHours(c.billableHours)}
                        </td>
                        <td className="px-4 py-2.5 text-right tabular-nums">{c.techniciansInvolved}</td>
                        {/* The sample travels with the average. A figure from 2 of 40 tickets is
                            not the same claim as one from 40, and hiding that difference is how a
                            dashboard becomes untrustworthy. */}
                        <td className="px-4 py-2.5 text-right tabular-nums">
                          {c.avgResolutionHours === null ? (
                            <span className="text-[var(--faint)]">—</span>
                          ) : (
                            <>
                              {fmtDuration(c.avgResolutionHours)}
                              <span className="ml-1 text-[11px] text-[var(--faint)]">
                                ({c.resolutionSample}/{c.totalTickets})
                              </span>
                            </>
                          )}
                        </td>
                        <td className="px-4 py-2.5 text-right tabular-nums">
                          {c.slaCompliancePct === null ? (
                            <span className="text-[var(--faint)]" title="No ticket in range carried an SLA target">
                              no target
                            </span>
                          ) : (
                            <>
                              {c.slaCompliancePct}%
                              <span className="ml-1 text-[11px] text-[var(--faint)]">({c.slaEligible})</span>
                            </>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
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
      <span>
        <span className="block text-lg font-semibold leading-tight tabular-nums">{value}</span>
        <span className="block text-xs text-[var(--muted)]">{label}</span>
      </span>
    </div>
  );
}

/**
 * A figure in the table that opens the tickets behind it.
 *
 * Zero is rendered as plain text: a link promising a list and delivering an empty one is a small
 * betrayal, and it happens on most rows of a table like this.
 */
function CountLink({ company, view, label }: {
  company: string; view?: 'open' | 'resolved'; label: string | number;
}) {
  const q = new URLSearchParams({ company });
  if (view) q.set('view', view);

  if (label === 0) return <span className="text-[var(--faint)]">0</span>;

  return (
    <Link href={`/dashboard/tickets?${q}`} className="hover:text-brand hover:underline underline-offset-2">
      {label}
    </Link>
  );
}
