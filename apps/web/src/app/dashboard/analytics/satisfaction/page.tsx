'use client';

import { useMemo, useState } from 'react';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { Smile, MessageSquareQuote } from 'lucide-react';
import { api, type SatisfactionSummary } from '@/lib/api';
import { PERIODS, periodRange, describeRange, type PeriodKey } from '@/lib/periods';

const LABELS = ['Very poor', 'Poor', 'Okay', 'Good', 'Excellent'];

function pct(v: number | null) {
  return v === null ? '—' : `${v.toLocaleString(undefined, { maximumFractionDigits: 1 })}%`;
}

/** A colour for a CSAT figure: good at 85% and over, a concern under 70%. Stated, not guessed. */
function tone(v: number | null) {
  if (v === null) return 'text-[var(--muted)]';
  if (v >= 85) return 'text-emerald-700 dark:text-emerald-400';
  if (v < 70) return 'text-red-600 dark:text-red-400';
  return 'text-amber-700 dark:text-amber-400';
}

/**
 * How clients rated the work: CSAT is the share of ratings that were 4 or 5 out of 5. Per
 * technician by whoever held the ticket when it was rated, per client, and the comments — every poor
 * rating, whether or not it came with words, because those are the ones somebody should follow up.
 */
export default function SatisfactionPage() {
  const [period, setPeriod] = useState<PeriodKey>('30d');
  const range = useMemo(() => periodRange(period), [period]);
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['satisfaction-summary', period],
    queryFn: () => api.satisfactionSummary(range.from, range.to),
    retry: false,
  });

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold">Customer satisfaction</h1>
          <p className="text-sm text-[var(--muted)]">
            How clients rated resolved tickets · <span className="tabular-nums">{describeRange(range.from, range.to)}</span>
          </p>
        </div>
        <select value={period} onChange={(e) => setPeriod(e.target.value as PeriodKey)} aria-label="Date range"
          className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm outline-none focus:border-brand">
          {PERIODS.map((p) => <option key={p.key} value={p.key}>{p.label}</option>)}
        </select>
      </div>

      {isLoading && <div className="h-40 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}
      {isError && (
        <p className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-5 py-4 text-sm text-[var(--muted)]">
          {/403|forbidden/i.test((error as Error)?.message ?? '') ? 'Satisfaction figures are for managers.' : `Couldn’t load the ratings: ${(error as Error).message}`}
        </p>
      )}

      {data && data.ratings === 0 && (
        <div className="flex flex-col items-center rounded-xl border border-dashed border-[var(--border)] px-6 py-12 text-center">
          <Smile className="mb-3 text-[var(--faint)]" size={26} />
          <p className="max-w-md text-sm text-[var(--muted)]">
            No ratings in this period. Clients are asked when a ticket they raised is resolved, on the
            ticket in their portal — so ratings arrive as clients use it.
          </p>
        </div>
      )}

      {data && data.ratings > 0 && <Summary data={data} />}
    </div>
  );
}

function Summary({ data }: { data: SatisfactionSummary }) {
  const max = Math.max(...data.distribution, 1);
  return (
    <>
      <section className="grid gap-4 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5 md:grid-cols-[auto_1fr]">
        <div className="pr-6 md:border-r md:border-[var(--border)]">
          <p className="text-xs uppercase tracking-wide text-[var(--faint)]">CSAT</p>
          <p className={`text-4xl font-semibold tabular-nums ${tone(data.csatPct)}`}>{pct(data.csatPct)}</p>
          <p className="mt-1 text-xs text-[var(--muted)]">
            {data.satisfied} of {data.ratings} rated 4 or 5 · average {data.average?.toFixed(2)} / 5
          </p>
        </div>
        {/* The spread behind the headline: 50% can be all 3s or half 5s and half 1s, which are
            different problems. */}
        <div className="space-y-1.5" aria-label="Ratings by score">
          {[5, 4, 3, 2, 1].map((n) => {
            const count = data.distribution[n - 1];
            return (
              <div key={n} className="flex items-center gap-3 text-xs">
                <span className="w-24 shrink-0 text-[var(--muted)]">{n} · {LABELS[n - 1]}</span>
                <span className="h-2.5 flex-1 overflow-hidden rounded-full bg-[var(--bg)]">
                  <span className={`block h-full rounded-full ${n >= 4 ? 'bg-emerald-500' : n === 3 ? 'bg-amber-400' : 'bg-red-500'}`}
                    style={{ width: `${(count / max) * 100}%` }} />
                </span>
                <span className="w-8 shrink-0 text-right tabular-nums">{count}</span>
              </div>
            );
          })}
        </div>
      </section>

      <div className="grid gap-4 lg:grid-cols-2">
        <GroupTable title="By technician" note="Who held the ticket when it was rated." rows={data.byTechnician} />
        <GroupTable title="By client" rows={data.byClient} />
      </div>

      <section className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <h2 className="flex items-center gap-2 border-b border-[var(--border)] px-5 py-3 text-sm font-semibold">
          <MessageSquareQuote size={14} /> Comments and poor ratings
        </h2>
        {data.recent.length === 0 && <p className="px-5 py-4 text-sm text-[var(--muted)]">No comments and no poor ratings in this period.</p>}
        <ul className="divide-y divide-[var(--border)]">
          {data.recent.map((r) => (
            <li key={r.ticketId} className="flex gap-3 px-5 py-3">
              <span className={`h-fit shrink-0 rounded-full px-2 py-0.5 text-xs font-semibold ${r.rating >= 4
                ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300'
                : r.rating <= 2 ? 'bg-red-50 text-red-700 dark:bg-red-950 dark:text-red-300'
                  : 'bg-amber-50 text-amber-700 dark:bg-amber-950 dark:text-amber-300'}`}>{r.rating}/5</span>
              <div className="min-w-0 text-sm">
                <Link href={`/dashboard/tickets/${r.ticketId}`} className="font-medium hover:underline">
                  {r.reference} · {r.title}
                </Link>
                <p className="text-xs text-[var(--muted)]">
                  {[r.clientName, r.technicianName, new Date(r.ratedAt).toLocaleDateString()].filter(Boolean).join(' · ')}
                </p>
                {r.comment ? <p className="mt-1">&ldquo;{r.comment}&rdquo;</p> : <p className="mt-1 text-xs italic text-[var(--faint)]">No comment left.</p>}
              </div>
            </li>
          ))}
        </ul>
      </section>
      <p className="text-xs text-[var(--muted)]">
        CSAT counts ratings of 4 or 5 as satisfied. Colours: 85% and over is good, under 70% needs a look.
        Ratings of 1 or 2 from the last week also appear on Integration Health&apos;s needs-attention list.
      </p>
    </>
  );
}

function GroupTable({ title, note, rows }: { title: string; note?: string; rows: SatisfactionSummary['byClient'] }) {
  return (
    <section className="overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      <div className="border-b border-[var(--border)] px-5 py-3">
        <h2 className="text-sm font-semibold">{title}</h2>
        {note && <p className="text-xs text-[var(--muted)]">{note}</p>}
      </div>
      <table className="w-full text-sm">
        <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
          <tr className="border-b border-[var(--border)]">
            <th className="px-5 py-2 font-medium">Name</th>
            <th className="px-2 py-2 text-right font-medium">Ratings</th>
            <th className="px-2 py-2 text-right font-medium">Average</th>
            <th className="px-5 py-2 text-right font-medium">CSAT</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((g) => (
            <tr key={g.key || g.name} className="border-b border-[var(--border)] last:border-0">
              <td className="px-5 py-2.5">{g.name}</td>
              <td className="px-2 py-2.5 text-right tabular-nums text-[var(--muted)]">{g.ratings}</td>
              <td className="px-2 py-2.5 text-right tabular-nums text-[var(--muted)]">{g.average.toFixed(2)}</td>
              <td className={`px-5 py-2.5 text-right font-medium tabular-nums ${tone(g.csatPct)}`}>{pct(g.csatPct)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
