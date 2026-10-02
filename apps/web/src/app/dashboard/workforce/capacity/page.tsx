'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { ChevronLeft, ChevronRight, Users } from 'lucide-react';
import { api } from '@/lib/api';
import { hours } from '@/components/Workforce';
import { addDays, dayState, fmtDay, fmtSlot, fmtTime, GroupAndSkillFilters, WorkforceNav } from '@/components/WorkforceCapacity';

const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm outline-none focus:border-brand';
const chip = 'whitespace-nowrap rounded-full px-1.5 text-[10px] font-medium';

/**
 * Capacity for everyone the viewer may see, for one date: what each person offers, what is planned,
 * and what is free. For planning work - not attendance, and not a ranking of anyone.
 */
export default function TeamCapacityPage() {
  const [date, setDate] = useState<string | null>(null);
  const [f, setF] = useState({ teamId: '', departmentId: '', skillIds: [] as string[], matchAll: true });
  const { data, isLoading, error } = useQuery({
    queryKey: ['capacity', 'team', date, f],
    queryFn: () => api.teamCapacity({ date, teamId: f.teamId, departmentId: f.departmentId, skills: f.skillIds, matchAll: f.matchAll }),
    retry: false, placeholderData: (previous) => previous,
  });
  // The server decides what "today" is (the organization's zone); until it answers there is no date to step from.
  const shown = date ?? data?.date ?? null;

  return (
    <div className="space-y-5">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><Users size={22} className="text-brand" aria-hidden="true" /> Team capacity</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">
          What each person offers for planned work on a date, what is already planned, and what is free. Capacity for planning; not attendance.
        </p>
      </div>
      <WorkforceNav />

      <section aria-label="Filters" className="flex flex-wrap items-center gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3">
        <button type="button" disabled={!shown} onClick={() => shown && setDate(addDays(shown, -1))} aria-label="Previous day"
          className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)] disabled:opacity-50"><ChevronLeft size={16} /></button>
        <input type="date" value={shown ?? ''} onChange={(e) => e.target.value && setDate(e.target.value)} aria-label="Date" className={field} />
        <button type="button" disabled={!shown} onClick={() => shown && setDate(addDays(shown, 1))} aria-label="Next day"
          className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)] disabled:opacity-50"><ChevronRight size={16} /></button>
        <button type="button" onClick={() => setDate(null)} className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Today</button>
        <GroupAndSkillFilters {...f} onChange={(patch) => setF((v) => ({ ...v, ...patch }))} />
      </section>

      {error && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>}
      <div className="overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <table className="w-full min-w-[820px] text-sm">
          <caption className="sr-only">Team capacity{shown ? ` for ${fmtDay(shown, { weekday: 'long', day: 'numeric', month: 'long' })}` : ''}</caption>
          <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
            <tr>
              <th scope="col" className="px-4 py-2.5 font-medium">Person</th>
              <th scope="col" className="px-3 py-2.5 font-medium">Working window</th>
              <th scope="col" className="px-3 py-2.5 text-right font-medium">Usable</th>
              <th scope="col" className="px-3 py-2.5 text-right font-medium">Planned</th>
              <th scope="col" className="px-3 py-2.5 text-right font-medium">Free</th>
              <th scope="col" className="px-3 py-2.5 font-medium">Free windows</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-[var(--border)]">
            {isLoading && <tr><td colSpan={6} className="px-4 py-6 text-center text-[var(--muted)]">Loading…</td></tr>}
            {data && data.people.length === 0 && <tr><td colSpan={6} className="px-4 py-6 text-center text-[var(--muted)]">Nobody matches these filters.</td></tr>}
            {data?.people.map((p) => {
              const d = p.day;
              const state = dayState(d);
              return (
                <tr key={p.appUserId} className="align-top">
                  <th scope="row" className="px-4 py-2.5 text-left font-normal">
                    <Link href={`/dashboard/workforce/people/${p.appUserId}?tab=availability&date=${d.date}`} className="font-medium hover:underline">{p.displayName}</Link>
                    <div className="flex flex-wrap gap-1 pt-0.5">
                      {p.teams.length > 0 && <span className="text-[11px] text-[var(--muted)]">{p.teams.join(', ')}</span>}
                      {!p.isSchedulable && <span className={`bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-200 ${chip}`}>Not offered for work</span>}
                      {!p.hasSchedule && <span className={`bg-[var(--bg)] text-[var(--muted)] ${chip}`}>No schedule</span>}
                    </div>
                  </th>
                  <td className="px-3 py-2.5 tabular-nums">
                    {d.unavailableAllDay ? <span className="text-amber-700 dark:text-amber-300">Away all day</span>
                      : d.windowStart && d.windowEnd ? <>{fmtTime(d.windowStart, d.timeZone)}–{fmtTime(d.windowEnd, d.timeZone)}<span className="block text-[11px] text-[var(--muted)]">{d.timeZone}</span></>
                        : <span className="text-[var(--muted)]">{state.label}</span>}
                  </td>
                  <td className="whitespace-nowrap px-3 py-2.5 text-right tabular-nums">{d.usableMinutes > 0 ? hours(d.usableMinutes) : '—'}</td>
                  <td className="whitespace-nowrap px-3 py-2.5 text-right tabular-nums">
                    {d.usableMinutes > 0 ? hours(d.confirmedMinutes) : '—'}
                    {d.tentativeMinutes > 0 && <span className="block text-[11px] text-[var(--muted)]">+ {hours(d.tentativeMinutes)} tentative</span>}
                  </td>
                  <td className="whitespace-nowrap px-3 py-2.5 text-right font-medium tabular-nums">{d.usableMinutes > 0 ? hours(d.remainingConfirmedMinutes) : '—'}</td>
                  <td className="px-3 py-2.5 tabular-nums">
                    {d.freeSlots.length === 0 ? <span className="text-[var(--muted)]">—</span>
                      : d.freeSlots.map((s) => <span key={s.start} className="mr-2 inline-block whitespace-nowrap">{fmtSlot(s, d.timeZone, d.date)}</span>)}
                  </td>
                </tr>
              );
            })}
          </tbody>
          {data && data.people.length > 0 && (
            <tfoot className="border-t border-[var(--border)] text-sm font-medium">
              <tr>
                <th scope="row" colSpan={2} className="px-4 py-2.5 text-left font-medium">
                  Total on offer <span className="font-normal text-[var(--muted)]">({data.people.filter((p) => p.isSchedulable).length} people offered for planned work)</span>
                </th>
                <td className="whitespace-nowrap px-3 py-2.5 text-right tabular-nums">{hours(data.usableMinutes)}</td>
                <td className="whitespace-nowrap px-3 py-2.5 text-right tabular-nums">{hours(data.confirmedMinutes)}</td>
                <td className="whitespace-nowrap px-3 py-2.5 text-right tabular-nums">{hours(data.remainingConfirmedMinutes)}</td>
                <td />
              </tr>
            </tfoot>
          )}
        </table>
      </div>
      <p className="text-xs text-[var(--muted)]">Each person&rsquo;s times are in their own time zone. Planned work is counted once work can be booked into people&rsquo;s time.</p>
    </div>
  );
}
