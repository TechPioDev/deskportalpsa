'use client';

import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Moon, Plus, Trash2, Copy, Check, X } from 'lucide-react';
import { api, type PersonSchedule, type StaffSkill, type WorkScheduleInput } from '@/lib/api';

// ---- Schedule arithmetic: the same rules the server applies (WorkingWindow.cs) -------------------

/** Monday first, as people read a week. Values are .NET's DayOfWeek (Sunday = 0). */
export const WEEK = [
  { day: 1, label: 'Monday' }, { day: 2, label: 'Tuesday' }, { day: 3, label: 'Wednesday' },
  { day: 4, label: 'Thursday' }, { day: 5, label: 'Friday' }, { day: 6, label: 'Saturday' }, { day: 0, label: 'Sunday' },
] as const;

const minutes = (hm: string) => { const [h, m] = hm.split(':').map(Number); return h * 60 + m; };
/** Minutes from the window's start to t, on the window's own timeline (past midnight for a night shift). */
const offset = (start: string, t: string) => { const d = minutes(t) - minutes(start); return d < 0 ? d + 1440 : d; };
export const crossesMidnight = (start: string, end: string) => minutes(end) <= minutes(start);
const gross = (start: string, end: string) => offset(start, end) || 1440;

type Brk = { start: string; end: string };
type DayEdit = { working: boolean; start: string; end: string; breaks: Brk[] };

/** Problems with one day, worded like the server's; empty when it is fine. */
export function dayProblems(d: DayEdit): string[] {
  if (!d.working) return [];
  if (!d.start || !d.end) return ['Enter a start and an end.'];
  if (d.start === d.end) return ["A working window can't start and end at the same time."];
  const g = gross(d.start, d.end);
  const out: string[] = [];
  const spans: [number, number][] = [];
  for (const b of d.breaks) {
    if (!b.start || !b.end) { out.push('Enter both times for each break.'); continue; }
    if (b.start === b.end) { out.push(`The break ${b.start}–${b.end} has no length.`); continue; }
    const from = offset(d.start, b.start); const to = offset(d.start, b.end);
    if (to <= from || to > g || from >= g) { out.push(`The break ${b.start}–${b.end} is outside the working window.`); continue; }
    spans.push([from, to]);
  }
  spans.sort((a, b) => a[0] - b[0]);
  for (let i = 1; i < spans.length; i++) if (spans[i][0] < spans[i - 1][1]) { out.push('Two breaks overlap.'); break; }
  if (!out.length && g - spans.reduce((s, [f, t]) => s + t - f, 0) <= 0) out.push('The breaks leave no working time.');
  return out;
}

export function usable(d: DayEdit): number {
  if (!d.working || dayProblems(d).length) return 0;
  return gross(d.start, d.end) - d.breaks.reduce((s, b) => s + offset(d.start, b.end) - offset(d.start, b.start), 0);
}

export const hours = (m: number) => `${Math.floor(m / 60)}h${m % 60 ? ` ${String(m % 60).padStart(2, '0')}m` : ''}`;

/** Today's date in a time zone, as YYYY-MM-DD. */
const todayIn = (tz: string) => { try { return new Date().toLocaleDateString('en-CA', { timeZone: tz }); } catch { return new Date().toISOString().slice(0, 10); } };

function allZones(extra: string[]): string[] {
  let zones: string[] = [];
  try { zones = (Intl as unknown as { supportedValuesOf(k: string): string[] }).supportedValuesOf('timeZone'); } catch { /* older browser */ }
  return Array.from(new Set([...extra.filter(Boolean), ...zones, 'UTC']));
}

const blankWeek = (): Record<number, DayEdit> =>
  Object.fromEntries(WEEK.map(({ day }) => [day, { working: day >= 1 && day <= 5, start: '08:30', end: '17:30', breaks: [{ start: '12:30', end: '13:30' }] }]));

function fromSchedule(s: PersonSchedule): { tz: string; days: Record<number, DayEdit> } {
  const version = s.upcoming ?? s.current;
  if (!version) return { tz: s.organizationTimeZone, days: blankWeek() };
  const days: Record<number, DayEdit> = Object.fromEntries(WEEK.map(({ day }) => [day, { working: false, start: '08:30', end: '17:30', breaks: [] }]));
  for (const d of version.days) days[d.day] = { working: true, start: d.start, end: d.end, breaks: d.breaks.map((b) => ({ ...b })) };
  return { tz: version.timeZone, days };
}

const fmtDate = (iso: string) => new Date(`${iso}T00:00:00`).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });

const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm outline-none focus:border-brand disabled:opacity-60';

// ---- Working schedule ----------------------------------------------------------------------------

/**
 * When someone normally works: a weekly window per day, planned breaks, and the time zone those
 * hours are in. A capacity boundary for planning work - nothing here records attendance.
 */
export function WorkScheduleEditor({ userId }: { userId: string }) {
  const qc = useQueryClient();
  const { data, isLoading, error } = useQuery({ queryKey: ['work-schedule', userId], queryFn: () => api.workSchedule(userId), retry: false });
  const [tz, setTz] = useState('');
  const [days, setDays] = useState<Record<number, DayEdit>>(blankWeek());
  const [from, setFrom] = useState('');
  const [copying, setCopying] = useState(false);
  const [saved, setSaved] = useState(false);

  const reset = (s: PersonSchedule) => {
    const v = fromSchedule(s);
    setTz(v.tz); setDays(v.days); setFrom(s.upcoming?.effectiveFrom ?? todayIn(v.tz));
  };
  // Load (and reload after a save) into the form.
  useEffect(() => {
    if (!data) return;
    const v = fromSchedule(data);
    setTz(v.tz); setDays(v.days); setFrom(data.upcoming?.effectiveFrom ?? todayIn(v.tz));
  }, [data]);

  const save = useMutation({
    mutationFn: () => {
      const body: WorkScheduleInput = {
        effectiveFrom: from || null, timeZone: tz,
        days: WEEK.filter(({ day }) => days[day].working).map(({ day }) => ({ day, start: days[day].start, end: days[day].end, breaks: days[day].breaks })),
      };
      return api.saveWorkSchedule(userId, body);
    },
    onSuccess: (s) => {
      qc.setQueryData(['work-schedule', userId], s);
      qc.invalidateQueries({ queryKey: ['workforce-people'] });
      setSaved(true); setTimeout(() => setSaved(false), 2500);
    },
  });
  const removeUpcoming = useMutation({
    mutationFn: (date: string) => api.removeUpcomingSchedule(userId, date),
    onSuccess: (s) => { qc.setQueryData(['work-schedule', userId], s); qc.invalidateQueries({ queryKey: ['workforce-people'] }); },
  });
  const schedulable = useMutation({
    mutationFn: (on: boolean) => api.setSchedulable(userId, on),
    onSuccess: (s) => { qc.setQueryData(['work-schedule', userId], s); qc.invalidateQueries({ queryKey: ['workforce-people'] }); },
  });

  const problems = useMemo(() => WEEK.flatMap(({ day, label }) => dayProblems(days[day]).map((p) => `${label}: ${p}`)), [days]);
  const weekly = WEEK.reduce((s, { day }) => s + usable(days[day]), 0);
  const zones = useMemo(() => allZones([tz, data?.organizationTimeZone ?? '']), [tz, data?.organizationTimeZone]);

  if (isLoading) return <p className="text-sm text-[var(--muted)]">Loading the schedule…</p>;
  if (error || !data) return <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(error as Error)?.message ?? 'The schedule could not be loaded.'}</p>;

  const edit = data.canManage;
  const setDay = (day: number, patch: Partial<DayEdit>) => setDays((all) => ({ ...all, [day]: { ...all[day], ...patch } }));
  const noneWorking = !WEEK.some(({ day }) => days[day].working);

  return (
    <div className="space-y-4">
      <p className="max-w-prose text-sm text-[var(--muted)]">
        When {data.displayName} normally works, so work can be planned against real capacity. This is not attendance:
        nothing here records whether anyone was at work.
      </p>

      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-xs text-[var(--muted)]">
        <span>{data.current ? <>In force since <strong className="text-[var(--fg)]">{fmtDate(data.current.effectiveFrom)}</strong> · {hours(data.current.weeklyUsableMinutes)} a week</> : 'No schedule yet.'}</span>
        {data.upcoming && (
          <span className="inline-flex items-center gap-2">
            Changes on <strong className="text-[var(--fg)]">{fmtDate(data.upcoming.effectiveFrom)}</strong>
            {edit && (
              <button type="button" onClick={() => removeUpcoming.mutate(data.upcoming!.effectiveFrom)} disabled={removeUpcoming.isPending}
                className="rounded border border-[var(--border)] px-1.5 py-0.5 text-[11px] font-medium hover:bg-[var(--bg)]">Withdraw that change</button>
            )}
          </span>
        )}
        {!data.isActive && <span className="rounded-full bg-[var(--bg)] px-2 py-0.5 font-medium">Account inactive</span>}
      </div>

      <label className="flex items-start gap-2 text-sm">
        <input type="checkbox" checked={data.isSchedulable} disabled={!edit || schedulable.isPending}
          onChange={(e) => schedulable.mutate(e.target.checked)} className="mt-1" aria-label="Offered for planned work" />
        <span><span className="font-medium">Offered for planned work</span>
          <span className="block text-xs text-[var(--muted)]">Turn off for someone who keeps their schedule but should not be given planned work.</span></span>
      </label>

      <div className="grid gap-3 sm:grid-cols-2">
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Time zone these hours are in
          <select value={tz} onChange={(e) => setTz(e.target.value)} disabled={!edit} aria-label="Time zone" className={`w-full ${field}`}>
            {zones.map((z) => <option key={z} value={z}>{z}{z === data.organizationTimeZone ? ' (organization)' : ''}</option>)}
          </select>
        </label>
        {edit && (
          <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
            Applies from
            <input type="date" value={from} min={todayIn(tz || 'UTC')} onChange={(e) => setFrom(e.target.value)} aria-label="Applies from" className={`w-full ${field}`} />
          </label>
        )}
      </div>

      <ul className="divide-y divide-[var(--border)] rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        {WEEK.map(({ day, label }) => {
          const d = days[day];
          const dayErrors = dayProblems(d);
          return (
            <li key={day} className="space-y-2 px-4 py-3">
              <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
                <span className="w-24 shrink-0 text-sm font-medium">{label}</span>
                <label className="inline-flex items-center gap-1.5 text-xs text-[var(--muted)]">
                  <input type="checkbox" checked={d.working} disabled={!edit} onChange={(e) => setDay(day, { working: e.target.checked })} aria-label={`${label} working`} />
                  Working
                </label>
                {d.working ? (
                  <>
                    <input type="time" step={300} value={d.start} disabled={!edit} onChange={(e) => setDay(day, { start: e.target.value })} aria-label={`${label} start`} className={field} />
                    <span aria-hidden="true" className="text-[var(--muted)]">→</span>
                    <input type="time" step={300} value={d.end} disabled={!edit} onChange={(e) => setDay(day, { end: e.target.value })} aria-label={`${label} end`} className={field} />
                    {d.start && d.end && d.start !== d.end && crossesMidnight(d.start, d.end) && (
                      <span className="inline-flex items-center gap-1 rounded-full bg-[var(--bg)] px-2 py-0.5 text-[11px] font-medium text-[var(--muted)]">
                        <Moon size={11} aria-hidden="true" /> Ends next day
                      </span>
                    )}
                    <span className="ml-auto text-xs tabular-nums text-[var(--muted)]">{dayErrors.length ? '—' : `${hours(usable(d))} to work`}</span>
                  </>
                ) : <span className="text-xs text-[var(--muted)]">Not working</span>}
              </div>
              {d.working && (
                <div className="space-y-1.5 pl-0 sm:pl-[6.75rem]">
                  {d.breaks.map((b, i) => (
                    <div key={i} className="flex flex-wrap items-center gap-2 text-xs">
                      <span className="w-10 text-[var(--muted)]">Break</span>
                      <input type="time" step={300} value={b.start} disabled={!edit} aria-label={`${label} break ${i + 1} start`}
                        onChange={(e) => setDay(day, { breaks: d.breaks.map((x, j) => (j === i ? { ...x, start: e.target.value } : x)) })} className={field} />
                      <span aria-hidden="true" className="text-[var(--muted)]">→</span>
                      <input type="time" step={300} value={b.end} disabled={!edit} aria-label={`${label} break ${i + 1} end`}
                        onChange={(e) => setDay(day, { breaks: d.breaks.map((x, j) => (j === i ? { ...x, end: e.target.value } : x)) })} className={field} />
                      {edit && (
                        <button type="button" onClick={() => setDay(day, { breaks: d.breaks.filter((_, j) => j !== i) })}
                          aria-label={`Remove ${label} break ${i + 1}`} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-red-600"><Trash2 size={13} /></button>
                      )}
                    </div>
                  ))}
                  {edit && d.breaks.length < 4 && (
                    <button type="button" onClick={() => setDay(day, { breaks: [...d.breaks, { start: '12:30', end: '13:00' }] })}
                      className="inline-flex items-center gap-1 text-xs font-medium text-brand hover:underline"><Plus size={12} /> Add break</button>
                  )}
                  {dayErrors.map((p) => <p key={p} className="text-xs text-red-600 dark:text-red-400">{p}</p>)}
                </div>
              )}
            </li>
          );
        })}
      </ul>

      <div className="flex flex-wrap items-center gap-3">
        <span className="text-sm">Week: <strong className="tabular-nums">{hours(weekly)}</strong> to work</span>
        {edit && (
          <>
            <button type="button" onClick={() => setDays((all) => ({ ...all, ...Object.fromEntries([2, 3, 4, 5].map((day) => [day, { ...all[1], breaks: all[1].breaks.map((b) => ({ ...b })) }])) }))}
              className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--bg)]">Copy Monday to Tue–Fri</button>
            <span className="ml-auto flex flex-wrap gap-2">
              {data.current && (
                <button type="button" onClick={() => setCopying(true)}
                  className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]"><Copy size={14} /> Apply to others…</button>
              )}
              <button type="button" onClick={() => reset(data)} className="rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
              <button type="button" onClick={() => save.mutate()} disabled={save.isPending || problems.length > 0 || noneWorking || !tz}
                className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
                {saved ? <><Check size={15} /> Saved</> : save.isPending ? 'Saving…' : 'Save schedule'}
              </button>
            </span>
          </>
        )}
      </div>
      {edit && noneWorking && <p className="text-xs text-[var(--muted)]">Choose at least one working day. To keep someone out of planned work, turn off &ldquo;Offered for planned work&rdquo; instead.</p>}
      {save.isError && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(save.error as Error).message}</p>}
      {copying && <CopyScheduleDialog userId={userId} name={data.displayName} onClose={() => setCopying(false)} />}
    </div>
  );
}

function CopyScheduleDialog({ userId, name, onClose }: { userId: string; name: string; onClose: () => void }) {
  const qc = useQueryClient();
  const { data: people } = useQuery({ queryKey: ['workforce-people', {}], queryFn: () => api.workforcePeople({}) });
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [from, setFrom] = useState('');
  const copy = useMutation({
    mutationFn: () => api.copyWorkSchedule(userId, [...picked], from || null),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['workforce-people'] }); qc.invalidateQueries({ queryKey: ['work-schedule'] }); onClose(); },
  });
  const others = (people ?? []).filter((p) => p.appUserId !== userId);
  return (
    <div role="dialog" aria-label="Apply schedule to others" className="fixed inset-0 z-50 flex items-start justify-center bg-black/40 p-4 pt-[10vh]" onClick={onClose}>
      <div className="w-full max-w-lg space-y-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between">
          <h2 className="text-sm font-semibold">Apply {name}&rsquo;s schedule to…</h2>
          <button type="button" aria-label="Close" onClick={onClose} className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)]"><X size={16} /></button>
        </div>
        <p className="text-xs text-[var(--muted)]">Each person gets the same days, hours, breaks and time zone, from the day you choose. Their earlier days keep the schedule they had.</p>
        <ul className="max-h-72 divide-y divide-[var(--border)] overflow-y-auto rounded-lg border border-[var(--border)]">
          {others.map((p) => (
            <li key={p.appUserId}>
              <label className="flex items-center gap-2 px-3 py-2 text-sm">
                <input type="checkbox" checked={picked.has(p.appUserId)} aria-label={`Apply to ${p.displayName}`}
                  onChange={(e) => setPicked((s) => { const n = new Set(s); if (e.target.checked) n.add(p.appUserId); else n.delete(p.appUserId); return n; })} />
                <span className="min-w-0 flex-1 truncate">{p.displayName}</span>
                <span className="truncate text-xs text-[var(--muted)]">{p.scheduleSummary ?? 'No schedule'}</span>
              </label>
            </li>
          ))}
        </ul>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Applies from (blank = today)
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} aria-label="Copy applies from" className={`w-full ${field}`} />
        </label>
        {copy.isError && <p role="alert" className="text-xs text-red-600 dark:text-red-400">{(copy.error as Error).message}</p>}
        <div className="flex justify-end gap-2">
          <button type="button" onClick={onClose} className="rounded-lg border border-[var(--border)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
          <button type="button" onClick={() => copy.mutate()} disabled={picked.size === 0 || copy.isPending}
            className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
            {copy.isPending ? 'Applying…' : `Apply to ${picked.size || ''} ${picked.size === 1 ? 'person' : 'people'}`}
          </button>
        </div>
      </div>
    </div>
  );
}

// ---- Skills --------------------------------------------------------------------------------------

export const LEVELS: { value: 1 | 2 | 3; label: string }[] = [{ value: 1, label: 'Basic' }, { value: 2, label: 'Proficient' }, { value: 3, label: 'Expert' }];
const levelName = (l: number) => LEVELS.find((x) => x.value === l)?.label ?? '';

/** Someone's skills, at a level each. The catalogue itself is managed on the Workforce → Skills page. */
export function StaffSkillsEditor({ userId, canManage }: { userId: string; canManage: boolean }) {
  const qc = useQueryClient();
  const { data: held, error } = useQuery({ queryKey: ['person-skills', userId], queryFn: () => api.personSkills(userId), retry: false });
  const { data: catalogue } = useQuery({ queryKey: ['skills', false], queryFn: () => api.skills(false), enabled: canManage });
  const [skillId, setSkillId] = useState('');
  const [level, setLevel] = useState<1 | 2 | 3>(2);
  const done = (rows: StaffSkill[]) => { qc.setQueryData(['person-skills', userId], rows); qc.invalidateQueries({ queryKey: ['workforce-people'] }); qc.invalidateQueries({ queryKey: ['skills'] }); };
  const assign = useMutation({ mutationFn: (v: { skillId: string; level: 1 | 2 | 3 }) => api.assignSkill(userId, v.skillId, v.level), onSuccess: (r) => { done(r); setSkillId(''); } });
  const remove = useMutation({ mutationFn: (id: string) => api.removeSkill(userId, id), onSuccess: done });
  const rows = held ?? [];
  const available = (catalogue ?? []).filter((s) => !rows.some((r) => r.skillId === s.id));
  const err = assign.error ?? remove.error;

  if (error) return <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(error as Error).message}</p>;
  return (
    <div className="space-y-3">
      {rows.length === 0 ? <p className="text-sm text-[var(--muted)]">No skills recorded yet.</p> : (
        <ul className="divide-y divide-[var(--border)] rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          {rows.map((s) => (
            <li key={s.skillId} className="flex flex-wrap items-center gap-3 px-4 py-2.5 text-sm">
              <span className="min-w-0 flex-1 font-medium">{s.name}{!s.skillIsActive && <span className="ml-2 text-xs font-normal text-[var(--muted)]">(retired)</span>}</span>
              {canManage ? (
                <select value={s.level} aria-label={`${s.name} level`} disabled={assign.isPending}
                  onChange={(e) => assign.mutate({ skillId: s.skillId, level: Number(e.target.value) as 1 | 2 | 3 })} className={field}>
                  {LEVELS.map((l) => <option key={l.value} value={l.value}>{l.label}</option>)}
                </select>
              ) : <span className="text-xs text-[var(--muted)]">{levelName(s.level)}</span>}
              {canManage && (
                <button type="button" onClick={() => remove.mutate(s.skillId)} disabled={remove.isPending} aria-label={`Remove ${s.name}`}
                  className="rounded p-1 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-red-600"><Trash2 size={14} /></button>
              )}
            </li>
          ))}
        </ul>
      )}
      {canManage && (
        <div className="flex flex-wrap items-center gap-2">
          <select value={skillId} onChange={(e) => setSkillId(e.target.value)} aria-label="Skill to add" className={`min-w-48 ${field}`}>
            <option value="">{available.length ? 'Choose a skill…' : 'No more skills in the catalogue'}</option>
            {available.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select>
          <select value={level} onChange={(e) => setLevel(Number(e.target.value) as 1 | 2 | 3)} aria-label="Level" className={field}>
            {LEVELS.map((l) => <option key={l.value} value={l.value}>{l.label}</option>)}
          </select>
          <button type="button" onClick={() => assign.mutate({ skillId, level })} disabled={!skillId || assign.isPending}
            className="inline-flex items-center gap-1 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50"><Plus size={14} /> Add skill</button>
        </div>
      )}
      {err && <p role="alert" className="text-xs text-red-600 dark:text-red-400">{(err as Error).message}</p>}
    </div>
  );
}
