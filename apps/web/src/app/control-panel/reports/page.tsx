'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  BarChart3, Ticket, FolderOpen, Clock, DollarSign, Download, CalendarClock, Plus, Pencil, Trash2,
  Check, X, Play, History, Mail, Info,
} from 'lucide-react';
import { api, type ReportSchedule, type ReportScheduleInput } from '@/lib/api';
import { CpHeader, AccessError, Field } from '../_ui';

const STATUS_TONE: Record<string, string> = {
  NEW: 'bg-blue-500', IN_PROGRESS: 'bg-amber-500', WAITING_CUSTOMER: 'bg-violet-500',
  ON_HOLD: 'bg-orange-500', RESOLVED: 'bg-green-500', CLOSED: 'bg-slate-400',
};
const tone = (s: string) => STATUS_TONE[s.toUpperCase()] ?? 'bg-slate-400';
const fmt = (iso: string) => new Date(iso).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
const fmtDT = (iso: string) => new Date(iso).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });

// CSV downloads stream a file through the BFF (which attaches auth server-side); fetch as a blob.
async function downloadCsv(url: string, fallbackName: string) {
  const res = await fetch(url, { cache: 'no-store' });
  if (!res.ok) return;
  const blob = await res.blob();
  const cd = res.headers.get('content-disposition');
  const name = cd?.match(/filename="?([^"]+)"?/)?.[1] ?? fallbackName;
  const href = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = href; a.download = name; document.body.appendChild(a); a.click();
  a.remove(); URL.revokeObjectURL(href);
}

const emptySchedule: ReportScheduleInput = { name: '', frequency: 'weekly', recipients: '', isEnabled: true };

export default function ReportsPage() {
  const qc = useQueryClient();
  const { data, isLoading, error } = useQuery({ queryKey: ['cp-report'], queryFn: api.cpReport, retry: false });
  const { data: schedules } = useQuery({ queryKey: ['cp-report-schedules'], queryFn: api.cpReportSchedules, retry: false, enabled: !error });
  const { data: runs } = useQuery({ queryKey: ['cp-report-runs'], queryFn: api.cpReportRuns, retry: false, enabled: !error });
  const max = Math.max(1, ...(data?.byStatus.map((s) => s.count) ?? [1]));

  const [draft, setDraft] = useState<ReportScheduleInput | null>(null);
  const save = useMutation({ mutationFn: (i: ReportScheduleInput) => api.cpSaveReportSchedule(i), onSuccess: () => { setDraft(null); qc.invalidateQueries({ queryKey: ['cp-report-schedules'] }); } });
  const del = useMutation({ mutationFn: (id: string) => api.cpDeleteReportSchedule(id), onSuccess: () => qc.invalidateQueries({ queryKey: ['cp-report-schedules'] }) });
  const run = useMutation({ mutationFn: (id: string) => api.cpRunReportSchedule(id), onSuccess: () => { qc.invalidateQueries({ queryKey: ['cp-report-runs'] }); qc.invalidateQueries({ queryKey: ['cp-report-schedules'] }); } });

  return (
    <div className="mx-auto max-w-4xl space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <CpHeader icon={BarChart3} title="Reports" subtitle="A summary of your tickets and time — export it, or schedule it to run automatically." />
        {!error && (
          <button onClick={() => downloadCsv(api.cpReportExportPath, 'account-report.csv')}
            className="inline-flex shrink-0 items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-medium hover:bg-[var(--bg)]">
            <Download size={15} /> Export CSV
          </button>
        )}
      </div>

      {error ? <AccessError label="Reports" /> : isLoading ? (
        <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-8 text-center text-sm text-[var(--muted)]">Loading…</div>
      ) : data && (
        <>
          <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
            <Stat icon={Ticket} tone="bg-blue-50 text-blue-600 dark:bg-blue-950/50 dark:text-blue-300" label="Total tickets" value={data.totalTickets} />
            <Stat icon={FolderOpen} tone="bg-amber-50 text-amber-600 dark:bg-amber-950/50 dark:text-amber-300" label="Open" value={data.openTickets} />
            <Stat icon={Clock} tone="bg-violet-50 text-violet-600 dark:bg-violet-950/50 dark:text-violet-300" label="Hours logged" value={data.hoursLogged.toFixed(2)} />
            <Stat icon={DollarSign} tone="bg-green-50 text-green-600 dark:bg-green-950/50 dark:text-green-300" label="Billable hours" value={data.billableHours.toFixed(2)} />
          </div>

          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
            <h2 className="mb-4 text-sm font-semibold">Tickets by status</h2>
            {data.byStatus.length === 0 ? <p className="text-sm text-[var(--muted)]">No tickets yet.</p> : (
              <div className="space-y-2.5">
                {data.byStatus.map((s) => (
                  <div key={s.status} className="flex items-center gap-3">
                    <span className="w-36 shrink-0 text-xs font-medium text-[var(--muted)]">{s.status.replace(/_/g, ' ')}</span>
                    <div className="h-5 flex-1 overflow-hidden rounded bg-[var(--bg)]">
                      <div className={`h-full rounded ${tone(s.status)}`} style={{ width: `${(s.count / max) * 100}%` }} />
                    </div>
                    <span className="w-8 shrink-0 text-right text-sm font-semibold tabular-nums">{s.count}</span>
                  </div>
                ))}
              </div>
            )}
          </div>

          {/* Scheduled reports */}
          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
            <div className="flex items-center justify-between border-b border-[var(--border)] px-5 py-3.5">
              <h2 className="flex items-center gap-2 text-sm font-semibold"><CalendarClock size={16} className="text-brand" /> Scheduled reports</h2>
              <button onClick={() => setDraft({ ...emptySchedule })} className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90">
                <Plus size={15} /> Add schedule
              </button>
            </div>
            <div className="flex items-start gap-2 px-5 pt-3 text-xs text-[var(--faint)]">
              <Info size={13} className="mt-0.5 shrink-0" /> <p>Reports generate automatically and appear in History below. They are also emailed to the recipients once email is set up for the portal.</p>
            </div>
            <div className="divide-y divide-[var(--border)]">
              {schedules?.length === 0 && !draft && <div className="px-5 py-6 text-center text-sm text-[var(--muted)]">No schedules yet.</div>}
              {schedules?.map((s) => <ScheduleRow key={s.id} schedule={s} onSave={(i) => save.mutate(i)} onDelete={() => del.mutate(s.id)} onRun={() => run.mutate(s.id)} running={run.isPending} saving={save.isPending} />)}
              {draft && <ScheduleEditor initial={draft} onCancel={() => setDraft(null)} onSave={(i) => save.mutate(i)} saving={save.isPending} />}
            </div>
          </div>

          {/* Report history */}
          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
            <h2 className="flex items-center gap-2 border-b border-[var(--border)] px-5 py-3.5 text-sm font-semibold"><History size={16} className="text-brand" /> Report history</h2>
            <div className="divide-y divide-[var(--border)]">
              {(!runs || runs.length === 0) && <div className="px-5 py-6 text-center text-sm text-[var(--muted)]">No reports generated yet — add a schedule and run it, or export on demand.</div>}
              {runs?.map((r) => (
                <div key={r.id} className="flex items-center gap-3 px-5 py-3">
                  <History size={15} className="shrink-0 text-[var(--muted)]" />
                  <div className="min-w-0 flex-1">
                    <div className="text-sm font-medium">{r.summary}</div>
                    <div className="text-xs text-[var(--muted)]">{fmtDT(r.generatedAt)} · {r.reportScheduleId ? 'scheduled' : 'manual'} · {r.delivered ? 'emailed' : 'portal only'}</div>
                  </div>
                  <button onClick={() => downloadCsv(api.cpReportRunDownloadPath(r.id), 'report.csv')} className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]">
                    <Download size={13} /> CSV
                  </button>
                </div>
              ))}
            </div>
          </div>

          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
            <h2 className="border-b border-[var(--border)] px-5 py-3.5 text-sm font-semibold">Recent tickets</h2>
            <div className="divide-y divide-[var(--border)]">
              {data.recent.length === 0 && <div className="px-5 py-8 text-center text-sm text-[var(--muted)]">No tickets yet.</div>}
              {data.recent.slice(0, 8).map((t) => (
                <div key={t.id} className="flex items-center gap-3 px-5 py-3">
                  <span className={`h-2 w-2 shrink-0 rounded-full ${tone(t.portalStatus)}`} />
                  <span className="w-20 shrink-0 truncate text-xs text-[var(--muted)]">{t.externalTicketId ?? '—'}</span>
                  <span className="min-w-0 flex-1 truncate text-sm font-medium">{t.title}</span>
                  <span className="shrink-0 text-xs text-[var(--muted)]">{t.portalStatus.replace(/_/g, ' ')}</span>
                  <span className="hidden shrink-0 text-xs text-[var(--faint)] sm:inline">{fmt(t.createdAt)}</span>
                </div>
              ))}
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function ScheduleRow({ schedule, onSave, onDelete, onRun, running, saving }: {
  schedule: ReportSchedule; onSave: (i: ReportScheduleInput) => void; onDelete: () => void; onRun: () => void; running: boolean; saving: boolean;
}) {
  const [editing, setEditing] = useState(false);
  if (editing) return <ScheduleEditor initial={schedule} onCancel={() => setEditing(false)} onSave={(i) => { onSave(i); setEditing(false); }} saving={saving} />;
  return (
    <div className="flex items-center gap-3 px-5 py-3">
      <CalendarClock size={16} className="shrink-0 text-[var(--muted)]" />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 font-medium">
          {schedule.name}
          <span className="rounded bg-[var(--bg)] px-1.5 py-0.5 text-[10px] font-medium uppercase tracking-wide text-[var(--muted)]">{schedule.frequency}</span>
          {!schedule.isEnabled && <span className="rounded bg-[var(--bg)] px-1.5 py-0.5 text-[10px] font-medium text-[var(--faint)]">Paused</span>}
        </div>
        <div className="truncate text-xs text-[var(--muted)]">
          {schedule.recipients ? <><Mail size={10} className="mr-1 inline" />{schedule.recipients} · </> : ''}next {fmtDT(schedule.nextRunAt)}{schedule.lastRunAt ? ` · last ${fmtDT(schedule.lastRunAt)}` : ''}
        </div>
      </div>
      <button onClick={onRun} disabled={running} aria-label="Run now" className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-brand disabled:opacity-50"><Play size={13} /> {running ? 'Running…' : 'Run now'}</button>
      <button onClick={() => setEditing(true)} aria-label="Edit" className="rounded-md p-1.5 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-brand"><Pencil size={15} /></button>
      <button onClick={() => { if (window.confirm(`Delete "${schedule.name}"?`)) onDelete(); }} aria-label="Delete" className="rounded-md p-1.5 text-[var(--muted)] hover:bg-red-50 hover:text-red-600 dark:hover:bg-red-950/50"><Trash2 size={15} /></button>
    </div>
  );
}

function ScheduleEditor({ initial, onCancel, onSave, saving }: { initial: ReportSchedule | ReportScheduleInput; onCancel: () => void; onSave: (i: ReportScheduleInput) => void; saving: boolean }) {
  const [f, setF] = useState<ReportScheduleInput>({
    id: 'id' in initial ? initial.id : undefined,
    name: initial.name, frequency: initial.frequency, recipients: initial.recipients ?? '', isEnabled: initial.isEnabled,
  });
  return (
    <div className="bg-[var(--bg)]/40 px-5 py-4">
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        <Field label="Name" value={f.name} onChange={(v) => setF({ ...f, name: v })} placeholder="Weekly summary" />
        <label className="block text-xs font-medium text-[var(--muted)]">
          Frequency
          <select value={f.frequency} onChange={(e) => setF({ ...f, frequency: e.target.value })}
            className="mt-1 w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand">
            <option value="daily">Daily</option>
            <option value="weekly">Weekly</option>
            <option value="monthly">Monthly</option>
          </select>
        </label>
        <Field label="Recipients (email)" value={f.recipients ?? ''} onChange={(v) => setF({ ...f, recipients: v })} placeholder="ops@company.com, cfo@company.com" />
      </div>
      <div className="mt-3 flex flex-wrap items-center justify-between gap-3">
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={f.isEnabled} onChange={(e) => setF({ ...f, isEnabled: e.target.checked })} className="h-4 w-4 accent-brand" /> Enabled
        </label>
        <div className="flex items-center gap-2">
          <button onClick={onCancel} className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm text-[var(--muted)] hover:bg-[var(--bg)]"><X size={14} /> Cancel</button>
          <button onClick={() => f.name.trim() && onSave(f)} disabled={!f.name.trim() || saving} className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-40"><Check size={14} /> Save</button>
        </div>
      </div>
    </div>
  );
}

function Stat({ icon: Icon, tone, label, value }: { icon: React.ElementType; tone: string; label: string; value: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
      <span className={`inline-flex h-9 w-9 items-center justify-center rounded-lg ${tone}`}><Icon size={17} /></span>
      <div className="mt-2 text-2xl font-semibold leading-tight tabular-nums">{value}</div>
      <div className="text-xs text-[var(--muted)]">{label}</div>
    </div>
  );
}
