'use client';

import Link from 'next/link';
import { use } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ArrowLeft } from 'lucide-react';
import { api } from '@/lib/api';
import { NoteBody } from '@/components/NoteBody';

/** One help article, in full. */
export default function HelpArticlePage({ params }: { params: Promise<{ source: string; id: string }> }) {
  const { source, id } = use(params);
  const { data: article, isError } = useQuery({
    queryKey: ['help-article', source, id], queryFn: () => api.helpArticle(source, id), retry: false,
  });

  return (
    <div className="mx-auto max-w-3xl space-y-4">
      <Link href="/dashboard/help" className="inline-flex items-center gap-1.5 text-sm text-[var(--muted)] hover:text-brand">
        <ArrowLeft size={14} /> Help
      </Link>
      {isError && <p className="text-sm text-[var(--muted)]">This article is not available.</p>}
      {article && (
        <article className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-6">
          {article.category && <p className="text-xs font-medium uppercase tracking-wide text-[var(--faint)]">{article.category}</p>}
          <h1 className="mt-1 text-xl font-semibold">{article.title}</h1>
          <div className="mt-3"><NoteBody body={article.body} full /></div>
          <p className="mt-6 border-t border-[var(--border)] pt-3 text-sm text-[var(--muted)]">
            Still stuck? <Link href="/dashboard/tickets/new" className="text-brand hover:underline">Raise a ticket</Link> and say which step didn&rsquo;t work.
          </p>
        </article>
      )}
    </div>
  );
}
