'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  AlertTriangle, ArrowRightLeft, CalendarPlus, Check, ChevronLeft, ChevronRight, ExternalLink, GripVertical, ListTodo, Lock, Pencil, RefreshCw, Search, Trash2, UserSearch, X,
} from 'lucide-react';
import {
  api, ApiError, type DayCapacity, type TeamPlan, type TeamPlanPerson, type TeamUnscheduledWork, type WorkAllocation, type ConflictProblem,
} from '@/lib/api';
import { hours } from '@/components/Workforce';
import { addDays, fmtDay, fmtSlot, fmtTime, GroupAndSkillFilters } from '@/components/WorkforceCapacity';
import { CONFLICT_NAMES, ConfirmWorkDialog, conflictProblem, PlanWorkDialog, ReassignDialog, TentativeChip, TicketPickerDialog, useDialog, whoPlanned, type PlanTarget } from '@/components/WorkforcePlan';
import { type Axis, buildAxis, clock, dateOf, lanes, minutesBetween, place, atPointer, snap } from '@/lib/timeline';

const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm outline-none focus:border-brand disabled:opacity-60';
const btn = 'inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-50';
const chip = 'inline-flex items-center gap-1 whitespace-nowrap rounded-full px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide';
const SNAP_MINUTES = 15;
const ROW_HEIGHT = 68;
const ZOOM: Record<Zoom, number> = { compact: 48, standard: 80, detailed: 136 };
type Zoom = 'compact' | 'standard' | 'detailed';
type View = 'day' | 'week';

// ---- small hooks ------------------------------------------------------------------------------------

/** Phones get cards, not a timeline. */
function useNarrow(): boolean {
  const [narrow, setNarrow] = useState(false);
  useEffect(() => {
    const mq = window.matchMedia('(max-width: 767px)');
    const on = () => setNarrow(mq.matches);
    on();
    mq.addEventListener('change', on);
    return () => mq.removeEventListener('change', on);
  }, []);
  return narrow;
}

/** The current minute, for the "now" line; the board is not re-rendered more often than that. */
function useMinute(enabled: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!enabled) return;
    setNow(Date.now());
    const id = window.setInterval(() => setNow(Date.now()), 60_000);
    return () => window.clearInterval(id);
  }, [enabled]);
  return now;
}

/** A team this size is drawn whole; beyond it only the rows in and around the viewport are rendered. */
const WINDOW_FROM = 60;

/** Only the rows in and around the viewport are rendered once a team is large: five hundred people is a scroll, not a wait. */
function useRowWindow(count: number, container: React.RefObject<HTMLDivElement | null>) {
  const [range, setRange] = useState({ start: 0, end: Math.min(count, WINDOW_FROM) });
  const recompute = useCallback(() => {
    const el = container.current;
    if (!el) return;
    if (count <= WINDOW_FROM) { setRange({ start: 0, end: count }); return; }
    const first = Math.floor(el.scrollTop / ROW_HEIGHT);
    const visible = Math.ceil(el.clientHeight / ROW_HEIGHT) + 1;
    setRange({ start: Math.max(0, first - 8), end: Math.min(count, first + visible + 8) });
  }, [count, container]);
  useEffect(() => { recompute(); }, [recompute]);
  useEffect(() => {
    const el = container.current;
    if (!el) return;
    el.addEventListener('scroll', recompute, { passive: true });
    const ro = new ResizeObserver(recompute);
    ro.observe(el);
    return () => { el.removeEventListener('scroll', recompute); ro.disconnect(); };
  }, [recompute, container]);
  return range;
}

// ---- helpers --------------------------------------------------------------------------------------

/** Where a piece of work comes from, read off its reference: the board's number, or a provider's. */
export function sourceOf(reference: string | null | undefined): { short: string; name: string } {
  if (!reference) return { short: '?', name: 'Work' };
  if (reference.startsWith('Autotask ')) return { short: 'AT', name: 'Autotask' };
  if (reference.startsWith('ConnectWise ')) return { short: 'CW', name: 'ConnectWise' };
  return { short: 'Board', name: 'Team board' };
}

type Pending = Record<string, { appUserId: string; startsAt: string; endsAt: string }>;
type Drag = { kind: 'move'; allocation: WorkAllocation; grabMs: number } | { kind: 'new'; work: TeamUnscheduledWork };
type Notice = { tone: 'error' | 'info'; text: string } | null;

function loadPrefs(): { view?: View; zoom?: Zoom; teamId?: string; departmentId?: string; queueOpen?: boolean } {
  try { return JSON.parse(window.localStorage.getItem('pio.schedule') ?? '{}'); } catch { return {}; }
}
function savePrefs(p: Record<string, unknown>) {
  try { window.localStorage.setItem('pio.schedule', JSON.stringify({ ...loadPrefs(), ...p })); } catch { /* storage unavailable */ }
}

// ---- the workspace -------------------------------------------------------------------------------

export function TeamScheduleWorkspace({ viewerId, initialDate, initialView, canPlanAtAll }: { viewerId: string; initialDate: string | null; initialView: View | null; canPlanAtAll: boolean }) {
  const qc = useQueryClient();
  const narrow = useNarrow();
  const prefs = useMemo(loadPrefs, []);
  const [date, setDate] = useState<string | null>(initialDate);
  const [view, setView] = useState<View>(initialView ?? prefs.view ?? 'day');
  const [zoom, setZoom] = useState<Zoom>(prefs.zoom ?? 'standard');
  const [f, setF] = useState({ teamId: prefs.teamId ?? '', departmentId: prefs.departmentId ?? '', skillIds: [] as string[], matchAll: true });
  const [search, setSearch] = useState('');
  const [blockFilter, setBlockFilter] = useState({ source: '', client: '', status: '' });
  const [queueOpen, setQueueOpen] = useState(prefs.queueOpen ?? true);
  const [finding, setFinding] = useState(false);
  const [selected, setSelected] = useState<WorkAllocation | null>(null);
  const [dialog, setDialog] = useState<
    | { kind: 'pick'; personId: string; personName: string; startIso: string | null }
    | { kind: 'plan'; target: PlanTarget; personId: string; startIso: string | null; minutes: number | null }
    | { kind: 'move'; allocation: WorkAllocation }
    | { kind: 'reassign'; allocation: WorkAllocation }
    | { kind: 'confirm'; allocation: WorkAllocation }
    | null>(null);
  const [pending, setPending] = useState<Pending>({});
  const [notice, setNotice] = useState<Notice>(null);
  const [conflict, setConflict] = useState<{ problem: ConflictProblem; message: string; retry: (reason: string) => void } | null>(null);
  const [highlight, setHighlight] = useState<string | null>(null);
  useEffect(() => { savePrefs({ view, zoom, teamId: f.teamId, departmentId: f.departmentId, queueOpen }); }, [view, zoom, f.teamId, f.departmentId, queueOpen]);
  useEffect(() => {
    if (!highlight) return;
    const id = window.setTimeout(() => setHighlight(null), 4000);
    return () => window.clearTimeout(id);
  }, [highlight]);

  const [known, setKnown] = useState<string | null>(null);
  // The week needs a first day: the chosen date, or the server's "today" once it has answered.
  const anchor = date ?? known;
  const from = anchor;
  const to = anchor && view === 'week' ? addDays(anchor, 6) : anchor;
  const { data: plan, isLoading, isFetching, error, refetch } = useQuery({
    queryKey: ['plan', 'team', from, to, f.teamId, f.departmentId, f.skillIds, f.matchAll],
    queryFn: () => api.teamPlan({ from, to, teamId: f.teamId, departmentId: f.departmentId, skills: f.skillIds, matchAll: f.matchAll }),
    retry: false, placeholderData: (prev) => prev,
  });
  useEffect(() => { if (plan && !known) setKnown(plan.today); }, [plan, known]);
  const shown = anchor ?? plan?.today ?? null;
  const { data: queue, error: queueError, isLoading: queueLoading } = useQuery({
    queryKey: ['plan', 'team-unscheduled', f.teamId, f.departmentId, f.skillIds, f.matchAll],
    queryFn: () => api.teamUnscheduled({ teamId: f.teamId, departmentId: f.departmentId, skills: f.skillIds, matchAll: f.matchAll }),
    retry: false, enabled: queueOpen,
  });

  const refresh = useCallback(() => Promise.all([qc.invalidateQueries({ queryKey: ['plan'] }), qc.invalidateQueries({ queryKey: ['capacity'] }), qc.invalidateQueries({ queryKey: ['ticket-plan'] })]), [qc]);

  // What the screen shows: the server's rows with any move still in flight laid over them.
  const people = useMemo<TeamPlanPerson[]>(() => {
    if (!plan) return [];
    const all = plan.people.flatMap((p) => p.allocations).map((a) => (pending[a.id] ? { ...a, ...pending[a.id] } : a));
    const byPerson = new Map<string, WorkAllocation[]>();
    for (const a of all) byPerson.set(a.appUserId, [...(byPerson.get(a.appUserId) ?? []), a]);
    const q = search.trim().toLowerCase();
    return plan.people
      .map((p) => {
        let allocations = byPerson.get(p.appUserId) ?? [];
        if (blockFilter.source) allocations = allocations.filter((a) => sourceOf(a.reference).name === blockFilter.source);
        if (blockFilter.client) allocations = allocations.filter((a) => a.clientName === blockFilter.client);
        if (blockFilter.status === 'open') allocations = allocations.filter((a) => !a.ticketFinished);
        if (blockFilter.status === 'finished') allocations = allocations.filter((a) => a.ticketFinished);
        return { ...p, allocations };
      })
      .filter((p) => !q || p.displayName.toLowerCase().includes(q) || p.teams.some((t) => t.toLowerCase().includes(q))
        || p.allocations.some((a) => (a.reference ?? '').toLowerCase().includes(q) || (a.title ?? '').toLowerCase().includes(q) || (a.clientName ?? '').toLowerCase().includes(q)));
  }, [plan, pending, search, blockFilter]);
  const clients = useMemo(() => Array.from(new Set((plan?.people ?? []).flatMap((p) => p.allocations).map((a) => a.clientName).filter((c): c is string => !!c))).sort(), [plan]);

  // ---- writes from the board: a drop or a resize is a proposal; the server decides ----
  const save = useMutation({
    mutationFn: async ({ allocation: a, personId, startMs, endMs, reason }: { allocation: WorkAllocation; personId: string; startMs: number; endMs: number; reason?: string }) => {
      const start = new Date(startMs).toISOString();
      const end = new Date(endMs).toISOString();
      return personId !== a.appUserId
        ? api.reassignPlannedWork(a.id, { appUserId: personId, version: a.version, start, end, overrideReason: reason ?? null })
        : api.updatePlannedWork(a.id, { start, end, version: a.version, overrideReason: reason ?? null });
    },
    onMutate: ({ allocation: a, personId, startMs, endMs }) => {
      setPending((p) => ({ ...p, [a.id]: { appUserId: personId, startsAt: new Date(startMs).toISOString(), endsAt: new Date(endMs).toISOString() } }));
      setNotice(null);
    },
    onError: (err, vars) => {
      // Back where it was, and told why. A clash the viewer may override asks for the reason.
      setPending((p) => { const { [vars.allocation.id]: _gone, ...rest } = p; return rest; });
      const problem = conflictProblem(err);
      if (problem?.canOverride && problem.overrideAllowedForCaller) {
        setConflict({ problem, message: (err as Error).message, retry: (reason) => save.mutate({ ...vars, reason }) });
      } else if (problem?.stale) {
        setNotice({ tone: 'error', text: 'This plan changed since the screen loaded. Reloading it.' });
        refresh();
      } else setNotice({ tone: 'error', text: (err as Error).message });
    },
    onSuccess: async () => { setConflict(null); await refresh(); },
    onSettled: (_r, _e, vars) => { setPending((p) => { const { [vars.allocation.id]: _gone, ...rest } = p; return rest; }); },
  });
  const remove = useMutation({
    mutationFn: (a: WorkAllocation) => api.cancelPlannedWork(a.id),
    onSuccess: () => { setSelected(null); refresh(); },
    onError: (err) => setNotice({ tone: 'error', text: (err as Error).message }),
  });
  // Committed work back to pencil: it stops taking confirmed capacity. Schedulers only; the server says so otherwise.
  const pencil = useMutation({
    mutationFn: (a: WorkAllocation) => api.makeTentative(a.id, { version: a.version }),
    onSuccess: () => { setSelected(null); refresh(); setNotice({ tone: 'info', text: 'Pencilled in. It no longer takes confirmed capacity.' }); },
    onError: (err) => setNotice({ tone: 'error', text: (err as Error).message }),
  });

  const onDropMove = useCallback((a: WorkAllocation, personId: string, startMs: number) => {
    const length = Date.parse(a.endsAt) - Date.parse(a.startsAt);
    if (personId === a.appUserId && startMs === Date.parse(a.startsAt)) return;
    if (personId !== a.appUserId && !(plan?.people.find((p) => p.appUserId === personId)?.canPlan)) { setNotice({ tone: 'error', text: 'You cannot plan work for that person.' }); return; }
    if (personId !== a.appUserId && !a.canReassign) { setNotice({ tone: 'error', text: 'Only someone who schedules others can give work to someone else.' }); return; }
    if (personId === a.appUserId && !a.canEdit) { setNotice({ tone: 'error', text: a.isFixed ? 'This work is fixed in place. Ask whoever planned it to move it.' : 'You cannot move this work.' }); return; }
    save.mutate({ allocation: a, personId, startMs, endMs: startMs + length });
  }, [save, plan]);
  const onResize = useCallback((a: WorkAllocation, endMs: number) => {
    if (endMs === Date.parse(a.endsAt) || !a.canEdit) return;
    save.mutate({ allocation: a, personId: a.appUserId, startMs: Date.parse(a.startsAt), endMs });
  }, [save]);
  const onDropNew = useCallback((work: TeamUnscheduledWork, personId: string, startMs: number) => {
    setDialog({ kind: 'plan', target: { ticketId: work.ticketId, reference: work.reference, title: work.title }, personId, startIso: new Date(startMs).toISOString(), minutes: 60 });
  }, []);
  const onFree = useCallback((p: TeamPlanPerson, startIso: string) => {
    setDialog({ kind: 'pick', personId: p.appUserId, personName: p.displayName, startIso });
  }, []);

  const totals = plan ? { usable: plan.usableMinutes, planned: plan.confirmedMinutes, tentative: plan.tentativeMinutes, free: plan.remainingConfirmedMinutes, projected: plan.projectedRemainingMinutes, blocks: plan.allocationCount } : null;

  return (
    <div className="space-y-3">
      {/* ---- toolbar ---- */}
      <section aria-label="Schedule controls" className="flex flex-wrap items-center gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3">
        <button type="button" disabled={!shown} onClick={() => shown && setDate(addDays(shown, view === 'week' ? -7 : -1))} aria-label={view === 'week' ? 'Previous week' : 'Previous day'} className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)] disabled:opacity-50"><ChevronLeft size={16} /></button>
        <button type="button" onClick={() => setDate(null)} className={btn}>Today</button>
        <button type="button" disabled={!shown} onClick={() => shown && setDate(addDays(shown, view === 'week' ? 7 : 1))} aria-label={view === 'week' ? 'Next week' : 'Next day'} className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)] disabled:opacity-50"><ChevronRight size={16} /></button>
        <input type="date" value={shown ?? ''} onChange={(e) => e.target.value && setDate(e.target.value)} aria-label="Date" className={field} />
        <div role="group" aria-label="View" className="inline-flex overflow-hidden rounded-lg border border-[var(--border)]">
          {(['day', 'week'] as View[]).map((v) => (
            <button key={v} type="button" aria-pressed={view === v} onClick={() => setView(v)} className={`px-3 py-1.5 text-sm font-medium ${view === v ? 'bg-brand text-brand-fg' : 'hover:bg-[var(--bg)]'}`}>{v === 'day' ? 'Day' : 'Week'}</button>
          ))}
        </div>
        {view === 'day' && !narrow && (
          <select value={zoom} onChange={(e) => setZoom(e.target.value as Zoom)} aria-label="Time scale" className={field}>
            <option value="compact">Compact</option><option value="standard">Standard</option><option value="detailed">Detailed</option>
          </select>
        )}
        <span className="text-xs text-[var(--muted)]">Times in {plan?.timeZone ?? '…'}</span>
        <span className="ml-auto flex flex-wrap items-center gap-2">
          <button type="button" onClick={() => setFinding(true)} className={btn}><UserSearch size={14} /> Find available technician</button>
          <button type="button" aria-pressed={queueOpen} onClick={() => setQueueOpen((v) => !v)} className={btn}><ListTodo size={14} /> Unscheduled work{queue ? ` (${queue.length})` : ''}</button>
          <Link href="/dashboard/workforce/queue" className={btn}>Planning queue</Link>
          <button type="button" onClick={() => refetch()} aria-label="Refresh" className="rounded-lg border border-[var(--border)] p-1.5 hover:bg-[var(--bg)]"><RefreshCw size={14} className={isFetching ? 'animate-spin' : ''} /></button>
        </span>
      </section>
      <section aria-label="Filters" className="flex flex-wrap items-center gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3">
        <GroupAndSkillFilters {...f} onChange={(patch) => setF((v) => ({ ...v, ...patch }))} />
        <label className="relative">
          <Search size={14} className="pointer-events-none absolute left-2 top-2.5 text-[var(--muted)]" aria-hidden="true" />
          <input value={search} onChange={(e) => setSearch(e.target.value)} aria-label="Search people and work" placeholder="Person, ticket or client" className={`${field} w-56 pl-7`} />
        </label>
        <select value={blockFilter.source} onChange={(e) => setBlockFilter((b) => ({ ...b, source: e.target.value }))} aria-label="Work source" className={field}>
          <option value="">Any source</option><option value="Team board">Team board</option><option value="Autotask">Autotask</option><option value="ConnectWise">ConnectWise</option>
        </select>
        <select value={blockFilter.client} onChange={(e) => setBlockFilter((b) => ({ ...b, client: e.target.value }))} aria-label="Client" className={field}>
          <option value="">Any client</option>{clients.map((c) => <option key={c} value={c}>{c}</option>)}
        </select>
        <select value={blockFilter.status} onChange={(e) => setBlockFilter((b) => ({ ...b, status: e.target.value }))} aria-label="Work status" className={field}>
          <option value="">Any status</option><option value="open">Open work</option><option value="finished">Finished tickets</option>
        </select>
      </section>

      {notice && (
        <p role="alert" className={`flex items-center gap-2 rounded-lg border px-3 py-2 text-sm ${notice.tone === 'error' ? 'border-red-300 bg-red-50 text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-200' : 'border-[var(--border)] bg-[var(--surface)]'}`}>
          <AlertTriangle size={14} aria-hidden="true" /> <span className="flex-1">{notice.text}</span>
          <button type="button" onClick={() => { setNotice(null); refresh(); }} className="text-xs font-medium underline">Reload</button>
          <button type="button" onClick={() => setNotice(null)} aria-label="Dismiss" className="rounded p-0.5 hover:bg-black/5"><X size={14} /></button>
        </p>
      )}
      {error && (
        <p role="alert" className="flex items-center gap-2 rounded-lg border border-red-300 bg-red-50 px-3 py-2 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-200">
          <span className="flex-1">{(error as Error).message}</span>
          {!(error instanceof ApiError && error.status === 403) && <button type="button" onClick={() => refetch()} className="text-xs font-medium underline">Try again</button>}
        </p>
      )}

      {/* ---- summary ---- */}
      {totals && (
        <dl className="grid grid-cols-2 gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3 text-sm sm:grid-cols-6" aria-label="Capacity summary">
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Capacity</dt><dd className="text-lg font-semibold tabular-nums">{hours(totals.usable)}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Planned</dt><dd className="text-lg font-semibold tabular-nums">{hours(totals.planned)}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Tentative</dt><dd className="text-lg font-semibold tabular-nums text-sky-700 dark:text-sky-300">{hours(totals.tentative)}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Free</dt><dd className="text-lg font-semibold tabular-nums text-green-700 dark:text-green-300">{hours(totals.free)}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Free if confirmed</dt><dd className="text-lg font-semibold tabular-nums">{hours(totals.projected)}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Pieces of work</dt><dd className="text-lg font-semibold tabular-nums">{totals.blocks}</dd></div>
        </dl>
      )}

      {/* ---- board + queue ---- */}
      <div className={`grid gap-3 ${queueOpen && !narrow ? 'lg:grid-cols-[minmax(0,1fr)_20rem]' : ''}`}>
        <div className="min-w-0">
          {isLoading && !plan && <SkeletonRows />}
          {plan && shown && people.length === 0 && (
            <div className="rounded-xl border border-dashed border-[var(--border)] px-4 py-10 text-center text-sm text-[var(--muted)]">
              {plan.people.length === 0 ? 'Nobody matches these filters. Choose another team or clear the skills.' : 'Nothing matches the search.'}
            </div>
          )}
          {plan && shown && people.length > 0 && (
            narrow
              ? <PersonCards people={people} date={shown} view={view} viewerId={viewerId} onOpen={setSelected} onPickDay={(d) => { setView('day'); setDate(d); }} />
              : view === 'day'
                ? <DayBoard plan={plan} people={people} date={shown} pxPerHour={ZOOM[zoom]} highlight={highlight} viewerId={viewerId}
                    onOpen={setSelected} onFree={onFree} onDropMove={onDropMove} onDropNew={onDropNew} onResize={onResize} />
                : <WeekGrid people={people} viewerId={viewerId} onPickDay={(d) => { setView('day'); setDate(d); }} />
          )}
        </div>
        {queueOpen && (
          <UnscheduledQueue rows={queue} loading={queueLoading} error={queueError} narrow={narrow} people={plan?.people ?? []} canPlan={canPlanAtAll} viewerId={viewerId} today={plan?.today ?? null} timeZone={plan?.timeZone ?? 'UTC'}
            onPlan={(w, personId) => setDialog({ kind: 'plan', target: { ticketId: w.ticketId, reference: w.reference, title: w.title }, personId, startIso: null, minutes: null })} />
        )}
      </div>

      {/* ---- panels and dialogs ---- */}
      {selected && (
        <AllocationDrawer allocation={people.flatMap((p) => p.allocations).find((a) => a.id === selected.id) ?? selected} viewerId={viewerId} onClose={() => setSelected(null)}
          onMove={(a) => setDialog({ kind: 'move', allocation: a })} onReassign={(a) => setDialog({ kind: 'reassign', allocation: a })}
          onRemove={(a) => { if (window.confirm(`Take ${a.reference ?? 'this work'} out of the plan? The ticket itself is not changed.`)) remove.mutate(a); }}
          onConfirm={(a) => setDialog({ kind: 'confirm', allocation: a })}
          onPencil={(a) => { if (window.confirm(`Pencil ${a.reference ?? 'this work'} back in? It stops taking confirmed capacity until it is confirmed again.`)) pencil.mutate(a); }} />
      )}
      {finding && plan && shown && (
        <FindPanel date={shown} filters={f} people={plan.people} onClose={() => setFinding(false)}
          onView={(personId) => { setFinding(false); setView('day'); setHighlight(personId); }}
          onSchedule={(personId, personName, startIso) => { setFinding(false); setDialog({ kind: 'pick', personId, personName, startIso }); }} />
      )}
      {conflict && <DragConflictDialog conflict={conflict} onClose={() => setConflict(null)} />}
      {dialog?.kind === 'pick' && <TicketPickerDialog personName={dialog.personName} onClose={() => setDialog(null)} onPick={(target) => setDialog({ kind: 'plan', target, personId: dialog.personId, startIso: dialog.startIso, minutes: null })} />}
      {dialog?.kind === 'plan' && <PlanWorkDialog target={dialog.target} personId={dialog.personId} lockPerson={!!dialog.startIso} initialStart={dialog.startIso} initialMinutes={dialog.minutes} viewerId={viewerId} onClose={() => setDialog(null)} onSaved={() => { setDialog(null); refresh(); }} />}
      {dialog?.kind === 'move' && <PlanWorkDialog allocation={dialog.allocation} personId={dialog.allocation.appUserId} viewerId={viewerId} onClose={() => setDialog(null)} onSaved={() => { setDialog(null); setSelected(null); refresh(); }} />}
      {dialog?.kind === 'reassign' && <ReassignDialog allocation={dialog.allocation} onClose={() => setDialog(null)} onSaved={() => { setDialog(null); setSelected(null); refresh(); }} />}
      {dialog?.kind === 'confirm' && <ConfirmWorkDialog allocation={dialog.allocation} onClose={() => setDialog(null)} onSaved={() => { setDialog(null); setSelected(null); refresh(); }} />}
    </div>
  );
}

function SkeletonRows() {
  return (
    <div aria-busy="true" aria-label="Loading the schedule" className="divide-y divide-[var(--border)] rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      {Array.from({ length: 6 }).map((_, i) => (
        <div key={i} className="flex items-center gap-4 px-4" style={{ height: ROW_HEIGHT }}>
          <div className="h-4 w-36 animate-pulse rounded bg-[var(--bg)]" />
          <div className="h-6 flex-1 animate-pulse rounded bg-[var(--bg)]" />
        </div>
      ))}
    </div>
  );
}

// ---- the day board --------------------------------------------------------------------------------

type BoardProps = {
  plan: TeamPlan; people: TeamPlanPerson[]; date: string; pxPerHour: number; highlight: string | null; viewerId: string;
  onOpen: (a: WorkAllocation) => void; onFree: (p: TeamPlanPerson, startIso: string) => void;
  onDropMove: (a: WorkAllocation, personId: string, startMs: number) => void; onDropNew: (w: TeamUnscheduledWork, personId: string, startMs: number) => void;
  onResize: (a: WorkAllocation, endMs: number) => void;
};

/**
 * People down the side, the day across the top, in the organization's zone. Every row has its own
 * working window, breaks and time away; a night shift runs past midnight on the same axis. Blocks
 * are buttons (the drawer has every action), and can also be dragged, resized and dropped on
 * another row; what a drop asks for is only a proposal until the server has said yes.
 */
function DayBoard({ plan, people, date, pxPerHour, highlight, viewerId, onOpen, onFree, onDropMove, onDropNew, onResize }: BoardProps) {
  const tz = plan.timeZone;
  const axis = useMemo(() => buildAxis(date, tz, people.map((p) => {
    const day = p.days.find((d) => d.date === date);
    return {
      windowStart: day?.windowStart, windowEnd: day?.windowEnd,
      spans: [...p.allocations.map((a) => ({ start: a.startsAt, end: a.endsAt })), ...(day?.exceptions ?? []).filter((e) => e.startsAt && e.endsAt).map((e) => ({ start: e.startsAt!, end: e.endsAt! }))],
    };
  })), [people, date, tz]);
  const width = axis.hours.length * pxPerHour;
  const scroller = useRef<HTMLDivElement>(null);
  const range = useRowWindow(people.length, scroller);
  const isToday = plan.today === date;
  const now = useMinute(isToday);
  const nowPos = isToday && now >= axis.start && now <= axis.end ? place(axis, now, now).left : null;
  const drag = useRef<Drag | null>(null);
  const [hover, setHover] = useState<{ personId: string; startMs: number; endMs: number } | null>(null);
  const hoverTo = (next: { personId: string; startMs: number; endMs: number } | null) =>
    setHover((h) => (h?.personId === next?.personId && h?.startMs === next?.startMs && h?.endMs === next?.endMs ? h : next));
  useEffect(() => {
    // A drag that ends over something else, or whose source row was re-rendered away, still resets.
    const reset = () => { drag.current = null; externalDrag.current = null; setHover(null); };
    window.addEventListener('dragend', reset);
    window.addEventListener('drop', reset);
    return () => { window.removeEventListener('dragend', reset); window.removeEventListener('drop', reset); };
  }, []);

  useEffect(() => {
    if (!highlight || !scroller.current) return;
    const i = people.findIndex((p) => p.appUserId === highlight);
    if (i >= 0) scroller.current.scrollTo({ top: Math.max(0, i * ROW_HEIGHT - 80), behavior: 'smooth' });
  }, [highlight, people]);

  const trackPos = (e: React.DragEvent | React.PointerEvent, track: HTMLElement) => {
    const rect = track.getBoundingClientRect();
    return atPointer(axis, e.clientX - rect.left, rect.width, SNAP_MINUTES);
  };

  return (
    <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      <div ref={scroller} className="max-h-[70vh] overflow-auto" role="grid" aria-label={`Team schedule for ${fmtDay(date, { weekday: 'long', day: 'numeric', month: 'long' })}`} aria-rowcount={people.length + 1}>
        <div style={{ minWidth: 224 + width }}>
          {/* hour header */}
          <div role="row" aria-rowindex={1} className="sticky top-0 z-20 flex border-b border-[var(--border)] bg-[var(--surface)]">
            <div role="columnheader" className="sticky left-0 z-30 w-56 shrink-0 border-r border-[var(--border)] bg-[var(--surface)] px-3 py-1.5 text-[11px] uppercase tracking-wide text-[var(--muted)]">Person</div>
            <div role="columnheader" className="relative h-7" style={{ width }} aria-label={`Hours from ${clock(axis.start, tz)} to ${clock(axis.end, tz)}`}>
              {axis.hours.map((h, i) => (
                <span key={`${h.at}-${i}`} className="absolute top-1.5 border-l border-[var(--border)] pl-1 text-[11px] tabular-nums text-[var(--muted)]" style={{ left: `${place(axis, h.at, h.at).left}%` }}>{h.label}</span>
              ))}
              {nowPos !== null && <span role="img" className="absolute top-0 h-full border-l-2 border-red-500" style={{ left: `${nowPos}%` }} aria-label="Now" />}
            </div>
          </div>
          {/* rows, windowed */}
          <div style={{ height: people.length * ROW_HEIGHT, position: 'relative' }}>
            {people.slice(range.start, range.end).map((p, i) => {
              const index = range.start + i;
              const day = p.days.find((d) => d.date === date);
              return (
                <PersonRow key={p.appUserId} person={p} day={day} axis={axis} width={width} top={index * ROW_HEIGHT} rowIndex={index + 2}
                  highlighted={highlight === p.appUserId} viewerId={viewerId} nowPos={nowPos}
                  hover={hover?.personId === p.appUserId ? hover : null}
                  onOpen={onOpen}
                  onFree={(startIso) => onFree(p, startIso)}
                  onDragStartBlock={(a, e) => {
                    // Where in the block it was picked up, so it keeps that grip under the pointer.
                    const track = (e.currentTarget as HTMLElement).closest('[role="gridcell"]') as HTMLElement;
                    drag.current = { kind: 'move', allocation: a, grabMs: trackPos(e, track) - Date.parse(a.startsAt) };
                    e.dataTransfer.effectAllowed = 'move';
                    e.dataTransfer.setData('text/plain', a.reference ?? 'work');
                  }}
                  onDragOverTrack={(e) => {
                    const d = drag.current ?? readExternalDrag(e);
                    // A row the viewer may not plan for takes no drop at all.
                    if (!d || !p.canPlan) return;
                    e.preventDefault();
                    e.dataTransfer.dropEffect = d.kind === 'new' ? 'copy' : 'move';
                    const at = trackPos(e, e.currentTarget as HTMLElement);
                    const startMs = d.kind === 'move' ? snap(at - d.grabMs, SNAP_MINUTES) : at;
                    const length = d.kind === 'move' ? Date.parse(d.allocation.endsAt) - Date.parse(d.allocation.startsAt) : 60 * 60_000;
                    hoverTo({ personId: p.appUserId, startMs, endMs: startMs + length });
                  }}
                  onDragLeaveTrack={() => hoverTo(null)}
                  onDropTrack={(e) => {
                    e.preventDefault();
                    const d = drag.current ?? readExternalDrag(e);
                    drag.current = null;
                    setHover(null);
                    if (!d || !p.canPlan) return;
                    const at = trackPos(e, e.currentTarget as HTMLElement);
                    if (d.kind === 'move') onDropMove(d.allocation, p.appUserId, snap(at - d.grabMs, SNAP_MINUTES));
                    else onDropNew(d.work, p.appUserId, at);
                  }}
                  onDragEnd={() => { drag.current = null; setHover(null); }}
                  onResize={onResize} />
              );
            })}
          </div>
        </div>
      </div>
      <p className="border-t border-[var(--border)] px-3 py-1.5 text-[11px] text-[var(--muted)]">
        Drag a block to move it or give it to another row; drag its right edge to change its planned time; drag work in from Unscheduled work. Every change is checked by the server before it is kept. The same actions are in each block&rsquo;s panel.
      </p>
    </div>
  );
}

/** A drag that started in the unscheduled list of this page: carried on the event, since refs do not cross components. */
function readExternalDrag(e: React.DragEvent): Drag | null {
  if (!e.dataTransfer.types.includes('application/x-pio-unscheduled')) return null;
  const raw = e.dataTransfer.getData('application/x-pio-unscheduled');
  if (raw) { try { return { kind: 'new', work: JSON.parse(raw) as TeamUnscheduledWork }; } catch { return null; } }
  // During dragover the payload is not readable; the item this page is dragging stands in. A drag
  // from another window carries the type but no item here, and is not accepted.
  return externalDrag.current ? { kind: 'new', work: externalDrag.current } : null;
}
/** The item being dragged out of the unscheduled list (set by the list, read by the board). */
const externalDrag: { current: TeamUnscheduledWork | null } = { current: null };

type RowProps = {
  person: TeamPlanPerson; day: DayCapacity | undefined; axis: Axis; width: number; top: number; rowIndex: number; highlighted: boolean; viewerId: string; nowPos: number | null;
  hover: { startMs: number; endMs: number } | null;
  onOpen: (a: WorkAllocation) => void; onFree: (startIso: string) => void;
  onDragStartBlock: (a: WorkAllocation, e: React.DragEvent) => void; onDragOverTrack: (e: React.DragEvent) => void; onDragLeaveTrack: () => void; onDropTrack: (e: React.DragEvent) => void; onDragEnd: () => void;
  onResize: (a: WorkAllocation, endMs: number) => void;
};

function PersonRow({ person: p, day, axis, width, top, rowIndex, highlighted, viewerId, nowPos, hover, onOpen, onFree, onDragStartBlock, onDragOverTrack, onDragLeaveTrack, onDropTrack, onDragEnd, onResize }: RowProps) {
  const tz = axis.timeZone;
  const laid = useMemo(() => lanes(p.allocations.map((a) => ({ start: a.startsAt, end: a.endsAt, a }))), [p.allocations]);
  const laneCount = laid[0]?.lanes ?? 1;
  const state = !day ? 'No schedule' : day.unavailableAllDay ? 'Away all day' : !day.isWorkingDay ? 'Not working' : null;
  const over = day ? day.confirmedMinutes > day.usableMinutes : false;
  const [resizing, setResizing] = useState<{ id: string; endMs: number } | null>(null);
  const trackRef = useRef<HTMLDivElement>(null);
  const resize = useRef<{ a: WorkAllocation; rect: DOMRect; endMs: number } | null>(null);

  // The handle captures the pointer, so every move and the release reach it wherever the pointer goes.
  const startResize = (a: WorkAllocation, e: React.PointerEvent) => {
    e.preventDefault(); e.stopPropagation();
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
    resize.current = { a, rect: trackRef.current!.getBoundingClientRect(), endMs: Date.parse(a.endsAt) };
    setResizing({ id: a.id, endMs: Date.parse(a.endsAt) });
  };
  const moveResize = (e: React.PointerEvent) => {
    const r = resize.current;
    if (!r) return;
    const startMs = Date.parse(r.a.startsAt);
    r.endMs = Math.max(startMs + SNAP_MINUTES * 60_000, atPointer(axis, e.clientX - r.rect.left, r.rect.width, SNAP_MINUTES));
    setResizing({ id: r.a.id, endMs: r.endMs });
  };
  const endResize = () => {
    const r = resize.current;
    resize.current = null;
    setResizing(null);
    if (r) onResize(r.a, r.endMs);
  };

  return (
    <div role="row" aria-rowindex={rowIndex} className={`absolute left-0 right-0 flex border-b border-[var(--border)] ${highlighted ? 'bg-brand/10' : ''}`} style={{ top, height: ROW_HEIGHT }}>
      <div role="rowheader" className="sticky left-0 z-10 w-56 shrink-0 border-r border-[var(--border)] bg-[var(--surface)] px-3 py-1.5">
        <div className="flex items-center gap-2">
          <span aria-hidden="true" className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-[var(--bg)] text-[11px] font-semibold">{initials(p.displayName)}</span>
          <div className="min-w-0">
            <Link href={`/dashboard/workforce/people/${p.appUserId}?tab=plan&date=${day?.date ?? ''}`} className="block truncate text-sm font-medium hover:underline" title={p.displayName}>{p.displayName}</Link>
            <div className="truncate text-[11px] text-[var(--muted)]">{[p.teams.join(', '), p.isSchedulable ? '' : 'Not offered for work', p.timeZone !== tz ? p.timeZone : ''].filter(Boolean).join(' · ')}</div>
          </div>
        </div>
        {day && !state && <CapacityBar day={day} compact />}
        {state && <div className="pt-1 text-[11px] text-[var(--muted)]">{state}</div>}
      </div>
      <div ref={trackRef} role="gridcell" className="relative" style={{ width }} aria-label={`${p.displayName}: ${state ?? `${hours(day!.confirmedMinutes)} planned of ${hours(day!.usableMinutes)}`}`}
        onDragOver={onDragOverTrack} onDragLeave={onDragLeaveTrack} onDrop={onDropTrack}>
        {/* the working window */}
        {day?.windowStart && day.windowEnd && (() => { const w = place(axis, day.windowStart, day.windowEnd); return <div className="absolute inset-y-0 bg-[var(--bg)]" style={{ left: `${w.left}%`, width: `${w.width}%` }} aria-hidden="true" />; })()}
        {/* free windows: click to schedule */}
        {day?.freeSlots.map((s) => {
          const w = place(axis, s.start, s.end);
          const m = minutesBetween(s.start, s.end);
          const text = w.width > 7 ? <span className="absolute inset-x-1 top-0.5 truncate text-left">{w.width > 12 ? `Available · ${hours(m)}` : hours(m)}</span> : null;
          const cls = 'absolute inset-y-2 rounded border border-dashed border-green-400/70 bg-green-50/70 text-[10px] text-green-800 dark:bg-green-950/30 dark:text-green-200';
          return p.canPlan ? (
            <button key={s.start} type="button" onClick={() => onFree(s.start)} title={`Available ${fmtSlot(s, tz)} · ${hours(m)}. Schedule work here.`}
              aria-label={`Available ${fmtSlot(s, tz)}, ${hours(m)}. Schedule work for ${p.displayName}`}
              className={`${cls} hover:bg-green-100 dark:hover:bg-green-900/40`} style={{ left: `${w.left}%`, width: `${w.width}%` }}>{text}</button>
          ) : (
            <div key={s.start} role="img" aria-label={`Available ${fmtSlot(s, tz)}, ${hours(m)}`} className={cls} style={{ left: `${w.left}%`, width: `${w.width}%` }}>{text}</div>
          );
        })}
        {/* breaks */}
        {day?.breaks.map((b) => { const w = place(axis, b.start, b.end); return (
          <div key={b.start} className="absolute inset-y-2 rounded bg-[repeating-linear-gradient(135deg,transparent,transparent_4px,var(--border)_4px,var(--border)_6px)] text-[10px] text-[var(--muted)]" style={{ left: `${w.left}%`, width: `${w.width}%` }} title={`Break ${fmtSlot(b, tz)}`} aria-label={`Break ${fmtSlot(b, tz)}`}>
            {w.width > 5 && <span className="absolute inset-x-1 top-0.5 truncate">Break</span>}
          </div>
        ); })}
        {/* time away */}
        {day?.exceptions.filter((e) => e.kind === 1 && e.startsAt && e.endsAt).map((e) => { const w = place(axis, e.startsAt!, e.endsAt!); return (
          <div key={e.id} className="absolute inset-y-2 rounded border border-amber-300 bg-[repeating-linear-gradient(135deg,rgba(245,158,11,0.18),rgba(245,158,11,0.18)_4px,transparent_4px,transparent_8px)] text-[10px] text-amber-900 dark:text-amber-200" style={{ left: `${w.left}%`, width: `${w.width}%` }}
            title={`Unavailable ${fmtSlot({ start: e.startsAt!, end: e.endsAt! }, tz)}${e.note ? ` · ${e.note}` : ''}`} aria-label={`Unavailable ${fmtSlot({ start: e.startsAt!, end: e.endsAt! }, tz)}`}>
            {w.width > 5 && <span className="absolute inset-x-1 top-0.5 truncate">Unavailable</span>}
          </div>
        ); })}
        {/* planned work */}
        {laid.map(({ block, lane }) => {
          const a = block.a;
          const endMs = resizing?.id === a.id ? resizing.endMs : Date.parse(a.endsAt);
          const w = place(axis, a.startsAt, endMs);
          const laneH = (ROW_HEIGHT - 12) / laneCount;
          return (
            <Block key={a.id} a={a} viewerId={viewerId} axisZone={tz} left={w.left} width={w.width} top={6 + lane * laneH} height={laneH - 2} pxWidth={(w.width / 100) * width}
              onOpen={() => onOpen(a)} onDragStart={(e) => onDragStartBlock(a, e)} onDragEnd={onDragEnd}
              resize={a.canEdit ? { start: (e) => startResize(a, e), move: moveResize, end: endResize } : undefined} />
          );
        })}
        {/* the drop preview */}
        {hover && (() => { const w = place(axis, hover.startMs, hover.endMs); return (
          <div aria-hidden="true" className="pointer-events-none absolute inset-y-1 rounded border-2 border-dashed border-brand bg-brand/10 px-1 text-[10px] font-medium text-brand" style={{ left: `${w.left}%`, width: `${w.width}%` }}>
            {clock(hover.startMs, tz)}–{clock(hover.endMs, tz)}
          </div>
        ); })()}
        {nowPos !== null && <span aria-hidden="true" className="pointer-events-none absolute inset-y-0 border-l-2 border-red-500/70" style={{ left: `${nowPos}%` }} />}
        {over && <span className={`${chip} absolute right-1 top-1 bg-red-100 text-red-800 dark:bg-red-950 dark:text-red-200`}>Over capacity</span>}
      </div>
    </div>
  );
}

const initials = (name: string) => name.split(/\s+/).filter(Boolean).slice(0, 2).map((s) => s[0]!.toUpperCase()).join('');

function Block({ a, viewerId, axisZone, left, width, top, height, pxWidth, onOpen, onDragStart, onDragEnd, resize }: {
  a: WorkAllocation; viewerId: string; axisZone: string; left: number; width: number; top: number; height: number; pxWidth: number;
  onOpen: () => void; onDragStart: (e: React.DragEvent) => void; onDragEnd: () => void;
  resize?: { start: (e: React.PointerEvent) => void; move: (e: React.PointerEvent) => void; end: () => void };
}) {
  const src = sourceOf(a.reference);
  const movable = a.canEdit || a.canReassign;
  const tentative = a.status === 2;
  const tone = a.ticketFinished
    ? 'border-[var(--border)] bg-[var(--bg)] text-[var(--muted)] line-through'
    : tentative ? 'border-dashed border-sky-500 bg-sky-50/70 text-sky-950 dark:bg-sky-950/40 dark:text-sky-100'
    : a.isFixed ? 'border-amber-400 bg-amber-50 text-amber-950 dark:bg-amber-950/50 dark:text-amber-100' : 'border-brand/60 bg-brand/15 text-[var(--fg)]';
  const label = `${a.reference ?? 'Work'}${a.title ? `, ${a.title}` : ''}, ${fmtSlot({ start: a.startsAt, end: a.endsAt }, a.timeZone)}${a.timeZone !== axisZone ? ` (${a.timeZone})` : ''}, ${hours(a.plannedMinutes)}${tentative ? ', tentative' : ''}${a.isFixed ? ', fixed' : ''}${a.ticketFinished ? ', ticket finished' : ''}`;
  return (
    <div className="absolute" style={{ left: `${left}%`, width: `${width}%`, top, height }}>
      <button type="button" draggable={movable} onDragStart={onDragStart} onDragEnd={onDragEnd} onClick={onOpen} aria-label={label} title={label}
        className={`group flex h-full w-full items-start gap-1 overflow-hidden rounded border px-1.5 py-0.5 text-left text-[11px] leading-tight ${tone} ${movable ? 'cursor-grab active:cursor-grabbing' : ''} focus:outline-none focus:ring-2 focus:ring-brand`}>
        {movable && pxWidth > 60 && <GripVertical size={10} className="mt-0.5 shrink-0 opacity-40" aria-hidden="true" />}
        <span className="min-w-0 flex-1">
          <span className="flex items-center gap-1">
            {a.isFixed && <Lock size={10} className="shrink-0" aria-hidden="true" />}
            <span className="truncate font-semibold">{pxWidth > 48 ? a.reference ?? 'Work' : src.short}</span>
            {pxWidth > 110 && <span className={`${chip} border border-current/30`} aria-label={src.name} title={src.name}>{src.short}</span>}
          </span>
          {pxWidth > 90 && height > 24 && <span className="block truncate">{a.ticketVisible ? a.title : 'Work you cannot open'}</span>}
          {pxWidth > 150 && height > 38 && (a.clientName || a.plannedMinutes) && <span className="block truncate text-[10px] opacity-80">{[a.clientName, hours(a.plannedMinutes), tentative ? 'tentative' : ''].filter(Boolean).join(' · ')}</span>}
        </span>
      </button>
      {resize && (
        <div role="separator" aria-label={`Resize ${a.reference ?? 'work'}`} aria-orientation="vertical"
          onPointerDown={resize.start} onPointerMove={resize.move} onPointerUp={resize.end} onPointerCancel={resize.end}
          className="absolute inset-y-0 right-0 w-2 cursor-ew-resize rounded-r hover:bg-brand/40" />
      )}
      <span className="sr-only">{whoPlanned(a, viewerId)}</span>
    </div>
  );
}

/** Planned against usable, with time away and anything over the top drawn as what it is. */
function CapacityBar({ day, compact = false }: { day: DayCapacity; compact?: boolean }) {
  const scale = Math.max(day.usableMinutes, day.confirmedMinutes, 1);
  const planned = Math.min(day.confirmedMinutes, day.usableMinutes);
  const overBy = Math.max(0, day.confirmedMinutes - day.usableMinutes);
  // Pencilled-in work sits inside the free time: it is drawn there, and the free figure stays confirmed-only.
  const tentative = Math.min(day.tentativeMinutes, Math.max(0, day.usableMinutes - day.confirmedMinutes));
  const free = Math.max(0, day.usableMinutes - day.confirmedMinutes - tentative);
  return (
    <div className={compact ? 'pt-1' : 'space-y-1'}>
      <div className="flex h-1.5 w-full overflow-hidden rounded-full bg-[var(--bg)]" role="img" aria-label={`${hours(day.confirmedMinutes)} planned of ${hours(day.usableMinutes)} usable${tentative ? `, ${hours(day.tentativeMinutes)} tentative` : ''}${overBy ? `, ${hours(overBy)} over` : ''}`}>
        <span className="bg-brand" style={{ width: `${(planned / scale) * 100}%` }} />
        {tentative > 0 && <span className="bg-sky-400/70" style={{ width: `${(tentative / scale) * 100}%` }} />}
        <span className="bg-green-400/70" style={{ width: `${(free / scale) * 100}%` }} />
        {overBy > 0 && <span className="bg-red-500" style={{ width: `${(overBy / scale) * 100}%` }} />}
      </div>
      <div className="text-[11px] tabular-nums text-[var(--muted)]">
        {hours(day.confirmedMinutes)} planned / {hours(day.usableMinutes)}{overBy ? <span className="text-red-600 dark:text-red-400"> · {hours(overBy)} over</span> : ` · ${hours(day.remainingConfirmedMinutes)} free`}{day.tentativeMinutes > 0 && !overBy ? <span className="text-sky-700 dark:text-sky-300"> · {hours(day.tentativeMinutes)} tentative</span> : null}
      </div>
    </div>
  );
}

// ---- the week grid --------------------------------------------------------------------------------

/** Each person's work by the date it starts in their own zone, computed once per render of the list. */
function byDate(people: TeamPlanPerson[]): Map<string, Map<string, WorkAllocation[]>> {
  const out = new Map<string, Map<string, WorkAllocation[]>>();
  for (const p of people) {
    const m = new Map<string, WorkAllocation[]>();
    for (const a of [...p.allocations].sort((x, y) => Date.parse(x.startsAt) - Date.parse(y.startsAt))) {
      const d = dateOf(Date.parse(a.startsAt), p.timeZone);
      m.set(d, [...(m.get(d) ?? []), a]);
    }
    out.set(p.appUserId, m);
  }
  return out;
}

function WeekGrid({ people, viewerId, onPickDay }: { people: TeamPlanPerson[]; viewerId: string; onPickDay: (date: string) => void }) {
  const dates = people[0]?.days.map((d) => d.date) ?? [];
  const grouped = useMemo(() => byDate(people), [people]);
  return (
    <div className="overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      <table className="w-full min-w-[900px] text-sm">
        <caption className="sr-only">Planned and usable time per person per day</caption>
        <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
          <tr>
            <th scope="col" className="sticky left-0 bg-[var(--surface)] px-3 py-2 font-medium">Person</th>
            {dates.map((d) => <th key={d} scope="col" className="px-2 py-2 font-medium"><button type="button" onClick={() => onPickDay(d)} className="hover:underline">{fmtDay(d)}</button></th>)}
          </tr>
        </thead>
        <tbody className="divide-y divide-[var(--border)]">
          {people.map((p) => (
            <tr key={p.appUserId}>
              <th scope="row" className="sticky left-0 bg-[var(--surface)] px-3 py-2 text-left font-medium">
                <Link href={`/dashboard/workforce/people/${p.appUserId}?tab=plan`} className="hover:underline">{p.displayName}</Link>
                <div className="text-[11px] font-normal text-[var(--muted)]">{p.teams.join(', ')}</div>
              </th>
              {p.days.map((d) => {
                const items = grouped.get(p.appUserId)?.get(d.date) ?? [];
                const state = d.unavailableAllDay ? 'Away' : !d.isWorkingDay ? 'Off' : null;
                return (
                  <td key={d.date} className="px-2 py-1.5 align-top">
                    <button type="button" onClick={() => onPickDay(d.date)} className="w-full rounded-lg border border-transparent p-1 text-left hover:border-[var(--border)] hover:bg-[var(--bg)]"
                      aria-label={`${p.displayName}, ${fmtDay(d.date)}: ${state ?? `${hours(d.confirmedMinutes)} planned of ${hours(d.usableMinutes)}, ${items.length} pieces of work`}. Open the day`}>
                      {state ? <span className="text-xs text-[var(--muted)]">{state}</span> : <CapacityBar day={d} compact />}
                      {items.slice(0, 3).map((a) => <span key={a.id} className={`block truncate text-[11px] ${a.status === 2 ? 'text-sky-700 dark:text-sky-300' : ''}`} title={`${whoPlanned(a, viewerId)}${a.status === 2 ? ' · tentative' : ''}`}>{a.isFixed ? '🔒 ' : ''}{a.reference} · {hours(a.plannedMinutes)}{a.status === 2 ? ' · tentative' : ''}</span>)}
                      {items.length > 3 && <span className="block text-[11px] text-[var(--muted)]">+{items.length - 3} more</span>}
                    </button>
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ---- phones: cards, not a timeline ------------------------------------------------------------------

function PersonCards({ people, date, view, viewerId, onOpen, onPickDay }: { people: TeamPlanPerson[]; date: string; view: View; viewerId: string; onOpen: (a: WorkAllocation) => void; onPickDay: (d: string) => void }) {
  const grouped = useMemo(() => byDate(people), [people]);
  return (
    <ul aria-label="People" className="space-y-2">
      {people.map((p) => {
        const days = view === 'day' ? p.days.filter((d) => d.date === date) : p.days;
        return (
          <li key={p.appUserId} className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3">
            <div className="flex items-center justify-between gap-2">
              <Link href={`/dashboard/workforce/people/${p.appUserId}?tab=plan&date=${date}`} className="font-medium hover:underline">{p.displayName}</Link>
              <span className="text-[11px] text-[var(--muted)]">{p.teams.join(', ')}</span>
            </div>
            {days.map((d) => {
              const items = grouped.get(p.appUserId)?.get(d.date) ?? [];
              return (
                <div key={d.date} className="pt-2">
                  {view === 'week' && <button type="button" onClick={() => onPickDay(d.date)} className="text-xs font-medium hover:underline">{fmtDay(d.date)}</button>}
                  {d.unavailableAllDay ? <p className="text-xs text-[var(--muted)]">Away all day</p> : !d.isWorkingDay ? <p className="text-xs text-[var(--muted)]">Not working</p> : <CapacityBar day={d} compact />}
                  <ul className="pt-1">
                    {items.map((a) => (
                      <li key={a.id}>
                        <button type="button" onClick={() => onOpen(a)} className="flex w-full items-center gap-2 rounded px-1 py-1 text-left text-sm hover:bg-[var(--bg)]">
                          <span className="w-24 shrink-0 tabular-nums text-xs">{fmtSlot({ start: a.startsAt, end: a.endsAt }, p.timeZone)}</span>
                          <span className="min-w-0 flex-1 truncate">{a.reference} · {a.ticketVisible ? a.title : 'Work you cannot open'}</span>
                          {a.isFixed && <Lock size={12} aria-label="Fixed" />}
                        </button>
                      </li>
                    ))}
                    {items.length === 0 && d.isWorkingDay && !d.unavailableAllDay && <li className="px-1 text-xs text-[var(--muted)]">Nothing planned</li>}
                  </ul>
                </div>
              );
            })}
            <span className="sr-only">{p.allocations.map((a) => whoPlanned(a, viewerId)).join('. ')}</span>
          </li>
        );
      })}
    </ul>
  );
}

// ---- unscheduled work -----------------------------------------------------------------------------

function UnscheduledQueue({ rows, loading, error, narrow, people, canPlan, viewerId, today, timeZone, onPlan }: {
  rows: TeamUnscheduledWork[] | undefined; loading: boolean; error: unknown; narrow: boolean; people: TeamPlanPerson[]; canPlan: boolean;
  viewerId: string; today: string | null; timeZone: string;
  onPlan: (w: TeamUnscheduledWork, personId: string) => void;
}) {
  const [q, setQ] = useState('');
  const [only, setOnly] = useState<'' | 'held' | 'team' | 'overdue' | 'due-today'>('');
  const shown = (rows ?? []).filter((w) => {
    const text = `${w.reference} ${w.title} ${w.clientName ?? ''} ${w.holderName ?? ''}`.toLowerCase();
    if (q && !text.includes(q.toLowerCase())) return false;
    if (only === 'held' && !w.holderId) return false;
    if (only === 'team' && w.holderId) return false;
    if (only === 'overdue' && !(w.dueAt && Date.parse(w.dueAt) < Date.now())) return false;
    if (only === 'due-today' && !(w.dueAt && today && dateOf(Date.parse(w.dueAt), timeZone) === today)) return false;
    return true;
  });
  // Whose time: the holder when they are on the board and plannable, else the viewer, else nobody.
  const defaultPerson = (w: TeamUnscheduledWork) => {
    if (w.holderId && people.some((p) => p.appUserId === w.holderId && p.canPlan)) return w.holderId;
    return people.some((p) => p.appUserId === viewerId && p.canPlan) ? viewerId : null;
  };
  return (
    <aside aria-labelledby="queue-heading" className="flex max-h-[80vh] flex-col rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      <div className="space-y-2 border-b border-[var(--border)] p-3">
        <h2 id="queue-heading" className="text-sm font-semibold">Unscheduled work{rows ? ` (${rows.length})` : ''}</h2>
        <p className="text-[11px] text-[var(--muted)]">Open work these people hold, or that sits with their teams, in nobody&rsquo;s plan.{!narrow ? ' Drag one onto a row, or press Plan.' : ''}</p>
        <input value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search unscheduled work" placeholder="Search" className={`w-full ${field}`} />
        <select value={only} onChange={(e) => setOnly(e.target.value as typeof only)} aria-label="Show" className={`w-full ${field}`}>
          <option value="">Everything</option><option value="held">Held by someone</option><option value="team">With a team, nobody holds it</option><option value="due-today">Due today</option><option value="overdue">Overdue</option>
        </select>
      </div>
      {error ? <p role="alert" className="p-3 text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p> : null}
      {loading && <p className="p-3 text-sm text-[var(--muted)]">Loading…</p>}
      {rows && shown.length === 0 && <p className="p-3 text-sm text-[var(--muted)]">{rows.length === 0 ? 'Nothing is waiting: everything open is planned.' : 'Nothing matches.'}</p>}
      <ul aria-label="Unscheduled work" className="flex-1 divide-y divide-[var(--border)] overflow-y-auto">
        {shown.map((w) => {
          const src = sourceOf(w.reference);
          const person = defaultPerson(w);
          return (
            <li key={w.ticketId} draggable={canPlan && !narrow && !!person}
              onDragStart={(e) => { externalDrag.current = w; e.dataTransfer.setData('application/x-pio-unscheduled', JSON.stringify(w)); e.dataTransfer.setData('text/plain', w.reference); e.dataTransfer.effectAllowed = 'copyMove'; }}
              onDragEnd={() => { externalDrag.current = null; }}
              className={`px-3 py-2 text-sm ${canPlan && !narrow ? 'cursor-grab active:cursor-grabbing' : ''}`}>
              <div className="flex items-center gap-2">
                {canPlan && !narrow && <GripVertical size={12} className="shrink-0 opacity-40" aria-hidden="true" />}
                <span className="font-mono text-xs text-[var(--muted)]">{w.reference}</span>
                <span className={`${chip} border border-[var(--border)]`} title={src.name}>{src.short}</span>
                {w.dueAt && Date.parse(w.dueAt) < Date.now() && <span className={`${chip} bg-red-100 text-red-800 dark:bg-red-950 dark:text-red-200`}>Overdue</span>}
              </div>
              <Link href={`/dashboard/tickets/${w.ticketId}`} className="block truncate font-medium hover:underline">{w.title}</Link>
              <div className="flex flex-wrap items-center gap-x-2 text-[11px] text-[var(--muted)]">
                {w.clientName && <span>{w.clientName}</span>}
                <span>{w.holderName ? `Held by ${w.holderName}` : w.heldOutside ? `Held outside this group${w.teamName ? `, with ${w.teamName}` : ''}` : w.teamName ? `With ${w.teamName}` : 'Nobody holds it'}</span>
                {w.plannedMinutesSoFar > 0 && <span>{hours(w.plannedMinutesSoFar)} planned before</span>}
                {canPlan && person && <button type="button" onClick={() => onPlan(w, person)} className="ml-auto inline-flex items-center gap-1 rounded-md border border-[var(--border)] px-2 py-0.5 text-[11px] font-medium hover:bg-[var(--bg)]"><CalendarPlus size={11} /> Plan</button>}
              </div>
            </li>
          );
        })}
      </ul>
    </aside>
  );
}

// ---- the detail drawer ------------------------------------------------------------------------------

function AllocationDrawer({ allocation: a, viewerId, onClose, onMove, onReassign, onRemove, onConfirm, onPencil }: {
  allocation: WorkAllocation; viewerId: string; onClose: () => void; onMove: (a: WorkAllocation) => void; onReassign: (a: WorkAllocation) => void; onRemove: (a: WorkAllocation) => void;
  onConfirm: (a: WorkAllocation) => void; onPencil: (a: WorkAllocation) => void;
}) {
  useDialog(onClose);
  const src = sourceOf(a.reference);
  const row = (k: string, v: React.ReactNode) => <div className="flex justify-between gap-3 py-1 text-sm"><dt className="text-[var(--muted)]">{k}</dt><dd className="text-right">{v}</dd></div>;
  return (
    <div role="dialog" aria-modal="true" aria-label={`Planned work ${a.reference ?? ''}`} className="fixed inset-0 z-50 flex justify-end bg-black/30" onClick={onClose}>
      <div className="flex h-full w-full max-w-md flex-col gap-3 overflow-y-auto border-l border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="flex items-center gap-2"><span className="font-mono text-xs text-[var(--muted)]">{a.reference}</span><span className={`${chip} border border-[var(--border)]`}>{src.name}</span><TentativeChip a={a} />{a.isFixed && <span className={`${chip} bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-200`}><Lock size={10} /> Fixed</span>}{a.ticketFinished && <span className={`${chip} bg-[var(--bg)]`}>Ticket finished</span>}</div>
            <h2 className="pt-1 text-base font-semibold">{a.ticketVisible ? a.title : 'Work you cannot open'}</h2>
          </div>
          <button type="button" aria-label="Close" onClick={onClose} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={16} /></button>
        </div>
        <dl className="divide-y divide-[var(--border)] rounded-lg border border-[var(--border)] px-3">
          {row('Person', a.personName)}
          {row('Client', a.clientName ?? '—')}
          {row('Date', fmtDay(dateOf(Date.parse(a.startsAt), a.timeZone), { weekday: 'long', day: 'numeric', month: 'long' }))}
          {row('Time', `${fmtSlot({ start: a.startsAt, end: a.endsAt }, a.timeZone)} (${a.timeZone})`)}
          {row('Planned', `${hours(a.plannedMinutes)}${a.status === 2 ? ' · tentative, takes no confirmed capacity' : ''}`)}
          {row('Ticket status', a.ticketStatus ?? '—')}
          {row('Scheduled', `${whoPlanned(a, viewerId)}${a.isFixed ? ' · fixed in place' : ' · flexible'}`)}
          {a.note && row('Note', a.note)}
          {a.overrideReason && row('Override', `${a.overrideReason}${a.overriddenByName ? ` (${a.overriddenByName})` : ''}`)}
        </dl>
        <div className="flex flex-wrap gap-2">
          {a.ticketVisible && <Link href={`/dashboard/tickets/${a.ticketId}`} className={btn}><ExternalLink size={14} /> Open ticket</Link>}
          {a.canConfirm && <button type="button" onClick={() => onConfirm(a)} className={`${btn} border-sky-500 text-sky-800 dark:text-sky-200`}><Check size={14} /> Confirm</button>}
          {a.status === 1 && a.canReassign && !a.ticketFinished && <button type="button" onClick={() => onPencil(a)} className={btn}>Pencil in</button>}
          {a.canEdit && <button type="button" onClick={() => onMove(a)} className={btn}><Pencil size={14} /> Reschedule</button>}
          {a.canReassign && <button type="button" onClick={() => onReassign(a)} className={btn}><ArrowRightLeft size={14} /> Give to someone else</button>}
          {a.canCancel && <button type="button" onClick={() => onRemove(a)} className={`${btn} text-red-700 dark:text-red-300`}><Trash2 size={14} /> Remove from plan</button>}
          {!a.canEdit && !a.canCancel && !a.canReassign && <p className="text-xs text-[var(--muted)]">You cannot change this work. Ask whoever planned it.</p>}
        </div>
      </div>
    </div>
  );
}

// ---- a clash on a drop that the viewer may override ---------------------------------------------------

function DragConflictDialog({ conflict, onClose }: { conflict: { problem: ConflictProblem; message: string; retry: (reason: string) => void }; onClose: () => void }) {
  const [reason, setReason] = useState('');
  useDialog(onClose);
  return (
    <div role="dialog" aria-modal="true" aria-label="Conflict" className="fixed inset-0 z-50 flex items-start justify-center bg-black/40 p-4 pt-[14vh]" onClick={onClose}>
      <form className="w-full max-w-md space-y-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl" onClick={(e) => e.stopPropagation()}
        onSubmit={(e) => { e.preventDefault(); if (reason.trim().length >= 5) conflict.retry(reason.trim()); }}>
        <h2 className="flex items-center gap-2 text-sm font-semibold"><AlertTriangle size={16} className="text-amber-600" aria-hidden="true" /> Conflict detected</h2>
        <p className="text-sm">{conflict.message}</p>
        <ul className="list-disc space-y-0.5 pl-5 text-xs">
          {conflict.problem.conflicts.map((c, i) => <li key={i}>{CONFLICT_NAMES[c.type] ?? 'Conflict'}: {c.message}</li>)}
        </ul>
        <label className="block space-y-1 text-xs font-medium">
          Reason to override (kept with the work)
          <input autoFocus value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} aria-label="Override reason" placeholder="Approved after-hours client maintenance" className={`w-full ${field}`} />
        </label>
        <div className="flex justify-end gap-2">
          <button type="button" onClick={onClose} className={btn}>Cancel</button>
          <button type="submit" disabled={reason.trim().length < 5} className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">Override and save</button>
        </div>
      </form>
    </div>
  );
}

// ---- find available technician ------------------------------------------------------------------------

function FindPanel({ date, filters, people, onClose, onView, onSchedule }: {
  date: string; filters: { teamId: string; departmentId: string; skillIds: string[]; matchAll: boolean }; people: TeamPlanPerson[];
  onClose: () => void; onView: (personId: string) => void; onSchedule: (personId: string, personName: string, startIso: string) => void;
}) {
  const [duration, setDuration] = useState('60');
  const [earliest, setEarliest] = useState('');
  const [latest, setLatest] = useState('');
  const [run, setRun] = useState(0);
  const minutes = Number(duration);
  const { data, isFetching, error } = useQuery({
    queryKey: ['capacity', 'find', 'scheduler', date, minutes, earliest, latest, filters, run],
    queryFn: () => api.findAvailable({ from: date, duration: minutes, earliest: earliest || undefined, latest: latest || undefined, teamId: filters.teamId || undefined, departmentId: filters.departmentId || undefined, skills: filters.skillIds, matchAll: filters.matchAll }),
    enabled: run > 0 && minutes >= 5, retry: false, gcTime: 0,
  });
  const onBoard = new Set(people.map((p) => p.appUserId));
  useDialog(onClose);
  return (
    <div role="dialog" aria-modal="true" aria-label="Find available technician" className="fixed inset-0 z-50 flex justify-end bg-black/30" onClick={onClose}>
      <div className="flex h-full w-full max-w-md flex-col gap-3 overflow-y-auto border-l border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between">
          <div><h2 className="text-sm font-semibold">Find available technician</h2><p className="text-xs text-[var(--muted)]">Who has one continuous free slot on {fmtDay(date)}, within the team, department and skills filtered above. Facts, not a ranking; nothing is booked.</p></div>
          <button type="button" aria-label="Close" onClick={onClose} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={16} /></button>
        </div>
        <form role="search" aria-label="Search" className="grid grid-cols-3 gap-2" onSubmit={(e) => { e.preventDefault(); setRun((r) => r + 1); }}>
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Minutes<input type="number" min={5} max={720} step={5} value={duration} onChange={(e) => setDuration(e.target.value)} aria-label="Duration" className={`w-full ${field}`} /></label>
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Earliest<input type="time" value={earliest} onChange={(e) => setEarliest(e.target.value)} aria-label="Earliest" className={`w-full ${field}`} /></label>
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Latest<input type="time" value={latest} onChange={(e) => setLatest(e.target.value)} aria-label="Latest" className={`w-full ${field}`} /></label>
          <button type="submit" className="col-span-3 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90">Find</button>
        </form>
        {error && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>}
        {isFetching && <p className="text-sm text-[var(--muted)]">Searching…</p>}
        {data && (
          <section aria-label="Results" className="space-y-1">
            {data.matches.length === 0 && <p className="text-sm text-[var(--muted)]">Nobody has a free slot that long. Try a shorter duration, another date, or fewer skills.</p>}
            <ul className="divide-y divide-[var(--border)] rounded-lg border border-[var(--border)]">
              {data.matches.map((m) => (
                <li key={m.appUserId} className="space-y-1 px-3 py-2 text-sm">
                  <div className="flex items-center justify-between gap-2"><span className="font-medium">{m.displayName}</span><span className="text-xs text-[var(--muted)]">{hours(m.freeMinutes)} free in the window</span></div>
                  <div className="text-xs tabular-nums">First fit {fmtSlot(m.recommended, m.timeZone, date)}{m.matchingSkills.length > 0 ? ` · ${m.matchingSkills.map((s) => s.name).join(', ')} ✓` : ''}</div>
                  <div className="flex gap-2">
                    {onBoard.has(m.appUserId) && <button type="button" onClick={() => onView(m.appUserId)} className={`${btn} px-2 py-1 text-xs`}>View on timeline</button>}
                    <button type="button" onClick={() => onSchedule(m.appUserId, m.displayName, m.recommended.start)} className={`${btn} px-2 py-1 text-xs`}><CalendarPlus size={12} /> Schedule work</button>
                  </div>
                </li>
              ))}
            </ul>
          </section>
        )}
      </div>
    </div>
  );
}

export { fmtTime };
