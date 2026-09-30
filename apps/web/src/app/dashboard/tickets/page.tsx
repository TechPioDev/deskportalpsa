'use client';

import Link from 'next/link';
import { MyApprovalsBanner } from '@/components/MyApprovalsBanner';
import { Suspense, useEffect, useMemo, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus, Inbox, Search, X, Eye, Users, ChevronLeft, ChevronRight } from 'lucide-react';
import { api, type TicketPageParams } from '@/lib/api';
import { StatusBadge, PriorityBadge, SourceBadge } from '@/components/badges';
import { TicketViewBar, EMPTY_FILTERS } from '@/components/TicketViewBar';
import type { SavedViewFilters } from '@/lib/types';
import { fmtHours } from '@/lib/format';

const ALL = '__all__';

/** Rows per page. Enough to scan, few enough that the page answers at once however many tickets there are. */
const PAGE_SIZE = 50;

const KINDS: { value: string; label: string }[] = [
  { value: 'psa', label: 'From a PSA' },
  { value: 'internal', label: 'Team boards' },
  { value: 'monitoring', label: 'Monitoring' },
];

function Select({ label, value, onChange, options, labelFor, title }: {
  label: string; value: string; onChange: (v: string) => void; options: string[];
  labelFor?: (v: string) => string; title?: string;
}) {
  return (
    <label className="flex items-center gap-1.5 text-xs" title={title}>
      <span className="text-[var(--muted)]">{label}</span>
      <select value={value} onChange={(e) => onChange(e.target.value)}
        className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1.5 text-sm outline-none focus:border-brand">
        <option value={ALL}>All</option>
        {options.map((o) => <option key={o} value={o}>{labelFor ? labelFor(o) : o}</option>)}
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

/** The value once it has stopped changing for a moment, so typing asks the server once, not per key. */
function useSettled<T>(value: T, ms = 300): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setSettled(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return settled;
}

function TicketsList() {
  // Filters can arrive in the URL, so a figure on the dashboard can link to the tickets behind it
  // — and so the resulting view is a link someone can send to a colleague. `view` handles the two
  // that are not a single status: "open" and "resolved" are each a SET of statuses.
  const params = useSearchParams();
  const view = params.get('view');
  // A window on the date the ticket was RAISED - the same axis the client-workload figures use.
  const from = params.get('from');

  const [q, setQ] = useState(() => params.get('q') ?? '');
  const [status, setStatus] = useState(() => params.get('status') ?? ALL);
  const [priority, setPriority] = useState(() => params.get('priority') ?? ALL);
  // Company arrives by NAME, not id: the list filters on the name it displays.
  const [company, setCompany] = useState(() => params.get('company') ?? ALL);
  const [source, setSource] = useState(ALL);
  const [queue, setQueue] = useState(ALL);
  const [kind, setKind] = useState(() => params.get('kind') ?? ALL);
  // A person KEY ("u:<portal user>" or "x:<PSA resource>"), not a name: two people can share a name.
  const [tech, setTech] = useState(() => params.get('tech') ?? ALL);

  // The view filters that are not a column: open/resolved, and the questions about the person reading.
  const [openness, setOpenness] = useState<string | null>(() => view);
  const [mine, setMine] = useState(() => params.get('mine') === '1');
  const [following, setFollowing] = useState(() => params.get('following') === '1');
  const [unassigned, setUnassigned] = useState(() => params.get('unassigned') === '1');
  const [overdue, setOverdue] = useState(() => params.get('overdue') === '1');
  const [dueSoon, setDueSoon] = useState(() => params.get('due') === 'soon');
  // Resolved work waiting for a board lead: arrives from the workload page, cleared with the rest.
  const [reviewPending, setReviewPending] = useState(() => params.get('review') === 'pending');
  const [pageIndex, setPageIndex] = useState(0);

  const settledQ = useSettled(q.trim());
  const query: TicketPageParams = {
    q: settledQ || undefined,
    status: status === ALL ? undefined : status,
    priority: priority === ALL ? undefined : priority,
    company: company === ALL ? undefined : company,
    queue: queue === ALL ? undefined : queue,
    source: source === ALL ? undefined : source,
    kind: kind === ALL ? undefined : kind,
    person: tech === ALL ? undefined : tech,
    openness, mine, following, unassigned, overdue, dueSoon,
    from: from ?? undefined,
    review: reviewPending ? 'pending' : undefined,
  };
  const filterKey = JSON.stringify(query);
  // Any change of filter starts from the first page: page 3 of a different question is meaningless.
  useEffect(() => { setPageIndex(0); }, [filterKey]);

  const { data, isLoading, isError, isFetching } = useQuery({
    queryKey: ['tickets', 'page', filterKey, pageIndex],
    queryFn: () => api.ticketPage({ ...query, skip: pageIndex * PAGE_SIZE, take: PAGE_SIZE }),
    // The old page stays on screen while the next one loads, instead of flashing empty.
    placeholderData: keepPreviousData,
  });
  const { data: facets } = useQuery({ queryKey: ['tickets', 'facets'], queryFn: api.ticketFacets, staleTime: 60_000 });

  const rows = useMemo(() => data?.items ?? [], [data]);
  const total = data?.total ?? 0;

  // Previous/Next on a ticket steps through what is on screen here.
  const qc = useQueryClient();
  useEffect(() => { if (rows.length) qc.setQueryData(['ticket-nav'], rows.map((t) => t.id)); }, [rows, qc]);

  // Everyone who holds or logged time on a ticket the caller can see. A client's list carries none,
  // and then neither the filter nor the Assignee column is offered.
  const people = facets?.people ?? [];
  const hasPeople = people.length > 0 || rows.some((t) => t.people !== null);
  const techNames = useMemo(() => new Map(people.map((p) => [p.key, p.name])), [people]);
  const techOptions = useMemo(() => {
    const keys = people.map((p) => p.key);
    // A key from a link that no ticket carries must still be selectable, or the control would read
    // "All" while the list stayed filtered.
    return tech !== ALL && !techNames.has(tech) ? [tech, ...keys] : keys;
  }, [people, techNames, tech]);
  const withCurrent = (options: string[] | undefined, current: string) =>
    current !== ALL && !(options ?? []).includes(current) ? [current, ...(options ?? [])] : (options ?? []);

  const anyTickets = (facets?.statuses.length ?? 0) > 0 || total > 0;
  const active = q.trim() !== '' || openness !== null || from !== null
    || mine || following || unassigned || overdue || dueSoon || reviewPending
    || [status, priority, source, company, queue, tech, kind].some((v) => v !== ALL);
  const router = useRouter();
  const clear = () => {
    setQ(''); setStatus(ALL); setPriority(ALL); setSource(ALL); setCompany(ALL); setQueue(ALL); setTech(ALL); setKind(ALL);
    setOpenness(null); setMine(false); setFollowing(false); setUnassigned(false); setOverdue(false); setDueSoon(false);
    setReviewPending(false);
    // Drops the URL's own filters as well. Leaving them would clear every visible control and still
    // filter the list, which reads as the page ignoring the button.
    if (params.toString()) router.replace('/dashboard/tickets');
  };

  // The filter set as a view sees it, and the one way back.
  const filters: SavedViewFilters = {
    ...EMPTY_FILTERS,
    search: q.trim() || null,
    status: status === ALL ? null : status,
    priority: priority === ALL ? null : priority,
    company: company === ALL ? null : company,
    queue: queue === ALL ? null : queue,
    connectionName: source === ALL ? null : source,
    personKey: tech === ALL ? null : tech,
    openness,
    mineOnly: mine,
    followingOnly: following,
    unassignedOnly: unassigned,
    overdueOnly: overdue,
    dueSoonOnly: dueSoon,
  };
  const applyView = (f: SavedViewFilters) => {
    setQ(f.search ?? '');
    setStatus(f.status ?? ALL);
    setPriority(f.priority ?? ALL);
    setCompany(f.company ?? ALL);
    setQueue(f.queue ?? ALL);
    setSource(f.connectionName ?? ALL);
    setTech(f.personKey ?? ALL);
    setOpenness(f.openness ?? null);
    setMine(f.mineOnly);
    setFollowing(f.followingOnly);
    setUnassigned(f.unassignedOnly);
    setOverdue(f.overdueOnly);
    setDueSoon(f.dueSoonOnly);
    // A view and a URL filter would fight; the view wins, because it is the thing just clicked.
    if (params.toString()) router.replace('/dashboard/tickets');
  };

  const first = total === 0 ? 0 : pageIndex * PAGE_SIZE + 1;
  const last = Math.min(total, (pageIndex + 1) * PAGE_SIZE);
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));

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

      <MyApprovalsBanner />

      {!isError && anyTickets && (
        <TicketViewBar filters={filters} onApply={applyView} canSave={hasPeople} />
      )}

      {isLoading && <SkeletonTable />}

      {isError && (
        <EmptyState
          title="No tickets to show"
          body="Connect your PSA under PSA Connections, then run a sync to load tickets. They appear here after the first successful sync."
        />
      )}

      {!isError && data && !anyTickets && !active && (
        <EmptyState title="No tickets yet" body="Create a ticket, or run a sync from PSA Connections to pull them from your PSA." />
      )}

      {!isError && (anyTickets || active) && (
        <div className="flex flex-wrap items-center gap-3 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3">
          <div className="relative">
            <Search size={14} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-[var(--faint)]" />
            <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter by title, number or customer…"
              aria-label="Filter tickets"
              className="w-56 rounded-lg border border-[var(--border)] bg-[var(--bg)] py-1.5 pl-8 pr-3 text-sm outline-none focus:border-brand" />
          </div>
          <Select label="Status" value={status} onChange={setStatus} options={withCurrent(facets?.statuses, status)} />
          <Select label="Priority" value={priority} onChange={setPriority} options={withCurrent(facets?.priorities, priority)} />
          <Select label="Kind" value={kind} onChange={setKind} options={KINDS.map((k) => k.value)}
            labelFor={(v) => KINDS.find((k) => k.value === v)?.label ?? v} title="Where the ticket came from" />
          <Select label="Source" value={source} onChange={setSource} options={withCurrent(facets?.sources, source)} />
          <Select label="Company" value={company} onChange={setCompany} options={withCurrent(facets?.companies, company)} />
          <Select label="Queue" value={queue} onChange={setQueue} options={withCurrent(facets?.queues, queue)} />
          {hasPeople && (
            <Select label="Technician" value={tech} onChange={setTech} options={techOptions}
              labelFor={(k) => techNames.get(k) ?? 'Selected technician'}
              title="Tickets this person holds, or logged time on" />
          )}
          <span className="ml-auto text-xs text-[var(--muted)]" aria-live="polite">
            {total === 0 ? 'No tickets' : `${first}–${last} of ${total} ticket${total === 1 ? '' : 's'}`}
            {data && data.hoursWorked > 0 && <span> · {fmtHours(data.hoursWorked)} worked, {fmtHours(data.hoursBillable)} billable</span>}
          </span>
          {reviewPending && (
            <button type="button" onClick={() => setReviewPending(false)} aria-label="Stop showing only work awaiting review"
              className="inline-flex items-center gap-1 rounded-full bg-amber-100 px-2.5 py-1 text-xs font-medium text-amber-900 hover:bg-amber-200 dark:bg-amber-950/50 dark:text-amber-200">
              Awaiting review <X size={12} />
            </button>
          )}
          {active && (
            <button onClick={clear} className="inline-flex items-center gap-1 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]">
              <X size={13} /> Clear
            </button>
          )}
        </div>
      )}

      {!isError && data && total === 0 && (anyTickets || active) && (
        <EmptyState title="No tickets match your filters" body="Try a different search term, or clear the filters to see everything." />
      )}

      {!isError && rows.length > 0 && (
        <div className={`overflow-x-auto rounded-xl border border-[var(--border)] bg-[var(--surface)] ${isFetching ? 'opacity-70' : ''}`}>
          <table className="w-full text-sm">
            <thead className="text-left text-xs uppercase tracking-wide text-[var(--muted)]">
              <tr className="border-b border-[var(--border)]">
                <th className="px-4 py-3 font-medium">Title</th>
                <th className="px-4 py-3 font-medium">Company</th>
                <th className="px-4 py-3 font-medium">Source</th>
                <th className="px-4 py-3 font-medium">Status</th>
                <th className="px-4 py-3 font-medium">Priority</th>
                <th className="px-4 py-3 font-medium">Queue</th>
                {hasPeople && <th className="px-4 py-3 font-medium">Assignee</th>}
                <th className="px-4 py-3 text-right font-medium">Worked</th>
                <th className="px-4 py-3 font-medium">Created</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((t) => (
                <tr key={t.id} className="border-b border-[var(--border)] last:border-0 hover:bg-[var(--bg)]">
                  <td className="px-4 py-3">
                    <Link href={`/dashboard/tickets/${t.id}`} className="font-medium hover:underline">
                      {t.title}
                    </Link>
                    {(t.externalTicketId ?? t.number) && (
                      <span className="ml-2 text-xs text-[var(--muted)]">{t.externalTicketId ? `#${t.externalTicketId}` : t.number}</span>
                    )}
                  </td>
                  <td className="px-4 py-3">{t.customerName ?? '—'}</td>
                  <td className="px-4 py-3"><SourceBadge provider={t.provider} connectionName={t.connectionName} origin={t.origin} /></td>
                  <td className="px-4 py-3"><StatusBadge status={t.portalStatus} /></td>
                  <td className="px-4 py-3"><PriorityBadge priority={t.portalPriority} /></td>
                  <td className="px-4 py-3 text-[var(--muted)]">{t.queueOrBoard ?? '—'}</td>
                  {hasPeople && (
                    <td className="px-4 py-3 text-[var(--muted)]">
                      {/* The team as well as the person: a ticket with Level 2 and nobody on it yet
                          is not the same thing as a ticket nobody has looked at. */}
                      {t.people?.find((p) => p.holds)?.name
                        ?? (t.assignedTeamName ? <span className="inline-flex items-center gap-1"><Users size={12} /> {t.assignedTeamName}</span> : '—')}
                      {t.following && <Eye size={12} className="ml-1.5 inline align-[-1px] text-[var(--faint)]" aria-label="You follow this ticket" />}
                    </td>
                  )}
                  <td className="px-4 py-3 text-right tabular-nums text-[var(--muted)]">{t.timeWorkedHours > 0 ? fmtHours(t.timeWorkedHours) : '—'}</td>
                  {/* The raise date, not the import date: the from-filter works on this one. */}
                  <td className="px-4 py-3 text-[var(--muted)]">{new Date(t.raisedAt ?? t.createdAt).toLocaleDateString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {!isError && total > PAGE_SIZE && (
        <nav aria-label="Pages" className="flex items-center justify-end gap-2 text-sm">
          <span className="text-xs text-[var(--muted)]">Page {pageIndex + 1} of {pages}</span>
          <button type="button" onClick={() => setPageIndex((p) => Math.max(0, p - 1))} disabled={pageIndex === 0}
            className="inline-flex items-center gap-1 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-40">
            <ChevronLeft size={13} /> Previous
          </button>
          <button type="button" onClick={() => setPageIndex((p) => Math.min(pages - 1, p + 1))} disabled={pageIndex >= pages - 1}
            className="inline-flex items-center gap-1 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-40">
            Next <ChevronRight size={13} />
          </button>
        </nav>
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
