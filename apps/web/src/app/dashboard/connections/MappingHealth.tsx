'use client';

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { RefreshCw } from 'lucide-react';
import { api } from '@/lib/api';
import type { MappingFieldHealth, MappingPreviewRow } from '@/lib/types';

const LEVEL: Record<string, { label: string; tone: string }> = {
  Pass: { label: 'All mapped', tone: 'bg-emerald-100 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300' },
  Optional: { label: 'Optional', tone: 'bg-sky-100 text-sky-700 dark:bg-sky-950 dark:text-sky-300' },
  Warning: { label: 'Warning', tone: 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300' },
  Blocking: { label: 'Blocking', tone: 'bg-rose-100 text-rose-700 dark:bg-rose-950 dark:text-rose-300' },
};

const NOT_MAPPED = 'rounded bg-amber-100 px-1.5 py-0.5 text-[11px] font-medium text-amber-700 dark:bg-amber-950 dark:text-amber-300';

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

/** What a level means, in a sentence: the one word is a summary, not an explanation. */
function summary(level: string, tickets: number, unmapped: number) {
  if (level === 'Blocking') return 'No status is mapped for this connection, so the portal cannot tell finished work from open work except by the PSA\'s own wording.';
  if (unmapped > 0) return `${unmapped.toLocaleString()} of ${tickets.toLocaleString()} ${tickets === 1 ? 'ticket holds' : 'tickets hold'} a status or priority nothing maps. They show the PSA's own word for it.`;
  if (level === 'Warning') return 'Every value tickets hold is mapped, but a status the portal can be set to has no counterpart in the PSA.';
  if (level === 'Optional') return 'Every value tickets hold is mapped. What is left can be mapped and need not be.';
  return 'Everything the PSA lists and everything tickets hold is mapped.';
}

/**
 * How well a connection's mapping covers what its PSA actually sends: each status and priority
 * with what it becomes, how many tickets hold it, and - where nothing maps a status - whether the
 * portal is counting those tickets as open or finished. Worked out by the server from the tickets
 * themselves, each time it is asked.
 */
export function MappingHealth({ connectionId }: { connectionId: string }) {
  const qc = useQueryClient();
  const health = useQuery({
    queryKey: ['connection-mapping-health', connectionId],
    queryFn: () => api.connectionMappingHealth(connectionId),
    retry: false, staleTime: 0,
  });
  const preview = useQuery({
    queryKey: ['connection-mapping-preview', connectionId],
    queryFn: () => api.connectionMappingPreview(connectionId, 8),
    retry: false, staleTime: 0,
  });
  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['connection-mapping-health', connectionId] });
    qc.invalidateQueries({ queryKey: ['connection-mapping-preview', connectionId] });
  };
  const apply = useMutation({
    mutationFn: () => api.applyConnectionMapping(connectionId),
    onSuccess: () => {
      refresh();
      ['tickets', 'team', 'trend'].forEach((k) => qc.invalidateQueries({ queryKey: [k] }));
    },
  });

  if (health.isLoading) return <p className="text-sm text-[var(--muted)]">Checking the mapping…</p>;
  if (health.isError || !health.data) {
    return <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(health.error, 'The mapping could not be checked.')}</p>;
  }

  const h = health.data;
  const level = LEVEL[h.level] ?? { label: h.level, tone: 'bg-[var(--bg)] text-[var(--muted)]' };
  const outbound = h.outboundStatuses.filter((o) => o.problem !== null);
  // Something for "apply" to do: a value that IS mapped, on tickets still showing the PSA's word,
  // cannot be told from here - so the button is offered whenever there are tickets, and says what it did.
  const canApply = h.tickets > 0;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
            <span className={'rounded px-1.5 py-0.5 text-[11px] font-medium ' + level.tone}>{level.label}</span>
            Mapping health
          </p>
          <p className="mt-1 max-w-3xl text-sm text-[var(--muted)]">{summary(h.level, h.tickets, h.unmappedTickets)}</p>
          {h.notes.map((n) => <p key={n} className="mt-1 max-w-3xl text-xs text-amber-700 dark:text-amber-300">{n}</p>)}
        </div>
        <button onClick={refresh}
          className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)]">
          <RefreshCw size={13} className={health.isFetching ? 'animate-spin' : undefined} aria-hidden="true" /> Check again
        </button>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        {h.fields.map((f) => <FieldTable key={f.field} field={f} connectionId={connectionId} />)}
      </div>

      {outbound.length > 0 && (
        <div>
          <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">Statuses set in the portal</h3>
          <ul className="divide-y divide-[var(--border)] rounded-lg border border-[var(--border)]">
            {outbound.map((o) => (
              <li key={o.portalValue} className="flex flex-wrap items-baseline gap-x-3 gap-y-0.5 px-3 py-2">
                <span className="text-sm font-medium">{o.portalValue}</span>
                <span className="min-w-0 flex-1 text-xs text-[var(--muted)]">{o.problem}</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-2">
        <details className="rounded-lg border border-[var(--border)] px-3 py-2.5">
          <summary className="cursor-pointer text-sm font-medium">
            Technicians{' '}
            <span className="font-normal text-[var(--muted)]">
              {h.technicians.technicians === 0
                ? 'none seen yet'
                : `${h.technicians.linked} of ${h.technicians.technicians} linked to a portal user`}
            </span>
          </summary>
          <p className="mt-1.5 text-xs text-[var(--muted)]">
            Linking is optional. An unlinked PSA login is shown under the PSA&apos;s own name; linked, its tickets and time count as that person&apos;s.
            Links are made on a user&apos;s page, under PSA identity.
          </p>
          {h.technicians.unlinked.length > 0 && (
            <ul className="mt-1.5 space-y-0.5 text-xs">
              {h.technicians.unlinked.map((p) => (
                <li key={p.externalId} className="flex justify-between gap-3">
                  <span>{p.name ?? `Login ${p.externalId}`} <span className="text-[var(--faint)]">({p.externalId})</span></span>
                  <span className="tabular-nums text-[var(--muted)]">{p.tickets} {p.tickets === 1 ? 'ticket' : 'tickets'}</span>
                </li>
              ))}
            </ul>
          )}
        </details>
        {h.ticketsWithoutClient > 0 && (
          <p className="rounded-lg border border-[var(--border)] px-3 py-2.5 text-sm">
            <span className="font-medium tabular-nums">{h.ticketsWithoutClient.toLocaleString()}</span>{' '}
            <span className="text-[var(--muted)]">{h.ticketsWithoutClient === 1 ? 'ticket has' : 'tickets have'} no client the PSA would name. They sit under &quot;unknown&quot; until the PSA sends one.</span>
          </p>
        )}
      </div>

      <Sample rows={preview.data} loading={preview.isLoading} error={preview.isError ? message(preview.error, 'The sample could not be read.') : null} />

      <div className="flex flex-wrap items-center gap-3 border-t border-[var(--border)] pt-3">
        <a href={`/dashboard/mappings?connection=${connectionId}&tab=status`}
          className="rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90">
          Open Field Mapping
        </a>
        <button
          onClick={() => {
            if (window.confirm('Re-map the tickets already imported? Only tickets still showing the PSA\'s own word for a status or priority that a rule now maps are changed. A status someone set in the portal is left as it is.'))
              apply.mutate();
          }}
          disabled={!canApply || apply.isPending}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50"
        >
          {apply.isPending ? 'Applying…' : 'Apply the mapping to tickets already here'}
        </button>
        {apply.isError && <span role="alert" className="text-xs text-rose-600 dark:text-rose-400">{message(apply.error, 'That could not be done.')}</span>}
        {apply.data && (
          <span className="text-xs text-emerald-600 dark:text-emerald-400">
            {apply.data.statusesChanged + apply.data.prioritiesChanged === 0
              ? 'Nothing to change: no imported ticket shows a value a rule now maps.'
              : `Changed ${apply.data.statusesChanged} ${apply.data.statusesChanged === 1 ? 'status' : 'statuses'} and ${apply.data.prioritiesChanged} ${apply.data.prioritiesChanged === 1 ? 'priority' : 'priorities'}: ${apply.data.changes.join('; ')}.`}
          </span>
        )}
      </div>
    </div>
  );
}

function FieldTable({ field, connectionId }: { field: MappingFieldHealth; connectionId: string }) {
  return (
    <div className="overflow-x-auto rounded-lg border border-[var(--border)]">
      <table className="w-full text-left text-sm">
        <caption className="border-b border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-left text-xs">
          <span className="font-semibold">{field.name}</span>{' '}
          <span className="text-[var(--muted)]">
            {field.values === 0 ? 'none seen' : `${field.mapped} of ${field.values} mapped${field.mappedPct === null ? '' : ` (${field.mappedPct}%)`}`}
            {field.unmappedTickets > 0 && ` · ${field.unmappedTickets.toLocaleString()} ${field.unmappedTickets === 1 ? 'ticket' : 'tickets'} unmapped`}
          </span>
        </caption>
        <thead className="text-xs text-[var(--muted)]">
          <tr>
            <th className="px-3 py-1.5 font-medium">In the PSA</th>
            <th className="px-3 py-1.5 text-right font-medium">Tickets</th>
            <th className="px-3 py-1.5 font-medium">In the portal</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-[var(--border)]">
          {field.items.length === 0 && <tr><td colSpan={3} className="px-3 py-2 text-xs text-[var(--muted)]">Nothing has arrived yet.</td></tr>}
          {field.items.map((i) => (
            <tr key={i.value} className="align-top">
              <td className="px-3 py-1.5">
                {i.value}
                {!i.listedByPsa && <span className="ml-1.5 text-[11px] text-[var(--faint)]">on tickets only</span>}
              </td>
              <td className="px-3 py-1.5 text-right tabular-nums">{i.tickets.toLocaleString()}</td>
              <td className="px-3 py-1.5">
                {i.mapsTo !== null && i.unmappedTickets === 0 ? (
                  <>{i.mapsTo}{i.byFallbackRule && <span className="ml-1.5 text-xs text-[var(--faint)]">by a catch-all rule</span>}</>
                ) : (
                  <>
                    <a href={`/dashboard/mappings?connection=${connectionId}&tab=${field.field}`} className={NOT_MAPPED + ' hover:underline'}>
                      {i.mapsTo === null ? 'Not mapped' : `Not mapped on ${i.unmappedTickets.toLocaleString()}`}
                    </a>
                    {i.treatedAs && (
                      <span className="mt-0.5 block text-xs text-[var(--muted)]">
                        {i.treatedAs === 'Open' ? 'Those tickets count as open work.' : 'Those tickets count as finished, from the PSA\'s wording.'}
                      </span>
                    )}
                  </>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** A handful of tickets with what the rules make of each. Also used by the add-connection wizard. */
export function Sample({ rows, loading, error }: { rows?: MappingPreviewRow[]; loading: boolean; error: string | null }) {
  if (loading) return <p className="text-sm text-[var(--muted)]">Reading a sample of tickets…</p>;
  if (error) return <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{error}</p>;
  if (!rows || rows.length === 0) return null;
  const fromPsa = rows.some((r) => r.readFromPsa);
  const cell = (source: string | null, mapped: string | null) => source === null || source === ''
    ? <span className="text-[var(--faint)]">—</span>
    : (
      <span className="flex flex-wrap items-center gap-x-1.5 gap-y-0.5">
        <span>{source}</span>
        <span aria-hidden="true" className="text-[var(--faint)]">→</span>
        {mapped === null ? <span className={NOT_MAPPED}>Not mapped</span> : <span className="font-medium">{mapped}</span>}
      </span>
    );
  return (
    <div>
      <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-[var(--faint)]">
        Sample tickets
        <span className="ml-2 font-normal normal-case tracking-normal text-[var(--muted)]">
          {fromPsa ? 'read from the PSA just now and not kept' : 'the most recently synced'}
        </span>
      </h3>
      <div className="overflow-x-auto rounded-lg border border-[var(--border)]">
        <table className="w-full min-w-[560px] text-left text-sm">
          <thead className="bg-[var(--bg)] text-xs text-[var(--muted)]">
            <tr>
              <th className="px-3 py-1.5 font-medium">Ticket</th>
              <th className="px-3 py-1.5 font-medium">Status</th>
              <th className="px-3 py-1.5 font-medium">Priority</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-[var(--border)]">
            {rows.map((r) => (
              <tr key={r.reference + r.title} className="align-top">
                <td className="px-3 py-1.5"><span className="tabular-nums text-[var(--muted)]">{r.reference}</span> {r.title}</td>
                <td className="px-3 py-1.5">{cell(r.sourceStatus, r.mappedStatus)}</td>
                <td className="px-3 py-1.5">{cell(r.sourcePriority, r.mappedPriority)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
