'use client';

import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { BookOpen, Plus, Search, Lock, Users, Building2, Trash2 } from 'lucide-react';
import { api, type KbArticle, type KbArticleInput, type KbArticleSummary } from '@/lib/api';
import { NoteBody } from '@/components/NoteBody';

const AUDIENCE: Record<KbArticleSummary['audience'], { label: string; icon: typeof Lock; tone: string }> = {
  Staff: { label: 'Staff only', icon: Lock, tone: 'bg-[var(--bg)] text-[var(--muted)]' },
  AllClients: { label: 'All clients', icon: Users, tone: 'bg-brand-tint text-brand dark:bg-brand/15' },
  SelectedClients: { label: 'Chosen clients', icon: Building2, tone: 'bg-sky-50 text-sky-800 dark:bg-sky-950/60 dark:text-sky-300' },
};

const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand';

/**
 * The team's knowledge base: runbooks for the desk, and articles for clients that the Help page shows
 * them and the new-ticket form offers while they type. The number at the top is the reason to write
 * them - tickets clients did not need to raise because an article answered them.
 */
export default function KnowledgePage() {
  const [search, setSearch] = useState('');
  const [editing, setEditing] = useState<string | 'new' | null>(null);
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const canWrite = me?.permissions.includes('tickets.update') ?? false;
  const { data: articles, isLoading } = useQuery({ queryKey: ['kb', search], queryFn: () => api.kbList(search.trim() || undefined) });
  const { data: stats } = useQuery({ queryKey: ['kb-stats'], queryFn: () => api.kbStats(30), retry: false });

  const groups = useMemo(() => {
    const byCategory = new Map<string, KbArticleSummary[]>();
    for (const a of articles ?? []) {
      const key = a.category ?? 'Uncategorised';
      byCategory.set(key, [...(byCategory.get(key) ?? []), a]);
    }
    return [...byCategory.entries()];
  }, [articles]);

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="flex items-center gap-2 text-xl font-semibold"><BookOpen size={20} aria-hidden="true" /> Knowledge base</h1>
          <p className="max-w-2xl text-sm text-[var(--muted)]">
            Articles for the desk and for clients. Client articles appear on their Help page, and are suggested while they type a new ticket.
          </p>
        </div>
        {canWrite && editing === null && (
          <button type="button" onClick={() => setEditing('new')}
            className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3.5 py-2 text-sm font-medium text-brand-fg hover:opacity-90">
            <Plus size={16} /> New article
          </button>
        )}
      </div>

      {stats && (
        <section aria-label="What the articles did" className="grid gap-3 sm:grid-cols-3">
          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
            <p className="text-xs text-[var(--muted)]">Tickets avoided, last {stats.days} days</p>
            <p className="mt-1 text-3xl font-semibold tabular-nums">{stats.ticketsAvoided}</p>
            <p className="text-xs text-[var(--faint)]">A client said an article solved it, so no ticket was raised.</p>
          </div>
          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 sm:col-span-2">
            <p className="text-xs text-[var(--muted)]">Most helpful articles</p>
            {stats.topArticles.length === 0 ? (
              <p className="mt-2 text-sm text-[var(--muted)]">Nothing yet. Publish an article for clients and it will be suggested as they type tickets.</p>
            ) : (
              <ul className="mt-2 space-y-1 text-sm">
                {stats.topArticles.slice(0, 4).map((t) => (
                  <li key={`${t.source}-${t.id}`} className="flex justify-between gap-3">
                    <span className="truncate">{t.title}{t.source === 'ClientFaq' && <span className="text-xs text-[var(--muted)]"> (client&rsquo;s own FAQ)</span>}</span>
                    <span className="shrink-0 tabular-nums text-[var(--muted)]">{t.solved} avoided</span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </section>
      )}

      {editing !== null && (
        <Editor id={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />
      )}

      <label className="relative block max-w-md">
        <span className="sr-only">Search articles</span>
        <Search size={15} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-[var(--faint)]" aria-hidden="true" />
        <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search titles and text…" className={`${field} pl-9`} />
      </label>

      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {articles && articles.length === 0 && (
        <p className="rounded-xl border border-dashed border-[var(--border)] px-5 py-10 text-center text-sm text-[var(--muted)]">
          {search ? 'No article matches that.' : 'No articles yet. Start with the question clients ask most — "How do I connect to the VPN?"'}
        </p>
      )}
      {groups.map(([category, items]) => (
        <section key={category} aria-label={category} className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
          <h2 className="border-b border-[var(--border)] px-5 py-3 text-sm font-semibold">{category}</h2>
          <ul className="divide-y divide-[var(--border)]">
            {items.map((a) => {
              const aud = AUDIENCE[a.audience];
              return (
                <li key={a.id}>
                  <button type="button" onClick={() => setEditing(a.id)} disabled={!canWrite}
                    className="flex w-full flex-wrap items-center gap-2 px-5 py-3 text-left hover:bg-[var(--bg)] disabled:cursor-default">
                    <span className="min-w-0 flex-1 truncate text-sm font-medium">{a.title}</span>
                    {!a.isPublished && <span className="rounded bg-amber-100 px-1.5 py-0.5 text-[11px] font-medium text-amber-800 dark:bg-amber-950 dark:text-amber-300">Draft</span>}
                    <span className={`inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-[11px] font-medium ${aud.tone}`}
                      title={a.audience === 'SelectedClients' ? a.clientNames.join(', ') : undefined}>
                      <aud.icon size={11} aria-hidden="true" />
                      {a.audience === 'SelectedClients' ? a.clientNames.join(', ') : aud.label}
                    </span>
                    {a.solved > 0 && <span className="text-xs tabular-nums text-[var(--muted)]">{a.solved} avoided</span>}
                  </button>
                </li>
              );
            })}
          </ul>
        </section>
      ))}
    </div>
  );
}

function Editor({ id, onClose }: { id: string | null; onClose: () => void }) {
  const qc = useQueryClient();
  const { data: existing } = useQuery({ queryKey: ['kb-article', id], queryFn: () => api.kbGet(id!), enabled: !!id });
  const { data: clients } = useQuery({ queryKey: ['report-clients'], queryFn: api.reportClients, staleTime: 5 * 60_000, retry: false });
  if (id && !existing) return <p className="text-sm text-[var(--muted)]">Loading…</p>;
  return <EditorForm key={existing?.id ?? 'new'} initial={existing ?? null} clients={clients ?? []}
    onDone={() => { qc.invalidateQueries({ queryKey: ['kb'] }); onClose(); }} onCancel={onClose} />;
}

function EditorForm({ initial, clients, onDone, onCancel }: {
  initial: KbArticle | null; clients: { id: string; name: string }[]; onDone: () => void; onCancel: () => void;
}) {
  const [f, setF] = useState<KbArticleInput>({
    title: initial?.title ?? '', body: initial?.body ?? '', category: initial?.category ?? '',
    audience: initial?.audience ?? 'AllClients', clientIds: initial?.clientIds ?? [], isPublished: initial?.isPublished ?? false,
  });
  const [preview, setPreview] = useState(false);
  const save = useMutation({ mutationFn: () => api.kbSave(initial?.id ?? null, { ...f, category: f.category?.trim() || null }), onSuccess: onDone });
  const remove = useMutation({ mutationFn: () => api.kbDelete(initial!.id), onSuccess: onDone });
  const toggleClient = (cid: string) =>
    setF({ ...f, clientIds: f.clientIds.includes(cid) ? f.clientIds.filter((x) => x !== cid) : [...f.clientIds, cid] });

  return (
    <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}
      className="space-y-4 rounded-xl border border-brand/30 bg-[var(--surface)] p-5">
      <h2 className="text-sm font-semibold">{initial ? 'Edit article' : 'New article'}</h2>
      <div className="grid gap-3 sm:grid-cols-3">
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)] sm:col-span-2">
          Title
          <input required maxLength={200} value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })}
            placeholder="How to connect to the VPN" className={field} />
        </label>
        <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
          Category
          <input maxLength={60} value={f.category ?? ''} onChange={(e) => setF({ ...f, category: e.target.value })}
            placeholder="VPN, Email, Printers…" className={field} />
        </label>
      </div>

      <fieldset className="space-y-2">
        <legend className="text-xs font-medium text-[var(--muted)]">Who can read it</legend>
        <div className="flex flex-wrap gap-2">
          {(Object.keys(AUDIENCE) as KbArticle['audience'][]).map((key) => (
            <label key={key} className={`inline-flex cursor-pointer items-center gap-1.5 rounded-lg border px-3 py-1.5 text-sm ${f.audience === key ? 'border-brand font-medium' : 'border-[var(--border)]'}`}>
              <input type="radio" name="audience" className="sr-only" aria-label={AUDIENCE[key].label}
                checked={f.audience === key} onChange={() => setF({ ...f, audience: key })} />
              {AUDIENCE[key].label}
            </label>
          ))}
        </div>
        {f.audience === 'SelectedClients' && (
          <div className="flex flex-wrap gap-x-4 gap-y-1 rounded-lg bg-[var(--bg)] px-3 py-2">
            {clients.map((c) => (
              <label key={c.id} className="inline-flex items-center gap-1.5 text-sm">
                <input type="checkbox" aria-label={c.name} checked={f.clientIds.includes(c.id)} onChange={() => toggleClient(c.id)} /> {c.name}
              </label>
            ))}
            {clients.length === 0 && <span className="text-sm text-[var(--muted)]">No clients yet.</span>}
          </div>
        )}
      </fieldset>

      <div className="space-y-1">
        <div className="flex items-center justify-between">
          <span className="text-xs font-medium text-[var(--muted)]">Article</span>
          <div className="flex gap-1 text-xs">
            <button type="button" onClick={() => setPreview(false)} className={`rounded px-2 py-0.5 ${!preview ? 'bg-[var(--bg)] font-medium' : 'text-[var(--muted)]'}`}>Write</button>
            <button type="button" onClick={() => setPreview(true)} className={`rounded px-2 py-0.5 ${preview ? 'bg-[var(--bg)] font-medium' : 'text-[var(--muted)]'}`}>Preview</button>
          </div>
        </div>
        {preview ? (
          <div className="min-h-40 rounded-lg border border-[var(--border)] px-3 py-2">{f.body.trim() ? <NoteBody body={f.body} full /> : <p className="text-sm text-[var(--muted)]">Nothing to preview.</p>}</div>
        ) : (
          <textarea aria-label="Article" rows={12} maxLength={20000} value={f.body} onChange={(e) => setF({ ...f, body: e.target.value })}
            placeholder={'1. Open **FortiClient**.\n2. Choose *Office VPN* and sign in with your work email.\n\nStill stuck? Raise a ticket and say which step failed.'}
            className={`${field} font-mono text-[13px]`} />
        )}
        <p className="text-[11px] text-[var(--faint)]">**bold**, *italic*, lists, `code`, tables and &gt; quotes work, as in ticket notes.</p>
      </div>

      <label className="inline-flex items-center gap-2 text-sm">
        <input type="checkbox" aria-label="Published" checked={f.isPublished} onChange={(e) => setF({ ...f, isPublished: e.target.checked })} />
        Published <span className="text-xs text-[var(--muted)]">— a draft is visible to the team only</span>
      </label>

      {(save.isError || remove.isError) && (
        <p role="alert" className="text-sm text-red-600 dark:text-red-400">{((save.error ?? remove.error) as Error).message}</p>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <button type="submit" disabled={save.isPending || !f.title.trim()}
          className="rounded-lg bg-brand px-3.5 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
          {save.isPending ? 'Saving…' : 'Save article'}
        </button>
        <button type="button" onClick={onCancel} className="rounded-lg border border-[var(--border)] px-3.5 py-2 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
        {initial && (
          <button type="button" disabled={remove.isPending}
            onClick={() => { if (window.confirm(`Delete "${initial.title}"? Clients will no longer see it.`)) remove.mutate(); }}
            className="ml-auto inline-flex items-center gap-1.5 rounded-lg px-3 py-2 text-sm text-red-700 hover:bg-red-50 dark:text-red-300 dark:hover:bg-red-950/40">
            <Trash2 size={14} aria-hidden="true" /> Delete
          </button>
        )}
      </div>
    </form>
  );
}
