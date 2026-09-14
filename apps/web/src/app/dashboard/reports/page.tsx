'use client';

import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  CalendarClock, Download, FileBarChart, Globe2, History, Info, Mail, Pencil, Play, Plus, Trash2,
} from 'lucide-react';
import {
  api, REPORT_FREQUENCY, REPORT_KIND,
  type StaffReportRun, type StaffReportSchedule, type StaffReportScheduleInput,
} from '@/lib/api';

/**
 * The MSP's own scheduled reports. Every run covers the last COMPLETE period and is sent at 07:00 in
 * the organization's time zone, as a PDF and a CSV — and kept here, so a report nobody received
 * (no email set up, a refused address) is still one click away.
 */

const FREQUENCIES: { value: number; label: string; covers: string }[] = [
  { value: REPORT_FREQUENCY.Daily, label: 'Daily', covers: 'yesterday' },
  { value: REPORT_FREQUENCY.Weekly, label: 'Weekly', covers: 'last Monday–Sunday' },
  { value: REPORT_FREQUENCY.Monthly, label: 'Monthly', covers: 'last calendar month' },
  { value: REPORT_FREQUENCY.Quarterly, label: 'Quarterly', covers: 'last quarter' },
];

const empty: StaffReportScheduleInput = {
  name: '', kind: REPORT_KIND.TechnicianProductivity, frequency: REPORT_FREQUENCY.Monthly,
  clientCompanyId: null, recipients: '', isEnabled: true,
};

export default function StaffReportsPage() {
  const qc = useQueryClient();
  const { data: settings } = useQuery({ queryKey: ['report-settings'], queryFn: api.reportSettings });
  const { data: schedules, isLoading, error } = useQuery({ queryKey: ['staff-report-schedules'], queryFn: api.staffReportSchedules });
  const { data: runs } = useQuery({ queryKey: ['staff-report-runs'], queryFn: api.staffReportRuns });
  const { data: clients } = useQuery({ queryKey: ['report-clients'], queryFn: api.reportClients });
  // Needs the integration-health permission; someone without it simply sees no email notice.
  const { data: email } = useQuery({ queryKey: ['email-status'], queryFn: api.emailStatus, retry: false });

  const [draft, setDraft] = useState<StaffReportScheduleInput | null>(null);
  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['staff-report-schedules'] });
    qc.invalidateQueries({ queryKey: ['staff-report-runs'] });
  };
  const save = useMutation({ mutationFn: api.saveStaffReportSchedule, onSuccess: () => { setDraft(null); refresh(); } });
  const remove = useMutation({ mutationFn: api.deleteStaffReportSchedule, onSuccess: refresh });
  const run = useMutation({ mutationFn: api.runStaffReport, onSuccess: refresh });

  const zone = settings?.timeZone ?? 'UTC';

  return (
    <div className="mx-auto max-w-5xl space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="flex items-center gap-2 text-xl font-semibold"><FileBarChart size={20} className="text-brand" /> Scheduled reports</h1>
          <p className="text-sm text-[var(--muted)]">
            Technician productivity, sent as PDF and CSV at 07:00 for the period that just closed.
          </p>
        </div>
        <TimeZoneControl zone={zone} />
      </div>

      {email && !email.configured && (
        <p className="flex items-start gap-2 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-200">
          <Mail size={16} className="mt-0.5 shrink-0" aria-hidden="true" />
          <span>Email is not set up yet, so reports are generated and kept below but not sent. Once the mail account is added on the server, they go out automatically.</span>
        </p>
      )}

      <section className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3.5">
          <h2 className="flex items-center gap-2 text-sm font-semibold"><CalendarClock size={16} className="text-brand" /> Schedules</h2>
          {!draft && (
            <button onClick={() => setDraft({ ...empty })}
              className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90">
              <Plus size={15} /> New schedule
            </button>
          )}
        </div>

        {error && <p className="px-5 py-4 text-sm text-red-600 dark:text-red-400">{error instanceof Error ? error.message : 'Could not load schedules.'}</p>}
        {save.isError && <p className="px-5 pt-3 text-sm text-red-600 dark:text-red-400">{save.error instanceof Error ? save.error.message : 'Could not save.'}</p>}
        {run.isError && <p className="px-5 pt-3 text-sm text-red-600 dark:text-red-400">{run.error instanceof Error ? run.error.message : 'Could not run the report.'}</p>}

        <div className="divide-y divide-[var(--border)]">
          {draft && !draft.id && (
            <Editor initial={draft} clients={clients ?? []} saving={save.isPending} onCancel={() => { setDraft(null); save.reset(); }} onSave={(i) => save.mutate(i)} />
          )}
          {isLoading && <p className="px-5 py-6 text-center text-sm text-[var(--muted)]">Loading…</p>}
          {schedules?.length === 0 && !draft && (
            <p className="px-5 py-8 text-center text-sm text-[var(--muted)]">
              No schedules yet. A monthly report to your managers is a good first one.
            </p>
          )}
          {schedules?.map((s) => draft?.id === s.id ? (
            <Editor key={s.id} initial={draft} clients={clients ?? []} saving={save.isPending} onCancel={() => { setDraft(null); save.reset(); }} onSave={(i) => save.mutate(i)} />
          ) : (
            <ScheduleRow key={s.id} s={s} zone={zone}
              running={run.isPending && run.variables === s.id}
              onRun={() => run.mutate(s.id)}
              onEdit={() => setDraft({ id: s.id, name: s.name, kind: s.kind, frequency: s.frequency, clientCompanyId: s.clientCompanyId, recipients: s.recipients ?? '', isEnabled: s.isEnabled })}
              onDelete={() => { if (window.confirm(`Delete the schedule "${s.name}"? Reports already sent stay in the history.`)) remove.mutate(s.id); }} />
          ))}
        </div>
      </section>

      <section className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <h2 className="flex items-center gap-2 border-b border-[var(--border)] px-5 py-3.5 text-sm font-semibold"><History size={16} className="text-brand" /> Sent and generated</h2>
        <div className="divide-y divide-[var(--border)]">
          {(!runs || runs.length === 0) && (
            <p className="px-5 py-8 text-center text-sm text-[var(--muted)]">Nothing yet. Use Run now on a schedule to see its report straight away.</p>
          )}
          {runs?.map((r) => <RunRow key={r.id} r={r} />)}
        </div>
      </section>
    </div>
  );
}

function ScheduleRow({ s, zone, running, onRun, onEdit, onDelete }: {
  s: StaffReportSchedule; zone: string; running: boolean; onRun: () => void; onEdit: () => void; onDelete: () => void;
}) {
  const freq = FREQUENCIES.find((f) => f.value === s.frequency);
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 px-5 py-3.5">
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-sm font-medium">{s.name}</span>
          {!s.isEnabled && <span className="rounded-full bg-[var(--bg)] px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-[var(--faint)]">Paused</span>}
        </div>
        <div className="mt-0.5 text-xs text-[var(--muted)]">
          {freq?.label} · covers {freq?.covers} · {s.clientName ?? 'all clients'} · {s.recipients ? s.recipients : 'no recipients (portal only)'}
        </div>
        {s.isEnabled && (
          <div className="mt-0.5 text-xs text-[var(--faint)]">
            Next: {when(s.nextRunAt, zone)}{s.lastRunAt ? ` · last: ${when(s.lastRunAt, zone)}` : ''}
          </div>
        )}
      </div>
      <div className="flex items-center gap-1.5">
        <button onClick={onRun} disabled={running} title="Generate the last complete period now and send it"
          className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-60">
          <Play size={13} aria-hidden="true" /> {running ? 'Running…' : 'Run now'}
        </button>
        <button onClick={onEdit} aria-label={`Edit ${s.name}`} className="rounded-lg p-1.5 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]"><Pencil size={15} /></button>
        <button onClick={onDelete} aria-label={`Delete ${s.name}`} className="rounded-lg p-1.5 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-red-600"><Trash2 size={15} /></button>
      </div>
    </div>
  );
}

function Editor({ initial, clients, saving, onCancel, onSave }: {
  initial: StaffReportScheduleInput; clients: { id: string; name: string }[]; saving: boolean;
  onCancel: () => void; onSave: (i: StaffReportScheduleInput) => void;
}) {
  const [v, setV] = useState(initial);
  const freq = FREQUENCIES.find((f) => f.value === v.frequency)!;
  const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand';
  return (
    <form className="grid gap-3 bg-[var(--bg)]/40 px-5 py-4 sm:grid-cols-2"
      onSubmit={(e) => { e.preventDefault(); onSave({ ...v, name: v.name.trim() }); }}>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Name
        <input required value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })} placeholder="Monthly technician report" className={field} />
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        How often
        <select value={v.frequency} onChange={(e) => setV({ ...v, frequency: Number(e.target.value) })} className={field}>
          {FREQUENCIES.map((f) => <option key={f.value} value={f.value}>{f.label} — {f.covers}</option>)}
        </select>
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Clients
        <select value={v.clientCompanyId ?? ''} onChange={(e) => setV({ ...v, clientCompanyId: e.target.value || null })} className={field}>
          <option value="">All clients</option>
          {clients.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </label>
      <label className="space-y-1 text-xs font-medium text-[var(--muted)]">
        Send to
        <input value={v.recipients} onChange={(e) => setV({ ...v, recipients: e.target.value })}
          placeholder="manager@yourmsp.com, lead@yourmsp.com" className={field} />
      </label>
      <p className="flex items-start gap-1.5 text-xs text-[var(--faint)] sm:col-span-2">
        <Info size={13} className="mt-0.5 shrink-0" aria-hidden="true" />
        Sent at 07:00 the day after each period closes, covering {freq.covers}. Recipients get a PDF and a CSV; leave it empty to keep reports in the portal only.
      </p>
      <div className="flex flex-wrap items-center gap-3 sm:col-span-2">
        <label className="inline-flex items-center gap-2 text-sm">
          <input type="checkbox" checked={v.isEnabled} onChange={(e) => setV({ ...v, isEnabled: e.target.checked })} /> Active
        </label>
        <span className="ml-auto flex gap-2">
          <button type="button" onClick={onCancel} className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
          <button type="submit" disabled={saving} className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-60">
            {saving ? 'Saving…' : v.id ? 'Save changes' : 'Create schedule'}
          </button>
        </span>
      </div>
    </form>
  );
}

function RunRow({ r }: { r: StaffReportRun }) {
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 px-5 py-3">
      <div className="min-w-0 flex-1">
        <div className="text-sm font-medium">{r.title}</div>
        <div className="text-xs text-[var(--muted)]">
          {r.summary} · generated {new Date(r.generatedAt).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })}
        </div>
        <div className={`mt-0.5 text-xs ${r.delivered ? 'text-green-600 dark:text-green-400' : 'text-amber-600 dark:text-amber-400'}`}>
          {r.deliveryNote ?? (r.delivered ? 'Emailed' : 'Not emailed')}
        </div>
      </div>
      <div className="flex items-center gap-1.5">
        {(['pdf', 'csv'] as const).map((f) => (
          <a key={f} href={api.staffReportFileUrl(r.id, f)}
            className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]">
            <Download size={13} aria-hidden="true" /> {f.toUpperCase()}
          </a>
        ))}
      </div>
    </div>
  );
}

/** The organization's time zone decides "yesterday" and the 07:00 send; changing it needs organization admin rights. */
function TimeZoneControl({ zone }: { zone: string }) {
  const qc = useQueryClient();
  const [editing, setEditing] = useState(false);
  const zones = useMemo(() => {
    try { return (Intl as unknown as { supportedValuesOf(k: string): string[] }).supportedValuesOf('timeZone'); }
    catch { return [] as string[]; }
  }, []);
  const save = useMutation({
    mutationFn: api.saveReportSettings,
    onSuccess: () => { setEditing(false); qc.invalidateQueries({ queryKey: ['report-settings'] }); qc.invalidateQueries({ queryKey: ['staff-report-schedules'] }); },
  });

  if (!editing) {
    return (
      <button onClick={() => setEditing(true)}
        className="inline-flex items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm hover:bg-[var(--bg)]">
        <Globe2 size={15} className="text-[var(--muted)]" aria-hidden="true" /> {zone}
      </button>
    );
  }
  return (
    <form className="flex flex-wrap items-center gap-2" onSubmit={(e) => { e.preventDefault(); save.mutate(new FormData(e.currentTarget).get('tz') as string); }}>
      <select name="tz" defaultValue={zones.includes(zone) ? zone : 'UTC'} aria-label="Time zone"
        className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm outline-none focus:border-brand">
        {(zones.length ? zones : ['UTC']).map((z) => <option key={z} value={z}>{z}</option>)}
      </select>
      <button type="submit" disabled={save.isPending} className="rounded-lg bg-brand px-3 py-2 text-sm font-medium text-brand-fg disabled:opacity-60">Save</button>
      <button type="button" onClick={() => setEditing(false)} className="rounded-lg border border-[var(--border)] px-3 py-2 text-sm">Cancel</button>
      {save.isError && <span className="w-full text-xs text-red-600 dark:text-red-400">{save.error instanceof Error ? save.error.message : 'Could not change the time zone.'}</span>}
    </form>
  );
}

function when(iso: string, zone: string) {
  try {
    return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short', timeZone: zone });
  } catch {
    return new Date(iso).toLocaleString();
  }
}
