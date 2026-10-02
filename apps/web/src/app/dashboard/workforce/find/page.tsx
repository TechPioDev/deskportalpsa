'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { Check, Search } from 'lucide-react';
import { api, type AvailabilitySearch } from '@/lib/api';
import { hours, LEVELS } from '@/components/Workforce';
import { fmtDay, fmtSlot, GroupAndSkillFilters, WorkforceNav } from '@/components/WorkforceCapacity';

const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm outline-none focus:border-brand';
const DURATIONS = [15, 30, 45, 60, 90, 120, 180, 240, 480];

type Form = { from: string; to: string; duration: string; earliest: string; latest: string; teamId: string; departmentId: string; skillIds: string[]; matchAll: boolean };

/** Problems with the search, worded like the server's; empty when it is fine. */
function problems(v: Form): string[] {
  const out: string[] = [];
  const minutes = Number(v.duration);
  if (!v.from) out.push('Choose a date to search.');
  if (!Number.isInteger(minutes) || minutes < 5 || minutes > 720) out.push('The work must take between 5 minutes and 12 hours.');
  if (v.to && v.from && v.to < v.from) out.push('The last date is before the first.');
  return out;
}

/**
 * Who has one continuous free slot long enough for a piece of work - by date, time of day, team and
 * skills. It lists facts (who is free and when); it does not rank people, and it books nothing.
 */
export default function FindAvailablePage() {
  // Starts on this browser's date. The server judges "today" in the organization's zone and says so
  // if the two disagree around midnight.
  const [v, setV] = useState<Form>(() => ({ from: new Date().toLocaleDateString('en-CA'), to: '', duration: '60', earliest: '', latest: '', teamId: '', departmentId: '', skillIds: [], matchAll: true }));
  const [asked, setAsked] = useState<AvailabilitySearch | null>(null);
  // Counted so that Find asks again even when nothing in the form changed: what was free a minute
  // ago may be taken now.
  const [run, setRun] = useState(0);
  const form = v;
  const issues = problems(form);

  const { data, error, isFetching } = useQuery({
    queryKey: ['capacity', 'find', asked, run], queryFn: () => api.findAvailable(asked!), enabled: !!asked, retry: false, gcTime: 0,
  });

  const search = () => { setRun((n) => n + 1); setAsked({
    from: form.from, to: form.to || null, duration: Number(form.duration), earliest: form.earliest || null, latest: form.latest || null,
    teamId: form.teamId || null, departmentId: form.departmentId || null, skills: form.skillIds, matchAll: form.matchAll,
  }); };
  const left = data ? [
    [data.withoutRequiredSkills, data.matchAllSkills ? 'without every skill asked for' : 'without any of the skills asked for'],
    [data.notOfferedForWork, 'not offered for planned work'],
    [data.withoutASchedule, 'with no working schedule'],
    [data.withNoFittingSlot, 'with no free slot long enough'],
  ].filter(([n]) => (n as number) > 0) : [];

  return (
    <div className="space-y-5">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><Search size={22} className="text-brand" aria-hidden="true" /> Find available technician</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">
          Who has one continuous free slot long enough for a piece of work. Nothing is booked from here, and what is free now can be taken by the time you book it.
        </p>
      </div>
      <WorkforceNav />

      <form aria-label="Search" onSubmit={(e) => { e.preventDefault(); if (issues.length === 0) search(); }}
        className="flex flex-wrap items-end gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3">
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Date
          <input type="date" required value={form.from} onChange={(e) => setV({ ...v, from: e.target.value })} aria-label="Date" className={`block ${field}`} />
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Until (optional)
          <input type="date" value={form.to} min={form.from} onChange={(e) => setV({ ...v, to: e.target.value })} aria-label="Until" className={`block ${field}`} />
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Duration (minutes)
          <input type="number" required min={5} max={720} step={5} list="durations" value={form.duration} onChange={(e) => setV({ ...v, duration: e.target.value })}
            aria-label="Duration in minutes" className={`block w-28 ${field}`} />
          <datalist id="durations">{DURATIONS.map((d) => <option key={d} value={d} />)}</datalist>
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Earliest
          <input type="time" step={300} value={form.earliest} onChange={(e) => setV({ ...v, earliest: e.target.value })} aria-label="Earliest time" className={`block ${field}`} />
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Latest
          <input type="time" step={300} value={form.latest} onChange={(e) => setV({ ...v, latest: e.target.value })} aria-label="Latest time" className={`block ${field}`} />
        </label>
        <button type="submit" disabled={issues.length > 0 || isFetching}
          className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
          <Search size={15} /> {isFetching ? 'Searching…' : 'Find'}
        </button>
        <div className="flex w-full flex-wrap items-center gap-2">
          <GroupAndSkillFilters teamId={v.teamId} departmentId={v.departmentId} skillIds={v.skillIds} matchAll={v.matchAll} onChange={(patch) => setV((x) => ({ ...x, ...patch }))} />
        </div>
        {form.duration !== '' && issues.map((p) => <p key={p} className="w-full text-xs text-red-600 dark:text-red-400">{p}</p>)}
      </form>

      {error && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>}

      {data && (
        <section aria-label="Results" className="space-y-3">
          <p className="text-sm">
            <strong>{data.totalMatches}</strong> of {data.peopleConsidered} {data.peopleConsidered === 1 ? 'person' : 'people'} can take {hours(data.durationMinutes)}.
            {data.totalMatches > data.matches.length && <span> Showing the first {data.matches.length}; choose a team or a skill to narrow it down.</span>}
            {left.length > 0 && <span className="text-[var(--muted)]"> Left out: {left.map(([n, why]) => `${n} ${why}`).join('; ')}.</span>}
            <span className="block text-xs text-[var(--muted)]">Earliest first. Times are in {data.timeZone}.</span>
          </p>
          {data.matches.length === 0 && <p className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-6 text-center text-sm text-[var(--muted)]">Nobody has a free slot that long in that window. Try a shorter piece of work, a wider time window or more days.</p>}
          {data.matches.length > 0 && (
            <ul className="divide-y divide-[var(--border)] rounded-xl border border-[var(--border)] bg-[var(--surface)]">
              {data.matches.map((m) => (
                <li key={m.appUserId} className="flex flex-wrap items-start gap-x-6 gap-y-2 px-4 py-3">
                  <div className="min-w-48 flex-1">
                    <span className="font-medium">{m.displayName}</span>
                    {m.teams.length > 0 && <span className="ml-2 text-xs text-[var(--muted)]">{m.teams.join(', ')}</span>}
                    <div className="flex flex-wrap gap-1 pt-1">
                      {m.matchingSkills.map((s) => (
                        <span key={s.skillId} className="inline-flex items-center gap-1 whitespace-nowrap rounded-full border border-[var(--border)] px-2 py-0.5 text-[11px]">
                          <Check size={11} aria-hidden="true" /> {s.name} · {LEVELS.find((l) => l.value === s.level)?.label}
                        </span>
                      ))}
                    </div>
                  </div>
                  <div className="text-sm tabular-nums">
                    <span className="block text-[11px] uppercase tracking-wide text-[var(--muted)]">First fit</span>
                    <span className="font-medium">{fmtDay(m.date)} {fmtSlot(m.recommended, data.timeZone, m.date)}</span>
                  </div>
                  <div className="text-sm tabular-nums">
                    <span className="block text-[11px] uppercase tracking-wide text-[var(--muted)]">Free windows that day</span>
                    {m.windows.map((w) => <span key={w.start} className="mr-2 inline-block whitespace-nowrap">{fmtSlot(w, data.timeZone, m.date)}</span>)}
                    <span className="block text-xs text-[var(--muted)]">{hours(m.freeMinutes)} free in the window{m.timeZone !== data.timeZone ? ` · works in ${m.timeZone}` : ''}</span>
                  </div>
                  <Link href={`/dashboard/workforce/people/${m.appUserId}?tab=availability&date=${m.date}`}
                    className="ml-auto self-center rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">View availability</Link>
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
    </div>
  );
}
