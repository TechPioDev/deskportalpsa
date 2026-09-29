'use client';

import { useEffect, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { BookOpen, CheckCircle2, ChevronDown, ChevronRight } from 'lucide-react';
import { api, type HelpSummary } from '@/lib/api';
import { NoteBody } from '@/components/NoteBody';

/**
 * Articles that might answer the ticket being typed, offered under its title. Read one in place; if it
 * fixes things, "This solved it" records a ticket that did not need raising - the figure the team's
 * knowledge base page reports - and the form steps aside.
 */
export function HelpSuggestions({ query, onSolved }: { query: string; onSolved: (title: string) => void }) {
  // Asked once the typing pauses, not on every keystroke.
  const [settled, setSettled] = useState(query);
  useEffect(() => {
    const t = setTimeout(() => setSettled(query.trim()), 400);
    return () => clearTimeout(t);
  }, [query]);

  const { data: suggestions } = useQuery({
    queryKey: ['help-suggest', settled], queryFn: () => api.helpSuggest(settled),
    enabled: settled.length >= 3, retry: false, staleTime: 60_000,
  });
  const [open, setOpen] = useState<string | null>(null);

  if (!suggestions || suggestions.length === 0 || settled.length < 3) return null;

  return (
    <section aria-labelledby="help-suggestions" className="rounded-lg border border-brand/30 bg-brand-tint p-3 dark:bg-brand/10">
      <h2 id="help-suggestions" className="flex items-center gap-1.5 text-sm font-medium">
        <BookOpen size={15} aria-hidden="true" /> These articles might help
      </h2>
      <ul className="mt-2 space-y-1.5">
        {suggestions.map((s) => {
          const key = `${s.source}-${s.id}`;
          return (
            <li key={key} className="rounded-lg bg-[var(--surface)]">
              <button type="button" onClick={() => setOpen(open === key ? null : key)} aria-expanded={open === key}
                className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm">
                {open === key ? <ChevronDown size={14} aria-hidden="true" /> : <ChevronRight size={14} aria-hidden="true" />}
                <span className="font-medium">{s.title}</span>
              </button>
              {open === key && <Expanded suggestion={s} query={settled} onSolved={() => onSolved(s.title)} />}
            </li>
          );
        })}
      </ul>
    </section>
  );
}

function Expanded({ suggestion, query, onSolved }: { suggestion: HelpSummary; query: string; onSolved: () => void }) {
  const { data: article } = useQuery({
    queryKey: ['help-article', suggestion.source, suggestion.id],
    queryFn: () => api.helpArticle(suggestion.source, suggestion.id), retry: false,
  });
  const solved = useMutation({ mutationFn: () => api.helpSolved(suggestion.source, suggestion.id, query), onSuccess: onSolved });

  return (
    <div className="border-t border-[var(--border)] px-3 pb-3">
      {article ? <NoteBody body={article.body} full /> : <p className="mt-2 text-sm text-[var(--muted)]">Loading…</p>}
      {solved.isError && <p role="alert" className="mt-2 text-xs text-red-600 dark:text-red-400">{(solved.error as Error).message}</p>}
      <button type="button" onClick={() => solved.mutate()} disabled={solved.isPending || !article}
        className="mt-3 inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
        <CheckCircle2 size={15} aria-hidden="true" /> This solved it — I don&rsquo;t need a ticket
      </button>
    </div>
  );
}
