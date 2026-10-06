'use client';

import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Search, Wand2 } from 'lucide-react';
import { api } from '@/lib/api';

type Field = 'status' | 'priority';

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

/** "In Progress" and "IN_PROGRESS" are the same words. Nothing looser than that is ever suggested. */
const asPortalWord = (s: string) => s.trim().toUpperCase().replace(/[^A-Z0-9]+/g, '_').replace(/^_+|_+$/g, '');

/**
 * What a connection's PSA sends for a field, and what each value becomes in the portal.
 *
 * A PSA has many statuses and the portal has six, so many arrive as one; the rows below it on this
 * page say the other direction, what the portal SENDS for each of its own. Every change here is
 * staged first and shown as a list before anything is saved - one value, a ticked group, or the
 * exact matches the page can suggest.
 */
export function InboundMapping({ connectionId, field, onSaved }: { connectionId: string; field: Field; onSaved: () => void }) {
  const qc = useQueryClient();
  const health = useQuery({
    queryKey: ['connection-mapping-health', connectionId],
    queryFn: () => api.connectionMappingHealth(connectionId),
    retry: false, staleTime: 0,
  });
  const vocabulary = useQuery({ queryKey: ['mapping-vocabulary'], queryFn: api.mappingVocabulary, staleTime: 60 * 60_000 });

  const [search, setSearch] = useState('');
  const [unmappedOnly, setUnmappedOnly] = useState(false);
  // What has been chosen and not yet saved: a value from the PSA to a portal value, or null for "not mapped".
  const [staged, setStaged] = useState<Record<string, string | null>>({});
  const [ticked, setTicked] = useState<Set<string>>(new Set());

  const section = health.data?.fields.find((f) => f.field === field);
  const items = useMemo(() => section?.items ?? [], [section]);
  const portalValues = (field === 'status' ? vocabulary.data?.statuses : vocabulary.data?.priorities) ?? [];

  const save = useMutation({
    mutationFn: () => api.setInboundMappings(connectionId,
      Object.entries(staged).map(([value, portalValue]) => ({ field, value, portalValue }))),
    onSuccess: () => {
      setStaged({});
      setTicked(new Set());
      qc.invalidateQueries({ queryKey: ['connection-mapping-health', connectionId] });
      onSaved();
    },
  });
  const apply = useMutation({
    mutationFn: () => api.applyConnectionMapping(connectionId),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['connection-mapping-health', connectionId] }),
  });

  const current = (value: string) => items.find((i) => i.value === value)?.mapsTo ?? null;
  const stage = (value: string, portalValue: string | null) => setStaged((s) => {
    const next = { ...s };
    // Choosing what it already is, is not a change.
    if (portalValue === current(value)) delete next[value]; else next[value] = portalValue;
    return next;
  });

  const shown = items.filter((i) => {
    const answer = i.value in staged ? staged[i.value] : i.mapsTo;
    if (unmappedOnly && answer !== null && i.unmappedTickets === 0) return false;
    return search.trim() === '' || i.value.toLowerCase().includes(search.trim().toLowerCase());
  });
  const suggestions = items
    .filter((i) => i.mapsTo === null && !(i.value in staged) && portalValues.includes(asPortalWord(i.value)));
  const pending = Object.entries(staged);

  if (health.isLoading) return <p className="text-sm text-[var(--muted)]">Reading what this PSA sends…</p>;
  if (health.isError || !section) {
    return <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(health.error, 'What this PSA sends could not be read.')}</p>;
  }

  return (
    <section aria-label="What the PSA sends" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      <header className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-[var(--border)] px-4 py-3">
        <div className="min-w-0 flex-1">
          <h2 className="text-sm font-semibold">What the PSA sends</h2>
          <p className="text-xs text-[var(--muted)]">
            {section.values === 0
              ? 'Nothing has arrived yet, and the PSA lists nothing.'
              : `${section.mapped} of ${section.values} mapped`}
            {section.unmappedTickets > 0 && ` · ${section.unmappedTickets.toLocaleString()} ${section.unmappedTickets === 1 ? 'ticket holds' : 'tickets hold'} a value nothing maps`}
            {'. '}Any number of these can become the same portal value.
          </p>
        </div>
        <label className="relative">
          <span className="sr-only">Search values</span>
          <Search size={13} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-[var(--faint)]" aria-hidden="true" />
          <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search values"
            className="w-44 rounded-lg border border-[var(--border)] bg-[var(--bg)] py-1.5 pl-8 pr-2 text-sm outline-none focus:border-brand" />
        </label>
        <label className="flex items-center gap-1.5 text-sm">
          <input type="checkbox" checked={unmappedOnly} onChange={(e) => setUnmappedOnly(e.target.checked)} /> Unmapped only
        </label>
        <button type="button" disabled={suggestions.length === 0}
          onClick={() => setStaged((s) => ({ ...s, ...Object.fromEntries(suggestions.map((i) => [i.value, asPortalWord(i.value)])) }))}
          title={suggestions.length === 0 ? 'No unmapped value is the same words as a portal value' : undefined}
          className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50">
          <Wand2 size={13} aria-hidden="true" /> Suggest exact matches{suggestions.length > 0 ? ` (${suggestions.length})` : ''}
        </button>
      </header>

      {ticked.size > 0 && (
        <div className="flex flex-wrap items-center gap-2 border-b border-[var(--border)] bg-[var(--bg)] px-4 py-2 text-sm">
          <span>{ticked.size} ticked</span>
          <label className="flex items-center gap-1.5">
            <span className="text-xs text-[var(--muted)]">Map them to</span>
            <select value="" aria-label="Map the ticked values to"
              onChange={(e) => {
                const answer = e.target.value === '__none' ? null : e.target.value;
                if (e.target.value !== '') ticked.forEach((value) => stage(value, answer));
              }}
              className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1 text-sm">
              <option value="">Choose…</option>
              {portalValues.map((p) => <option key={p} value={p}>{p}</option>)}
              <option value="__none">Not mapped</option>
            </select>
          </label>
          <button type="button" onClick={() => setTicked(new Set())} className="text-xs text-[var(--muted)] underline underline-offset-2">Untick all</button>
        </div>
      )}

      <div className="overflow-x-auto">
        <table className="w-full min-w-[560px] text-left text-sm">
          <thead className="text-xs text-[var(--muted)]">
            <tr>
              <th className="w-8 px-4 py-2"><span className="sr-only">Tick</span></th>
              <th className="px-2 py-2 font-medium">In the PSA</th>
              <th className="px-2 py-2 text-right font-medium">Tickets</th>
              <th className="px-4 py-2 font-medium">In the portal</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-[var(--border)]">
            {shown.length === 0 && (
              <tr><td colSpan={4} className="px-4 py-3 text-xs text-[var(--muted)]">
                {items.length === 0 ? 'Nothing to map yet.' : 'Nothing matches the search and filter.'}
              </td></tr>
            )}
            {shown.map((i) => {
              const isStaged = i.value in staged;
              const answer = isStaged ? staged[i.value] : i.mapsTo;
              return (
                <tr key={i.value} className={isStaged ? 'bg-amber-50/60 dark:bg-amber-950/20' : undefined}>
                  <td className="px-4 py-1.5">
                    <input type="checkbox" aria-label={`Tick ${i.value}`} checked={ticked.has(i.value)}
                      onChange={(e) => setTicked((t) => { const next = new Set(t); if (e.target.checked) next.add(i.value); else next.delete(i.value); return next; })} />
                  </td>
                  <td className="px-2 py-1.5">
                    {i.value}
                    {!i.listedByPsa && <span className="ml-1.5 text-[11px] text-[var(--faint)]">on tickets only</span>}
                    {i.mapsTo === null && i.treatedAs && (
                      <span className="block text-xs text-[var(--muted)]">
                        {i.treatedAs === 'Open' ? 'Its tickets count as open work.' : 'Its tickets count as finished, from the PSA\'s wording.'}
                      </span>
                    )}
                  </td>
                  <td className="px-2 py-1.5 text-right tabular-nums">{i.tickets.toLocaleString()}</td>
                  <td className="px-4 py-1.5">
                    <select value={answer ?? '__none'} aria-label={`What ${i.value} becomes in the portal`}
                      onChange={(e) => stage(i.value, e.target.value === '__none' ? null : e.target.value)}
                      className={'rounded-lg border bg-[var(--bg)] px-2 py-1 text-sm ' + (answer === null ? 'border-amber-400 text-amber-700 dark:text-amber-300' : 'border-[var(--border)]')}>
                      <option value="__none">Not mapped</option>
                      {portalValues.map((p) => <option key={p} value={p}>{p}</option>)}
                      {/* A rule may map it to something that is no longer one of the portal's words: shown, not hidden. */}
                      {answer !== null && !portalValues.includes(answer) && <option value={answer}>{answer}</option>}
                    </select>
                    {isStaged && <span className="ml-2 text-xs text-[var(--muted)]">was {i.mapsTo ?? 'not mapped'}</span>}
                    {!isStaged && i.mapsTo !== null && i.unmappedTickets > 0 && (
                      <span className="ml-2 text-xs text-amber-700 dark:text-amber-300">not in every queue: {i.unmappedTickets.toLocaleString()} unmapped</span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      {pending.length > 0 && (
        <div className="border-t border-[var(--border)] bg-amber-50/60 px-4 py-3 dark:bg-amber-950/20">
          <p className="text-sm font-medium">{pending.length} {pending.length === 1 ? 'change' : 'changes'} not saved yet</p>
          <ul className="mt-1 max-h-40 space-y-0.5 overflow-y-auto text-xs text-[var(--muted)]">
            {pending.map(([value, to]) => (
              <li key={value}><span className="font-medium text-[var(--fg)]">{value}</span> → {to ?? 'not mapped'} <span className="text-[var(--faint)]">(was {current(value) ?? 'not mapped'})</span></li>
            ))}
          </ul>
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <button type="button" onClick={() => save.mutate()} disabled={save.isPending}
              className="rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
              {save.isPending ? 'Saving…' : `Save ${pending.length === 1 ? 'this change' : `these ${pending.length} changes`}`}
            </button>
            <button type="button" onClick={() => setStaged({})} disabled={save.isPending}
              className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-xs font-medium hover:bg-[var(--bg)]">Discard</button>
            {save.isError && <span role="alert" className="text-xs text-rose-600 dark:text-rose-400">{message(save.error, 'The changes could not be saved.')}</span>}
          </div>
        </div>
      )}

      {pending.length === 0 && save.data && save.data.changed > 0 && (
        <div className="flex flex-wrap items-center gap-2 border-t border-[var(--border)] px-4 py-3 text-xs">
          <span className="text-emerald-600 dark:text-emerald-400">Saved {save.data.changed} {save.data.changed === 1 ? 'change' : 'changes'}.</span>
          <span className="text-[var(--muted)]">Tickets already imported keep the PSA&apos;s own word until the mapping is applied to them.</span>
          <button type="button" onClick={() => apply.mutate()} disabled={apply.isPending}
            className="rounded-lg border border-[var(--border)] px-2.5 py-1 font-medium hover:bg-[var(--bg)] disabled:opacity-50">
            {apply.isPending ? 'Applying…' : 'Apply to tickets already here'}
          </button>
          {apply.data && (
            <span className="text-emerald-600 dark:text-emerald-400">
              {apply.data.statusesChanged + apply.data.prioritiesChanged === 0
                ? 'No imported ticket needed changing.'
                : `Changed ${apply.data.statusesChanged + apply.data.prioritiesChanged} ${apply.data.statusesChanged + apply.data.prioritiesChanged === 1 ? 'ticket value' : 'ticket values'}.`}
            </span>
          )}
          {apply.isError && <span role="alert" className="text-rose-600 dark:text-rose-400">{message(apply.error, 'That could not be done.')}</span>}
        </div>
      )}
    </section>
  );
}
