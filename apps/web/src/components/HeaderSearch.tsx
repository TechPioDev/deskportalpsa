'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { Search, Loader2, CornerDownLeft } from 'lucide-react';
import { api } from '@/lib/api';
import { StatusBadge } from '@/components/badges';

/**
 * The search box in the header, which for a long time was a placeholder: it had a magnifying glass,
 * a prompt naming three things it would find, and a keyboard shortcut printed inside it, and typing
 * in it did nothing at all. A control that promises and does nothing is worse than no control.
 *
 * It searches in the database rather than over a loaded page, which is the whole reason it exists
 * beside the list page's own filters: those can only match the rows already fetched and the columns
 * they carry, and the ticket somebody is looking for is usually remembered by a phrase in a reply.
 */
export function HeaderSearch() {
  const router = useRouter();
  const [raw, setRaw] = useState('');
  const [q, setQ] = useState('');
  const [open, setOpen] = useState(false);
  const [cursor, setCursor] = useState(-1);
  const box = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);

  // Debounced: a query per keystroke would search the whole tenant eight times to answer the
  // eighth one.
  useEffect(() => {
    const t = setTimeout(() => setQ(raw.trim()), 250);
    return () => clearTimeout(t);
  }, [raw]);

  const enabled = q.length >= 2;
  const { data, isFetching } = useQuery({
    queryKey: ['ticket-search', q],
    // Conversations are searched only once there is enough to go on: matching two letters against
    // every note in the tenant is expensive and tells nobody anything.
    queryFn: () => api.searchTickets({ q, take: 8, notes: q.length >= 4 }),
    enabled,
    staleTime: 30_000,
    placeholderData: (prev) => prev,
  });

  const results = useMemo(() => (enabled ? data?.items ?? [] : []), [enabled, data]);

  // Ctrl+/ from anywhere, as the box itself has always claimed. Ignored while the caret is in
  // another field, so it cannot steal a keystroke from someone writing a reply.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const el = e.target as HTMLElement | null;
      const typing = el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable);
      if ((e.ctrlKey || e.metaKey) && e.key === '/' && !typing) {
        e.preventDefault();
        input.current?.focus();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // A click anywhere else closes the panel; the input keeps what was typed, so coming back to it
  // does not mean typing it again.
  useEffect(() => {
    const onDown = (e: MouseEvent) => {
      if (box.current && !box.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, []);

  const go = (href: string) => {
    setOpen(false);
    router.push(href);
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Escape') { setOpen(false); input.current?.blur(); return; }
    if (e.key === 'ArrowDown') { e.preventDefault(); setCursor((c) => Math.min(c + 1, results.length - 1)); return; }
    if (e.key === 'ArrowUp') { e.preventDefault(); setCursor((c) => Math.max(c - 1, -1)); return; }
    if (e.key === 'Enter') {
      e.preventDefault();
      // A highlighted result opens that ticket; otherwise the whole search opens as a list, which
      // is also a link somebody can send to a colleague.
      if (cursor >= 0 && results[cursor]) go(`/dashboard/tickets/${results[cursor].id}`);
      else if (q.length >= 2) go(`/dashboard/tickets?q=${encodeURIComponent(q)}`);
    }
  };

  return (
    <div ref={box} className="relative hidden max-w-xl flex-1 md:block">
      <Search size={15} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-[var(--faint)]" />
      <input
        ref={input}
        type="search"
        value={raw}
        onChange={(e) => { setRaw(e.target.value); setOpen(true); setCursor(-1); }}
        onFocus={() => setOpen(true)}
        onKeyDown={onKeyDown}
        role="combobox"
        aria-expanded={open && enabled}
        aria-controls="header-search-results"
        placeholder="Search tickets — number, subject, customer, or a phrase in a reply (Ctrl+/)"
        className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] py-2 pl-9 pr-9 text-sm outline-none focus:border-brand"
      />
      {isFetching && enabled && (
        <Loader2 size={14} className="absolute right-3 top-1/2 -translate-y-1/2 animate-spin text-[var(--faint)]" />
      )}

      {open && enabled && (
        <div id="header-search-results" role="listbox"
          className="absolute left-0 right-0 top-11 z-40 overflow-hidden rounded-xl border border-[var(--border)] bg-[var(--surface)] shadow-lg">
          {results.length === 0 && !isFetching && (
            <p className="px-4 py-3 text-sm text-[var(--muted)]">
              Nothing matches “{q}”. Numbers, subjects, customers and replies are all searched.
            </p>
          )}
          {results.map((t, i) => (
            <button key={t.id} role="option" aria-selected={i === cursor}
              onMouseEnter={() => setCursor(i)}
              onClick={() => go(`/dashboard/tickets/${t.id}`)}
              className={`flex w-full items-center gap-3 border-b border-[var(--border)] px-4 py-2.5 text-left last:border-0 ${
                i === cursor ? 'bg-[var(--bg)]' : ''}`}>
              <span className="w-24 shrink-0 truncate font-mono text-[11px] text-[var(--muted)]">
                {t.number ?? (t.externalTicketId ? `#${t.externalTicketId}` : '—')}
              </span>
              <span className="min-w-0 flex-1">
                <span className="block truncate text-sm">{t.title}</span>
                <span className="block truncate text-[11px] text-[var(--muted)]">
                  {[t.customerName, t.assignedToName ?? t.assignedTeamName].filter(Boolean).join(' · ') || 'No customer'}
                </span>
              </span>
              <StatusBadge status={t.portalStatus} />
            </button>
          ))}
          {/* Says how many there are, not just how many fit: a list cut at eight that looks complete
              is how somebody concludes their ticket is gone. */}
          {data && data.total > results.length && (
            <button onClick={() => go(`/dashboard/tickets?q=${encodeURIComponent(q)}`)}
              className="flex w-full items-center justify-between px-4 py-2.5 text-left text-xs text-[var(--muted)] hover:bg-[var(--bg)]">
              <span>{data.total} match “{q}” — see all</span>
              <CornerDownLeft size={12} />
            </button>
          )}
        </div>
      )}
    </div>
  );
}
