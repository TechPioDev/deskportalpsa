'use client';

import Link from 'next/link';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { AlarmClock, CalendarClock, CheckCircle2, Clock, Flame, Hourglass, Inbox } from 'lucide-react';
import { api } from '@/lib/api';
import { StatusBadge, PriorityBadge, SourceBadge } from '@/components/badges';
import { fmtHours } from '@/lib/format';

/**
 * Everything that is mine to do, wherever it came from: Autotask, ConnectWise, the team's boards,
 * a monitoring alert. The counts are the ones a technician starts the day with; each opens the list
 * behind it. "Mine" is what I hold, or what sits with a team I am in - the same rule as the list.
 */
export default function MyWorkPage() {
  const { data: summary } = useQuery({ queryKey: ['tickets', 'summary', true], queryFn: () => api.ticketSummary(true) });
  const { data: work, isLoading } = useQuery({
    queryKey: ['tickets', 'page', 'my-work'],
    queryFn: () => api.ticketPage({ mine: true, openness: 'open', take: 100 }),
  });
  const rows = work?.items ?? [];
  const qc = useQueryClient();
  useEffect(() => { if (rows.length) qc.setQueryData(['ticket-nav'], rows.map((t) => t.id)); }, [rows, qc]);

  const tiles = [
    { label: 'Open', value: summary?.open, icon: Inbox, href: '/dashboard/tickets?mine=1&view=open', tone: 'text-brand' },
    { label: 'Due today', value: summary?.dueToday, icon: CalendarClock, href: null, tone: 'text-amber-600 dark:text-amber-400' },
    { label: 'Overdue', value: summary?.overdue, icon: AlarmClock, href: '/dashboard/tickets?mine=1&overdue=1', tone: 'text-red-600 dark:text-red-400' },
    { label: 'High priority', value: summary?.highPriority, icon: Flame, href: '/dashboard/tickets?mine=1&view=open&priority=HIGH', tone: 'text-orange-600 dark:text-orange-400' },
    { label: 'Waiting on someone', value: summary?.waiting, icon: Hourglass, href: null, tone: 'text-sky-600 dark:text-sky-400' },
    { label: 'Resolved, 7 days', value: summary?.resolvedLast7Days, icon: CheckCircle2, href: '/dashboard/tickets?mine=1&view=resolved', tone: 'text-emerald-600 dark:text-emerald-400' },
  ];
  const now = Date.now();

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">My work</h1>
          <p className="text-sm text-[var(--muted)]">What you hold, or what sits with your team, from every source.</p>
        </div>
        {summary?.hoursLoggedThisWeek != null && (
          <p className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm">
            <Clock size={14} className="text-[var(--muted)]" aria-hidden="true" />
            <span className="tabular-nums font-medium">{fmtHours(summary.hoursLoggedThisWeek)}</span>
            <span className="text-[var(--muted)]">logged this week</span>
          </p>
        )}
      </div>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
        {/* A tile links only where the list can show exactly the tickets it counted: a number that
            opens a list of a different length reads as a bug. */}
        {tiles.map((t) => {
          const body = (
            <>
              <t.icon size={16} className={t.tone} aria-hidden="true" />
              <p className="mt-2 text-2xl font-semibold tabular-nums">{t.value ?? '–'}</p>
              <p className="text-xs text-[var(--muted)]">{t.label}</p>
            </>
          );
          return t.href ? (
            <Link key={t.label} href={t.href}
              className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 transition-colors hover:border-brand/40 focus-visible:outline focus-visible:outline-2 focus-visible:outline-brand">
              {body}
            </Link>
          ) : (
            <div key={t.label} className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">{body}</div>
          );
        })}
      </div>

      {(summary?.openBySource.length ?? 0) > 0 && (
        <p className="flex flex-wrap items-center gap-2 text-xs text-[var(--muted)]">
          <span>Open, by where it came from:</span>
          {summary!.openBySource.map((s) => (
            <span key={s.label} className="rounded-full bg-[var(--bg)] px-2.5 py-1 text-[var(--fg)]">
              {s.label} <span className="tabular-nums font-semibold">{s.count}</span>
            </span>
          ))}
        </p>
      )}

      <section aria-labelledby="my-open-heading" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <div className="flex flex-wrap items-center justify-between gap-2 border-b border-[var(--border)] px-5 py-3">
          <h2 id="my-open-heading" className="text-sm font-semibold">
            My open work {work ? <span className="font-normal text-[var(--muted)]">({work.total})</span> : null}
          </h2>
          {/* A long queue shows its newest here; the full list pages through the rest. */}
          {work && work.total > rows.length && (
            <Link href="/dashboard/tickets?mine=1&view=open" className="text-xs font-medium text-brand hover:underline">
              Showing the newest {rows.length} · See all {work.total}
            </Link>
          )}
        </div>
        {isLoading && <div className="m-4 h-24 animate-pulse rounded-lg bg-[var(--bg)]" />}
        {work && rows.length === 0 && (
          <p className="px-5 py-8 text-center text-sm text-[var(--muted)]">Nothing open is yours right now.</p>
        )}
        {rows.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
                <tr className="border-b border-[var(--border)]">
                  <th className="px-5 py-2.5 font-medium">Ticket</th>
                  <th className="px-2 py-2.5 font-medium">Source</th>
                  <th className="px-2 py-2.5 font-medium">Client</th>
                  <th className="px-2 py-2.5 font-medium">Status</th>
                  <th className="px-2 py-2.5 font-medium">Priority</th>
                  <th className="px-5 py-2.5 font-medium">Due</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((t) => {
                  const due = t.dueAt ? new Date(t.dueAt).getTime() : null;
                  const late = due !== null && due < now && !t.slaPausedAt;
                  return (
                    <tr key={t.id} className="border-b border-[var(--border)] last:border-0 hover:bg-[var(--bg)]">
                      <td className="px-5 py-3">
                        <Link href={`/dashboard/tickets/${t.id}`} className="font-medium hover:underline">{t.title}</Link>
                        <span className="ml-2 font-mono text-xs text-[var(--muted)]">{t.number ?? (t.externalTicketId ? `#${t.externalTicketId}` : '')}</span>
                      </td>
                      <td className="px-2 py-3"><SourceBadge provider={t.provider} connectionName={t.connectionName} origin={t.origin} /></td>
                      <td className="px-2 py-3 text-[var(--muted)]">{t.customerName ?? '—'}</td>
                      <td className="px-2 py-3"><StatusBadge status={t.portalStatus} /></td>
                      <td className="px-2 py-3"><PriorityBadge priority={t.portalPriority} /></td>
                      <td className={`px-5 py-3 text-xs ${late ? 'font-semibold text-red-600 dark:text-red-400' : 'text-[var(--muted)]'}`}>
                        {due === null ? '—' : `${late ? 'Overdue · ' : ''}${new Date(due).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}`}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}
