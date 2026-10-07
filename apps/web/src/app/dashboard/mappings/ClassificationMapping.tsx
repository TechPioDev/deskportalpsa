'use client';

import { useEffect, useId, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus, Trash2 } from 'lucide-react';
import { api } from '@/lib/api';
import { psaLevelNames } from '@/lib/psaLevels';
import type { ClassificationRule, ClassificationSeen } from '@/lib/types';

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

type Draft = { key: string; ticketType: string; issueType: string; subIssueType: string; category: string; workType: string; subcategory: string };
const LEVELS = ['ticketType', 'issueType', 'subIssueType'] as const;
const TARGETS = ['category', 'workType', 'subcategory'] as const;
const TARGET_NAMES = { category: 'Category', workType: 'Work type', subcategory: 'Subcategory' } as const;

let made = 0;
const draftOf = (r: Partial<ClassificationRule>): Draft => ({
  key: `r${++made}`,
  ticketType: r.ticketType ?? '', issueType: r.issueType ?? '', subIssueType: r.subIssueType ?? '',
  category: r.category ?? '', workType: r.workType ?? '', subcategory: r.subcategory ?? '',
});
const clean = (s: string) => { const v = s.trim(); return v === '' ? null : v; };
const ruleOf = (d: Draft): ClassificationRule => ({
  id: null, ticketType: clean(d.ticketType), issueType: clean(d.issueType), subIssueType: clean(d.subIssueType),
  category: clean(d.category), workType: clean(d.workType), subcategory: clean(d.subcategory),
});
/** Two sets of rules are the same when they say the same things, whatever order they are listed in. */
const signature = (rules: ClassificationRule[]) =>
  rules.map((r) => [r.ticketType, r.issueType, r.subIssueType, r.category, r.workType, r.subcategory].map((v) => v ?? '').join('\u0001')).sort().join('\u0002');
const levelsOf = (s: { ticketType: string | null; issueType: string | null; subIssueType: string | null }) =>
  [s.ticketType, s.issueType, s.subIssueType].map((l) => l ?? '–').join(' / ');
const became = (s: { category: string | null; workType: string | null; subcategory: string | null }) =>
  TARGETS.filter((t) => s[t]).map((t) => `${TARGET_NAMES[t]}: ${s[t]}`).join(' · ');

/**
 * What a connection's PSA classification means in the portal's own words.
 *
 * A PSA files a ticket under up to three levels. A rule names some of them and says what a ticket
 * filed there is here: its category, its work type, its subcategory. Nothing is translated without
 * a rule. A classification no rule names is listed as UNMAPPED and stays that way until someone
 * writes one, and there is no rule for "everything else".
 *
 * Rules are edited here and nothing is sent until Save. Before saving, Preview says what the rules
 * would do to the tickets already here; saving does not touch those either - that is its own step.
 */
export function ClassificationMapping({ connectionId, onSaved }: { connectionId: string; onSaved?: () => void }) {
  const qc = useQueryClient();
  const listId = useId();
  const queryKey = useMemo(() => ['classification-mapping', connectionId], [connectionId]);
  const mapping = useQuery({ queryKey, queryFn: () => api.classificationMapping(connectionId), retry: false, staleTime: 0 });

  const [drafts, setDrafts] = useState<Draft[]>([]);
  const [unmappedOnly, setUnmappedOnly] = useState(false);
  // The rules as last read are what is edited; a save or a discard starts from them again.
  const saved = mapping.data?.rules;
  useEffect(() => { if (saved) setDrafts(saved.map(draftOf)); }, [saved]);

  const rules = useMemo(() => drafts.map(ruleOf), [drafts]);
  const dirty = saved !== undefined && signature(rules) !== signature(saved);

  const preview = useMutation({ mutationFn: () => api.previewClassificationMapping(connectionId, rules) });
  const save = useMutation({
    mutationFn: () => api.saveClassificationMapping(connectionId, rules),
    onSuccess: (data) => {
      qc.setQueryData(queryKey, data);
      qc.invalidateQueries({ queryKey: ['connection-mapping-health', connectionId] });
      preview.reset();
      onSaved?.();
    },
  });
  const apply = useMutation({
    mutationFn: () => api.applyClassificationMapping(connectionId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey });
      qc.invalidateQueries({ queryKey: ['connection-mapping-health', connectionId] });
    },
  });

  const change = (key: string, field: keyof Omit<Draft, 'key'>, value: string) => {
    setDrafts((ds) => ds.map((d) => (d.key === key ? { ...d, [field]: value } : d)));
    preview.reset();
    save.reset();
  };
  const add = (from: Partial<ClassificationRule> = {}) => { setDrafts((ds) => [...ds, draftOf(from)]); preview.reset(); save.reset(); };
  const remove = (key: string) => { setDrafts((ds) => ds.filter((d) => d.key !== key)); preview.reset(); save.reset(); };

  if (mapping.isLoading) return <p className="text-sm text-[var(--muted)]">Reading what this PSA files tickets under…</p>;
  if (mapping.isError || !mapping.data) {
    return <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(mapping.error, 'The classification rules could not be read.')}</p>;
  }

  const data = mapping.data;
  const names = psaLevelNames(data.provider);
  const h = data.health;
  const seen = data.seen.filter((s) => !unmappedOnly || !s.mapped);
  // What each box offers: the PSA's words from the tickets, and the portal's from rules and tickets already written.
  const offered: Record<string, string[]> = {
    ticketType: [...new Set(data.seen.map((s) => s.ticketType).filter((v): v is string => !!v))],
    issueType: [...new Set(data.seen.map((s) => s.issueType).filter((v): v is string => !!v))],
    subIssueType: [...new Set(data.seen.map((s) => s.subIssueType).filter((v): v is string => !!v))],
    category: data.categories, workType: data.workTypes, subcategory: data.subcategories,
  };
  const hasRuleFor = (s: ClassificationSeen) => rules.some((r) =>
    LEVELS.every((l) => (r[l] ?? '').toLowerCase() === (s[l] ?? '').toLowerCase()));
  const input = 'w-full min-w-[7rem] rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1 text-sm outline-none focus:border-brand';

  return (
    <section aria-label="Classification rules" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
      {Object.entries(offered).map(([field, values]) => (
        <datalist key={field} id={`${listId}-${field}`}>{values.map((v) => <option key={v} value={v} />)}</datalist>
      ))}

      <header className="border-b border-[var(--border)] px-4 py-3">
        <h2 className="text-sm font-semibold">What the PSA&apos;s classification means here</h2>
        <p className="mt-0.5 text-xs text-[var(--muted)]">
          {h.classifiedTickets === 0
            ? 'No ticket of this connection is filed under anything yet.'
            : `${(h.classifiedTickets - h.unmappedTickets).toLocaleString()} of ${h.classifiedTickets.toLocaleString()} classified tickets are named by a rule`}
          {h.coverage !== null && ` (${h.coverage}%)`}
          {h.unmappedTickets > 0 && ` · ${h.unmappedTickets.toLocaleString()} unmapped`}
          {h.rulesMatchingNothing > 0 && ` · ${h.rulesMatchingNothing} ${h.rulesMatchingNothing === 1 ? 'rule names' : 'rules name'} nothing any ticket is filed under`}
          .
        </p>
        <p className="mt-1.5 max-w-3xl text-xs text-[var(--muted)]">
          A rule says what tickets filed under some of the PSA&apos;s levels are in the portal. Leave a level empty for &quot;whatever it is&quot;.
          Where several rules name a ticket, each of the three is taken from the most exact one that gives it.
          Nothing is translated without a rule: a classification no rule names stays unmapped, the PSA&apos;s own words stay on
          the ticket either way, and there is no rule for everything else. The work type here is the ticket&apos;s kind of work,
          not the billing work type of a time entry, which has its own tab.
        </p>
      </header>

      <div className="overflow-x-auto">
        <table className="w-full min-w-[820px] text-left text-sm">
          <thead className="text-xs text-[var(--muted)]">
            <tr>
              {names.map((n) => <th key={n} className="px-2 py-2 font-medium first:pl-4">{n} (PSA)</th>)}
              <th className="px-1 py-2" aria-hidden="true" />
              {TARGETS.map((t) => <th key={t} className="px-2 py-2 font-medium">{TARGET_NAMES[t]}</th>)}
              <th className="w-10 px-2 py-2"><span className="sr-only">Remove</span></th>
            </tr>
          </thead>
          <tbody className="divide-y divide-[var(--border)]">
            {drafts.length === 0 && (
              <tr><td colSpan={8} className="px-4 py-3 text-xs text-[var(--muted)]">
                No rules. Every ticket keeps the PSA&apos;s own classification and nothing is translated.
              </td></tr>
            )}
            {drafts.map((d, i) => (
              <tr key={d.key}>
                {LEVELS.map((l, n) => (
                  <td key={l} className="px-2 py-1.5 first:pl-4">
                    <input value={d[l]} onChange={(e) => change(d.key, l, e.target.value)} list={`${listId}-${l}`}
                      aria-label={`Rule ${i + 1}: ${names[n]}`} placeholder="any" maxLength={200} className={input} />
                  </td>
                ))}
                <td className="px-1 py-1.5 text-[var(--faint)]" aria-hidden="true">→</td>
                {TARGETS.map((t) => (
                  <td key={t} className="px-2 py-1.5">
                    <input value={d[t]} onChange={(e) => change(d.key, t, e.target.value)} list={`${listId}-${t}`}
                      aria-label={`Rule ${i + 1}: ${TARGET_NAMES[t]}`} placeholder="–" maxLength={200} className={input} />
                  </td>
                ))}
                <td className="px-2 py-1.5">
                  <button type="button" onClick={() => remove(d.key)} aria-label={`Remove rule ${i + 1}`}
                    className="rounded-lg p-1.5 text-[var(--muted)] hover:bg-[var(--bg)] hover:text-rose-600">
                    <Trash2 size={14} aria-hidden="true" />
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="flex flex-wrap items-center gap-2 border-t border-[var(--border)] px-4 py-3">
        <button type="button" onClick={() => add()}
          className="inline-flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)]">
          <Plus size={13} aria-hidden="true" /> Add a rule
        </button>
        {dirty && (
          <>
            <span className="text-xs font-medium text-amber-700 dark:text-amber-300">Not saved yet</span>
            <button type="button" onClick={() => preview.mutate()} disabled={preview.isPending || save.isPending}
              className="rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50">
              {preview.isPending ? 'Working it out…' : 'Preview'}
            </button>
            <button type="button" onClick={() => save.mutate()} disabled={save.isPending}
              className="rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
              {save.isPending ? 'Saving…' : 'Save rules'}
            </button>
            <button type="button" onClick={() => { setDrafts((saved ?? []).map(draftOf)); preview.reset(); save.reset(); }} disabled={save.isPending}
              className="rounded-lg border border-[var(--border)] px-2.5 py-1.5 text-xs font-medium hover:bg-[var(--bg)]">Discard</button>
          </>
        )}
        {!dirty && save.isSuccess && <span className="text-xs text-emerald-600 dark:text-emerald-400">Saved.</span>}
        {save.isError && <span role="alert" className="basis-full text-xs text-rose-600 dark:text-rose-400">{message(save.error, 'The rules could not be saved.')}</span>}
        {preview.isError && <span role="alert" className="basis-full text-xs text-rose-600 dark:text-rose-400">{message(preview.error, 'The preview could not be worked out.')}</span>}
      </div>

      {preview.data && (
        <div aria-label="Preview" className="border-t border-[var(--border)] bg-amber-50/60 px-4 py-3 dark:bg-amber-950/20">
          <p className="text-sm font-medium">
            {preview.data.ticketsThatWouldChange === 0
              ? 'These rules would change no ticket already here.'
              : `These rules would change ${preview.data.ticketsThatWouldChange.toLocaleString()} ${preview.data.ticketsThatWouldChange === 1 ? 'ticket' : 'tickets'} already here.`}
            <span className="ml-2 font-normal text-[var(--muted)]">
              {preview.data.health.unmappedTickets.toLocaleString()} would be unmapped. Nothing has been saved or changed.
            </span>
          </p>
          {preview.data.sample.length > 0 && (
            <ul className="mt-1.5 space-y-1 text-xs">
              {preview.data.sample.map((t) => (
                <li key={t.reference + t.title}>
                  <span className="font-medium">#{t.reference}</span> {t.title}{' '}
                  <span className="text-[var(--faint)]">({levelsOf(t)})</span>
                  <span className="block text-[var(--muted)]">
                    {became({ category: t.categoryNow, workType: t.workTypeNow, subcategory: t.subcategoryNow }) || 'nothing'}
                    {' → '}
                    <span className="text-[var(--fg)]">{became({ category: t.categoryThen, workType: t.workTypeThen, subcategory: t.subcategoryThen }) || 'nothing'}</span>
                  </span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {!dirty && (h.ticketsOutOfStep > 0 || apply.data) && (
        <div className="flex flex-wrap items-center gap-2 border-t border-[var(--border)] px-4 py-3 text-xs">
          {h.ticketsOutOfStep > 0 && (
            <>
              <span className="text-[var(--muted)]">
                {h.ticketsOutOfStep.toLocaleString()} {h.ticketsOutOfStep === 1 ? 'ticket already here holds' : 'tickets already here hold'} something
                other than what the rules say. Each is put right when the PSA next sends it, or now:
              </span>
              <button type="button" onClick={() => apply.mutate()} disabled={apply.isPending}
                className="rounded-lg border border-[var(--border)] px-2.5 py-1 font-medium hover:bg-[var(--bg)] disabled:opacity-50">
                {apply.isPending ? 'Applying…' : 'Apply to tickets already here'}
              </button>
            </>
          )}
          {apply.data && (
            <span className="text-emerald-600 dark:text-emerald-400">
              {apply.data.ticketsChanged === 0 ? 'No ticket needed changing.' : `Changed ${apply.data.ticketsChanged.toLocaleString()} ${apply.data.ticketsChanged === 1 ? 'ticket' : 'tickets'}.`}
            </span>
          )}
          {apply.isError && <span role="alert" className="text-rose-600 dark:text-rose-400">{message(apply.error, 'That could not be done.')}</span>}
        </div>
      )}

      <div className="border-t border-[var(--border)]">
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1 px-4 py-2.5">
          <h3 className="min-w-0 flex-1 text-sm font-semibold">
            What tickets are filed under
            <span className="ml-2 text-xs font-normal text-[var(--muted)]">
              {h.mappedClassifications} of {h.classifications} named by a saved rule
              {h.classifications > data.seenShown && ` · ${data.seenShown} are listed, the unmapped first`}
            </span>
          </h3>
          <label className="flex items-center gap-1.5 text-sm">
            <input type="checkbox" checked={unmappedOnly} onChange={(e) => setUnmappedOnly(e.target.checked)} /> Unmapped only
          </label>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full min-w-[720px] text-left text-sm">
            <thead className="text-xs text-[var(--muted)]">
              <tr>
                {names.map((n) => <th key={n} className="px-2 py-2 font-medium first:pl-4">{n}</th>)}
                <th className="px-2 py-2 text-right font-medium">Tickets</th>
                <th className="px-4 py-2 font-medium">In the portal</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-[var(--border)]">
              {seen.length === 0 && (
                <tr><td colSpan={5} className="px-4 py-3 text-xs text-[var(--muted)]">
                  {data.seen.length === 0 ? 'Nothing has arrived filed under anything yet.' : 'Every classification listed is named by a rule.'}
                </td></tr>
              )}
              {seen.map((s) => (
                <tr key={levelsOf(s)}>
                  {LEVELS.map((l) => <td key={l} className="px-2 py-1.5 first:pl-4">{s[l] ?? <span className="text-[var(--faint)]">–</span>}</td>)}
                  <td className="px-2 py-1.5 text-right tabular-nums">{s.tickets.toLocaleString()}</td>
                  <td className="px-4 py-1.5">
                    {s.mapped ? (
                      <span>{became(s)}</span>
                    ) : (
                      <span className="flex flex-wrap items-center gap-2">
                        <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-semibold uppercase tracking-wide text-amber-800 dark:bg-amber-950 dark:text-amber-300">Unmapped</span>
                        {!hasRuleFor(s) && (
                          <button type="button" onClick={() => add({ ticketType: s.ticketType, issueType: s.issueType, subIssueType: s.subIssueType })}
                            aria-label={`Write a rule for ${levelsOf(s)}`}
                            className="text-xs text-[var(--muted)] underline underline-offset-2 hover:text-[var(--fg)]">Write a rule</button>
                        )}
                      </span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </section>
  );
}
