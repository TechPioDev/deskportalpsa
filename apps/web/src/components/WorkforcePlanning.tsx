'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, CalendarRange, Check, ChevronDown, ChevronUp, Pencil, X } from 'lucide-react';
import { api, ApiError, PlanChangedSchema, type PlanningQueueItem, type PlanningRequirement, type PlanPreview } from '@/lib/api';
import { hours } from '@/components/Workforce';
import { fmtDay, fmtSlot, GroupAndSkillFilters } from '@/components/WorkforceCapacity';
import { CONFLICT_NAMES, conflictProblem, PlanWorkDialog, useDialog, type PlanTarget } from '@/components/WorkforcePlan';
import { clock, dateOf, wallToIso } from '@/lib/timeline';

const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm outline-none focus:border-brand disabled:opacity-60';
const btn = 'inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)] disabled:opacity-50';
const chip = 'inline-flex items-center gap-1 whitespace-nowrap rounded-full px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide';

export const REASONS: Record<number, string> = { 1: 'Awaiting planning', 2: 'No technician assigned', 3: 'Not enough capacity before the due date' };
export const DUE: Record<number, { label: string; cls: string } | undefined> = {
  1: { label: 'Due tomorrow', cls: 'bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-200' },
  2: { label: 'Due today', cls: 'bg-amber-200 text-amber-950 dark:bg-amber-900 dark:text-amber-100' },
  3: { label: 'Overdue', cls: 'bg-red-100 text-red-800 dark:bg-red-950 dark:text-red-200' },
};
const PRIORITY: Record<string, string> = { CRITICAL: 'bg-red-100 text-red-800 dark:bg-red-950 dark:text-red-200', HIGH: 'bg-amber-100 text-amber-900 dark:bg-amber-950 dark:text-amber-200' };

/** "2026-10-05T14:00" for a datetime-local input, from an instant in a zone. */
const localInput = (iso: string, tz: string) => `${dateOf(Date.parse(iso), tz)}T${clock(Date.parse(iso), tz)}`;
const fromLocalInput = (v: string, tz: string) => (v ? wallToIso(v.slice(0, 10), v.slice(11, 16), tz) : null);

// ---- what the work needs ---------------------------------------------------------------------

/** The planning requirement of one piece of work, read and edited in place. Staff only. */
export function RequirementEditor({ ticketId, canEdit, timeZone, compact = false }: { ticketId: string; canEdit: boolean; timeZone: string; compact?: boolean }) {
  const qc = useQueryClient();
  const { data, error } = useQuery({ queryKey: ['plan', 'requirement', ticketId], queryFn: () => api.planningRequirement(ticketId), retry: false });
  const { data: skills } = useQuery({ queryKey: ['skills', false], queryFn: () => api.skills(false), staleTime: 60_000, enabled: canEdit });
  const [editing, setEditing] = useState(false);
  const [form, setForm] = useState({ minutes: '', earliest: '', latest: '', splittable: false, skill: '', note: '' });
  useEffect(() => {
    if (!data) return;
    setForm({
      minutes: data.requiredMinutes ? String(data.requiredMinutes) : '', earliest: data.earliestStart ? localInput(data.earliestStart, timeZone) : '',
      latest: data.latestEnd ? localInput(data.latestEnd, timeZone) : '', splittable: data.splittable, skill: data.requiredSkillId ?? '', note: data.note ?? '',
    });
  }, [data, timeZone]);
  const save = useMutation({
    mutationFn: () => api.setPlanningRequirement(ticketId, {
      requiredMinutes: form.minutes ? Number(form.minutes) : null, earliestStart: fromLocalInput(form.earliest, timeZone), latestEnd: fromLocalInput(form.latest, timeZone),
      splittable: form.splittable, requiredSkillId: form.skill || null, note: form.note.trim() || null,
    }),
    onSuccess: () => { setEditing(false); qc.invalidateQueries({ queryKey: ['plan'] }); },
  });
  if (error) return <p role="alert" className="text-xs text-red-600 dark:text-red-400">{(error as Error).message}</p>;
  if (!data) return <p className="text-xs text-[var(--muted)]">Loading…</p>;
  const allocated = `${hours(data.confirmedMinutes)} planned${data.tentativeMinutes ? ` (+${hours(data.tentativeMinutes)} tentative)` : ''}`;
  const summary = [
    data.requiredMinutes ? `${hours(data.requiredMinutes)} needed` : 'No estimate',
    data.requiredMinutes ? `${allocated}, ${hours(data.remainingMinutes ?? 0)} to plan` : data.confirmedMinutes || data.tentativeMinutes ? allocated : null,
    data.splittable ? 'may be split' : null,
    data.earliestStart && data.latestEnd ? `between ${fmtDay(dateOf(Date.parse(data.earliestStart), timeZone))} ${clock(Date.parse(data.earliestStart), timeZone)} and ${fmtDay(dateOf(Date.parse(data.latestEnd), timeZone))} ${clock(Date.parse(data.latestEnd), timeZone)} (${timeZone})` : null,
    data.requiredSkillName ? `needs ${data.requiredSkillName}` : null,
  ].filter(Boolean).join(' · ');
  return (
    <div className={compact ? 'text-xs' : 'text-sm'}>
      {!editing && (
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-[var(--muted)]">{summary}</span>
          {data.note && <span className="text-[var(--muted)]">· {data.note}</span>}
          {canEdit && <button type="button" onClick={() => setEditing(true)} className="inline-flex items-center gap-1 text-xs font-medium text-brand hover:underline"><Pencil size={11} /> {data.requiredMinutes ? 'Change' : 'Set what it needs'}</button>}
        </div>
      )}
      {editing && (
        <form className="grid gap-2 sm:grid-cols-2" onSubmit={(e) => { e.preventDefault(); save.mutate(); }} aria-label="What the work needs">
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Effort needed (minutes)
            <input type="number" min={5} max={6000} step={5} value={form.minutes} onChange={(e) => setForm((f) => ({ ...f, minutes: e.target.value }))} aria-label="Effort needed" className={`w-full ${field}`} placeholder="e.g. 120" />
          </label>
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Skill it asks for
            <select value={form.skill} onChange={(e) => setForm((f) => ({ ...f, skill: e.target.value }))} aria-label="Required skill" className={`w-full ${field}`}>
              <option value="">None</option>{(skills ?? []).filter((s) => s.isActive).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </select>
          </label>
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Earliest start ({timeZone})
            <input type="datetime-local" value={form.earliest} onChange={(e) => setForm((f) => ({ ...f, earliest: e.target.value }))} aria-label="Earliest start" className={`w-full ${field}`} />
          </label>
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Latest finish ({timeZone})
            <input type="datetime-local" value={form.latest} onChange={(e) => setForm((f) => ({ ...f, latest: e.target.value }))} aria-label="Latest finish" className={`w-full ${field}`} />
          </label>
          <label className="flex items-center gap-2 text-sm sm:col-span-2">
            <input type="checkbox" checked={form.splittable} onChange={(e) => setForm((f) => ({ ...f, splittable: e.target.checked }))} aria-label="May be split" />
            <span>May be done in several sittings<span className="block text-xs text-[var(--muted)]">Off, the work needs one uninterrupted period.</span></span>
          </label>
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">Planning note
            <input value={form.note} maxLength={300} onChange={(e) => setForm((f) => ({ ...f, note: e.target.value }))} aria-label="Planning note" className={`w-full ${field}`} />
          </label>
          {save.error && <p role="alert" className="text-xs text-red-600 dark:text-red-400 sm:col-span-2">{(save.error as Error).message}</p>}
          <div className="flex justify-end gap-2 sm:col-span-2">
            <button type="button" onClick={() => setEditing(false)} className={btn}>Cancel</button>
            <button type="submit" disabled={save.isPending} className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">{save.isPending ? 'Saving…' : 'Save'}</button>
          </div>
        </form>
      )}
    </div>
  );
}

// ---- the preview dialog ----------------------------------------------------------------------

/**
 * Effort in a window, for one person: continuous or split, committed or pencilled in. Calculate,
 * look at what would be placed and what would not fit, then confirm. Nothing persists until the
 * confirmation, and a confirmation whose plan has changed since comes back as a fresh preview.
 */
export function PlanningPreviewDialog({ target, personId, viewerId, timeZone, requirement: given, onClose, onSaved }: {
  target: PlanTarget; personId: string | null; viewerId: string; timeZone: string;
  /** What the work needs, when the caller already has it; otherwise the dialog reads it. */
  requirement?: PlanningRequirement | null;
  onClose: () => void; onSaved: () => void;
}) {
  const qc = useQueryClient();
  useDialog(onClose);
  const { data: people } = useQuery({ queryKey: ['plan-people'], queryFn: api.plannablePeople, staleTime: 60_000 });
  const { data: loaded } = useQuery({ queryKey: ['plan', 'requirement', target.ticketId], queryFn: () => api.planningRequirement(target.ticketId), retry: false, enabled: given === undefined });
  const requirement = given === undefined ? loaded : given;
  const [person, setPerson] = useState(personId ?? viewerId);
  // The holder may be someone the viewer cannot plan for (a technician on a colleague's ticket): then the viewer's own time.
  useEffect(() => {
    if (people && !people.some((p) => p.appUserId === person)) { setPerson(viewerId); setPreview(null); }
  }, [people, person, viewerId]);
  const tz = people?.find((p) => p.appUserId === person)?.timeZone ?? timeZone;
  const today = dateOf(Date.now(), tz);
  const [earliest, setEarliest] = useState(`${today}T08:00`);
  const [latest, setLatest] = useState(`${today}T18:00`);
  const [minutes, setMinutes] = useState('60');
  const [splittable, setSplittable] = useState(false);
  const [tentative, setTentative] = useState(false);
  // The requirement seeds the form once it is known (window, remaining effort, splittable) - unless the user typed first.
  const touched = useRef(false);
  const seeded = useRef(false);
  useEffect(() => {
    if (seeded.current || !requirement || touched.current) return;
    seeded.current = true;
    if (requirement.earliestStart) setEarliest(localInput(requirement.earliestStart, tz));
    if (requirement.latestEnd) setLatest(localInput(requirement.latestEnd, tz));
    if (requirement.remainingMinutes || requirement.requiredMinutes) setMinutes(String(requirement.remainingMinutes || requirement.requiredMinutes));
    setSplittable(requirement.splittable);
  }, [requirement, tz]);
  const [note, setNote] = useState('');
  const [reason, setReason] = useState('');
  const [preview, setPreview] = useState<PlanPreview | null>(null);
  const [changed, setChanged] = useState(false);

  const request = useMemo(() => {
    const e = fromLocalInput(earliest, tz); const l = fromLocalInput(latest, tz);
    return e && l && Number(minutes) >= 5 ? { ticketId: target.ticketId, appUserId: person, earliestStart: e, latestEnd: l, requiredMinutes: Number(minutes), splittable, tentative, minChunkMinutes: 30 } : null;
  }, [earliest, latest, minutes, splittable, tentative, person, target.ticketId, tz]);

  const calculate = useMutation({
    mutationFn: () => api.planPreview({ ticketId: request!.ticketId, appUserId: request!.appUserId, earliest: request!.earliestStart, latest: request!.latestEnd, minutes: request!.requiredMinutes, splittable, tentative }),
    onSuccess: (p) => { setPreview(p); setChanged(false); },
  });
  const confirm = useMutation({
    mutationFn: () => api.confirmPlanPreview({ request: request!, pieces: preview!.pieces, planToken: preview!.planToken, overrideReason: reason.trim() || null, note: note.trim() || null }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['plan'] }); qc.invalidateQueries({ queryKey: ['capacity'] }); qc.invalidateQueries({ queryKey: ['ticket-plan'] }); onSaved(); },
    onError: (err) => {
      // The plan moved under the preview: show the fresh proposal and ask again.
      if (err instanceof ApiError && err.status === 409) {
        const fresh = PlanChangedSchema.safeParse(err.payload);
        if (fresh.success) { setPreview(fresh.data.preview); setChanged(true); }
      }
    },
  });
  const conflict = conflictProblem(confirm.error);
  const invalid = !request ? 'Choose a window and an effort.' : request.latestEnd <= request.earliestStart ? 'The window must end after it starts.' : null;
  const canConfirm = !!preview && preview.pieces.length > 0 && !confirm.isPending && !calculate.isPending
    && (!conflict || (conflict.canOverride && conflict.overrideAllowedForCaller && reason.trim().length >= 5));

  return (
    <div role="dialog" aria-modal="true" aria-label="Plan with a window" className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/40 p-4 pt-[5vh]" onClick={onClose}>
      <div className="w-full max-w-2xl space-y-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between gap-3">
          <div>
            <h2 className="flex items-center gap-2 text-sm font-semibold"><CalendarRange size={16} className="text-brand" aria-hidden="true" /> Plan {target.reference} with a window</h2>
            <p className="text-xs text-[var(--muted)]">{target.title}. Say how much effort, between when and when, and whether it may be split; see what would be placed before anything is kept.</p>
          </div>
          <button type="button" aria-label="Close" onClick={onClose} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={16} /></button>
        </div>
        <form className="grid gap-2 sm:grid-cols-3" onSubmit={(e) => { e.preventDefault(); if (request && !invalid) calculate.mutate(); }} aria-label="Planning window">
          {(people?.length ?? 0) > 1 && (
            <label className="block space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-3">Whose time
              <select value={person} onChange={(e) => { setPerson(e.target.value); setPreview(null); }} aria-label="Person" className={`w-full ${field}`}>
                {people!.map((p) => <option key={p.appUserId} value={p.appUserId}>{p.displayName}{p.appUserId === viewerId ? ' (you)' : ''}{p.isSchedulable ? '' : ' · not offered for work'}</option>)}
              </select>
            </label>
          )}
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Earliest start ({tz})
            <input type="datetime-local" required value={earliest} onChange={(e) => { touched.current = true; setEarliest(e.target.value); setPreview(null); }} aria-label="Earliest start" className={`w-full ${field}`} />
          </label>
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Latest finish ({tz})
            <input type="datetime-local" required value={latest} onChange={(e) => { touched.current = true; setLatest(e.target.value); setPreview(null); }} aria-label="Latest finish" className={`w-full ${field}`} />
          </label>
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Effort (minutes)
            <input type="number" required min={5} max={6000} step={5} value={minutes} onChange={(e) => { touched.current = true; setMinutes(e.target.value); setPreview(null); }} aria-label="Effort" className={`w-full ${field}`} />
          </label>
          <label className="flex items-center gap-2 text-sm sm:col-span-1">
            <input type="checkbox" checked={splittable} onChange={(e) => { touched.current = true; setSplittable(e.target.checked); setPreview(null); }} aria-label="May be split" />
            <span>May be split<span className="block text-xs text-[var(--muted)]">Several sittings of 30 minutes or more.</span></span>
          </label>
          <label className="flex items-center gap-2 text-sm sm:col-span-1">
            <input type="checkbox" checked={tentative} onChange={(e) => { setTentative(e.target.checked); setPreview(null); }} aria-label="Pencil in" />
            <span>Pencil in<span className="block text-xs text-[var(--muted)]">Tentative: takes no confirmed capacity.</span></span>
          </label>
          <div className="flex items-end justify-end sm:col-span-1">
            <button type="submit" disabled={!request || !!invalid || calculate.isPending} className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">{calculate.isPending ? 'Calculating…' : 'Calculate'}</button>
          </div>
        </form>
        {invalid && request && <p className="text-xs text-red-600 dark:text-red-400">{invalid}</p>}
        {calculate.error && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(calculate.error as Error).message}</p>}

        {preview && (
          <section aria-label="Proposed plan" className="space-y-2 rounded-lg border border-[var(--border)] p-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h3 className="text-sm font-semibold">Proposed plan for {preview.personName}</h3>
              <span className="text-xs tabular-nums text-[var(--muted)]">{hours(preview.allocatedMinutes)} of {hours(preview.requiredMinutes)} placed{preview.unallocatedMinutes > 0 ? <span className="text-amber-700 dark:text-amber-300"> · {hours(preview.unallocatedMinutes)} unallocated</span> : ''} · {hours(preview.freeMinutesInWindow)} free in the window, longest {hours(preview.longestFreeMinutes)}</span>
            </div>
            {changed && <p role="alert" className="rounded-md border border-amber-300 bg-amber-50 px-2 py-1 text-xs text-amber-900 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-100">The plan changed since the preview. This is the new proposal; confirm it again if it still suits.</p>}
            {preview.pieces.length === 0 && <p className="text-sm text-[var(--muted)]">Nothing can be placed in this window.</p>}
            <ol className="divide-y divide-[var(--border)] rounded-md border border-[var(--border)] text-sm" aria-label="Pieces">
              {preview.pieces.map((p) => (
                <li key={p.start} className="flex items-center gap-3 px-3 py-1.5">
                  <span className="w-28 shrink-0 tabular-nums">{fmtDay(dateOf(Date.parse(p.start), preview.timeZone))}</span>
                  <span className="tabular-nums">{fmtSlot(p, preview.timeZone)}</span>
                  <span className="ml-auto text-xs text-[var(--muted)]">{hours(p.minutes)}{preview.tentative ? ' · tentative' : ''}</span>
                </li>
              ))}
            </ol>
            {preview.warnings.length > 0 && (
              <ul className="space-y-0.5 text-xs text-amber-800 dark:text-amber-200">
                {preview.warnings.map((w, i) => <li key={i} className="flex items-start gap-1"><AlertTriangle size={12} className="mt-0.5 shrink-0" aria-hidden="true" /> {w}</li>)}
              </ul>
            )}
            <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">Note for the plan (optional)
              <input value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} aria-label="Plan note" className={`w-full ${field}`} />
            </label>
            {confirm.error && !changed && (
              <div role="alert" className="space-y-1 rounded-md border border-amber-300 bg-amber-50 p-2 text-xs text-amber-900 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-100">
                <p className="font-medium">{(confirm.error as Error).message}</p>
                {conflict?.conflicts.map((c, i) => <p key={i}>{CONFLICT_NAMES[c.type] ?? 'Conflict'}: {c.message}</p>)}
                {conflict?.canOverride && conflict.overrideAllowedForCaller && (
                  <label className="block space-y-1 font-medium">Reason to override (kept with the work)
                    <input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} aria-label="Override reason" className={`w-full ${field}`} />
                  </label>
                )}
              </div>
            )}
            <div className="flex justify-end gap-2">
              <button type="button" onClick={onClose} className={btn}>Cancel</button>
              <button type="button" disabled={!canConfirm} onClick={() => confirm.mutate()} className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
                <Check size={14} /> {confirm.isPending ? 'Saving…' : conflict?.canOverride && conflict.overrideAllowedForCaller ? 'Override and confirm' : preview.tentative ? 'Pencil in' : 'Confirm plan'}
              </button>
            </div>
          </section>
        )}
      </div>
    </div>
  );
}

// ---- the planning queue -----------------------------------------------------------------------

/** The group's work that waits, why, and what the group is short over the next fortnight. */
export function PlanningQueueView({ viewerId, canPlan }: { viewerId: string; canPlan: boolean }) {
  const [f, setF] = useState({ teamId: '', departmentId: '', skillIds: [] as string[], matchAll: true });
  const [only, setOnly] = useState<'' | 'due' | 'short' | 'unheld' | 'estimated' | 'unestimated'>('');
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState<string | null>(null);
  const [dialog, setDialog] = useState<{ kind: 'plan' | 'window'; item: PlanningQueueItem } | null>(null);
  const { data, isLoading, error, refetch } = useQuery({
    queryKey: ['plan', 'queue', f.teamId, f.departmentId, f.skillIds, f.matchAll],
    queryFn: () => api.planningQueue({ teamId: f.teamId, departmentId: f.departmentId, skills: f.skillIds, matchAll: f.matchAll, horizonDays: 14 }),
    retry: false, placeholderData: (prev) => prev,
  });
  // Windows are typed in this browser's zone; the server keeps instants and judges days in each person's zone.
  const tz = useMemo(() => Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC', []);
  const items = (data?.items ?? []).filter((i) => {
    const q = search.trim().toLowerCase();
    if (q && !`${i.work.reference} ${i.work.title} ${i.work.clientName ?? ''} ${i.work.holderName ?? ''}`.toLowerCase().includes(q)) return false;
    if (only === 'due' && i.due === 0) return false;
    if (only === 'short' && i.reason !== 3) return false;
    if (only === 'unheld' && i.reason !== 2) return false;
    if (only === 'estimated' && i.requiredMinutes == null) return false;
    if (only === 'unestimated' && i.requiredMinutes != null) return false;
    return true;
  });
  return (
    <div className="space-y-3">
      <section aria-label="Filters" className="flex flex-wrap items-center gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3">
        <GroupAndSkillFilters {...f} onChange={(patch) => setF((v) => ({ ...v, ...patch }))} />
        <input value={search} onChange={(e) => setSearch(e.target.value)} aria-label="Search the queue" placeholder="Ticket, title, client, holder" className={`${field} w-56`} />
        <select value={only} onChange={(e) => setOnly(e.target.value as typeof only)} aria-label="Show" className={field}>
          <option value="">Everything waiting</option><option value="due">Due soon or overdue</option><option value="short">Not enough capacity before due</option><option value="unheld">Nobody holds it</option><option value="estimated">With an estimate</option><option value="unestimated">Without an estimate</option>
        </select>
        <button type="button" onClick={() => refetch()} className={btn}>Refresh</button>
      </section>
      {error && <p role="alert" className="rounded-lg border border-red-300 bg-red-50 px-3 py-2 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/40 dark:text-red-200">{(error as Error).message}</p>}
      {data && (
        <dl className="grid grid-cols-2 gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3 text-sm sm:grid-cols-5" aria-label="Demand against capacity">
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Waiting</dt><dd className="text-lg font-semibold tabular-nums">{data.items.length}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Demand</dt><dd className="text-lg font-semibold tabular-nums">{hours(data.demandMinutes)}</dd><dd className="text-[11px] text-[var(--muted)]">{data.itemsWithoutEstimate > 0 ? `${data.itemsWithoutEstimate} without an estimate` : 'everything estimated'}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Free, next {Math.round((Date.parse(data.to) - Date.parse(data.from)) / 86_400_000) + 1} days</dt><dd className="text-lg font-semibold tabular-nums text-green-700 dark:text-green-300">{hours(data.availableMinutes)}</dd><dd className="text-[11px] text-[var(--muted)]">{data.peopleCounted} people offered for work</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Shortage</dt><dd className={`text-lg font-semibold tabular-nums ${data.shortageMinutes > 0 ? 'text-red-700 dark:text-red-300' : ''}`}>{hours(data.shortageMinutes)}</dd></div>
          <div><dt className="text-[11px] uppercase tracking-wide text-[var(--muted)]">Window</dt><dd className="text-sm tabular-nums">{fmtDay(data.from)} – {fmtDay(data.to)}</dd></div>
        </dl>
      )}
      {isLoading && !data && <div aria-busy="true" className="h-40 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}
      {data && items.length === 0 && <div className="rounded-xl border border-dashed border-[var(--border)] px-4 py-10 text-center text-sm text-[var(--muted)]">{data.items.length === 0 ? 'Nothing is waiting: everything open in this group is planned.' : 'Nothing matches.'}</div>}
      {items.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <table className="w-full min-w-[960px] text-sm">
            <caption className="sr-only">Work waiting to be planned</caption>
            <thead className="border-b border-[var(--border)] text-left text-[11px] uppercase tracking-wide text-[var(--muted)]">
              <tr>
                <th scope="col" className="px-3 py-2 font-medium">Work</th><th scope="col" className="px-3 py-2 font-medium">Client</th><th scope="col" className="px-3 py-2 font-medium">Priority</th>
                <th scope="col" className="px-3 py-2 font-medium">Holder</th><th scope="col" className="px-3 py-2 font-medium">Needs</th><th scope="col" className="px-3 py-2 font-medium">Due</th>
                <th scope="col" className="px-3 py-2 font-medium">Waiting because</th><th scope="col" className="px-3 py-2 text-right font-medium">Age</th><th scope="col" className="px-3 py-2 font-medium"><span className="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-[var(--border)]">
              {items.map((i) => {
                const due = DUE[i.due];
                const expanded = open === i.work.ticketId;
                return [
                  <tr key={i.work.ticketId}>
                    <td className="px-3 py-2"><span className="font-mono text-xs text-[var(--muted)]">{i.work.reference}</span> <Link href={`/dashboard/tickets/${i.work.ticketId}`} className="font-medium hover:underline">{i.work.title}</Link><div className="text-[11px] text-[var(--muted)]">{i.work.source}{i.requiredSkillName ? ` · needs ${i.requiredSkillName}` : ''}</div></td>
                    <td className="px-3 py-2 text-[var(--muted)]">{i.work.clientName ?? '—'}</td>
                    <td className="px-3 py-2"><span className={`${chip} ${PRIORITY[i.work.priority.toUpperCase()] ?? 'bg-[var(--bg)] text-[var(--muted)]'}`}>{i.work.priority}</span></td>
                    <td className="px-3 py-2">{i.work.holderName ?? (i.work.heldOutside ? 'Outside this group' : i.work.teamName ? `With ${i.work.teamName}` : '—')}</td>
                    <td className="px-3 py-2 tabular-nums">{i.requiredMinutes ? <>{hours(i.remainingMinutes ?? 0)}<span className="text-[11px] text-[var(--muted)]"> of {hours(i.requiredMinutes)}{i.splittable ? ', may split' : ''}</span></> : <span className="text-[var(--muted)]">no estimate</span>}</td>
                    <td className="px-3 py-2">{i.work.dueAt ? <span className="tabular-nums text-xs">{new Date(i.work.dueAt).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}</span> : <span className="text-[var(--muted)]">—</span>}{due && <span className={`${chip} ml-1 ${due.cls}`}>{due.label}</span>}</td>
                    <td className="px-3 py-2 text-xs">{REASONS[i.reason]}{i.reason === 3 && i.freeBeforeDueMinutes != null && <span className="block text-[var(--muted)]">{hours(i.freeBeforeDueMinutes)} free before it is due</span>}</td>
                    <td className="px-3 py-2 text-right tabular-nums text-xs">{i.ageDays}d</td>
                    <td className="px-3 py-2">
                      <div className="flex justify-end gap-1">
                        {canPlan && <button type="button" onClick={() => setDialog({ kind: 'plan', item: i })} className={`${btn} px-2 py-1 text-xs`}>Schedule</button>}
                        {canPlan && <button type="button" onClick={() => setDialog({ kind: 'window', item: i })} className={`${btn} px-2 py-1 text-xs`}>Plan with a window</button>}
                        <Link href={`/dashboard/workforce/find?duration=${i.remainingMinutes || i.requiredMinutes || 60}`} className={`${btn} px-2 py-1 text-xs`}>Find technician</Link>
                        <button type="button" onClick={() => setOpen(expanded ? null : i.work.ticketId)} aria-expanded={expanded} aria-label={`${expanded ? 'Hide' : 'Show'} what ${i.work.reference} needs`} className="rounded p-1 hover:bg-[var(--bg)]">{expanded ? <ChevronUp size={14} /> : <ChevronDown size={14} />}</button>
                      </div>
                    </td>
                  </tr>,
                  expanded ? <tr key={`${i.work.ticketId}-req`}><td colSpan={9} className="bg-[var(--bg)] px-3 py-2"><RequirementEditor ticketId={i.work.ticketId} canEdit={canPlan} timeZone={tz} compact /></td></tr> : null,
                ];
              })}
            </tbody>
          </table>
        </div>
      )}
      {dialog?.kind === 'plan' && <PlanWorkDialog target={{ ticketId: dialog.item.work.ticketId, reference: dialog.item.work.reference, title: dialog.item.work.title }} personId={dialog.item.work.holderId ?? viewerId} initialMinutes={dialog.item.remainingMinutes || dialog.item.requiredMinutes || null} viewerId={viewerId} onClose={() => setDialog(null)} onSaved={() => { setDialog(null); refetch(); }} />}
      {dialog?.kind === 'window' && <PlanningPreviewDialog target={{ ticketId: dialog.item.work.ticketId, reference: dialog.item.work.reference, title: dialog.item.work.title }} personId={dialog.item.work.holderId} viewerId={viewerId} timeZone={tz}
        requirement={{ ticketId: dialog.item.work.ticketId, requiredMinutes: dialog.item.requiredMinutes, earliestStart: dialog.item.earliestStart, latestEnd: dialog.item.latestEnd, splittable: dialog.item.splittable, requiredSkillId: null, requiredSkillName: dialog.item.requiredSkillName, note: null, confirmedMinutes: dialog.item.confirmedMinutes, tentativeMinutes: dialog.item.tentativeMinutes, remainingMinutes: dialog.item.remainingMinutes, updatedByName: null, updatedAt: null }}
        onClose={() => setDialog(null)} onSaved={() => { setDialog(null); refetch(); }} />}
    </div>
  );
}
