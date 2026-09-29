'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Plus, Archive, RotateCcw, Timer, CalendarOff, X, PauseCircle } from 'lucide-react';
import { api, type SlaPlan, type SlaPlanInput } from '@/lib/api';

/** Sunday is bit 0, matching the server's bitmask. Listed Monday first, the way a week is read. */
const DAYS = [
  { bit: 1, label: 'Mon' }, { bit: 2, label: 'Tue' }, { bit: 3, label: 'Wed' }, { bit: 4, label: 'Thu' },
  { bit: 5, label: 'Fri' }, { bit: 6, label: 'Sat' }, { bit: 0, label: 'Sun' },
];

function hoursText(h: number) {
  if (h % 24 === 0 && h >= 24) return `${h / 24} day${h === 24 ? '' : 's'}`;
  return `${h} hour${h === 1 ? '' : 's'}`;
}

function scheduleText(p: SlaPlan) {
  if (!p.businessHoursOnly) return 'Round the clock';
  const days = DAYS.filter((d) => (p.workingDays & (1 << d.bit)) !== 0).map((d) => d.label);
  const pad = (n: number) => `${String(n).padStart(2, '0')}:00`;
  // A closing hour at or before the opening one is a night shift that ends the next morning.
  const overnight = p.workdayEndHour <= p.workdayStartHour;
  return `${days.join(', ')} · ${pad(p.workdayStartHour)}–${pad(p.workdayEndHour)}${overnight ? ' next morning' : ''}${p.skipHolidays ? ' · not on holidays' : ''}`;
}

/**
 * SLA plans: how quickly each kind of board work is owed. A topic or a board names a plan, and the
 * plan becomes a ticket's due dates when it is raised. Leads and administrators manage them.
 */
export default function SlaPlansPage() {
  const qc = useQueryClient();
  const [editing, setEditing] = useState<SlaPlan | 'new' | null>(null);
  const { data: plans, isLoading, isError, error } = useQuery({
    queryKey: ['sla-plans', true], queryFn: () => api.slaPlans(true), retry: false,
  });
  const setActive = useMutation({
    mutationFn: ({ id, active }: { id: string; active: boolean }) => api.setSlaPlanActive(id, active),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['sla-plans'] }),
  });

  if (isError) {
    const message = error instanceof Error ? error.message : '';
    return (
      <p className="rounded-xl border border-[var(--border)] bg-[var(--surface)] px-5 py-4 text-sm text-[var(--muted)]">
        {/403|forbidden|permission/i.test(message)
          ? 'SLA plans are set up by leads and administrators.'
          : `Couldn’t load the SLA plans: ${message || 'the server did not answer.'}`}
      </p>
    );
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="max-w-2xl">
          <Link href="/dashboard/boards" className="inline-flex items-center gap-1 text-xs text-[var(--muted)] hover:text-[var(--fg)]">
            <ArrowLeft size={13} /> All boards
          </Link>
          <h1 className="mt-1 text-2xl font-semibold tracking-tight">SLA plans</h1>
          <p className="text-sm text-[var(--muted)]">
            How quickly each kind of board work is owed. Give a plan to a topic, or make it a board&apos;s
            default, and every ticket raised there gets a reply-by and a resolve-by date. Changing a plan
            later does not re-date tickets already raised. PSA tickets keep the SLA from their PSA.
          </p>
        </div>
        <button onClick={() => setEditing('new')}
          className="inline-flex items-center gap-2 rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
          <Plus size={15} /> New plan
        </button>
      </div>

      {editing && (
        <PlanForm plan={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); qc.invalidateQueries({ queryKey: ['sla-plans'] }); qc.invalidateQueries({ queryKey: ['boards'] }); }} />
      )}

      {isLoading && <div className="h-24 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}

      {!isLoading && (plans ?? []).length === 0 && (
        <div className="flex flex-col items-center rounded-xl border border-dashed border-[var(--border)] px-6 py-10 text-center">
          <Timer className="mb-3 text-[var(--faint)]" size={26} />
          <p className="max-w-md text-sm text-[var(--muted)]">
            No plans yet. A common start is <strong>Priority</strong> — reply in 1 hour, resolve in 8 — and
            <strong> Standard</strong> — reply in 4 hours, resolve in 2 days.
          </p>
        </div>
      )}

      {(plans ?? []).length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <table className="w-full text-sm">
            <thead className="text-left text-[10px] uppercase tracking-wide text-[var(--faint)]">
              <tr className="border-b border-[var(--border)]">
                <th className="px-5 py-2.5 font-medium">Plan</th>
                <th className="px-2 py-2.5 font-medium">First reply</th>
                <th className="px-2 py-2.5 font-medium">Resolved within</th>
                <th className="px-2 py-2.5 font-medium">Clock</th>
                <th className="px-2 py-2.5 font-medium">Used by</th>
                <th className="px-5 py-2.5 font-medium"></th>
              </tr>
            </thead>
            <tbody>
              {(plans ?? []).map((p) => (
                <tr key={p.id} className={`border-b border-[var(--border)] last:border-0 ${p.isActive ? '' : 'opacity-60'}`}>
                  <td className="px-5 py-3 font-medium">
                    {p.name}
                    {!p.isActive && <span className="ml-2 text-[11px] font-normal text-[var(--faint)]">retired</span>}
                  </td>
                  <td className="px-2 py-3 text-xs text-[var(--muted)]">{p.firstResponseWithinHours ? hoursText(p.firstResponseWithinHours) : '—'}</td>
                  <td className="px-2 py-3 text-xs text-[var(--muted)]">{hoursText(p.resolveWithinHours)}</td>
                  <td className="px-2 py-3 text-xs text-[var(--muted)]">
                    {scheduleText(p)}
                    {p.pauseWhileWaiting && (
                      <span className="mt-0.5 flex items-center gap-1 text-[11px] text-[var(--faint)]">
                        <PauseCircle size={11} aria-hidden="true" /> Pauses while waiting on the customer
                      </span>
                    )}
                  </td>
                  <td className="px-2 py-3 text-xs tabular-nums text-[var(--muted)]">
                    {p.usedBy === 0 ? 'Nothing yet' : `${p.usedBy} board${p.usedBy === 1 ? '' : 's'} or topic${p.usedBy === 1 ? '' : 's'}`}
                  </td>
                  <td className="px-5 py-3 text-right text-xs font-medium">
                    <button onClick={() => setEditing(p)} className="text-[var(--muted)] hover:text-[var(--fg)]">Edit</button>
                    <button onClick={() => setActive.mutate({ id: p.id, active: !p.isActive })}
                      title={p.isActive ? 'Stop applying this plan to new tickets' : undefined}
                      className="ml-3 inline-flex items-center gap-1 text-[var(--muted)] hover:text-[var(--fg)]">
                      {p.isActive ? <><Archive size={12} /> Retire</> : <><RotateCcw size={12} /> Restore</>}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="text-xs text-[var(--muted)]">
        Retiring a plan stops it being applied to new tickets. Boards and topics that name it simply stop
        setting due dates until they are given another.
      </p>

      <Holidays />
    </div>
  );
}

/**
 * The desk's own closed days. Working-hours plans step over them; round-the-clock plans work through
 * them. Adding one does not re-date tickets already raised.
 */
function Holidays() {
  const qc = useQueryClient();
  const today = new Date().toISOString().slice(0, 10);
  const [date, setDate] = useState('');
  const [name, setName] = useState('');
  const { data: holidays } = useQuery({ queryKey: ['desk-holidays'], queryFn: () => api.deskHolidays(today), retry: false });
  const add = useMutation({
    mutationFn: () => api.addDeskHoliday(date, name.trim()),
    onSuccess: () => { setDate(''); setName(''); qc.invalidateQueries({ queryKey: ['desk-holidays'] }); },
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.removeDeskHoliday(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['desk-holidays'] }),
  });
  const field = 'rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-1.5 text-sm outline-none focus:border-brand';

  return (
    <section className="rounded-xl border border-[var(--border)] bg-[var(--surface)]" aria-labelledby="holidays-heading">
      <div className="border-b border-[var(--border)] px-5 py-3">
        <h2 id="holidays-heading" className="flex items-center gap-2 text-sm font-semibold"><CalendarOff size={14} /> Holidays</h2>
        <p className="text-xs text-[var(--muted)]">
          Days the desk is closed. Working-hours plans skip them, so a ticket raised the evening before
          is not due on the holiday itself. Round-the-clock plans are not affected.
        </p>
      </div>
      {(holidays ?? []).length === 0 && (
        <p className="px-5 py-4 text-sm text-[var(--muted)]">No upcoming holidays.</p>
      )}
      {(holidays ?? []).length > 0 && (
        <ul className="divide-y divide-[var(--border)]">
          {(holidays ?? []).map((h) => (
            <li key={h.id} className="flex items-center gap-3 px-5 py-2.5 text-sm">
              <span className="w-32 shrink-0 tabular-nums text-[var(--muted)]">
                {new Date(`${h.date}T00:00:00`).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })}
              </span>
              <span className="min-w-0 flex-1 truncate">{h.name}</span>
              <button type="button" onClick={() => remove.mutate(h.id)} aria-label={`Remove ${h.name}`}
                className="rounded p-1 text-[var(--faint)] hover:bg-[var(--bg)] hover:text-red-600"><X size={13} /></button>
            </li>
          ))}
        </ul>
      )}
      <form className="flex flex-wrap items-center gap-2 border-t border-[var(--border)] px-5 py-3"
        onSubmit={(e) => { e.preventDefault(); if (date && name.trim()) add.mutate(); }}>
        <input type="date" required min={today} value={date} onChange={(e) => setDate(e.target.value)}
          aria-label="Holiday date" className={field} />
        <input required maxLength={80} value={name} onChange={(e) => setName(e.target.value)}
          placeholder="Diwali" aria-label="Holiday name" className={`${field} min-w-0 flex-1`} />
        <button type="submit" disabled={add.isPending || !date || !name.trim()}
          className="inline-flex items-center gap-1 rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
          <Plus size={13} /> Add holiday
        </button>
        {add.isError && <span role="alert" className="text-xs text-red-600 dark:text-red-400">{(add.error as Error).message}</span>}
      </form>
    </section>
  );
}

function PlanForm({ plan, onClose, onSaved }: { plan: SlaPlan | null; onClose: () => void; onSaved: () => void }) {
  const [v, setV] = useState<SlaPlanInput>({
    name: plan?.name ?? '',
    resolveWithinHours: plan?.resolveWithinHours ?? 24,
    firstResponseWithinHours: plan?.firstResponseWithinHours ?? 4,
    businessHoursOnly: plan?.businessHoursOnly ?? false,
    workdayStartHour: plan?.workdayStartHour ?? 9,
    workdayEndHour: plan?.workdayEndHour ?? 18,
    workingDays: plan?.workingDays ?? 0b0111110,
    sortOrder: plan?.sortOrder ?? 0,
    skipHolidays: plan?.skipHolidays ?? true,
    pauseWhileWaiting: plan?.pauseWhileWaiting ?? true,
  });
  const save = useMutation({ mutationFn: () => api.saveSlaPlan(plan?.id ?? null, v), onSuccess: onSaved });
  const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand';
  const num = (s: string) => (s === '' ? 0 : Number(s));

  return (
    <form className="grid gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 sm:grid-cols-3"
      onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Plan name
        <input required maxLength={80} value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })}
          placeholder="Priority" className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        First reply within (hours)
        <input type="number" min={1} max={8760} value={v.firstResponseWithinHours ?? ''}
          onChange={(e) => setV({ ...v, firstResponseWithinHours: e.target.value ? Number(e.target.value) : null })}
          placeholder="No reply promise" className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Resolved within (hours)
        <input type="number" required min={1} max={8760} value={v.resolveWithinHours}
          onChange={(e) => setV({ ...v, resolveWithinHours: num(e.target.value) })} className={field} />
      </label>

      <fieldset className="space-y-2 text-xs text-[var(--muted)] sm:col-span-3">
        <legend className="mb-1 font-medium">Which hours count</legend>
        <label className="flex items-start gap-2">
          <input type="radio" name="clock" checked={!v.businessHoursOnly} className="mt-0.5"
            onChange={() => setV({ ...v, businessHoursOnly: false })} />
          <span><strong className="font-medium text-[var(--fg)]">Round the clock.</strong> For a desk that never closes: every hour counts.</span>
        </label>
        <label className="flex items-start gap-2">
          <input type="radio" name="clock" checked={v.businessHoursOnly} className="mt-0.5"
            onChange={() => setV({ ...v, businessHoursOnly: true })} />
          <span><strong className="font-medium text-[var(--fg)]">Working hours only</strong>, in your organization&apos;s time zone. A ticket raised at 17:30 on Friday with four hours to go is due on Monday. A night shift works too — open at 22:00, close at 06:00.</span>
        </label>
      </fieldset>

      {v.businessHoursOnly && (
        <div className="grid gap-3 rounded-lg border border-[var(--border)] bg-[var(--bg)] p-3 sm:col-span-3 sm:grid-cols-[1fr_auto_auto]">
          <div className="space-y-1 text-xs font-medium text-[var(--muted)]" role="group" aria-label="Working days">
            Working days
            <div className="flex flex-wrap gap-1.5">
              {DAYS.map((d) => {
                const on = (v.workingDays & (1 << d.bit)) !== 0;
                return (
                  <button key={d.label} type="button" aria-pressed={on}
                    onClick={() => setV({ ...v, workingDays: v.workingDays ^ (1 << d.bit) })}
                    className={`rounded-lg border px-2.5 py-1 text-xs font-medium ${on
                      ? 'border-brand bg-brand-tint text-brand dark:bg-brand/20'
                      : 'border-[var(--border)] text-[var(--muted)] hover:bg-[var(--surface)]'}`}>
                    {d.label}
                  </button>
                );
              })}
            </div>
          </div>
          <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
            Opens
            <select value={v.workdayStartHour} onChange={(e) => setV({ ...v, workdayStartHour: Number(e.target.value) })} className={field}>
              {Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
            </select>
          </label>
          <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
            Closes
            <select value={v.workdayEndHour} onChange={(e) => setV({ ...v, workdayEndHour: Number(e.target.value) })} className={field}>
              {Array.from({ length: 24 }, (_, i) => i + 1).map((h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}
            </select>
          </label>
          {/* Said in words, because "Opens 22:00, Closes 06:00" can read as a mistake: it is a shift
              that belongs to the day it starts and runs into the next morning. */}
          <p className="text-[11px] font-normal text-[var(--faint)] sm:col-span-3">
            {v.workdayEndHour % 24 === v.workdayStartHour
              ? 'Opening and closing at the same hour is either no hours or every hour. For a desk that never closes, choose round the clock.'
              : v.workdayEndHour < v.workdayStartHour
                ? `A night shift: each working day's shift opens at ${String(v.workdayStartHour).padStart(2, '0')}:00 and closes at ${String(v.workdayEndHour).padStart(2, '0')}:00 the next morning. A holiday cancels the shift that would start that night.`
                : `Open ${v.workdayEndHour - v.workdayStartHour} hours on each working day.`}
          </p>
        </div>
      )}

      {v.businessHoursOnly && (
        <label className="flex items-start gap-2 text-xs text-[var(--muted)] sm:col-span-3">
          <input type="checkbox" className="mt-0.5" checked={v.skipHolidays}
            onChange={(e) => setV({ ...v, skipHolidays: e.target.checked })} />
          <span>Skip the desk&apos;s holidays, listed below, as if they were weekends.</span>
        </label>
      )}
      <label className="flex items-start gap-2 text-xs text-[var(--muted)] sm:col-span-3">
        <input type="checkbox" className="mt-0.5" checked={v.pauseWhileWaiting}
          onChange={(e) => setV({ ...v, pauseWhileWaiting: e.target.checked })} />
        <span>
          <strong className="font-medium text-[var(--fg)]">Pause while waiting.</strong> Stop the clock while a
          ticket is Waiting customer or On hold, and give the time back when it moves on — so a ticket does
          not go overdue while nobody on the desk can act on it.
        </span>
      </label>
      {save.isError && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400 sm:col-span-3">{(save.error as Error).message}</p>
      )}
      <div className="flex justify-end gap-2 sm:col-span-3">
        <button type="button" onClick={onClose}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
        <button type="submit" disabled={save.isPending}
          className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
          {save.isPending ? 'Saving…' : plan ? 'Save plan' : 'Add plan'}
        </button>
      </div>
    </form>
  );
}
