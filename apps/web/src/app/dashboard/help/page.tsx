'use client';

import Link from 'next/link';
import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { LifeBuoy, Search } from 'lucide-react';
import { api, type HelpSummary } from '@/lib/api';

/**
 * The client's Help page: answers from their IT team and their own company's FAQ, together, grouped
 * by subject. For every client user - the person who cannot connect to the VPN is the one who needs
 * the VPN article.
 */
export default function HelpPage() {
  const [search, setSearch] = useState('');
  const { data: articles, isLoading, isError } = useQuery({
    queryKey: ['help', search], queryFn: () => api.help(search.trim() || undefined), retry: false,
  });

  const groups = useMemo(() => {
    const byCategory = new Map<string, HelpSummary[]>();
    for (const a of articles ?? []) byCategory.set(a.category ?? 'General', [...(byCategory.get(a.category ?? 'General') ?? []), a]);
    return [...byCategory.entries()];
  }, [articles]);

  return (
    <div className="mx-auto max-w-3xl space-y-5">
      <div>
        <h1 className="flex items-center gap-2 text-xl font-semibold"><LifeBuoy size={20} aria-hidden="true" /> Help</h1>
        <p className="text-sm text-[var(--muted)]">Answers to common questions. Can&rsquo;t find yours? <Link href="/dashboard/tickets/new" className="text-brand hover:underline">Raise a ticket</Link>.</p>
      </div>
      <label className="relative block">
        <span className="sr-only">Search help</span>
        <Search size={16} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-[var(--faint)]" aria-hidden="true" />
        <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search — e.g. VPN, printer, password"
          className="w-full rounded-xl border border-[var(--border)] bg-[var(--surface)] py-2.5 pl-10 pr-3 text-sm outline-none focus:border-brand" />
      </label>

      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {isError && <p className="text-sm text-[var(--muted)]">Help articles are for your company&rsquo;s portal users.</p>}
      {articles && articles.length === 0 && (
        <p className="rounded-xl border border-dashed border-[var(--border)] px-5 py-10 text-center text-sm text-[var(--muted)]">
          {search ? 'Nothing matches that. Try another word, or raise a ticket.' : 'No help articles yet.'}
        </p>
      )}
      {groups.map(([category, items]) => (
        <section key={category} aria-label={category} className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <h2 className="border-b border-[var(--border)] px-5 py-3 text-sm font-semibold">{category}</h2>
          <ul className="divide-y divide-[var(--border)]">
            {items.map((a) => (
              <li key={`${a.source}-${a.id}`}>
                <Link href={`/dashboard/help/${a.source}/${a.id}`} className="block px-5 py-3 hover:bg-[var(--bg)]">
                  <span className="block text-sm font-medium">{a.title}</span>
                  {a.excerpt && <span className="block truncate text-xs text-[var(--muted)]">{a.excerpt}</span>}
                </Link>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}
