'use client';

import Link from 'next/link';
import { Suspense, useMemo, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { Plus, Inbox, Search, X } from 'lucide-react';
import { api } from '@/lib/api';
import { StatusBadge, PriorityBadge, SourceBadge } from '@/components/badges';
import type { TicketListItem } from '@/lib/types';
import { isResolvedStatus } from '@/lib/status';

const ALL = '__all__';

/** Distinct, sorted values for a column — the filter options come from the data itself, so they
 *  stay correct for any PSA without hard-coding provider vocabulary. */
function optionsFor(rows: TicketListItem[], pick: (t: TicketListItem) => string | null | undefined) {
  return Array.from(new Set(rows.map((r) => pick(r) ?? '').filter(Boolean))).sort();
}

function Select({ label, value, onChange, options }: {
  label: string; value: string; onChange: (v: string) => void; options: string[];
}) {
  return (
    <label className="flex items-center gap-1.5 text-xs">
      <span className="text-[var(--muted)]">{label}</span>
      <select value={value} onChange={(e) => onChange(e.target.value)}
        className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1.5 text-sm outline-none focus:border-brand">
        <option value={ALL}>All</option>
        {options.map((o) => <option key={o} value={o}>{o}</option>)}
      </select>
    </label>
  );
}

/**
 * Wrapped in Suspense because useSearchParams() below opts the page out of the static shell
 * otherwise — the build fails outright rather than degrading, which is the better failure but
 * still needs this boundary.
 */
export default function TicketsPage() {
  return (
    <Suspense fallback={<div className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}>
      <TicketsList />
    </Suspense>
  );
}

function TicketsList() {
  const { data, isLoading, isError } = useQuery({ queryKey: ['tickets'], queryFn: api.listTickets });

  // Filters can arrive in the URL, so a figure on the dashboard can link to the tickets behind it
  // — and so the resulting view is a link someone can send to a colleague. `view` handles the two
  // that are not a single status: "open" and "resolved" are each a SET of statuses, and which
  // statuses those are is a decision that already lives in isResolvedStatus.
  const params = useSearchParams();
  const view = params.get('view');

  const [q, setQ] = useState('');
  const [status, setStatus] = useState(() => params.get('status') ?? ALL);
  const [priority, setPriority] = useState(() => params.get('priority') ?? ALL);
  // Company arrives by NAME, not id: this list filters on the name it displays, and a link that
  // carried an id would have to resolve it before it could select anything.
  const [company, setCompany] = useState(() => params.get('company') ?? ALL);
  const [source, setSource] = useState(ALL);
  const [queue, setQueue] = useState(ALL);

  const rows = useMemo(() => data ?? [], [data]);
  const filtered = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return rows.filter((t) =>
      (!needle || (t.title ?? '').toLowerCase().includes(needle) || (t.externalTicketId ?? '').toLowerCase().includes(needle))
      && (status === ALL || t.portalStatus === status)
      && (view !== 'open' || !isResolvedStatus(t.portalStatus))
      && (view !== 'resolved' || isResolvedStatus(t.portalStatus))
      && (priority === ALL || t.portalPriority === priority)
      && (source === ALL || (t.connectionName ?? '') === source)
      && (company === ALL || (t.customerName ?? '') === company)
      && (queue === ALL || (t.queueOrBoard ?? '') === queue));
  }, [rows, q, status, priority, source, company, queue, view]);

  const active = q.trim() !== '' || view !== null
    || [status, priority, source, company, queue].some((v) => v !== ALL);
  const router = useRouter();
  const clear = () => {
    setQ(''); setStatus(ALL); setPriority(ALL); setSource(ALL); setCompany(ALL); setQueue(ALL);
    // Drops `view` as well. Leaving it would clear every visible control and still filter the list,
    // which reads as the page ignoring the button.
    if (params.toString()) router.replace('/dashboard/tickets');
  };

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">Tickets</h1>
          <p className="text-sm text-[var(--muted)]">Your support requests across all connected systems.</p>
        </div>
        <Link
          href="/dashboard/tickets/new"
          className="inline-flex items-center gap-2 rounded-lg bg-brand px-3.5 py-2 text-sm font-medium text-brand-fg hover:opacity-90"
        >
          <Plus size={16} /> New ticket
        </Link>
      </div>

      {isLoading && <SkeletonTable />}

      {isError && (
        <EmptyState
          title="No tickets to show"
          body="Connect your PSA under PSA Connections, then run a sync to load tickets. They appear here after the first successful sync."
        />
      )}

      {!isError && data && rows.length === 0 && (
        <EmptyState title="No tickets yet" body="Create a ticket, or run a sync from PSA Connections to pull them from your PSA." />
      )}

      {!isError && rows.length > 0 && (
        <div className="flex flex-wrap items-center gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3">
          <div className="relative">
            <Search size={14} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-[var(--faint)]" />
            <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search title or reference…"
              className="w-56 rounded-lg border border-[var(--border)] bg-[var(--bg)] py-1.5 pl-8 pr-3 text-sm outline-none focus:border-brand" />
          </div>
          <Select label="Status" value={status} onChange={setStatus} options={optionsFor(rows, (t) => t.portalStatus)} />
          <Select label="Priority" value={priority} onChange={setPriority} options={optionsFor(rows, (t) => t.portalPriority)} />
          <Select label="Source" value={source} onChange={setSource} options={optionsFor(rows, (t) => t.connectionName)} />
          <Select label="Company" value={company} onChange={setCompany} options={optionsFor(rows, (t) => t.customerName)} />
          <Select label="Queue" value={queue} onChange={setQueue} options={optionsFor(rows, (t) => t.queueOrBoard)} />
          <span className="ml-auto text-xs text-[var(--muted)]">
            {filtered.length === rows.length ? `${rows.length} tickets` : `${filtered.length} of ${rows.length} tickets`}
          </span>
          {active && (
            <button onClick={clear} className="inline-flex items-center gap-1 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]">
              <X size={13} /> Clear
            </button>
          )}
        </div>
      )}

      {!isError && rows.length > 0 && filtered.length === 0 && (
        <EmptyState title="No tickets match your filters" body="Try a different search term, or clear the filters to see everything." />
      )}

      {!isError && filtered.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <table className="w-full text-sm">
            <thead className="text-left text-xs uppercase tracking-wide text-[var(--muted)]">
              <tr className="border-b border-[var(--border)]">
                <th className="px-4 py-3 font-medium">Title</th>
                <th className="px-4 py-3 font-medium">Company</th>
                <th className="px-4 py-3 font-medium">Source</th>
                <th className="px-4 py-3 font-medium">Status</th>
                <th className="px-4 py-3 font-medium">Priority</th>
                <th className="px-4 py-3 font-medium">Queue</th>
                <th className="px-4 py-3 font-medium">Created</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((t) => (
                <tr key={t.id} className="border-b border-[var(--border)] last:border-0 hover:bg-[var(--bg)]">
                  <td className="px-4 py-3">
                    <Link href={`/dashboard/tickets/${t.id}`} className="font-medium hover:underline">
                      {t.title}
                    </Link>
                    {t.externalTicketId && (
                      <span className="ml-2 text-xs text-[var(--muted)]">#{t.externalTicketId}</span>
                    )}
                  </td>
                  <td className="px-4 py-3">{t.customerName ?? '—'}</td>
                  <td className="px-4 py-3"><SourceBadge provider={t.provider} connectionName={t.connectionName} /></td>
                  <td className="px-4 py-3"><StatusBadge status={t.portalStatus} /></td>
                  <td className="px-4 py-3"><PriorityBadge priority={t.portalPriority} /></td>
                  <td className="px-4 py-3 text-[var(--muted)]">{t.queueOrBoard ?? '—'}</td>
                  <td className="px-4 py-3 text-[var(--muted)]">{new Date(t.createdAt).toLocaleDateString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function SkeletonTable() {
  return (
    <div className="space-y-2">
      {[0, 1, 2, 3].map((i) => (
        <div key={i} className="h-12 animate-pulse rounded-lg bg-[var(--surface)] border border-[var(--border)]" />
      ))}
    </div>
  );
}

function EmptyState({ title, body }: { title: string; body: string }) {
  return (
    <div className="flex flex-col items-center rounded-xl border border-dashed border-[var(--border)] px-6 py-14 text-center">
      <Inbox className="mb-3 text-[var(--faint)]" size={28} />
      <h3 className="font-medium">{title}</h3>
      <p className="mt-1 max-w-sm text-sm text-[var(--muted)]">{body}</p>
    </div>
  );
}
