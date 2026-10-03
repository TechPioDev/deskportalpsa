'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ChevronLeft, ChevronRight, Moon, Pencil, Plus, Trash2 } from 'lucide-react';
import { api, type CapacityException, type CapacityExceptionInput, type DayCapacity, type Slot } from '@/lib/api';
import { hours } from '@/components/Workforce';

// ---- Dates and times -----------------------------------------------------------------------------
// A date here is a plain "YYYY-MM-DD" - a shift date in the person's own zone, worked out by the
// server. It is never turned into a local Date for arithmetic: that would shift it by the browser's
// offset. Instants come from the server as UTC and are always shown in a NAMED zone.

export const addDays = (date: string, n: number) => {
  const d = new Date(`${date}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
};
/** The Monday of the week a date falls in. */
export const weekStart = (date: string) => addDays(date, -((new Date(`${date}T00:00:00Z`).getUTCDay() + 6) % 7));

export const fmtDay = (date: string, opts: Intl.DateTimeFormatOptions = { weekday: 'short', day: 'numeric', month: 'short' }) =>
  new Date(`${date}T00:00:00Z`).toLocaleDateString('en-GB', { ...opts, timeZone: 'UTC' });

/** An instant as HH:mm in a named zone (the technician's, or the search's) - never the browser's by accident. */
export function fmtTime(iso: string, timeZone: string) {
  try {
    return new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false, timeZone }).format(new Date(iso));
  } catch {
    return new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false, timeZone: 'UTC' }).format(new Date(iso));
  }
}
/** The calendar date an instant falls on in a named zone, as YYYY-MM-DD. */
export function dateIn(iso: string, timeZone: string) {
  try { return new Date(iso).toLocaleDateString('en-CA', { timeZone }); } catch { return iso.slice(0, 10); }
}
/** "14:00–17:30", with the day named when the stretch is not on the date being looked at (a night shift after midnight). */
export function fmtSlot(slot: { start: string; end: string }, timeZone: string, onDate?: string) {
  const startDate = dateIn(slot.start, timeZone);
  const prefix = onDate && startDate !== onDate ? `${fmtDay(startDate, { weekday: 'short' })} ` : '';
  return `${prefix}${fmtTime(slot.start, timeZone)}–${fmtTime(slot.end, timeZone)}`;
}

export const EXCEPTION_REASONS: { value: number; label: string }[] = [
  { value: 1, label: 'Meeting' }, { value: 2, label: 'Training' }, { value: 3, label: 'Appointment' },
  { value: 4, label: 'Time off' }, { value: 5, label: 'Sick' }, { value: 6, label: 'Internal event' }, { value: 0, label: 'Other' },
];
const reasonLabel = (r: number) => EXCEPTION_REASONS.find((x) => x.value === r)?.label ?? 'Other';

const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm outline-none focus:border-brand disabled:opacity-60';
const chip = 'whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-medium';

// ---- Sub-navigation ------------------------------------------------------------------------------

/**
 * The workforce pages. Team capacity and the technician search are offered only to someone who can
 * see more than their own schedule - a technician sees People and My capacity.
 */
export function WorkforceNav() {
  const pathname = usePathname();
  const { data: people } = useQuery({ queryKey: ['workforce-people', 'nav'], queryFn: () => api.workforcePeople({}), staleTime: 5 * 60_000 });
  const seesOthers = (people?.length ?? 0) > 1;
  const items = [
    { href: '/dashboard/workforce', label: 'People' },
    { href: '/dashboard/workforce/my-plan', label: 'My plan' },
    { href: '/dashboard/workforce/my-capacity', label: 'My capacity' },
    ...(seesOthers ? [
      { href: '/dashboard/workforce/schedule', label: 'Team schedule' },
      { href: '/dashboard/workforce/capacity', label: 'Team capacity' },
      { href: '/dashboard/workforce/find', label: 'Find available technician' },
    ] : []),
  ];
  return (
    <nav aria-label="Workforce" className="flex flex-wrap gap-1 border-b border-[var(--border)]">
      {items.map((i) => {
        const on = pathname === i.href;
        return (
          <Link key={i.href} href={i.href} aria-current={on ? 'page' : undefined}
            className={`border-b-2 px-3 py-2 text-sm font-medium ${on ? 'border-brand text-[var(--fg)]' : 'border-transparent text-[var(--muted)] hover:text-[var(--fg)]'}`}>
            {i.label}
          </Link>
        );
      })}
    </nav>
  );
}

// ---- One day's figures ---------------------------------------------------------------------------

/** What kind of day it is, in a word, for lists and the week strip. */
export function dayState(d: DayCapacity): { label: string; tone: 'off' | 'away' | 'full' | 'free' } {
  if (d.unavailableAllDay) return { label: 'Away', tone: 'away' };
  if (d.usableMinutes === 0) return { label: 'Not working', tone: 'off' };
  if (d.remainingConfirmedMinutes === 0) return { label: 'Fully planned', tone: 'full' };
  return { label: `${hours(d.remainingConfirmedMinutes)} free`, tone: 'free' };
}

function Figure({ label, value, hint, strong }: { label: string; value: string; hint?: string; strong?: boolean }) {
  return (
    <div className="flex items-baseline justify-between gap-4 py-1.5">
      <dt className="text-sm text-[var(--muted)]">{label}{hint && <span className="block text-[11px]">{hint}</span>}</dt>
      <dd className={`whitespace-nowrap text-sm tabular-nums ${strong ? 'font-semibold' : ''}`}>{value}</dd>
    </div>
  );
}

function SlotList({ slots, timeZone, date, empty }: { slots: Slot[]; timeZone: string; date: string; empty: string }) {
  if (slots.length === 0) return <p className="text-sm text-[var(--muted)]">{empty}</p>;
  return (
    <ul className="divide-y divide-[var(--border)]">
      {slots.map((s) => (
        <li key={s.start} className="flex items-baseline justify-between gap-4 py-1.5 text-sm">
          <span className="tabular-nums">{fmtSlot(s, timeZone, date)}</span>
          <span className="whitespace-nowrap tabular-nums text-[var(--muted)]">{hours(s.minutes)}</span>
        </li>
      ))}
    </ul>
  );
}

/** One day: the working window, the capacity sums and the exact free windows. */
export function DayCapacityCard({ day }: { day: DayCapacity }) {
  const tz = day.timeZone;
  const overnight = !!day.windowStart && !!day.windowEnd && dateIn(day.windowEnd, tz) !== dateIn(day.windowStart, tz);
  return (
    <div className="grid gap-4 md:grid-cols-2">
      <section aria-label="Capacity" className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3">
        <h3 className="pb-1 text-[11px] font-medium uppercase tracking-wide text-[var(--muted)]">{fmtDay(day.date, { weekday: 'long', day: 'numeric', month: 'long' })}</h3>
        <dl className="divide-y divide-[var(--border)]">
          <div className="flex items-baseline justify-between gap-4 py-1.5">
            <dt className="text-sm text-[var(--muted)]">Working window</dt>
            <dd className="text-right text-sm tabular-nums">
              {day.unavailableAllDay ? 'Away all day'
                : day.windowStart && day.windowEnd ? <>{fmtTime(day.windowStart, tz)}–{fmtTime(day.windowEnd, tz)}
                  {overnight && <span className={`ml-2 inline-flex items-center gap-1 bg-[var(--bg)] text-[var(--muted)] ${chip}`}><Moon size={11} aria-hidden="true" /> Ends next day</span>}</>
                  : 'Not a working day'}
            </dd>
          </div>
          {day.breakMinutes > 0 && <Figure label="Breaks" value={`− ${hours(day.breakMinutes)}`} />}
          {day.unavailableMinutes > 0 && <Figure label="Unavailable" value={`− ${hours(day.unavailableMinutes)}`} />}
          {day.additionalMinutes > 0 && <Figure label="Extra availability" value={`+ ${hours(day.additionalMinutes)}`} />}
          <Figure label="Usable capacity" value={hours(day.usableMinutes)} strong />
          <Figure label="Confirmed work" value={hours(day.confirmedMinutes)} />
          <Figure label="Tentative work" value={hours(day.tentativeMinutes)} hint="Pencilled in. Holds no capacity until confirmed." />
          <Figure label="Free capacity" value={hours(day.remainingConfirmedMinutes)} strong />
          <Figure label="Projected free" value={hours(day.projectedRemainingMinutes)} hint="If the tentative work is confirmed." />
        </dl>
        {day.holiday && (
          <p className="pt-2 text-xs text-[var(--muted)]">{day.holiday} is a desk holiday. A holiday does not change anyone&rsquo;s capacity by itself: record time away for whoever is off.</p>
        )}
      </section>
      <section aria-label="Available windows" className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3">
        <h3 className="pb-1 text-[11px] font-medium uppercase tracking-wide text-[var(--muted)]">Available windows</h3>
        <SlotList slots={day.freeSlots} timeZone={tz} date={day.date}
          empty={day.unavailableAllDay ? 'Away all day.' : day.usableMinutes === 0 ? 'No working time on this day.' : 'Nothing free: the day is fully planned.'} />
        {day.tentativeMinutes > 0 && (
          <>
            <h3 className="pb-1 pt-3 text-[11px] font-medium uppercase tracking-wide text-[var(--muted)]">If the tentative work is confirmed</h3>
            <SlotList slots={day.projectedFreeSlots} timeZone={tz} date={day.date} empty="Nothing would be free." />
          </>
        )}
        <p className="pt-2 text-xs text-[var(--muted)]">Each window is one continuous stretch. Times are in {tz}.</p>
      </section>
    </div>
  );
}

// ---- Capacity exceptions -------------------------------------------------------------------------

type ExceptionEdit = { kind: number; allDay: boolean; fromDate: string; toDate: string; startTime: string; endTime: string; reason: number; note: string };

const blankException = (date: string): ExceptionEdit => ({ kind: 1, allDay: false, fromDate: date, toDate: date, startTime: '10:00', endTime: '11:00', reason: 3, note: '' });
const fromException = (e: CapacityException): ExceptionEdit => ({
  kind: e.kind, allDay: e.allDay, fromDate: e.fromDate, toDate: e.allDay ? e.toDate : e.fromDate,
  startTime: e.startTime ?? '10:00', endTime: e.endTime ?? '11:00', reason: e.reason ?? 0, note: e.note ?? '',
});

/** Problems with the form, worded like the server's; empty when it is fine. */
export function exceptionProblems(v: ExceptionEdit): string[] {
  const out: string[] = [];
  if (!v.fromDate) out.push('Choose a date.');
  if (v.allDay) {
    if (v.kind === 2) out.push('Extra availability needs a start and an end time.');
    if (v.toDate && v.fromDate && v.toDate < v.fromDate) out.push('The last day is before the first.');
  } else {
    if (!v.startTime || !v.endTime) out.push('Enter a start and an end time.');
    else if (v.startTime === v.endTime) out.push('The start and end times are the same. For a whole day, choose "All day".');
  }
  if (v.note.length > 200) out.push('Keep the note to 200 characters.');
  return out;
}

export function describeException(e: CapacityException) {
  const when = e.allDay
    ? (e.toDate === e.fromDate ? `${fmtDay(e.fromDate)}, all day` : `${fmtDay(e.fromDate)} to ${fmtDay(e.toDate)}, all day`)
    : `${fmtDay(e.fromDate)} ${e.startTime}–${e.endTime}${e.toDate !== e.fromDate ? ' (next day)' : ''}`;
  return when;
}

function ExceptionForm({ userId, timeZone, defaultDate, editing, onDone }: {
  userId: string; timeZone: string; defaultDate: string; editing: CapacityException | null; onDone: () => void;
}) {
  const qc = useQueryClient();
  const [v, setV] = useState<ExceptionEdit>(editing ? fromException(editing) : blankException(defaultDate));
  const problems = exceptionProblems(v);
  const save = useMutation({
    mutationFn: () => {
      const body: CapacityExceptionInput = {
        kind: v.kind, allDay: v.allDay, fromDate: v.fromDate, toDate: v.allDay ? (v.toDate || v.fromDate) : null,
        startTime: v.allDay ? null : v.startTime, endTime: v.allDay ? null : v.endTime, reason: v.reason, note: v.note.trim() || null,
      };
      return editing ? api.updateCapacityException(userId, editing.id, body) : api.addCapacityException(userId, body);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['capacity'] });
      qc.invalidateQueries({ queryKey: ['capacity-exceptions', userId] });
      onDone();
    },
  });
  const nextDay = !v.allDay && !!v.startTime && !!v.endTime && v.endTime < v.startTime;

  return (
    <form aria-label={editing ? 'Change time away' : 'Add time away'} onSubmit={(e) => { e.preventDefault(); if (problems.length === 0) save.mutate(); }}
      className="grid gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 sm:grid-cols-2">
      <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
        What
        <select value={v.kind} aria-label="Kind" className={`w-full ${field}`}
          onChange={(e) => { const kind = Number(e.target.value); setV({ ...v, kind, allDay: kind === 2 ? false : v.allDay }); }}>
          <option value={1}>Unavailable for planned work</option>
          <option value={2}>Extra availability (outside usual hours)</option>
        </select>
      </label>
      <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
        Reason
        <select value={v.reason} onChange={(e) => setV({ ...v, reason: Number(e.target.value) })} aria-label="Reason" className={`w-full ${field}`}>
          {EXCEPTION_REASONS.map((r) => <option key={r.value} value={r.value}>{r.label}</option>)}
        </select>
      </label>

      {v.kind === 1 && (
        <label className="flex items-center gap-2 text-sm sm:col-span-2">
          <input type="checkbox" checked={v.allDay} onChange={(e) => setV({ ...v, allDay: e.target.checked })} aria-label="All day" />
          All day <span className="text-xs text-[var(--muted)]">(takes off the whole working window that starts on each day, including a night shift&rsquo;s hours after midnight)</span>
        </label>
      )}

      <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
        {v.allDay ? 'First day' : 'Date'}
        <input type="date" required value={v.fromDate} aria-label={v.allDay ? 'First day' : 'Date'} className={`w-full ${field}`}
          onChange={(e) => setV({ ...v, fromDate: e.target.value, toDate: v.toDate < e.target.value ? e.target.value : v.toDate })} />
      </label>
      {v.allDay ? (
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Last day
          <input type="date" required value={v.toDate} min={v.fromDate} onChange={(e) => setV({ ...v, toDate: e.target.value })} aria-label="Last day" className={`w-full ${field}`} />
        </label>
      ) : (
        <div className="space-y-1 text-xs font-medium text-[var(--muted)]">
          From → to
          <div className="flex flex-wrap items-center gap-2">
            <input type="time" step={300} required value={v.startTime} onChange={(e) => setV({ ...v, startTime: e.target.value })} aria-label="Start time" className={field} />
            <span aria-hidden="true">→</span>
            <input type="time" step={300} required value={v.endTime} onChange={(e) => setV({ ...v, endTime: e.target.value })} aria-label="End time" className={field} />
            {nextDay && <span className={`inline-flex items-center gap-1 bg-[var(--bg)] text-[var(--muted)] ${chip}`}><Moon size={11} aria-hidden="true" /> Ends next day</span>}
          </div>
        </div>
      )}

      <label className="block space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
        Note for planners (optional)
        <input value={v.note} maxLength={200} onChange={(e) => setV({ ...v, note: e.target.value })} aria-label="Note" placeholder="Dentist, back by 16:00" className={`w-full ${field}`} />
      </label>

      <p className="text-xs text-[var(--muted)] sm:col-span-2">
        Times are in {timeZone}. This changes capacity for planning work; it is not a leave or attendance record.
      </p>
      {problems.map((p) => <p key={p} className="text-xs text-red-600 dark:text-red-400 sm:col-span-2">{p}</p>)}
      {save.isError && <p role="alert" className="text-sm text-red-600 dark:text-red-400 sm:col-span-2">{(save.error as Error).message}</p>}
      <div className="flex justify-end gap-2 sm:col-span-2">
        <button type="button" onClick={onDone} className="rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
        <button type="submit" disabled={save.isPending || problems.length > 0}
          className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
          {save.isPending ? 'Saving…' : editing ? 'Save changes' : 'Add'}
        </button>
      </div>
    </form>
  );
}

function ExceptionList({ userId, timeZone, today, defaultDate, canManage }: { userId: string; timeZone: string; today: string; defaultDate: string; canManage: boolean }) {
  const qc = useQueryClient();
  // From as far back as one can be recorded (31 days), so nothing that was just saved is missing from the list.
  const { data: rows, error } = useQuery({ queryKey: ['capacity-exceptions', userId], queryFn: () => api.capacityExceptions(userId, addDays(today, -31)), retry: false });
  const [form, setForm] = useState<{ editing: CapacityException | null } | null>(null);
  const remove = useMutation({
    mutationFn: (id: string) => api.removeCapacityException(userId, id),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['capacity'] }); qc.invalidateQueries({ queryKey: ['capacity-exceptions', userId] }); },
  });

  return (
    <section aria-labelledby="exceptions-heading" className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="exceptions-heading" className="text-sm font-semibold">Time away and extra availability</h2>
        {canManage && !form && (
          <button type="button" onClick={() => setForm({ editing: null })}
            className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]"><Plus size={14} /> Add time away</button>
        )}
      </div>
      {form && <ExceptionForm key={form.editing?.id ?? 'new'} userId={userId} timeZone={timeZone} defaultDate={defaultDate} editing={form.editing} onDone={() => setForm(null)} />}
      {error && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>}
      {remove.isError && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(remove.error as Error).message}</p>}
      {rows && rows.length === 0 && <p className="text-sm text-[var(--muted)]">Nothing recorded from the last month onwards.</p>}
      {rows && rows.length > 0 && (
        <ul className="divide-y divide-[var(--border)] rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          {rows.map((e) => (
            <li key={e.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5 text-sm">
              <span className={`${chip} ${e.kind === 1 ? 'bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-200' : 'bg-green-100 text-green-900 dark:bg-green-950 dark:text-green-200'}`}>
                {e.kind === 1 ? 'Unavailable' : 'Extra availability'}
              </span>
              <span className="tabular-nums">{describeException(e)}</span>
              {/* The reason and note reach only the person and whoever manages their availability. */}
              {e.reason !== null && <span className="text-[var(--muted)]">{reasonLabel(e.reason)}</span>}
              {/* Plain text: React escapes it, so a note is shown exactly as typed and never run. */}
              {e.note && <span className="min-w-0 flex-1 truncate text-[var(--muted)]" title={e.note}>{e.note}</span>}
              <span className="ml-auto flex items-center gap-1">
                {e.updatedBy && <span className="text-[11px] text-[var(--muted)]">by {e.updatedBy}</span>}
                {canManage && (
                  <>
                    <button type="button" onClick={() => setForm({ editing: e })} aria-label={`Change ${describeException(e)}`}
                      className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]"><Pencil size={14} /></button>
                    <button type="button" disabled={remove.isPending} aria-label={`Remove ${describeException(e)}`}
                      onClick={() => { if (window.confirm(`Remove this? ${describeException(e)}`)) remove.mutate(e.id); }}
                      className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-red-600"><Trash2 size={14} /></button>
                  </>
                )}
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

// ---- A person's capacity -------------------------------------------------------------------------

/**
 * One person's capacity: a week at a glance, the chosen day's sums and free windows, and their time
 * away. Capacity for planning work - not attendance, and not a measure of how anyone performs.
 */
export function CapacityPanel({ userId, initialDate }: { userId: string; initialDate?: string | null }) {
  // The person's own "today" comes from the server (their zone, not this browser's).
  const { data: now, error: nowError } = useQuery({ queryKey: ['capacity', userId, 'today'], queryFn: () => api.personCapacity(userId), retry: false });
  const [picked, setPicked] = useState<string | null>(initialDate ?? null);
  const date = picked ?? now?.today ?? null;
  const monday = date ? weekStart(date) : null;
  const { data: week, error, isLoading } = useQuery({
    queryKey: ['capacity', userId, monday], queryFn: () => api.personCapacity(userId, monday, addDays(monday!, 6)), enabled: !!monday, retry: false,
  });
  useEffect(() => { setPicked(initialDate ?? null); }, [userId, initialDate]);

  const failed = (nowError ?? error) as Error | null;
  if (failed) return <p role="alert" className="text-sm text-red-600 dark:text-red-400">{failed.message}</p>;
  if (!date || !monday || !now) return <p className="text-sm text-[var(--muted)]">Loading capacity…</p>;
  const day = week?.days.find((d) => d.date === date);
  const tz = day?.timeZone ?? now.timeZone;

  return (
    <div className="space-y-5">
      <p className="max-w-prose text-sm text-[var(--muted)]">
        How much of {now.displayName}&rsquo;s time is offered for planned work, what is already planned, and exactly when they are free.
        This is capacity for planning; it is not attendance.
      </p>
      {!now.hasSchedule && <p className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm">No working schedule yet, so there is no capacity to plan against. Whoever manages the workforce sets one under Workforce → this person → Work schedule.</p>}
      {!now.isSchedulable && <p className="rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-200">Not offered for planned work. Their capacity is shown, but they are left out of team totals and of the technician search.</p>}

      <div className="flex flex-wrap items-center gap-2">
        <button type="button" onClick={() => setPicked(addDays(date, -7))} aria-label="Previous week"
          className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><ChevronLeft size={16} /></button>
        <button type="button" onClick={() => setPicked(now.today)} className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Today</button>
        <button type="button" onClick={() => setPicked(addDays(date, 7))} aria-label="Next week"
          className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><ChevronRight size={16} /></button>
        <input type="date" value={date} onChange={(e) => e.target.value && setPicked(e.target.value)} aria-label="Date" className={field} />
        <span className="text-xs text-[var(--muted)]">Times in {tz}</span>
      </div>

      <div role="group" aria-label="Week" className="grid grid-cols-2 gap-2 sm:grid-cols-4 lg:grid-cols-7">
        {(week?.days ?? Array.from({ length: 7 }, (_, i) => null)).map((d, i) => {
          const dDate = d?.date ?? addDays(monday, i);
          const on = dDate === date;
          const state = d ? dayState(d) : null;
          return (
            <button key={dDate} type="button" aria-pressed={on} onClick={() => setPicked(dDate)}
              className={`rounded-xl border px-3 py-2 text-left ${on ? 'border-brand bg-[var(--surface)] ring-1 ring-brand' : 'border-[var(--border)] bg-[var(--surface)] hover:bg-[var(--bg)]'}`}>
              <span className="block text-xs font-medium">{fmtDay(dDate)}{dDate === now.today && <span className="ml-1 text-[var(--muted)]">· today</span>}</span>
              <span className="block text-sm font-semibold tabular-nums">{d ? (d.usableMinutes > 0 ? hours(d.usableMinutes) : '—') : '…'}</span>
              <span className={`block text-[11px] ${state?.tone === 'away' ? 'text-amber-700 dark:text-amber-300' : state?.tone === 'free' ? 'text-green-700 dark:text-green-300' : 'text-[var(--muted)]'}`}>
                {state?.label ?? ' '}
              </span>
            </button>
          );
        })}
      </div>

      {isLoading && !day && <p className="text-sm text-[var(--muted)]">Loading capacity…</p>}
      {day && <DayCapacityCard day={day} />}
      {day && day.confirmedMinutes === 0 && day.tentativeMinutes === 0 && day.usableMinutes > 0 && (
        <p className="text-xs text-[var(--muted)]">No planned work yet: it is counted here once work can be booked into people&rsquo;s time.</p>
      )}

      <ExceptionList userId={userId} timeZone={tz} today={now.today} defaultDate={date} canManage={now.canManageExceptions} />
    </div>
  );
}

// ---- Shared filters ------------------------------------------------------------------------------

/** Team, department and skills, as the team capacity table and the technician search both filter. */
export function GroupAndSkillFilters({ teamId, departmentId, skillIds, matchAll, onChange }: {
  teamId: string; departmentId: string; skillIds: string[]; matchAll: boolean;
  onChange: (patch: Partial<{ teamId: string; departmentId: string; skillIds: string[]; matchAll: boolean }>) => void;
}) {
  const { data: groups } = useQuery({ queryKey: ['workforce-groups'], queryFn: api.workforceGroups, staleTime: 5 * 60_000 });
  const { data: skills } = useQuery({ queryKey: ['skills', false], queryFn: () => api.skills(false) });
  const active = useMemo(() => skills ?? [], [skills]);
  return (
    <>
      <select value={teamId} onChange={(e) => onChange({ teamId: e.target.value })} aria-label="Team" className={field}>
        <option value="">All teams</option>
        {(groups?.teams ?? []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
      </select>
      <select value={departmentId} onChange={(e) => onChange({ departmentId: e.target.value })} aria-label="Department" className={field}>
        <option value="">All departments</option>
        {(groups?.departments ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
      </select>
      {active.length > 0 && (
        <div className="flex w-full flex-wrap items-center gap-1.5" role="group" aria-label="Skills">
          <span className="text-xs text-[var(--muted)]">Skills:</span>
          {active.map((s) => {
            const on = skillIds.includes(s.id);
            return (
              <button key={s.id} type="button" aria-pressed={on}
                onClick={() => onChange({ skillIds: on ? skillIds.filter((i) => i !== s.id) : [...skillIds, s.id] })}
                className={`rounded-full border px-2.5 py-0.5 text-xs font-medium ${on ? 'border-brand bg-brand text-brand-fg' : 'border-[var(--border)] hover:bg-[var(--bg)]'}`}>
                {s.name}
              </button>
            );
          })}
          {skillIds.length > 1 && (
            <select value={matchAll ? 'all' : 'any'} onChange={(e) => onChange({ matchAll: e.target.value === 'all' })} aria-label="Skill match"
              className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-0.5 text-xs">
              <option value="all">holds all of them</option>
              <option value="any">holds any of them</option>
            </select>
          )}
        </div>
      )}
    </>
  );
}
