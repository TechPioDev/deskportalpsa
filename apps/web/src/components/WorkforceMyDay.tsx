'use client';

import { useMemo, useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, ChevronLeft, ChevronRight, Clock, Plus, RefreshCw, X } from 'lucide-react';
import { api, type MyDay, type MyDayItem, type TeamToday, type UnscheduledWork } from '@/lib/api';
import { hours } from '@/components/Workforce';
import { addDays, fmtDay, fmtSlot, GroupAndSkillFilters } from '@/components/WorkforceCapacity';
import { useDialog } from '@/components/WorkforcePlan';
import { elapsedSeconds, fmtClock, fmtDuration, PAUSE_REASONS, useTicking, useWorkTime, WorkControls } from '@/components/WorkTime';
import { clock, dateOf } from '@/lib/timeline';

const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm outline-none focus:border-brand disabled:opacity-60';
const btn = 'inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-50';
const chip = 'inline-flex items-center gap-1 whitespace-nowrap rounded-full px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide';

/** Item state: 1 planned, 2 active, 3 paused, 4 in progress, 5 completed. */
const STATE: Record<number, { label: string; cls: string }> = {
  1: { label: 'Planned', cls: 'bg-[var(--bg)] text-[var(--muted)]' },
  2: { label: 'Working', cls: 'bg-brand/15 text-brand' },
  3: { label: 'Paused', cls: 'bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-200' },
  4: { label: 'In progress', cls: 'bg-sky-100 text-sky-900 dark:bg-sky-950 dark:text-sky-200' },
  5: { label: 'Completed', cls: 'bg-green-100 text-green-900 dark:bg-green-950 dark:text-green-200' },
};

/** Actual against planned, as a fact: "+35m" over, "−8m" under, "not planned" when nothing was. */
function Variance({ item }: { item: MyDayItem }) {
  if (item.varianceMinutes == null) return <span className="text-[var(--muted)]">not planned</span>;
  const v = item.varianceMinutes;
  const cls = v > 0 ? 'text-amber-700 dark:text-amber-300' : v < 0 ? 'text-green-700 dark:text-green-300' : 'text-[var(--muted)]';
  return <span className={`tabular-nums ${cls}`}>{v > 0 ? '+' : v < 0 ? '−' : ''}{hours(Math.abs(v))}{item.variancePercent != null && item.variancePercent !== 0 ? ` (${item.variancePercent > 0 ? '+' : ''}${item.variancePercent}%)` : ''}</span>;
}

/**
 * Live actual time: the server's figure (entries plus the clock at the moment it answered) plus the
 * seconds this screen has watched the clock run since. Display only; the server never streams ticks.
 */
const sinceAnswer = (running: boolean, nowMs: number, fetchedAt: number) => (running ? Math.max(0, Math.floor((nowMs - fetchedAt) / 1000)) : 0);

// ---- My Day -------------------------------------------------------------------------------------------

export function MyDayView({ viewerId, appUserId, initialDate }: { viewerId: string; appUserId?: string | null; initialDate?: string | null }) {
  const qc = useQueryClient();
  const work = useWorkTime();
  const [picked, setPicked] = useState<string | null>(initialDate ?? null);
  const { data, isLoading, error, refetch, isFetching, dataUpdatedAt } = useQuery({
    queryKey: ['my-day', appUserId ?? viewerId, picked ?? 'today'],
    queryFn: () => api.myDay({ appUserId: appUserId ?? null, date: picked }), retry: false, placeholderData: (prev) => prev, refetchInterval: 60_000,
  });
  const running = !!data?.current;
  const now = useTicking(running);
  const [adding, setAdding] = useState<{ ticketId: string; reference: string; title: string } | null>(null);
  const date = picked ?? data?.date ?? null;
  const self = (appUserId ?? viewerId) === viewerId;

  const sections = useMemo(() => {
    const items = data?.items ?? [];
    const current = items.filter((i) => i.state === 2 || i.state === 3);
    const next = items.filter((i) => i.state === 1 && i.slots.length > 0 && i.slots.some((s) => Date.parse(s.endsAt) > now)).slice(0, 1);
    return { current, next, all: items, done: items.filter((i) => i.state === 5) };
  }, [data, now]);

  if (error) return <p role="alert" className="rounded-lg border border-red-300 bg-red-50 px-3 py-2 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-200">{(error as Error).message}</p>;
  if (!data) return <div aria-busy="true" className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />;
  const tz = data.timeZone;
  const day = data.day;
  const isToday = data.date === data.today;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <button type="button" onClick={() => date && setPicked(addDays(date, -1))} aria-label="Previous day" className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><ChevronLeft size={16} /></button>
        <button type="button" onClick={() => setPicked(null)} className={btn}>Today</button>
        <button type="button" onClick={() => date && setPicked(addDays(date, 1))} aria-label="Next day" className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><ChevronRight size={16} /></button>
        <input type="date" value={date ?? ''} onChange={(e) => e.target.value && setPicked(e.target.value)} aria-label="Day" className={field} />
        <span className="text-sm font-medium">{date ? fmtDay(date, { weekday: 'long', day: 'numeric', month: 'long' }) : ''}</span>
        <span className="text-xs text-[var(--muted)]">Times in {tz}{!self ? ` · ${data.displayName}'s day` : ''}</span>
        <button type="button" onClick={() => refetch()} aria-label="Refresh" className="ml-auto rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><RefreshCw size={14} className={isFetching ? 'animate-spin' : ''} /></button>
      </div>
      {work.error && <p role="alert" className="rounded-lg border border-red-300 bg-red-50 px-3 py-2 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-200">{work.error}</p>}

      {/* ---- summary: facts about the day, never a score ---- */}
      <dl className="grid grid-cols-2 gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3 text-sm sm:grid-cols-6" aria-label="Today summary">
        <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Planned</dt><dd className="text-lg font-semibold tabular-nums">{hours(data.summary.plannedMinutes)}</dd>{data.summary.tentativeMinutes > 0 && <dd className="text-[11px] text-sky-700 dark:text-sky-300">+{hours(data.summary.tentativeMinutes)} tentative</dd>}</div>
        <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Actual logged</dt><dd className="text-lg font-semibold tabular-nums">{fmtDuration(data.summary.actualSeconds + sinceAnswer(running, now, dataUpdatedAt))}</dd>{data.summary.unplannedActualSeconds > 0 && <dd className="text-[11px] text-[var(--muted)]">{fmtDuration(data.summary.unplannedActualSeconds)} unplanned</dd>}</div>
        <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Completed</dt><dd className="text-lg font-semibold tabular-nums">{data.summary.completed}</dd></div>
        <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">In progress</dt><dd className="text-lg font-semibold tabular-nums">{data.summary.inProgress}</dd></div>
        <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Not started</dt><dd className="text-lg font-semibold tabular-nums">{data.summary.notStarted}</dd></div>
        <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Remaining planned</dt><dd className="text-lg font-semibold tabular-nums">{hours(data.summary.remainingPlannedMinutes)}</dd>{day && <dd className="text-[11px] text-[var(--muted)]">{hours(day.usableMinutes)} usable{day.unavailableAllDay ? ' · away' : !day.isWorkingDay ? ' · not a working day' : ''}</dd>}</div>
      </dl>

      {/* ---- now / next ---- */}
      {(sections.current.length > 0 || sections.next.length > 0) && (
        <div className="grid gap-2 md:grid-cols-2">
          {sections.current.map((i) => <ItemCard key={i.ticketId} item={i} tz={tz} now={now} fetchedAt={dataUpdatedAt} canWork={data.canWork} heading={i.state === 2 ? 'Now' : 'Paused'} onAddTime={() => setAdding({ ticketId: i.ticketId, reference: i.reference ?? '', title: i.title ?? '' })} />)}
          {sections.next.map((i) => <ItemCard key={`next-${i.ticketId}`} item={i} tz={tz} now={now} fetchedAt={dataUpdatedAt} canWork={data.canWork} heading="Next" onAddTime={() => setAdding({ ticketId: i.ticketId, reference: i.reference ?? '', title: i.title ?? '' })} />)}
        </div>
      )}

      {/* ---- the day ---- */}
      <section aria-labelledby="today-heading" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <div className="flex items-center justify-between gap-2 px-4 py-3">
          <h2 id="today-heading" className="text-sm font-semibold">{isToday ? 'Today' : fmtDay(data.date)}</h2>
          <span className="text-xs text-[var(--muted)]">{day?.windowStart && day.windowEnd ? `Working ${clock(Date.parse(day.windowStart), tz)}–${clock(Date.parse(day.windowEnd), tz)}` : 'No working window'}{day && day.breaks.length > 0 ? ` · break ${day.breaks.map((b) => fmtSlot(b, tz)).join(', ')}` : ''}</span>
        </div>
        {sections.all.length === 0 && <p className="px-4 pb-4 text-sm text-[var(--muted)]">{self ? 'Nothing planned and nothing logged yet. Start work on anything below, or plan your day.' : 'Nothing planned or logged.'}</p>}
        {sections.all.length > 0 && (
          <ol aria-label="Work" className="divide-y divide-[var(--border)] border-t border-[var(--border)]">
            {sections.all.map((i) => <ItemRow key={i.ticketId} item={i} tz={tz} now={now} fetchedAt={dataUpdatedAt} canWork={data.canWork} onAddTime={() => setAdding({ ticketId: i.ticketId, reference: i.reference ?? '', title: i.title ?? '' })} />)}
          </ol>
        )}
      </section>

      {/* ---- unscheduled, mine ---- */}
      {self && (
        <section aria-labelledby="unscheduled-heading" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <div className="flex items-center justify-between gap-2 px-4 py-3">
            <h2 id="unscheduled-heading" className="text-sm font-semibold">Unscheduled my work{data.unscheduled.length ? ` (${data.unscheduled.length})` : ''}</h2>
            <Link href="/dashboard/workforce/my-plan" className="text-xs font-medium text-brand hover:underline">Plan it</Link>
          </div>
          {data.unscheduled.length === 0 && <p className="px-4 pb-3 text-xs text-[var(--muted)]">Everything you hold is in your plan, or nothing is open.</p>}
          {data.unscheduled.length > 0 && (
            <ul className="divide-y divide-[var(--border)] border-t border-[var(--border)]">
              {data.unscheduled.map((u) => <UnscheduledRow key={u.ticketId} work={u} canWork={data.canWork} inDay={!!data.items.find((i) => i.ticketId === u.ticketId)} onAddTime={() => setAdding({ ticketId: u.ticketId, reference: u.reference, title: u.title })} />)}
            </ul>
          )}
        </section>
      )}

      {adding && <AddTimeDialog target={adding} onClose={() => setAdding(null)} onSaved={() => { setAdding(null); qc.invalidateQueries({ queryKey: ['my-day'] }); }} />}
    </div>
  );
}

function ItemCard({ item: i, tz, now, fetchedAt, canWork, heading, onAddTime }: { item: MyDayItem; tz: string; now: number; fetchedAt: number; canWork: boolean; heading: string; onAddTime: () => void }) {
  const live = i.actualSeconds + sinceAnswer(i.session?.status === 1, now, fetchedAt);
  return (
    <div className={`rounded-xl border p-4 ${i.state === 2 ? 'border-brand/60 bg-brand/5' : 'border-[var(--border)] bg-[var(--surface)]'}`}>
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="text-[11px] font-semibold uppercase tracking-wide text-[var(--muted)]">{heading}</div>
          <div className="flex flex-wrap items-center gap-x-2"><span className="font-mono text-xs text-[var(--muted)]">{i.reference}</span>{i.ticketVisible ? <Link href={`/dashboard/tickets/${i.ticketId}`} className="font-medium hover:underline">{i.title}</Link> : <span className="font-medium text-[var(--muted)]">Work you cannot open</span>}</div>
          <div className="text-xs text-[var(--muted)]">{[i.clientName, i.slots.length ? i.slots.map((s) => fmtSlot({ start: s.startsAt, end: s.endsAt }, tz)).join(', ') : 'not planned'].filter(Boolean).join(' · ')}</div>
        </div>
        {i.session?.status === 1 && <span className="text-xl font-semibold tabular-nums" aria-label="Running clock">{fmtClock(elapsedSeconds(i.session, now))}</span>}
        {i.session?.status === 2 && <span className={`${chip} bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-200`}>{PAUSE_REASONS[i.session.pauseReason] ?? 'Paused'}</span>}
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-3 text-xs">
        <span>Planned <b className="tabular-nums">{i.plannedMinutes ? hours(i.plannedMinutes) : '—'}</b></span>
        <span>Actual <b className="tabular-nums">{fmtDuration(live)}</b></span>
        {i.overPlannedEnd && <span className="inline-flex items-center gap-1 text-amber-700 dark:text-amber-300"><AlertTriangle size={12} /> Past its planned end; the next work may be affected</span>}
        {i.session?.outsideSchedule && <span className="text-[var(--muted)]">Outside your working hours</span>}
        <span className="ml-auto"><WorkControls session={i.session} ticketId={i.ticketId} allocationId={i.slots[0]?.allocationId} canStart={canWork && !i.ticketFinished} /></span>
      </div>
      {canWork && <button type="button" onClick={onAddTime} className="mt-1 text-xs text-brand hover:underline">Add time</button>}
    </div>
  );
}

function ItemRow({ item: i, tz, now, fetchedAt, canWork, onAddTime }: { item: MyDayItem; tz: string; now: number; fetchedAt: number; canWork: boolean; onAddTime: () => void }) {
  const st = STATE[i.state] ?? STATE[1]!;
  const live = i.actualSeconds + sinceAnswer(i.session?.status === 1, now, fetchedAt);
  return (
    <li className="flex flex-wrap items-start gap-x-4 gap-y-1 px-4 py-2.5">
      <span className="w-32 shrink-0 text-sm tabular-nums">{i.slots.length ? i.slots.map((s) => fmtSlot({ start: s.startsAt, end: s.endsAt }, tz)).join(', ') : <span className="text-[var(--muted)]">unplanned</span>}</span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-x-2">
          <span className="font-mono text-xs text-[var(--muted)]">{i.reference}</span>
          {i.ticketVisible ? <Link href={`/dashboard/tickets/${i.ticketId}`} className="font-medium hover:underline">{i.title}</Link> : <span className="font-medium text-[var(--muted)]">Work you cannot open</span>}
          {i.clientName && <span className="text-xs text-[var(--muted)]">· {i.clientName}</span>}
          <span className={`${chip} ${st.cls}`}>{st.label}</span>
          {i.slots.some((s) => s.tentative) && <span className={`${chip} border border-dashed border-sky-500 text-sky-800 dark:text-sky-200`}>Tentative</span>}
          {i.entriesNotSynced > 0 && <span className={`${chip} bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-200`} title="Logged here; not yet in the PSA">Not in the PSA yet</span>}
        </div>
        <div className="flex flex-wrap items-center gap-x-3 pt-0.5 text-xs text-[var(--muted)]">
          <span>Planned <b className="tabular-nums text-[var(--fg)]">{i.plannedMinutes ? hours(i.plannedMinutes) : '—'}</b></span>
          <span>Actual <b className="tabular-nums text-[var(--fg)]">{live > 0 ? fmtDuration(live) : '—'}</b></span>
          <span>Variance <Variance item={i} /></span>
          {i.session?.status === 1 && <span className="tabular-nums font-semibold text-brand">{fmtClock(elapsedSeconds(i.session, now))}</span>}
          {i.session?.status === 2 && <span>{PAUSE_REASONS[i.session.pauseReason] ?? 'Paused'} · {fmtClock(i.session.activeSeconds)}</span>}
          {i.overPlannedEnd && <span className="inline-flex items-center gap-1 text-amber-700 dark:text-amber-300"><AlertTriangle size={12} /> past its planned end</span>}
          {i.session?.outsideSchedule && <span>outside working hours</span>}
        </div>
      </div>
      <span className="flex items-center gap-1">
        {canWork && !i.ticketFinished && <button type="button" onClick={onAddTime} aria-label={`Add time to ${i.reference ?? 'work'}`} className={`${btn} px-2 py-1 text-xs`}><Plus size={12} /> Add time</button>}
        <WorkControls session={i.session} ticketId={i.ticketId} allocationId={i.slots[0]?.allocationId} compact canStart={canWork && !i.ticketFinished} />
      </span>
    </li>
  );
}

function UnscheduledRow({ work: u, canWork, inDay, onAddTime }: { work: UnscheduledWork; canWork: boolean; inDay: boolean; onAddTime: () => void }) {
  return (
    <li className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2 text-sm">
      <span className="font-mono text-xs text-[var(--muted)]">{u.reference}</span>
      <Link href={`/dashboard/tickets/${u.ticketId}`} className="font-medium hover:underline">{u.title}</Link>
      {u.clientName && <span className="text-xs text-[var(--muted)]">· {u.clientName}</span>}
      <span className={`${chip} bg-[var(--bg)] text-[var(--muted)]`}>{u.priority}</span>
      {u.dueAt && <span className="text-xs text-[var(--muted)]">due {fmtDay(dateOf(Date.parse(u.dueAt), 'UTC'))}</span>}
      <span className="ml-auto flex items-center gap-1">
        {canWork && <button type="button" onClick={onAddTime} aria-label={`Add time to ${u.reference}`} className={`${btn} px-2 py-1 text-xs`}><Plus size={12} /> Add time</button>}
        {!inDay && <WorkControls session={null} ticketId={u.ticketId} compact canStart={canWork} />}
      </span>
    </li>
  );
}

/** Thirty minutes typed in: the ticket's own time log, from the day. */
export function AddTimeDialog({ target, onClose, onSaved }: { target: { ticketId: string; reference: string; title: string }; onClose: () => void; onSaved: () => void }) {
  useDialog(onClose);
  const [minutes, setMinutes] = useState('30');
  const [billable, setBillable] = useState(true);
  const [notes, setNotes] = useState('');
  const { data: opts } = useQuery({ queryKey: ['time-options', target.ticketId], queryFn: () => api.ticketTimeOptions(target.ticketId), retry: false });
  const [workType, setWorkType] = useState('');
  const save = useMutation({
    mutationFn: () => api.logTime(target.ticketId, { hours: Math.round((Number(minutes) / 60) * 10000) / 10000, billable: billable ? 'Billable' : 'DoNotBill', notes: notes.trim() || undefined, workType: workType || undefined }),
    onSuccess: onSaved,
  });
  const m = Number(minutes);
  const ok = Number.isInteger(m) && m >= 1 && m <= 1440;
  return (
    <div role="dialog" aria-modal="true" aria-label="Add time" className="fixed inset-0 z-50 flex items-start justify-center bg-black/40 p-4 pt-[12vh]" onClick={onClose}>
      <form className="w-full max-w-md space-y-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl" onClick={(e) => e.stopPropagation()} onSubmit={(e) => { e.preventDefault(); if (ok) save.mutate(); }}>
        <div className="flex items-start justify-between gap-3">
          <div><h2 className="text-sm font-semibold">Add time to {target.reference}</h2><p className="text-xs text-[var(--muted)]">{target.title}. Work already done, without a clock.</p></div>
          <button type="button" aria-label="Close" onClick={onClose} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={16} /></button>
        </div>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Actual minutes
          <input type="number" min={1} max={1440} step={1} value={minutes} onChange={(e) => setMinutes(e.target.value)} aria-label="Actual minutes" className={`w-full ${field}`} />
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Note
          <textarea value={notes} rows={2} maxLength={2000} onChange={(e) => setNotes(e.target.value)} aria-label="Time note" className={`w-full ${field}`} placeholder="Reviewed backup failure and restarted the failed job." />
        </label>
        <div className="flex flex-wrap items-center gap-3">
          <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={billable} onChange={(e) => setBillable(e.target.checked)} aria-label="Billable" /> Billable</label>
          {opts && opts.workTypes.length > 0 && <select value={workType} onChange={(e) => setWorkType(e.target.value)} aria-label="Work type" className={field}><option value="">Work type (default)</option>{opts.workTypes.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}</select>}
        </div>
        {save.error && <p role="alert" className="text-xs text-red-600 dark:text-red-400">{(save.error as Error).message}</p>}
        <div className="flex justify-end gap-2">
          <button type="button" onClick={onClose} className={btn}>Cancel</button>
          <button type="submit" disabled={!ok || save.isPending} className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50"><Clock size={14} /> {save.isPending ? 'Saving…' : 'Add time'}</button>
        </div>
      </form>
    </div>
  );
}

// ---- Team Today ---------------------------------------------------------------------------------------

export function TeamTodayView({ viewerId }: { viewerId: string }) {
  const [f, setF] = useState({ teamId: '', departmentId: '', skillIds: [] as string[], matchAll: true });
  const [date, setDate] = useState<string | null>(null);
  const { data, isLoading, error, refetch, isFetching } = useQuery({
    queryKey: ['team-today', date ?? 'today', f.teamId, f.departmentId, f.skillIds, f.matchAll],
    queryFn: () => api.teamToday({ date, teamId: f.teamId, departmentId: f.departmentId, skills: f.skillIds, matchAll: f.matchAll }), retry: false, placeholderData: (prev) => prev, refetchInterval: 60_000,
  });
  const now = useTicking(!!data?.people.some((p) => p.current));
  void viewerId;
  return (
    <div className="space-y-3">
      <section aria-label="Filters" className="flex flex-wrap items-center gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3">
        <input type="date" value={date ?? data?.date ?? ''} onChange={(e) => setDate(e.target.value || null)} aria-label="Day" className={field} />
        <button type="button" onClick={() => setDate(null)} className={btn}>Today</button>
        <GroupAndSkillFilters {...f} onChange={(patch) => setF((v) => ({ ...v, ...patch }))} />
        <button type="button" onClick={() => refetch()} aria-label="Refresh" className="ml-auto rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><RefreshCw size={14} className={isFetching ? 'animate-spin' : ''} /></button>
      </section>
      {error && <p role="alert" className="rounded-lg border border-red-300 bg-red-50 px-3 py-2 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-200">{(error as Error).message}</p>}
      {data && (
        <dl className="grid grid-cols-2 gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3 text-sm sm:grid-cols-4" aria-label="Team summary">
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Working now</dt><dd className="text-lg font-semibold tabular-nums">{data.working}</dd>{data.paused > 0 && <dd className="text-[11px] text-[var(--muted)]">{data.paused} paused</dd>}</div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Planned today</dt><dd className="text-lg font-semibold tabular-nums">{hours(data.plannedMinutes)}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Actual logged</dt><dd className="text-lg font-semibold tabular-nums">{fmtDuration(data.actualSeconds)}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">People</dt><dd className="text-lg font-semibold tabular-nums">{data.people.length}</dd><dd className="text-[11px] text-[var(--muted)]">{fmtDay(data.date)} · {data.timeZone}</dd></div>
        </dl>
      )}
      {isLoading && !data && <div aria-busy="true" className="h-40 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}
      {data && data.people.length === 0 && <div className="rounded-xl border border-dashed border-[var(--border)] px-4 py-10 text-center text-sm text-[var(--muted)]">Nobody matches these filters.</div>}
      {data && data.people.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <table className="w-full min-w-[760px] text-sm">
            <caption className="sr-only">Team today</caption>
            <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
              <tr><th scope="col" className="px-3 py-2 font-medium">Person</th><th scope="col" className="px-3 py-2 font-medium">Current work</th><th scope="col" className="px-3 py-2 text-right font-medium">Planned today</th><th scope="col" className="px-3 py-2 text-right font-medium">Actual logged</th><th scope="col" className="px-3 py-2 text-right font-medium">Done / going / not started</th></tr>
            </thead>
            <tbody className="divide-y divide-[var(--border)]">
              {data.people.map((p) => (
                <tr key={p.appUserId}>
                  <td className="px-3 py-2"><Link href={`/dashboard/workforce/my-day?person=${p.appUserId}`} className="font-medium hover:underline">{p.displayName}</Link>{!p.hasSchedule && <div className="text-[11px] text-[var(--muted)]">No schedule</div>}</td>
                  <td className="px-3 py-2">
                    {p.current ? (
                      <span className="flex flex-wrap items-center gap-2"><span className="font-mono text-xs text-[var(--muted)]">{p.current.reference ?? '—'}</span><span className="truncate">{p.current.title ?? 'Work you cannot open'}</span><span className="tabular-nums font-semibold text-brand">{fmtClock(elapsedSeconds(p.current, now))}</span>{p.current.outsideSchedule && <span className="text-[11px] text-[var(--muted)]">outside hours</span>}</span>
                    ) : <span className="text-[var(--muted)]">{p.pausedCount > 0 ? `None running · ${p.pausedCount} paused` : 'None'}</span>}
                  </td>
                  <td className="px-3 py-2 text-right tabular-nums">{hours(p.plannedMinutes)}<span className="block text-[11px] text-[var(--muted)]">of {hours(p.usableMinutes)} usable</span></td>
                  <td className="px-3 py-2 text-right tabular-nums">{fmtDuration(p.actualSeconds)}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{p.completed} / {p.inProgress} / {p.notStarted}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="text-xs text-[var(--muted)]">Operational facts about work: who is on what and how much of the plan is done. Not attendance, not a ranking.</p>
    </div>
  );
}

export type { MyDay, TeamToday };
