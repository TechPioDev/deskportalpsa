'use client';

import Link from 'next/link';
import { Suspense, useMemo, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { Plus, Inbox, Search, X, Eye, Users } from 'lucide-react';
import { api } from '@/lib/api';
import { StatusBadge, PriorityBadge, SourceBadge } from '@/components/badges';
import { TicketViewBar, EMPTY_FILTERS } from '@/components/TicketViewBar';
import type { TicketListItem, SavedViewFilters } from '@/lib/types';
import { isResolvedStatus } from '@/lib/status';
import { fmtHours } from '@/lib/format';

const ALL = '__all__';

/** The same window the server's Due soon filter and the board's "soon" label use. */
const DUE_SOON_HOURS = 8;

/** Distinct, sorted values for a column — the filter options come from the data itself, so they
 *  stay correct for any PSA without hard-coding provider vocabulary. */
function optionsFor(rows: TicketListItem[], pick: (t: TicketListItem) => string | null | undefined) {
  return Array.from(new Set(rows.map((r) => pick(r) ?? '').filter(Boolean))).sort();
}

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

function TicketsList() {
  const { data, isLoading, isError } = useQuery({ queryKey: ['tickets'], queryFn: api.listTickets });

  // Filters can arrive in the URL, so a figure on the dashboard can link to the tickets behind it
  // — and so the resulting view is a link someone can send to a colleague. `view` handles the two
  // that are not a single status: "open" and "resolved" are each a SET of statuses, and which
  // statuses those are is a decision that already lives in isResolvedStatus.
  const params = useSearchParams();
  const view = params.get('view');
  // A window on the date the ticket was RAISED - the same axis the client-workload figures use.
  // Filtering on the import date instead would make this list disagree with the number that
  // linked here, which is the one thing a link from a figure must never do.
  const from = params.get('from');

  const [q, setQ] = useState(() => params.get('q') ?? '');
  const [status, setStatus] = useState(() => params.get('status') ?? ALL);
  const [priority, setPriority] = useState(() => params.get('priority') ?? ALL);
  // Company arrives by NAME, not id: this list filters on the name it displays, and a link that
  // carried an id would have to resolve it before it could select anything.
  const [company, setCompany] = useState(() => params.get('company') ?? ALL);
  const [source, setSource] = useState(ALL);
  const [queue, setQueue] = useState(ALL);
  // A person KEY ("u:<portal user>" or "x:<PSA resource>"), not a name: two people can share a
  // name, and Client workload's People list links here with exactly the key it counted by.
  const [tech, setTech] = useState(() => params.get('tech') ?? ALL);

  // The view filters that are not a column: open/resolved, and the four questions about the person
  // reading the page. Initialised from the URL — `view` has always carried the first of them — and
  // then owned here, because a view chip has to be able to set all of them in one go.
  const [openness, setOpenness] = useState<string | null>(() => view);
  const [mine, setMine] = useState(() => params.get('mine') === '1');
  const [following, setFollowing] = useState(() => params.get('following') === '1');
  const [unassigned, setUnassigned] = useState(() => params.get('unassigned') === '1');
  const [overdue, setOverdue] = useState(() => params.get('overdue') === '1');
  const [dueSoon, setDueSoon] = useState(() => params.get('due') === 'soon');

  // Who is asking. "Mine" is a question about them, and without an answer it would quietly mean
  // "nobody's" — so the chip is only offered once this has arrived.
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 10 * 60_000, retry: false });
  const myKey = me?.userId ? `u:${me.userId}` : null;
  // Memoised: a fresh [] on every render would re-run the filter memo below on every render too.
  const myTeams = useMemo(() => me?.teamIds ?? [], [me]);

  const rows = useMemo(() => data ?? [], [data]);

  // Everyone who holds or logged time on a ticket in the list. Staff lists carry this; a client's
  // carries none, and then neither the filter nor the Assignee column is offered.
  const hasPeople = rows.some((t) => t.people !== null);
  const techNames = useMemo(() => {
    const names = new Map<string, string>();
    for (const t of rows) for (const p of t.people ?? []) if (!names.has(p.key)) names.set(p.key, p.name);
    return names;
  }, [rows]);
  const techOptions = useMemo(() => {
    const keys = Array.from(techNames.keys())
      .sort((a, b) => techNames.get(a)!.localeCompare(techNames.get(b)!));
    // A key from a link that no ticket here carries must still be selectable, or the control would
    // read "All" while the list stayed filtered - the page contradicting itself.
    return tech !== ALL && !techNames.has(tech) ? [tech, ...keys] : keys;
  }, [techNames, tech]);
  const filtered = useMemo(() => {
    const needle = q.trim().toLowerCase();
    const now = Date.now();
    return rows.filter((t) => {
      const resolved = isResolvedStatus(t.portalStatus);
      const holder = (t.people ?? []).find((p) => p.holds);
      return (!needle
          || (t.title ?? '').toLowerCase().includes(needle)
          || (t.externalTicketId ?? '').toLowerCase().includes(needle)
          // The board number people actually quote to each other, which a search for "INT-00012"
          // was finding nothing for.
          || (t.number ?? '').toLowerCase().includes(needle)
          || (t.customerName ?? '').toLowerCase().includes(needle))
        && (status === ALL || t.portalStatus === status)
        && (openness !== 'open' || !resolved)
        && (openness !== 'resolved' || resolved)
        && (!from || new Date(t.raisedAt ?? t.createdAt) >= new Date(from))
        && (priority === ALL || t.portalPriority === priority)
        && (source === ALL || (t.connectionName ?? '') === source)
        && (company === ALL || (t.customerName ?? '') === company)
        && (queue === ALL || (t.queueOrBoard ?? '') === queue)
        // Holds it OR logged time on it - the rule People counts by. Holder alone would send anyone
        // who only logged time from that list to an empty one.
        && (tech === ALL || (t.people ?? []).some((p) => p.key === tech))
        // Mine covers a team I am in as well as my own name: a ticket routed to Level 2 is mine to
        // pick up, which is the whole point of routing it there.
        && (!mine || holder?.key === myKey
            || (t.assignedTeamId !== null && myTeams.includes(t.assignedTeamId)))
        && (!following || t.following)
        && (!unassigned || holder === undefined)
        // Overdue means past due AND still open. A ticket closed late is history, not work to do,
        // and a list that keeps showing it can never be emptied.
        // ...and not paused: a ticket waiting on the customer is not late, which is what the server's
        // own Overdue filter and the needs-attention list already say.
        && (!overdue || (t.dueAt !== null && new Date(t.dueAt).getTime() < now && !resolved && !t.slaPausedAt))
        // Due soon: not late yet, due within the same 8 hours the board's "soon" label uses.
        && (!dueSoon || (t.dueAt !== null && !resolved && !t.slaPausedAt
            && new Date(t.dueAt).getTime() >= now && new Date(t.dueAt).getTime() <= now + DUE_SOON_HOURS * 3_600_000));
    });
  }, [rows, q, status, priority, source, company, queue, openness, from, tech,
      mine, following, unassigned, overdue, dueSoon, myKey, myTeams]);

  // The hours behind what is on screen. Opened from a client's hours figure, this is the same
  // sum - which is what makes that link honest rather than approximate.
  const totalWorked = filtered.reduce((a, t) => a + t.timeWorkedHours, 0);
  const totalBillable = filtered.reduce((a, t) => a + t.billableHours, 0);

  const active = q.trim() !== '' || openness !== null || from !== null
    || mine || following || unassigned || overdue || dueSoon
    || [status, priority, source, company, queue, tech].some((v) => v !== ALL);
  const router = useRouter();
  const clear = () => {
    setQ(''); setStatus(ALL); setPriority(ALL); setSource(ALL); setCompany(ALL); setQueue(ALL); setTech(ALL);
    setOpenness(null); setMine(false); setFollowing(false); setUnassigned(false); setOverdue(false); setDueSoon(false);
    // Drops the URL's own filters as well. Leaving them would clear every visible control and still
    // filter the list, which reads as the page ignoring the button.
    if (params.toString()) router.replace('/dashboard/tickets');
  };

  // The filter set as a view sees it, and the one way back. Everything the bar can set is set here,
  // so a chip cannot leave a stale control behind contradicting the list.
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

      {!isError && rows.length > 0 && (
        <TicketViewBar filters={filters} onApply={applyView} canSave={hasPeople} />
      )}

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
            <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter by title, number or customer…"
              className="w-56 rounded-lg border border-[var(--border)] bg-[var(--bg)] py-1.5 pl-8 pr-3 text-sm outline-none focus:border-brand" />
          </div>
          <Select label="Status" value={status} onChange={setStatus} options={optionsFor(rows, (t) => t.portalStatus)} />
          <Select label="Priority" value={priority} onChange={setPriority} options={optionsFor(rows, (t) => t.portalPriority)} />
          <Select label="Source" value={source} onChange={setSource} options={optionsFor(rows, (t) => t.connectionName)} />
          <Select label="Company" value={company} onChange={setCompany} options={optionsFor(rows, (t) => t.customerName)} />
          <Select label="Queue" value={queue} onChange={setQueue} options={optionsFor(rows, (t) => t.queueOrBoard)} />
          {hasPeople && (
            <Select label="Technician" value={tech} onChange={setTech} options={techOptions}
              labelFor={(k) => techNames.get(k) ?? 'Selected technician'}
              title="Tickets this person holds, or logged time on" />
          )}
          <span className="ml-auto text-xs text-[var(--muted)]">
            {filtered.length === rows.length ? `${rows.length} tickets` : `${filtered.length} of ${rows.length} tickets`}
            {totalWorked > 0 && <span> · {fmtHours(totalWorked)} worked, {fmtHours(totalBillable)} billable</span>}
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
                {hasPeople && <th className="px-4 py-3 font-medium">Assignee</th>}
                <th className="px-4 py-3 text-right font-medium">Worked</th>
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
                  {/* The raise date, not the import date: the from-filter works on this one, and
                      a list showing one date while filtering on another looks broken. */}
                  <td className="px-4 py-3 text-[var(--muted)]">{new Date(t.raisedAt ?? t.createdAt).toLocaleDateString()}</td>
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
