'use client';

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, CheckCircle2, Clock, RefreshCw } from 'lucide-react';
import { api } from '@/lib/api';
import type { SyncFailure, SyncRun } from '@/lib/types';

// SyncRunStatus, as the server names it.
const RUN_STATUS: Record<string, { label: string; tone: string }> = {
  Running: { label: 'Running', tone: 'text-sky-600 dark:text-sky-400' },
  Succeeded: { label: 'Completed', tone: 'text-emerald-600 dark:text-emerald-400' },
  // Stopped part-way and will carry on from the same place next time.
  Partial: { label: 'Partly done', tone: 'text-amber-600 dark:text-amber-400' },
  Failed: { label: 'Failed', tone: 'text-rose-600 dark:text-rose-400' },
  // The app stopped under it (a restart, a deploy). Nothing is lost: the next run starts where this began.
  Abandoned: { label: 'Interrupted', tone: 'text-[var(--muted)]' },
};

const TRIGGER: Record<string, string> = { Scheduled: 'Automatic', Manual: 'Sync now', ManualFull: 'Re-sync all' };

// What the sync was doing with the record when it failed.
const OPERATION: Record<string, string> = { apply: 'Ticket', notes: 'Notes', time: 'Time entries', attachments: 'Attachments' };

const when = (iso: string | null) =>
  iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '—';

function duration(run: SyncRun) {
  if (!run.finishedAt) return '';
  const secs = Math.max(0, Math.round((new Date(run.finishedAt).getTime() - new Date(run.startedAt).getTime()) / 1000));
  return secs < 60 ? `${secs}s` : `${Math.floor(secs / 60)}m ${secs % 60}s`;
}

/**
 * What a connection's sync has done and what it could not do: how far it has read, its last runs,
 * and the records it is still trying to get through. The two things a person may decide about a
 * failed record are here too — try it again now, or stop trying.
 */
export function SyncActivity({ connectionId }: { connectionId: string }) {
  const qc = useQueryClient();
  const state = useQuery({
    queryKey: ['connection-sync-state', connectionId],
    queryFn: () => api.connectionSyncState(connectionId),
    retry: false,
    // While a run is going the numbers are moving; otherwise there is nothing to watch.
    refetchInterval: (q) => (q.state.data?.running ? 5000 : false),
  });
  const failures = useQuery({
    queryKey: ['connection-sync-failures', connectionId],
    queryFn: () => api.connectionSyncFailures(connectionId),
    retry: false,
  });

  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['connection-sync-state', connectionId] });
    qc.invalidateQueries({ queryKey: ['connection-sync-failures', connectionId] });
  };
  const decide = useMutation({
    mutationFn: (v: { failureId: string; action: 'retry' | 'dismiss' }) =>
      v.action === 'retry' ? api.retrySyncFailure(connectionId, v.failureId) : api.dismissSyncFailure(connectionId, v.failureId),
    onSuccess: refresh,
  });

  if (state.isLoading) return <p className="text-sm text-[var(--muted)]">Loading sync activity…</p>;
  if (state.isError || !state.data) {
    return (
      <p className="text-sm text-rose-600 dark:text-rose-400">
        {state.error instanceof Error ? state.error.message : 'Sync activity could not be loaded.'}
      </p>
    );
  }

  const s = state.data;
  const open = failures.data ?? [];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <ul className="space-y-1 text-sm">
          <li className="flex items-center gap-2">
            <Clock size={14} className="shrink-0 text-[var(--faint)]" aria-hidden="true" />
            {s.watermark
              ? <span>Everything changed in the PSA before <strong className="font-semibold">{when(s.watermark)}</strong> has been read.</span>
              : <span>Nothing has been read from the PSA yet.</span>}
          </li>
          {s.running && (
            <li className="flex items-center gap-2 text-sky-600 dark:text-sky-400">
              <RefreshCw size={14} className="shrink-0 animate-spin" aria-hidden="true" /> A sync is running now.
            </li>
          )}
          {s.readInProgress && (
            <li className="flex items-center gap-2 text-[var(--muted)]">
              <RefreshCw size={14} className="shrink-0" aria-hidden="true" />
              A long read is under way — {s.pagesReadSoFar} {s.pagesReadSoFar === 1 ? 'page' : 'pages'} so far. It carries on by itself, run after run.
            </li>
          )}
          {s.openFailures === 0 && s.needsReview === 0 ? (
            <li className="flex items-center gap-2 text-emerald-600 dark:text-emerald-400">
              <CheckCircle2 size={14} className="shrink-0" aria-hidden="true" /> No record is waiting to be read again.
            </li>
          ) : (
            <li className="flex items-center gap-2 text-amber-700 dark:text-amber-300">
              <AlertTriangle size={14} className="shrink-0" aria-hidden="true" />
              <span>
                {s.openFailures > 0 && `${s.openFailures} ${s.openFailures === 1 ? 'record is' : 'records are'} waiting to be tried again`}
                {s.openFailures > 0 && s.needsReview > 0 && ' · '}
                {s.needsReview > 0 && `${s.needsReview} ${s.needsReview === 1 ? 'needs' : 'need'} your decision`}
              </span>
            </li>
          )}
        </ul>
        <button
          onClick={refresh}
          className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)]"
        >
          <RefreshCw size={13} className={state.isFetching ? 'animate-spin' : undefined} /> Refresh
        </button>
      </div>

      {open.length > 0 && (
        <div>
          <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">Records that could not be read</h3>
          <ul className="divide-y divide-[var(--border)] rounded-lg border border-[var(--border)]">
            {open.map((f) => (
              <FailureRow
                key={f.id}
                f={f}
                busy={decide.isPending && decide.variables?.failureId === f.id}
                onRetry={() => decide.mutate({ failureId: f.id, action: 'retry' })}
                onDismiss={() => {
                  if (window.confirm(`Stop trying ${f.entity} ${f.externalId}? It stays as it is in the portal until it next changes in the PSA.`))
                    decide.mutate({ failureId: f.id, action: 'dismiss' });
                }}
              />
            ))}
          </ul>
          {decide.isError && (
            <p className="mt-1.5 text-xs text-rose-600 dark:text-rose-400">
              {decide.error instanceof Error ? decide.error.message : 'That could not be done.'}
            </p>
          )}
        </div>
      )}

      <div>
        <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">Recent runs</h3>
        {s.runs.length === 0 ? (
          <p className="text-sm text-[var(--muted)]">No sync has run yet.</p>
        ) : (
          <div className="overflow-x-auto rounded-lg border border-[var(--border)]">
            <table className="w-full min-w-[640px] text-left text-sm">
              <thead className="bg-[var(--bg)] text-xs text-[var(--muted)]">
                <tr>
                  <th className="px-3 py-2 font-medium">Started</th>
                  <th className="px-3 py-2 font-medium">Started by</th>
                  <th className="px-3 py-2 font-medium">Result</th>
                  <th className="px-3 py-2 text-right font-medium">Read</th>
                  <th className="px-3 py-2 text-right font-medium">New</th>
                  <th className="px-3 py-2 text-right font-medium">Updated</th>
                  <th className="px-3 py-2 text-right font-medium">Not read</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-[var(--border)]">
                {s.runs.map((r) => {
                  const status = RUN_STATUS[r.status] ?? { label: r.status, tone: '' };
                  const detail = r.error ?? r.notice;
                  return (
                    <tr key={r.id} className="align-top">
                      <td className="whitespace-nowrap px-3 py-2">
                        {when(r.startedAt)}
                        {duration(r) && <span className="ml-1.5 text-xs text-[var(--faint)]">{duration(r)}</span>}
                      </td>
                      <td className="px-3 py-2 text-[var(--muted)]">
                        {TRIGGER[r.trigger] ?? r.trigger}
                        {r.requestedBy && <span className="block text-xs text-[var(--faint)]">{r.requestedBy}</span>}
                      </td>
                      <td className="px-3 py-2">
                        <span className={'font-medium ' + status.tone}>{status.label}</span>
                        {detail && <span className="mt-0.5 block max-w-md text-xs text-[var(--muted)]">{detail}</span>}
                      </td>
                      <td className="px-3 py-2 text-right tabular-nums">{r.fetched.toLocaleString()}</td>
                      <td className="px-3 py-2 text-right tabular-nums">{r.created.toLocaleString()}</td>
                      <td className="px-3 py-2 text-right tabular-nums">{r.updated.toLocaleString()}</td>
                      <td className={'px-3 py-2 text-right tabular-nums ' + (r.failedRecords > 0 ? 'text-amber-700 dark:text-amber-300' : '')}>
                        {r.failedRecords.toLocaleString()}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

function FailureRow({ f, busy, onRetry, onDismiss }: { f: SyncFailure; busy: boolean; onRetry: () => void; onDismiss: () => void }) {
  const needsReview = f.status === 'NeedsReview';
  return (
    <li className="flex flex-wrap items-start gap-3 px-3 py-2.5">
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">
          {OPERATION[f.operation] ?? f.operation} · {f.entity} {f.externalId}
          <span className={'ml-2 rounded px-1.5 py-0.5 text-[10px] font-medium ' + (needsReview
            ? 'bg-rose-100 text-rose-700 dark:bg-rose-950 dark:text-rose-300'
            : 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300')}>
            {needsReview ? 'Needs your decision' : 'Will be tried again'}
          </span>
        </p>
        <p className="mt-0.5 break-words text-xs text-[var(--muted)]">{f.message}</p>
        <p className="mt-0.5 text-xs text-[var(--faint)]">
          Tried {f.attempts} {f.attempts === 1 ? 'time' : 'times'} · last {when(f.lastFailedAt)}
          {f.nextAttemptAt && !needsReview && ` · next ${when(f.nextAttemptAt)}`}
        </p>
      </div>
      <div className="flex shrink-0 gap-2">
        <button
          onClick={onRetry}
          disabled={busy}
          className="rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50"
        >
          {/* Not "now": it goes to the front of the next run, which is what the server does with it. */}
          Try on next sync
        </button>
        <button
          onClick={onDismiss}
          disabled={busy}
          className="rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] disabled:opacity-50"
        >
          Stop trying
        </button>
      </div>
    </li>
  );
}
