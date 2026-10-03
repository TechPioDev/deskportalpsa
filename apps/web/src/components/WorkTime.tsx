'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, Pause, Play, Square, Timer, X } from 'lucide-react';
import { api, ApiError, ActiveWorkProblemSchema, type ActiveWorkSwitch, type WorkSession } from '@/lib/api';
import { useDialog } from '@/components/WorkforcePlan';
import { isStaffPermissions } from '@/lib/staff';

/**
 * The clock on a piece of work, as the SERVER holds it. The browser never counts time of its own:
 * it reads the running session (closed seconds + when the open segment began) and draws the clock
 * from that, so a refresh, a second tab or another device all show the same clock. State changes
 * (start, pause, resume, stop) are the only requests; nothing is sent every second.
 */
const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm outline-none focus:border-brand disabled:opacity-60';
const btn = 'inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-50';

export const PAUSE_REASONS: Record<number, string> = { 0: 'Paused', 1: 'Waiting on client', 2: 'Waiting on vendor', 3: 'Waiting on reboot', 4: 'Waiting on third party', 9: 'Paused' };

/** Active seconds right now, from the server's timestamps and this device's clock. Display only. */
export function elapsedSeconds(s: WorkSession, nowMs = Date.now()): number {
  return s.activeSeconds + (s.runningSince ? Math.max(0, Math.floor((nowMs - Date.parse(s.runningSince)) / 1000)) : 0);
}
export const fmtClock = (secs: number) => {
  const h = Math.floor(secs / 3600); const m = Math.floor((secs % 3600) / 60); const s = secs % 60;
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
};
/** "1h 05m" for a figure; seconds never shown in a total. */
export const fmtDuration = (secs: number) => {
  const m = Math.round(secs / 60);
  if (m === 0 && secs > 0) return '<1m';
  return m < 60 ? `${m}m` : `${Math.floor(m / 60)}h${m % 60 ? ` ${String(m % 60).padStart(2, '0')}m` : ''}`;
};

/** A tick once a second while something runs, so clocks redraw; nothing is fetched by it. */
export function useTicking(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    setNow(Date.now());
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, [active]);
  return now;
}

type Conflict = { message: string; current: WorkSession; onPause: () => void; onStop: () => void };
type Ctx = {
  sessions: WorkSession[]; current: WorkSession | null; loading: boolean; enabled: boolean;
  start: (ticketId: string, allocationId?: string | null) => Promise<WorkSession | null>;
  pause: (s: WorkSession, reason?: number) => Promise<void>;
  resume: (s: WorkSession) => Promise<void>;
  /** Opens the stop dialog for a session. */
  stop: (s: WorkSession) => void;
  refresh: () => Promise<unknown>;
  error: string | null;
};
const WorkCtx = createContext<Ctx | null>(null);

export function WorkTimeProvider({ children }: { children: React.ReactNode }) {
  const qc = useQueryClient();
  // Staff with the module on and schedule.view: a client account never asks, and never sees the widget.
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const enabled = !!me?.userId && me.features?.workforce === true && isStaffPermissions(me.permissions) && me.permissions.includes('schedule.view');
  const { data, isLoading, refetch } = useQuery({ queryKey: ['work', 'active'], queryFn: api.activeWork, enabled, retry: false, staleTime: 15_000, refetchOnWindowFocus: true });
  const sessions = useMemo(() => data ?? [], [data]);
  const current = sessions.find((s) => s.status === 1) ?? null;
  const [conflict, setConflict] = useState<Conflict | null>(null);
  const [stopping, setStopping] = useState<WorkSession | null>(null);
  const [error, setError] = useState<string | null>(null);
  const refresh = useCallback(() => Promise.all([qc.invalidateQueries({ queryKey: ['work'] }), qc.invalidateQueries({ queryKey: ['my-day'] }), qc.invalidateQueries({ queryKey: ['team-today'] }), qc.invalidateQueries({ queryKey: ['time-entries'] }), qc.invalidateQueries({ queryKey: ['ticket'] })]), [qc]);

  /** A 409 naming the running work becomes the choice dialog; anything else is a notice. */
  const handle = useCallback((err: unknown, retry: (choice: ActiveWorkSwitch, currentId: string) => Promise<unknown>) => {
    if (err instanceof ApiError && err.status === 409) {
      const parsed = ActiveWorkProblemSchema.safeParse(err.payload);
      if (parsed.success) {
        const id = parsed.data.current.id;
        const after = (p: Promise<unknown>) => p.then(refresh).catch((e) => { setError((e as Error).message); refresh(); });
        setConflict({ message: err.message, current: parsed.data.current, onPause: () => { setConflict(null); after(retry(1, id)); }, onStop: () => { setConflict(null); after(retry(2, id)); } });
        return;
      }
    }
    // Anything else (a stale version, a rule): say so and read the server's state again.
    setError((err as Error).message);
    refresh();
  }, [refresh]);

  const start = useCallback(async (ticketId: string, allocationId?: string | null) => {
    setError(null);
    try {
      const s = await api.startWork({ ticketId, allocationId: allocationId ?? null });
      await refresh();
      return s;
    } catch (err) {
      handle(err, (choice, currentId) => api.startWork({ ticketId, allocationId: allocationId ?? null, switch: choice, currentId }));
      return null;
    }
  }, [handle, refresh]);
  const pause = useCallback(async (s: WorkSession, reason = 0) => {
    setError(null);
    try { await api.pauseWork(s.id, { version: s.version, pauseReason: reason }); await refresh(); }
    catch (err) { setError((err as Error).message); await refresh(); }
  }, [refresh]);
  const resume = useCallback(async (s: WorkSession) => {
    setError(null);
    try { await api.resumeWork(s.id, { version: s.version }); await refresh(); }
    catch (err) { handle(err, (choice, currentId) => api.resumeWork(s.id, { version: s.version, switch: choice, currentId })); }
  }, [handle, refresh]);
  const stop = useCallback((s: WorkSession) => setStopping(s), []);

  return (
    <WorkCtx.Provider value={{ sessions, current, loading: isLoading, enabled, start, pause, resume, stop, refresh: refetch, error }}>
      {children}
      {conflict && <SwitchWorkDialog conflict={conflict} onClose={() => setConflict(null)} />}
      {stopping && <StopWorkDialog session={stopping} onClose={() => setStopping(null)} onStopped={() => { setStopping(null); refresh(); }} />}
    </WorkCtx.Provider>
  );
}

export function useWorkTime(): Ctx {
  const c = useContext(WorkCtx);
  if (!c) throw new Error('useWorkTime must be used within WorkTimeProvider');
  return c;
}

/** "You already have active work on …": return, pause it and start, or stop it and start. Nothing stops without the person saying so. */
function SwitchWorkDialog({ conflict, onClose }: { conflict: Conflict; onClose: () => void }) {
  useDialog(onClose);
  const now = useTicking(true);
  return (
    <div role="dialog" aria-modal="true" aria-label="Work already running" className="fixed inset-0 z-50 flex items-start justify-center bg-black/40 p-4 pt-[14vh]" onClick={onClose}>
      <div className="w-full max-w-md space-y-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl" onClick={(e) => e.stopPropagation()}>
        <h2 className="flex items-center gap-2 text-sm font-semibold"><AlertTriangle size={16} className="text-amber-600" aria-hidden="true" /> {conflict.message}</h2>
        <p className="text-sm"><span className="font-mono text-xs text-[var(--muted)]">{conflict.current.reference}</span> {conflict.current.title ?? 'Work you cannot open'} · running <span className="tabular-nums">{fmtClock(elapsedSeconds(conflict.current, now))}</span></p>
        <p className="text-xs text-[var(--muted)]">One clock runs at a time. Pausing keeps its time for later; stopping logs its time now.</p>
        <div className="flex flex-wrap justify-end gap-2">
          <button type="button" onClick={onClose} className={btn}>Return</button>
          <button type="button" onClick={conflict.onPause} className={btn}><Pause size={14} /> Pause current and start new</button>
          <button type="button" onClick={conflict.onStop} className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90"><Square size={14} /> Stop current and start new</button>
        </div>
      </div>
    </div>
  );
}

/** Stop the clock: review the time, add a note, say whether it is billable, then it becomes a time entry (or is thrown away). */
export function StopWorkDialog({ session: s, onClose, onStopped }: { session: WorkSession; onClose: () => void; onStopped: (s: WorkSession) => void }) {
  useDialog(onClose);
  const now = useTicking(s.status === 1);
  const seconds = elapsedSeconds(s, now);
  const [note, setNote] = useState('');
  const [billable, setBillable] = useState(true);
  const [workType, setWorkType] = useState('');
  const { data: opts } = useQuery({ queryKey: ['time-options', s.ticketId], queryFn: () => api.ticketTimeOptions(s.ticketId), enabled: s.ticketVisible, retry: false });
  const stop = useMutation({
    mutationFn: (discard: boolean) => api.stopWork(s.id, { version: s.version, note: note.trim() || null, billable, workType: workType || null, discard }),
    onSuccess: (row) => onStopped(row),
  });
  const underMinute = seconds < 60;
  return (
    <div role="dialog" aria-modal="true" aria-label="Stop work" className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/40 p-4 pt-[10vh]" onClick={onClose}>
      <form className="w-full max-w-md space-y-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl" onClick={(e) => e.stopPropagation()}
        onSubmit={(e) => { e.preventDefault(); stop.mutate(false); }}>
        <div className="flex items-start justify-between gap-3">
          <div>
            <h2 className="text-sm font-semibold">Stop work on {s.reference ?? 'this ticket'}</h2>
            <p className="text-xs text-[var(--muted)]">{s.title ?? 'Work you cannot open'}{s.clientName ? ` · ${s.clientName}` : ''}</p>
          </div>
          <button type="button" aria-label="Close" onClick={onClose} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={16} /></button>
        </div>
        <p className="text-2xl font-semibold tabular-nums" aria-live="polite">{fmtClock(seconds)}</p>
        <p className="text-xs text-[var(--muted)]">{underMinute ? 'Under a minute: nothing is logged when you stop.' : `${fmtDuration(seconds)} of actual work becomes a time entry on the ticket. The ticket's status does not change.`}{s.outsideSchedule ? ' Started outside your working hours.' : ''}</p>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Work note (goes with the time entry; never client-visible by itself)
          <textarea value={note} maxLength={2000} rows={3} onChange={(e) => setNote(e.target.value)} aria-label="Work note" className={`w-full ${field}`} placeholder="Reviewed firewall logs and found repeated WAN negotiation failures." />
        </label>
        <div className="flex flex-wrap items-center gap-3">
          <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={billable} onChange={(e) => setBillable(e.target.checked)} aria-label="Billable" /> Billable</label>
          {opts && opts.workTypes.length > 0 && (
            <select value={workType} onChange={(e) => setWorkType(e.target.value)} aria-label="Work type" className={field}>
              <option value="">Work type (default)</option>{opts.workTypes.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          )}
        </div>
        {stop.error && <p role="alert" className="text-xs text-red-600 dark:text-red-400">{(stop.error as Error).message}</p>}
        <div className="flex flex-wrap justify-end gap-2">
          <button type="button" onClick={onClose} className={btn}>Keep running</button>
          <button type="button" disabled={stop.isPending} onClick={() => { if (window.confirm('Throw this time away? Nothing will be logged.')) stop.mutate(true); }} className={`${btn} text-red-700 dark:text-red-300`}>Discard</button>
          <button type="submit" disabled={stop.isPending} className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50"><Square size={14} /> {stop.isPending ? 'Stopping…' : underMinute ? 'Stop' : 'Stop and log time'}</button>
        </div>
      </form>
    </div>
  );
}

/** The buttons for one session or one ticket: Start / Pause / Resume / Stop. */
export function WorkControls({ session, ticketId, allocationId, compact = false, canStart = true }: { session: WorkSession | null; ticketId: string; allocationId?: string | null; compact?: boolean; canStart?: boolean }) {
  const work = useWorkTime();
  const cls = compact ? `${btn} px-2 py-1 text-xs` : btn;
  if (session?.status === 1 && session.canControl) return (
    <span className="inline-flex gap-1">
      <button type="button" onClick={() => work.pause(session)} aria-label={`Pause ${session.reference ?? 'work'}`} className={cls}><Pause size={13} /> Pause</button>
      <button type="button" onClick={() => work.stop(session)} aria-label={`Stop ${session.reference ?? 'work'}`} className={`${cls} border-brand/60 text-brand`}><Square size={13} /> Stop</button>
    </span>
  );
  if (session?.status === 2 && session.canControl) return (
    <span className="inline-flex gap-1">
      <button type="button" onClick={() => work.resume(session)} aria-label={`Resume ${session.reference ?? 'work'}`} className={`${cls} border-brand/60 text-brand`}><Play size={13} /> Resume</button>
      <button type="button" onClick={() => work.stop(session)} aria-label={`Stop ${session.reference ?? 'work'}`} className={cls}><Square size={13} /> Stop</button>
    </span>
  );
  if (!canStart) return null;
  return <button type="button" onClick={() => work.start(ticketId, allocationId)} aria-label="Start work" className={`${cls} border-brand/60 text-brand`}><Play size={13} /> Start work</button>;
}

/** The header indicator: what is running, for how long, and Pause / Stop. Quiet when nothing runs. */
export function ActiveWorkWidget() {
  const work = useWorkTime();
  const running = work.current;
  const paused = work.sessions.filter((s) => s.status === 2);
  const now = useTicking(!!running);
  if (!work.enabled) return null;
  if (!running && paused.length === 0) return (
    <Link href="/dashboard/workforce/my-day" className="hidden items-center gap-1.5 rounded-lg px-2.5 py-2 text-sm text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)] sm:inline-flex" title="My day"><Timer size={16} /> <span className="hidden md:inline">My day</span></Link>
  );
  const s = running ?? paused[0]!;
  return (
    <div role="status" aria-label="Current work" className={`flex items-center gap-2 rounded-lg border px-2 py-1 text-sm ${running ? 'border-brand/50 bg-brand/10' : 'border-amber-400/60 bg-amber-50 dark:bg-amber-950/40'}`}>
      <Link href="/dashboard/workforce/my-day" className="flex min-w-0 items-center gap-2 hover:underline" title={s.title ?? undefined}>
        <Timer size={14} className={running ? 'text-brand' : 'text-amber-700 dark:text-amber-300'} aria-hidden="true" />
        <span className="hidden max-w-[10rem] truncate font-mono text-xs sm:inline">{s.reference ?? 'Work'}</span>
        <span className="tabular-nums font-semibold">{fmtClock(elapsedSeconds(s, now))}</span>
        {!running && <span className="hidden text-[11px] text-amber-800 dark:text-amber-200 md:inline">{PAUSE_REASONS[s.pauseReason] ?? 'Paused'}</span>}
      </Link>
      {s.canControl && (
        <span className="flex gap-0.5">
          {running ? <button type="button" onClick={() => work.pause(s)} aria-label="Pause work" className="rounded p-1 hover:bg-[var(--bg)]"><Pause size={13} /></button>
            : <button type="button" onClick={() => work.resume(s)} aria-label="Resume work" className="rounded p-1 hover:bg-[var(--bg)]"><Play size={13} /></button>}
          <button type="button" onClick={() => work.stop(s)} aria-label="Stop work" className="rounded p-1 hover:bg-[var(--bg)]"><Square size={13} /></button>
        </span>
      )}
    </div>
  );
}
