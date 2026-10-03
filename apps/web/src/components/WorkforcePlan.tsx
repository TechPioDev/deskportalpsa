'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowRightLeft, CalendarPlus, ChevronLeft, ChevronRight, Lock, Pencil, Plus, Search, Trash2, X } from 'lucide-react';
import {
  api, ApiError, ConflictProblemSchema, type ConflictProblem, type DayCapacity, type UnscheduledWork, type WorkAllocation,
} from '@/lib/api';
import { hours } from '@/components/Workforce';
import { addDays, dateIn, fmtDay, fmtSlot, fmtTime } from '@/components/WorkforceCapacity';

// ---- Instants from wall-clock times ---------------------------------------------------------------
// The server stores instants; people type a time of day in the person's zone. The browser has no
// "wall time in zone X" primitive, so the offset is read back from Intl and corrected once (twice is
// enough even across a clock change).

function zoneOffsetMinutes(at: Date, timeZone: string): number {
  try {
    const parts = new Intl.DateTimeFormat('en-US', { timeZone, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }).formatToParts(at);
    const get = (t: string) => Number(parts.find((p) => p.type === t)?.value ?? 0);
    const asUtc = Date.UTC(get('year'), get('month') - 1, get('day'), get('hour'), get('minute'), get('second'));
    return Math.round((asUtc - at.getTime()) / 60_000);
  } catch { return 0; }
}
/** "2026-10-05" + "14:00" in a zone → ISO instant. */
export function wallToIso(date: string, hm: string, timeZone: string): string {
  const [h, m] = hm.split(':').map(Number);
  const naive = Date.UTC(Number(date.slice(0, 4)), Number(date.slice(5, 7)) - 1, Number(date.slice(8, 10)), h, m);
  let guess = naive - zoneOffsetMinutes(new Date(naive), timeZone) * 60_000;
  guess = naive - zoneOffsetMinutes(new Date(guess), timeZone) * 60_000;
  return new Date(guess).toISOString();
}
const addMinutesIso = (iso: string, minutes: number) => new Date(new Date(iso).getTime() + minutes * 60_000).toISOString();
const minutesBetween = (a: string, b: string) => Math.round((new Date(b).getTime() - new Date(a).getTime()) / 60_000);
/** Instants arrive in two spellings ("…Z" from the browser, "…+00:00" from the server): compare them as moments, never as text. */
const ms = (iso: string) => Date.parse(iso);

/** A dialog closes on Escape and tells assistive technology it is modal; what is behind it waits. */
function useDialog(onClose: () => void) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
}

const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm outline-none focus:border-brand disabled:opacity-60';
const chip = 'inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-medium';
const DURATIONS = [15, 30, 45, 60, 90, 120, 180, 240];
const CONFLICT_NAMES: Record<number, string> = { 1: 'Already planned', 2: 'Tentative work', 3: 'Break', 4: 'Unavailable', 5: 'Outside working hours', 6: 'Over capacity', 7: 'Skill', 8: 'Not offered for work' };

/** "Planned by you", "Scheduled by Lena Lead", with Fixed said plainly. */
export function whoPlanned(a: WorkAllocation, viewerId: string | null): string {
  if (a.method === 3) return 'Placed by the system';
  if (a.scheduledByUserId === viewerId) return a.method === 1 ? 'Planned by you' : 'Scheduled by you';
  return a.method === 1 ? `Planned by ${a.scheduledByName ?? 'them'}` : `Scheduled by ${a.scheduledByName ?? 'someone'}`;
}

function conflictProblem(error: unknown): ConflictProblem | null {
  if (!(error instanceof ApiError) || error.status !== 409) return null;
  const parsed = ConflictProblemSchema.safeParse(error.payload);
  return parsed.success ? parsed.data : null;
}

/** What a 409 said, with the conflicts listed and (where allowed) a reason box. */
function ConflictNotice({ error, reason, onReason }: { error: unknown; reason: string; onReason: (r: string) => void }) {
  const problem = conflictProblem(error);
  if (!problem) return error ? <p role="alert" className="text-sm text-red-600 dark:text-red-400 sm:col-span-2">{(error as Error).message}</p> : null;
  return (
    <div role="alert" className="space-y-2 rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-100 sm:col-span-2">
      <p className="font-medium">{(error as Error).message}</p>
      {problem.conflicts.length > 0 && (
        <ul className="list-disc space-y-0.5 pl-5 text-xs">
          {problem.conflicts.map((c, i) => <li key={i}>{CONFLICT_NAMES[c.type] ?? 'Conflict'}: {c.message}</li>)}
        </ul>
      )}
      {problem.stale && <p className="text-xs">Reload the page to see the current plan.</p>}
      {problem.canOverride && problem.overrideAllowedForCaller && (
        <label className="block space-y-1 text-xs font-medium">
          Reason to override (kept with the work)
          <input value={reason} onChange={(e) => onReason(e.target.value)} maxLength={300} aria-label="Override reason" placeholder="Client-approved after-hours maintenance" className={`w-full ${field}`} />
        </label>
      )}
      {problem.canOverride && !problem.overrideAllowedForCaller && <p className="text-xs">Someone who can override scheduling conflicts can place it anyway, with a reason.</p>}
    </div>
  );
}

// ---- Placing or moving work --------------------------------------------------------------------

export type PlanTarget = { ticketId: string; reference: string; title: string };

/**
 * One form for putting work into someone's time, or moving it: whose time, which date, how long,
 * and where in their free windows. The server checks everything again when it saves; what this
 * shows as free is what was free a moment ago.
 */
export function PlanWorkDialog({ target, allocation, personId, lockPerson = false, viewerId, onClose, onSaved }: {
  target?: PlanTarget; allocation?: WorkAllocation; personId: string; lockPerson?: boolean; viewerId: string | null; onClose: () => void; onSaved: (a: WorkAllocation) => void;
}) {
  const qc = useQueryClient();
  const moving = !!allocation;
  const { data: people } = useQuery({ queryKey: ['plan-people'], queryFn: api.plannablePeople, staleTime: 60_000 });
  const [person, setPerson] = useState(allocation?.appUserId ?? personId);
  const tz = allocation?.timeZone ?? people?.find((p) => p.appUserId === person)?.timeZone ?? 'UTC';
  const [date, setDate] = useState(allocation ? dateIn(allocation.startsAt, allocation.timeZone) : new Date().toLocaleDateString('en-CA'));
  const [duration, setDuration] = useState(String(allocation?.plannedMinutes ?? 60));
  const [start, setStart] = useState(allocation ? fmtTime(allocation.startsAt, allocation.timeZone) : '');
  const [isFixed, setIsFixed] = useState(allocation?.isFixed ?? false);
  const [note, setNote] = useState(allocation?.note ?? '');
  const [reason, setReason] = useState('');
  const [finding, setFinding] = useState(false);
  const minutes = Number(duration);
  const self = person === viewerId;

  const { data: capacity } = useQuery({
    queryKey: ['capacity', person, date, 'dialog'], queryFn: () => api.personCapacity(person, date, date), enabled: !!person && !!date, retry: false,
  });
  const day: DayCapacity | undefined = capacity?.days[0];
  // Free windows that hold the work; when moving, the work's own place is free too.
  const windows = useMemo(() => {
    if (!day) return [];
    const own = allocation ? { start: allocation.startsAt, end: allocation.endsAt } : null;
    const slots = [...day.freeSlots.map((s) => ({ start: s.start, end: s.end })), ...(own ? [own] : [])]
      .sort((a, b) => ms(a.start) - ms(b.start));
    const merged: { start: string; end: string }[] = [];
    for (const s of slots) {
      const last = merged[merged.length - 1];
      if (last && ms(s.start) <= ms(last.end)) { if (ms(s.end) > ms(last.end)) last.end = s.end; } else merged.push({ ...s });
    }
    return merged.filter((s) => minutesBetween(s.start, s.end) >= minutes);
  }, [day, allocation, minutes]);

  const { data: found, isFetching: searching } = useQuery({
    queryKey: ['capacity', 'find', 'dialog', date, minutes, finding],
    queryFn: () => api.findAvailable({ from: date, duration: minutes }), enabled: finding && minutes >= 5, retry: false, gcTime: 0,
  });

  const startIso = start && date ? wallToIso(date, start, tz) : null;
  const endIso = startIso ? addMinutesIso(startIso, minutes) : null;
  const problems: string[] = [];
  if (!date) problems.push('Choose a date.');
  if (!Number.isInteger(minutes) || minutes < 5 || minutes > 1440) problems.push('The work must take between 5 minutes and 24 hours.');
  if (!start) problems.push('Choose a start time or one of the free windows.');

  const save = useMutation({
    mutationFn: () => {
      const body = { start: startIso!, end: endIso!, note: note.trim() || null, overrideReason: reason.trim() || null };
      return allocation
        ? api.updatePlannedWork(allocation.id, { ...body, version: allocation.version, isFixed: self ? null : isFixed })
        : api.planWork({ ticketId: target!.ticketId, appUserId: person, isFixed: !self && isFixed, ...body });
    },
    onSuccess: (a) => {
      qc.invalidateQueries({ queryKey: ['plan'] });
      qc.invalidateQueries({ queryKey: ['capacity'] });
      qc.invalidateQueries({ queryKey: ['ticket-plan'] });
      onSaved(a);
    },
  });
  const conflict = conflictProblem(save.error);
  const canSave = problems.length === 0 && !save.isPending && (!conflict || (conflict.canOverride && conflict.overrideAllowedForCaller && reason.trim().length >= 5));
  useDialog(onClose);

  return (
    <div role="dialog" aria-modal="true" aria-label={moving ? 'Move planned work' : 'Plan work'} className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/40 p-4 pt-[6vh]" onClick={onClose}>
      <form className="grid w-full max-w-xl gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl sm:grid-cols-2" onClick={(e) => e.stopPropagation()}
        onSubmit={(e) => { e.preventDefault(); if (canSave) save.mutate(); }}>
        <div className="flex items-start justify-between gap-3 sm:col-span-2">
          <div>
            <h2 className="text-sm font-semibold">{moving ? 'Move' : 'Plan'} {allocation?.reference ?? target?.reference}</h2>
            <p className="text-xs text-[var(--muted)]">{allocation?.title ?? target?.title}{lockPerson && !self ? ` · for ${people?.find((p) => p.appUserId === person)?.displayName ?? 'them'}` : ''}</p>
          </div>
          <button type="button" aria-label="Close" onClick={onClose} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={16} /></button>
        </div>

        {!moving && !lockPerson && (people?.length ?? 0) > 1 && (
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
            Whose time
            <select value={person} onChange={(e) => { setPerson(e.target.value); setStart(''); }} aria-label="Person" className={`w-full ${field}`}>
              {people!.map((p) => <option key={p.appUserId} value={p.appUserId}>{p.displayName}{p.isSelf ? ' (you)' : ''}{p.isSchedulable ? '' : ' · not offered for work'}</option>)}
            </select>
          </label>
        )}
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Date
          <input type="date" required value={date} onChange={(e) => { setDate(e.target.value); setStart(''); }} aria-label="Plan date" className={`w-full ${field}`} />
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Planned duration (minutes)
          <input type="number" required min={5} max={1440} step={5} list="plan-durations" value={duration} onChange={(e) => setDuration(e.target.value)} aria-label="Planned duration" className={`w-full ${field}`} />
          <datalist id="plan-durations">{DURATIONS.map((d) => <option key={d} value={d} />)}</datalist>
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Start
          <input type="time" required step={300} value={start} onChange={(e) => setStart(e.target.value)} aria-label="Start time" className={`w-full ${field}`} />
          <span className="block text-[11px] font-normal">Times in {tz}{endIso ? ` · ends ${fmtTime(endIso, tz)}` : ''}</span>
        </label>

        <div className="space-y-1 sm:col-span-2">
          <span className="text-xs font-medium text-[var(--muted)]">Free windows that hold {hours(minutes || 0)}</span>
          {!day && <p className="text-xs text-[var(--muted)]">Loading…</p>}
          {day && windows.length === 0 && <p className="text-xs text-[var(--muted)]">{day.usableMinutes === 0 ? 'No working time on this day.' : 'No single free window is long enough. Try a shorter duration or another day.'}</p>}
          <div className="flex flex-wrap gap-1.5" role="group" aria-label="Free windows">
            {windows.map((w) => {
              const on = !!startIso && !!endIso && ms(startIso) >= ms(w.start) && ms(endIso) <= ms(w.end);
              return (
                <button key={w.start} type="button" aria-pressed={on} onClick={() => setStart(fmtTime(w.start, tz))}
                  className={`rounded-full border px-2.5 py-0.5 text-xs font-medium tabular-nums ${on ? 'border-brand bg-brand text-brand-fg' : 'border-[var(--border)] hover:bg-[var(--bg)]'}`}>
                  {fmtSlot(w, tz, date)} · {hours(minutesBetween(w.start, w.end))}
                </button>
              );
            })}
          </div>
          {!moving && !lockPerson && (people?.length ?? 0) > 1 && (
            <div className="pt-1">
              <button type="button" onClick={() => setFinding(true)} className="inline-flex items-center gap-1 text-xs font-medium text-brand hover:underline"><Search size={12} /> Find who is free on this date</button>
              {finding && searching && <span className="ml-2 text-xs text-[var(--muted)]">Searching…</span>}
              {finding && found && (
                <ul className="mt-1 max-h-40 divide-y divide-[var(--border)] overflow-y-auto rounded-lg border border-[var(--border)]">
                  {found.matches.length === 0 && <li className="px-3 py-2 text-xs text-[var(--muted)]">Nobody has a free window that long.</li>}
                  {found.matches.filter((m) => people!.some((p) => p.appUserId === m.appUserId)).map((m) => (
                    <li key={m.appUserId}>
                      <button type="button" onClick={() => { setPerson(m.appUserId); setStart(fmtTime(m.recommended.start, m.timeZone)); }}
                        className="flex w-full items-center justify-between gap-2 px-3 py-1.5 text-left text-xs hover:bg-[var(--bg)]">
                        <span className="font-medium">{m.displayName}</span>
                        <span className="tabular-nums text-[var(--muted)]">first fit {fmtSlot(m.recommended, m.timeZone, date)} · {hours(m.freeMinutes)} free</span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          )}
        </div>

        {!self && (
          <label className="flex items-start gap-2 text-sm sm:col-span-2">
            <input type="checkbox" checked={isFixed} onChange={(e) => setIsFixed(e.target.checked)} aria-label="Fixed" className="mt-0.5" disabled={moving && allocation?.method === 1} />
            <span><span className="font-medium">Fixed</span><span className="block text-xs text-[var(--muted)]">They cannot move it; only someone who schedules others can. Leave off for work they may rearrange.</span></span>
          </label>
        )}
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
          Note (optional)
          <input value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} aria-label="Planning note" placeholder="Client wants it done before 16:00" className={`w-full ${field}`} />
        </label>

        {problems.length > 0 && start !== '' && problems.map((p) => <p key={p} className="text-xs text-red-600 dark:text-red-400 sm:col-span-2">{p}</p>)}
        <ConflictNotice error={save.error} reason={reason} onReason={setReason} />
        <div className="flex justify-end gap-2 sm:col-span-2">
          <button type="button" onClick={onClose} className="rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
          <button type="submit" disabled={!canSave} className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
            {save.isPending ? 'Saving…' : conflict?.canOverride && conflict.overrideAllowedForCaller ? 'Override and save' : moving ? 'Move' : 'Add to plan'}
          </button>
        </div>
      </form>
    </div>
  );
}

/** Giving work to someone else, optionally at another time. */
function ReassignDialog({ allocation, onClose, onSaved }: { allocation: WorkAllocation; onClose: () => void; onSaved: (a: WorkAllocation) => void }) {
  const qc = useQueryClient();
  const { data: people } = useQuery({ queryKey: ['plan-people'], queryFn: api.plannablePeople, staleTime: 60_000 });
  const others = (people ?? []).filter((p) => p.appUserId !== allocation.appUserId);
  const [person, setPerson] = useState('');
  const tz = others.find((p) => p.appUserId === person)?.timeZone ?? allocation.timeZone;
  const [date, setDate] = useState(dateIn(allocation.startsAt, allocation.timeZone));
  const [start, setStart] = useState(fmtTime(allocation.startsAt, allocation.timeZone));
  const [reason, setReason] = useState('');
  const startIso = date && start ? wallToIso(date, start, tz) : null;
  const save = useMutation({
    mutationFn: () => api.reassignPlannedWork(allocation.id, {
      appUserId: person, version: allocation.version, start: startIso, end: startIso ? addMinutesIso(startIso, allocation.plannedMinutes) : null, overrideReason: reason.trim() || null,
    }),
    onSuccess: (a) => { qc.invalidateQueries({ queryKey: ['plan'] }); qc.invalidateQueries({ queryKey: ['capacity'] }); qc.invalidateQueries({ queryKey: ['ticket-plan'] }); onSaved(a); },
  });
  const conflict = conflictProblem(save.error);
  const canSave = !!person && !!startIso && !save.isPending && (!conflict || (conflict.canOverride && conflict.overrideAllowedForCaller && reason.trim().length >= 5));
  useDialog(onClose);
  return (
    <div role="dialog" aria-modal="true" aria-label="Give work to someone else" className="fixed inset-0 z-50 flex items-start justify-center bg-black/40 p-4 pt-[10vh]" onClick={onClose}>
      <form className="grid w-full max-w-lg gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl sm:grid-cols-2" onClick={(e) => e.stopPropagation()}
        onSubmit={(e) => { e.preventDefault(); if (canSave) save.mutate(); }}>
        <div className="sm:col-span-2">
          <h2 className="text-sm font-semibold">Give {allocation.reference ?? 'this work'} to someone else</h2>
          <p className="text-xs text-[var(--muted)]">Now planned for {allocation.personName}, {fmtSlot({ start: allocation.startsAt, end: allocation.endsAt }, allocation.timeZone)} ({hours(allocation.plannedMinutes)}). Their capacity is checked before it moves.</p>
        </div>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
          To
          <select value={person} onChange={(e) => setPerson(e.target.value)} aria-label="New person" className={`w-full ${field}`}>
            <option value="">Choose a person…</option>
            {others.map((p) => <option key={p.appUserId} value={p.appUserId}>{p.displayName}{p.isSchedulable ? '' : ' · not offered for work'}</option>)}
          </select>
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Date
          <input type="date" value={date} onChange={(e) => setDate(e.target.value)} aria-label="New date" className={`w-full ${field}`} />
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Start
          <input type="time" step={300} value={start} onChange={(e) => setStart(e.target.value)} aria-label="New start time" className={`w-full ${field}`} />
          <span className="block text-[11px] font-normal">Times in {tz}</span>
        </label>
        <ConflictNotice error={save.error} reason={reason} onReason={setReason} />
        <div className="flex justify-end gap-2 sm:col-span-2">
          <button type="button" onClick={onClose} className="rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
          <button type="submit" disabled={!canSave} className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
            {save.isPending ? 'Saving…' : conflict?.canOverride && conflict.overrideAllowedForCaller ? 'Override and give' : 'Give the work'}
          </button>
        </div>
      </form>
    </div>
  );
}

/** A small piece of internal work with no ticket yet: raised on a board and planned in one go. */
function InternalWorkDialog({ viewerId, onClose, onSaved }: { viewerId: string; onClose: () => void; onSaved: (a: WorkAllocation) => void }) {
  const qc = useQueryClient();
  const { data: boards } = useQuery({ queryKey: ['boards', false], queryFn: () => api.boards(false), staleTime: 60_000 });
  const { data: people } = useQuery({ queryKey: ['plan-people'], queryFn: api.plannablePeople, staleTime: 60_000 });
  const internal = (boards ?? []).filter((b) => b.kind === 0 && b.isActive);
  const tz = people?.find((p) => p.appUserId === viewerId)?.timeZone ?? 'UTC';
  const [board, setBoard] = useState('');
  const [title, setTitle] = useState('');
  const [date, setDate] = useState(new Date().toLocaleDateString('en-CA'));
  const [start, setStart] = useState('');
  const [duration, setDuration] = useState('60');
  const [reason, setReason] = useState('');
  useEffect(() => { if (!board && internal.length > 0) setBoard(internal[0].id); }, [board, internal]);
  const minutes = Number(duration);
  const startIso = date && start ? wallToIso(date, start, tz) : null;
  const save = useMutation({
    mutationFn: () => api.planInternalWork({ boardId: board, title: title.trim(), start: startIso!, end: addMinutesIso(startIso!, minutes), overrideReason: reason.trim() || null }),
    onSuccess: (a) => { qc.invalidateQueries({ queryKey: ['plan'] }); qc.invalidateQueries({ queryKey: ['capacity'] }); qc.invalidateQueries({ queryKey: ['tickets'] }); onSaved(a); },
  });
  const conflict = conflictProblem(save.error);
  const canSave = !!board && title.trim().length > 0 && !!startIso && minutes >= 5 && minutes <= 1440 && !save.isPending
    && (!conflict || (conflict.canOverride && conflict.overrideAllowedForCaller && reason.trim().length >= 5));
  useDialog(onClose);
  return (
    <div role="dialog" aria-modal="true" aria-label="Plan internal work" className="fixed inset-0 z-50 flex items-start justify-center bg-black/40 p-4 pt-[10vh]" onClick={onClose}>
      <form className="grid w-full max-w-lg gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl sm:grid-cols-2" onClick={(e) => e.stopPropagation()}
        onSubmit={(e) => { e.preventDefault(); if (canSave) save.mutate(); }}>
        <div className="sm:col-span-2">
          <h2 className="text-sm font-semibold">Plan internal work</h2>
          <p className="text-xs text-[var(--muted)]">A ticket is raised on the board in your name and placed in your time, so the work can be measured like any other.</p>
        </div>
        {boards && internal.length === 0 && <p className="text-sm text-[var(--muted)] sm:col-span-2">There is no internal board to raise it on yet. Ask whoever manages Internal boards to create one.</p>}
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
          What
          <input value={title} maxLength={500} required onChange={(e) => setTitle(e.target.value)} aria-label="Work title" placeholder="Review Microsoft 365 Secure Score" className={`w-full ${field}`} />
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
          Board
          <select value={board} onChange={(e) => setBoard(e.target.value)} aria-label="Board" className={`w-full ${field}`}>
            {internal.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
          </select>
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Date
          <input type="date" required value={date} onChange={(e) => setDate(e.target.value)} aria-label="Internal work date" className={`w-full ${field}`} />
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Start
          <input type="time" required step={300} value={start} onChange={(e) => setStart(e.target.value)} aria-label="Internal work start" className={`w-full ${field}`} />
          <span className="block text-[11px] font-normal">Times in {tz}</span>
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Duration (minutes)
          <input type="number" required min={5} max={1440} step={5} list="plan-durations" value={duration} onChange={(e) => setDuration(e.target.value)} aria-label="Internal work duration" className={`w-full ${field}`} />
        </label>
        <ConflictNotice error={save.error} reason={reason} onReason={setReason} />
        <div className="flex justify-end gap-2 sm:col-span-2">
          <button type="button" onClick={onClose} className="rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
          <button type="submit" disabled={!canSave} className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
            {save.isPending ? 'Saving…' : conflict?.canOverride && conflict.overrideAllowedForCaller ? 'Override and plan' : 'Raise and plan'}
          </button>
        </div>
      </form>
    </div>
  );
}

/** Choosing which open work to put into someone's plan: a search over the tickets the viewer may see. */
function TicketPickerDialog({ personName, onClose, onPick }: { personName: string; onClose: () => void; onPick: (t: PlanTarget) => void }) {
  const [q, setQ] = useState('');
  const { data, isFetching } = useQuery({
    queryKey: ['plan', 'ticket-picker', q], queryFn: () => api.searchTickets({ q: q.trim() || undefined, openness: 'open', take: 15 }), staleTime: 15_000, retry: false,
  });
  useDialog(onClose);
  return (
    <div role="dialog" aria-modal="true" aria-label="Choose work to plan" className="fixed inset-0 z-50 flex items-start justify-center bg-black/40 p-4 pt-[10vh]" onClick={onClose}>
      <div className="w-full max-w-lg space-y-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between gap-3">
          <div>
            <h2 className="text-sm font-semibold">Plan work for {personName}</h2>
            <p className="text-xs text-[var(--muted)]">Open tickets you can see. Planning it puts it in their time; if nobody holds it yet, they will.</p>
          </div>
          <button type="button" aria-label="Close" onClick={onClose} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={16} /></button>
        </div>
        <input autoFocus value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search tickets" placeholder="Number, title or client" className={`w-full ${field}`} />
        <ul aria-label="Matching tickets" className="max-h-72 divide-y divide-[var(--border)] overflow-y-auto rounded-lg border border-[var(--border)]">
          {data && data.items.length === 0 && <li className="px-3 py-3 text-sm text-[var(--muted)]">No open ticket matches.</li>}
          {(data?.items ?? []).map((t) => {
            const reference = t.number ?? (t.externalTicketId ? `#${t.externalTicketId}` : 'Ticket');
            return (
              <li key={t.id}>
                <button type="button" onClick={() => onPick({ ticketId: t.id, reference, title: t.title })} className="flex w-full flex-wrap items-center gap-x-2 px-3 py-2 text-left text-sm hover:bg-[var(--bg)]">
                  <span className="font-mono text-xs text-[var(--muted)]">{reference}</span>
                  <span className="font-medium">{t.title}</span>
                  <span className="ml-auto text-xs text-[var(--muted)]">{t.customerName ?? ''}{t.assignedToName ? ` · ${t.assignedToName}` : ' · unheld'}</span>
                </button>
              </li>
            );
          })}
        </ul>
        {isFetching && <p className="text-xs text-[var(--muted)]">Searching…</p>}
        {data?.truncated && <p className="text-xs text-[var(--muted)]">Only the first {data.items.length} shown. Narrow the search.</p>}
      </div>
    </div>
  );
}

// ---- The plan itself ---------------------------------------------------------------------------

type AgendaItem = { kind: 'work'; start: string; end: string; allocation: WorkAllocation } | { kind: 'break' | 'free'; start: string; end: string };

function agenda(day: DayCapacity, allocations: WorkAllocation[]): AgendaItem[] {
  const items: AgendaItem[] = [
    ...allocations.map((a) => ({ kind: 'work' as const, start: a.startsAt, end: a.endsAt, allocation: a })),
    ...day.breaks.map((b) => ({ kind: 'break' as const, start: b.start, end: b.end })),
    ...day.freeSlots.map((s) => ({ kind: 'free' as const, start: s.start, end: s.end })),
  ];
  return items.sort((a, b) => ms(a.start) - ms(b.start) || ms(a.end) - ms(b.end));
}

/**
 * One person's plan for a day: what is planned, when, by whom; the breaks and the free windows
 * between; and the capacity sums. The planned person moves and removes what they may; whoever
 * schedules others does the rest.
 */
export function PlanAgenda({ userId, viewerId, initialDate }: { userId: string; viewerId: string | null; initialDate?: string | null }) {
  const qc = useQueryClient();
  const [picked, setPicked] = useState<string | null>(initialDate ?? null);
  // Without a chosen day the server answers with the person's own "today"; from then on one day at a time.
  const { data: plan, error, isLoading } = useQuery({
    queryKey: ['plan', userId, picked ?? 'today'], queryFn: () => api.personPlan(userId, picked, picked), retry: false, placeholderData: (prev) => prev,
  });
  const date = picked ?? plan?.today ?? null;
  const [dialog, setDialog] = useState<{ kind: 'move' | 'reassign'; allocation: WorkAllocation } | { kind: 'pick' } | { kind: 'plan'; target: PlanTarget } | null>(null);
  const cancel = useMutation({
    mutationFn: (a: WorkAllocation) => api.cancelPlannedWork(a.id),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['plan'] }); qc.invalidateQueries({ queryKey: ['capacity'] }); qc.invalidateQueries({ queryKey: ['ticket-plan'] }); },
  });
  useEffect(() => { setPicked(initialDate ?? null); }, [userId, initialDate]);

  if (error) return <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>;
  if (!date || !plan) return <p className="text-sm text-[var(--muted)]">Loading the plan…</p>;
  const day = plan.days.find((d) => d.date === date) ?? plan.days[0];
  const tz = plan.timeZone;
  const items = day ? agenda(day, plan.allocations) : [];
  const self = plan.appUserId === viewerId;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <button type="button" onClick={() => setPicked(addDays(date, -1))} aria-label="Previous day" className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><ChevronLeft size={16} /></button>
        <button type="button" onClick={() => setPicked(plan.today)} className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Today</button>
        <button type="button" onClick={() => setPicked(addDays(date, 1))} aria-label="Next day" className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><ChevronRight size={16} /></button>
        <input type="date" value={date} onChange={(e) => e.target.value && setPicked(e.target.value)} aria-label="Plan day" className={field} />
        <span className="text-sm font-medium">{fmtDay(date, { weekday: 'long', day: 'numeric', month: 'long' })}</span>
        <span className="text-xs text-[var(--muted)]">Times in {tz}</span>
        {plan.canPlan && (
          <button type="button" onClick={() => setDialog({ kind: 'pick' })} className="ml-auto inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90">
            <CalendarPlus size={14} /> Plan work
          </button>
        )}
      </div>

      {day && (
        <dl className="grid grid-cols-3 gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3 text-sm">
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Capacity</dt><dd className="text-lg font-semibold tabular-nums">{hours(day.usableMinutes)}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Planned</dt><dd className="text-lg font-semibold tabular-nums">{hours(day.confirmedMinutes)}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Free</dt><dd className="text-lg font-semibold tabular-nums text-green-700 dark:text-green-300">{hours(day.remainingConfirmedMinutes)}</dd></div>
        </dl>
      )}

      <ol aria-label="Plan" className="divide-y divide-[var(--border)] rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        {isLoading && items.length === 0 && <li className="px-4 py-6 text-center text-sm text-[var(--muted)]">Loading…</li>}
        {!isLoading && items.length === 0 && (
          <li className="px-4 py-6 text-center text-sm text-[var(--muted)]">{day?.unavailableAllDay ? 'Away all day.' : day && day.usableMinutes === 0 ? 'Not a working day.' : 'Nothing here yet.'}</li>
        )}
        {items.map((it, i) => (
          <li key={`${it.kind}-${it.start}-${i}`} className={`flex flex-wrap items-start gap-x-4 gap-y-1 px-4 py-2.5 ${it.kind === 'free' ? 'bg-green-50/60 dark:bg-green-950/20' : ''}`}>
            <span className="w-28 shrink-0 text-sm tabular-nums">{fmtSlot(it, tz, date)}</span>
            {it.kind === 'free' && <span className="text-sm text-green-800 dark:text-green-200">Available · {hours(minutesBetween(it.start, it.end))}</span>}
            {it.kind === 'break' && <span className="text-sm text-[var(--muted)]">Break</span>}
            {it.kind === 'work' && <WorkRow a={it.allocation} viewerId={viewerId} self={self}
              onMove={() => setDialog({ kind: 'move', allocation: it.allocation })}
              onReassign={() => setDialog({ kind: 'reassign', allocation: it.allocation })}
              onCancel={() => { if (window.confirm(`Take ${it.allocation.reference ?? 'this work'} out of the plan? The ticket itself is not changed.`)) cancel.mutate(it.allocation); }} />}
          </li>
        ))}
      </ol>
      {cancel.isError && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(cancel.error as Error).message}</p>}
      {dialog?.kind === 'move' && <PlanWorkDialog allocation={dialog.allocation} personId={dialog.allocation.appUserId} viewerId={viewerId} onClose={() => setDialog(null)} onSaved={() => setDialog(null)} />}
      {dialog?.kind === 'reassign' && <ReassignDialog allocation={dialog.allocation} onClose={() => setDialog(null)} onSaved={() => setDialog(null)} />}
      {dialog?.kind === 'pick' && <TicketPickerDialog personName={self ? 'you' : plan.displayName} onClose={() => setDialog(null)} onPick={(target) => setDialog({ kind: 'plan', target })} />}
      {dialog?.kind === 'plan' && <PlanWorkDialog target={dialog.target} personId={plan.appUserId} lockPerson viewerId={viewerId} onClose={() => setDialog(null)} onSaved={() => setDialog(null)} />}
    </div>
  );
}

function WorkRow({ a, viewerId, self, onMove, onReassign, onCancel }: { a: WorkAllocation; viewerId: string | null; self: boolean; onMove: () => void; onReassign: () => void; onCancel: () => void }) {
  const title = a.ticketVisible ? a.title : 'Work you cannot open';
  return (
    <div className="min-w-0 flex-1">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
        {a.ticketVisible ? <Link href={`/dashboard/tickets/${a.ticketId}`} className="font-medium hover:underline">{title}</Link> : <span className="font-medium text-[var(--muted)]">{title}</span>}
        {a.reference && <span className="font-mono text-xs text-[var(--muted)]">{a.reference}</span>}
        {a.clientName && <span className="text-xs text-[var(--muted)]">· {a.clientName}</span>}
        <span className="text-xs tabular-nums text-[var(--muted)]">· {hours(a.plannedMinutes)}</span>
      </div>
      <div className="flex flex-wrap items-center gap-1.5 pt-1">
        <span className={`${chip} ${a.method === 1 ? 'bg-[var(--bg)] text-[var(--muted)]' : 'bg-sky-100 text-sky-900 dark:bg-sky-950 dark:text-sky-200'}`}>{whoPlanned(a, viewerId)}</span>
        {a.isFixed && <span className={`${chip} bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-200`}><Lock size={10} aria-hidden="true" /> Fixed</span>}
        {a.overrideReason && <span className={`${chip} border border-[var(--border)]`} title={a.overrideReason}>Override: {a.overrideReason}</span>}
        {a.ticketFinished && <span className={`${chip} bg-[var(--bg)] text-[var(--muted)]`}>Ticket finished</span>}
        {a.note && <span className="text-xs text-[var(--muted)]">{a.note}</span>}
        <span className="ml-auto flex items-center gap-0.5">
          {a.canEdit && <button type="button" onClick={onMove} aria-label={`Move ${a.reference ?? 'work'}`} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]"><Pencil size={14} /></button>}
          {a.canReassign && <button type="button" onClick={onReassign} aria-label={`Give ${a.reference ?? 'work'} to someone else`} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]"><ArrowRightLeft size={14} /></button>}
          {a.canCancel && <button type="button" onClick={onCancel} aria-label={`Remove ${a.reference ?? 'work'} from the plan`} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-red-600"><Trash2 size={14} /></button>}
          {self && !a.canEdit && !a.canCancel && <span className="text-[11px] text-[var(--muted)]">Ask whoever planned it to change it</span>}
        </span>
      </div>
    </div>
  );
}

// ---- Unscheduled work --------------------------------------------------------------------------

/** My open work that is not yet in my plan, each a click from being planned. */
export function UnscheduledWorkList({ viewerId, canPlan }: { viewerId: string; canPlan: boolean }) {
  const { data: rows, isLoading, error } = useQuery({ queryKey: ['plan', 'unscheduled'], queryFn: api.unscheduledWork, retry: false });
  const [planning, setPlanning] = useState<PlanTarget | null>(null);
  const [internal, setInternal] = useState(false);
  return (
    <section aria-labelledby="unscheduled-heading" className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="unscheduled-heading" className="text-sm font-semibold">Unscheduled work of mine{rows ? ` (${rows.length})` : ''}</h2>
        {canPlan && (
          <button type="button" onClick={() => setInternal(true)} className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]"><Plus size={14} /> Internal work</button>
        )}
      </div>
      {error && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>}
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {rows && rows.length === 0 && <p className="text-sm text-[var(--muted)]">Everything you hold is planned, or there is nothing open. Work that sits with your team counts as yours to plan.</p>}
      {rows && rows.length > 0 && (
        <ul className="divide-y divide-[var(--border)] rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          {rows.map((t: UnscheduledWork) => (
            <li key={t.ticketId} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5 text-sm">
              <div className="min-w-0 flex-1">
                <Link href={`/dashboard/tickets/${t.ticketId}`} className="font-medium hover:underline">{t.title}</Link>
                <span className="ml-2 font-mono text-xs text-[var(--muted)]">{t.reference}</span>
                <div className="flex flex-wrap gap-x-2 text-xs text-[var(--muted)]">
                  <span>{t.source}</span>
                  {t.clientName && <span>· {t.clientName}</span>}
                  {!t.assignedToMe && t.teamName && <span>· with {t.teamName}</span>}
                  {t.dueAt && <span>· due {new Date(t.dueAt).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}</span>}
                  {t.plannedMinutesSoFar > 0 && <span>· {hours(t.plannedMinutesSoFar)} planned before</span>}
                </div>
              </div>
              {canPlan && (
                <button type="button" onClick={() => setPlanning({ ticketId: t.ticketId, reference: t.reference, title: t.title })}
                  className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90"><CalendarPlus size={14} /> Add to plan</button>
              )}
            </li>
          ))}
        </ul>
      )}
      {planning && <PlanWorkDialog target={planning} personId={viewerId} viewerId={viewerId} onClose={() => setPlanning(null)} onSaved={() => setPlanning(null)} />}
      {internal && <InternalWorkDialog viewerId={viewerId} onClose={() => setInternal(false)} onSaved={() => setInternal(false)} />}
    </section>
  );
}

// ---- On a ticket --------------------------------------------------------------------------------

/** What is planned on this ticket, for the people the viewer may see, and a way to plan it. Staff only. */
export function TicketPlanPanel({ ticketId, reference, title, viewerId, canPlan }: { ticketId: string; reference: string; title: string; viewerId: string | null; canPlan: boolean }) {
  const { data: rows, error } = useQuery({ queryKey: ['ticket-plan', ticketId], queryFn: () => api.ticketPlan(ticketId), retry: false });
  const [planning, setPlanning] = useState(false);
  const live = (rows ?? []).filter((a) => a.status === 1);
  return (
    <section aria-labelledby="ticket-plan-heading" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      <div className="flex items-center justify-between gap-2 px-4 py-3">
        <h2 id="ticket-plan-heading" className="text-sm font-semibold">Planned work</h2>
        {canPlan && viewerId && (
          <button type="button" onClick={() => setPlanning(true)} className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1 text-xs font-medium hover:bg-[var(--bg)]"><CalendarPlus size={13} /> Plan this work</button>
        )}
      </div>
      {error && <p role="alert" className="px-4 pb-3 text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>}
      {rows && live.length === 0 && <p className="px-4 pb-3 text-xs text-[var(--muted)]">Not in anyone&rsquo;s plan yet.</p>}
      {live.length > 0 && (
        <ul className="divide-y divide-[var(--border)] border-t border-[var(--border)]">
          {live.map((a) => (
            <li key={a.id} className="px-4 py-2 text-sm">
              <div className="flex flex-wrap items-center gap-x-2">
                <Link href={`/dashboard/workforce/people/${a.appUserId}?tab=plan&date=${dateIn(a.startsAt, a.timeZone)}`} className="font-medium hover:underline">{a.personName}</Link>
                <span className="tabular-nums">{fmtDay(dateIn(a.startsAt, a.timeZone))} {fmtSlot({ start: a.startsAt, end: a.endsAt }, a.timeZone)}</span>
                <span className="text-xs text-[var(--muted)]">· {hours(a.plannedMinutes)}</span>
              </div>
              <div className="flex flex-wrap gap-1 pt-0.5 text-[11px] text-[var(--muted)]">
                <span>{whoPlanned(a, viewerId)}</span>{a.isFixed && <span>· Fixed</span>}{a.note && <span>· {a.note}</span>}
              </div>
            </li>
          ))}
        </ul>
      )}
      {planning && viewerId && <PlanWorkDialog target={{ ticketId, reference, title }} personId={viewerId} viewerId={viewerId} onClose={() => setPlanning(false)} onSaved={() => setPlanning(false)} />}
    </section>
  );
}
