import {
  Paperclip, CircleDot, ShieldCheck, MessageSquare, CheckCircle2, Bell, RefreshCw, UserCheck,
} from 'lucide-react';

/**
 * Marketing product visuals.
 *
 * Deliberately abstract. An earlier version showed named people, company names, ticket numbers and
 * file names — which made a public page read like a screenshot of somebody's live client database.
 * A visitor only needs to recognise the shape of the experience: a request, its state, a reply, an
 * attachment. Nothing here is a record; every label is a category.
 *
 * Built from markup rather than screenshots so it cannot go stale, inverts with the theme, is
 * readable by a screen reader, and ships no image bytes.
 */

export type ProductView = 'requests' | 'conversation' | 'files' | 'updates' | 'sync';

const STATE = {
  open: 'bg-sky-100 text-sky-800 dark:bg-sky-950 dark:text-sky-300',
  progress: 'bg-brand-tint text-brand-deep dark:bg-brand/30 dark:text-brand-soft',
  waiting: 'bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300',
  resolved: 'bg-slate-200 text-slate-700 dark:bg-slate-800 dark:text-slate-300',
};

const REQUESTS = [
  { title: 'Connectivity issue', state: 'In progress', tone: STATE.progress, meta: 'Technician assigned' },
  { title: 'Access request', state: 'Open', tone: STATE.open, meta: 'Awaiting triage' },
  { title: 'Email not syncing', state: 'Waiting on you', tone: STATE.waiting, meta: 'Reply needed' },
  { title: 'Printer offline', state: 'Resolved', tone: STATE.resolved, meta: 'Closed' },
];

export function BrowserFrame({
  children, url = 'PioManage', className = '',
}: { children: React.ReactNode; url?: string; className?: string }) {
  return (
    <div
      className={`overflow-hidden rounded-2xl border border-[var(--border)] bg-[var(--surface)] shadow-[0_24px_60px_-24px_rgba(11,18,32,0.35)] ${className}`}
    >
      <div className="flex items-center gap-2 border-b border-[var(--border)] bg-[var(--bg)] px-3.5 py-2.5">
        <span className="flex gap-1.5" aria-hidden="true">
          {['bg-rose-400', 'bg-amber-400', 'bg-emerald-400'].map((c) => (
            <span key={c} className={`h-2.5 w-2.5 rounded-full ${c}`} />
          ))}
        </span>
        <span className="mx-auto flex items-center gap-1.5 rounded-md bg-[var(--surface)] px-3 py-1 text-[11px] text-[var(--muted)]">
          <ShieldCheck size={11} className="text-brand-mid" aria-hidden="true" />
          {url}
        </span>
      </div>
      {children}
    </div>
  );
}

function Pill({ label, tone }: { label: string; tone: string }) {
  return <span className={`rounded px-1.5 py-0.5 text-[11px] font-medium ${tone}`}>{label}</span>;
}

export function ProductScreen({ view = 'requests' }: { view?: ProductView }) {
  if (view === 'conversation') {
    return (
      <div className="space-y-3 p-4">
        <div className="flex items-center justify-between">
          <p className="text-sm font-semibold">Support request</p>
          <Pill label="In progress" tone={STATE.progress} />
        </div>
        <div className="space-y-2.5">
          {[
            { mine: false, who: 'Client', body: 'The office connection keeps dropping this morning.' },
            { mine: true, who: 'Support team', body: 'Thanks — we are looking into it now and will update you shortly.' },
            { mine: false, who: 'Client', body: 'Screenshot attached.' },
          ].map((m) => (
            <div
              key={m.body}
              className={`max-w-[85%] rounded-xl px-3 py-2 text-[12.5px] leading-relaxed ${
                m.mine ? 'ml-auto bg-brand text-brand-fg' : 'bg-[var(--bg)]'
              }`}
            >
              <p className={`mb-0.5 text-[10.5px] font-semibold ${m.mine ? 'text-brand-fg/70' : 'text-[var(--faint)]'}`}>
                {m.who}
              </p>
              {m.body}
            </div>
          ))}
        </div>
        <p className="flex items-center gap-2 rounded-lg border border-[var(--border)] px-3 py-2 text-[11px] text-[var(--muted)]">
          <MessageSquare size={12} aria-hidden="true" /> Internal notes stay internal — never shown to the client.
        </p>
      </div>
    );
  }

  if (view === 'files') {
    return (
      <div className="space-y-2.5 p-4">
        <p className="text-sm font-semibold">Shared files</p>
        {[
          { n: 'Screenshot', s: 'Added by client' },
          { n: 'Error log', s: 'Added by support team' },
          { n: 'Resolution summary', s: 'Added by support team' },
        ].map((f) => (
          <div key={f.n} className="flex items-center gap-3 rounded-lg border border-[var(--border)] px-3 py-2.5">
            <Paperclip size={14} className="shrink-0 text-brand-mid" aria-hidden="true" />
            <span className="min-w-0 flex-1">
              <span className="block truncate text-[12.5px] font-medium">{f.n}</span>
              <span className="block text-[11px] text-[var(--muted)]">{f.s}</span>
            </span>
            <CheckCircle2 size={14} className="shrink-0 text-brand-mid" aria-hidden="true" />
          </div>
        ))}
        <p className="text-[11px] text-[var(--muted)]">Files shared either way arrive on the request in your PSA.</p>
      </div>
    );
  }

  if (view === 'updates') {
    return (
      <div className="p-4">
        <p className="mb-3 text-sm font-semibold">Recent updates</p>
        <ol className="relative space-y-3 border-l border-[var(--border)] pl-4">
          {[
            { e: 'Request submitted', s: 'From the client portal', i: Bell },
            { e: 'Technician assigned', s: 'Synced with your PSA', i: UserCheck },
            { e: 'Status updated', s: 'Open → In progress', i: CircleDot },
            { e: 'Response received', s: 'Visible to the client', i: MessageSquare },
            { e: 'File uploaded', s: 'Shared both ways', i: Paperclip },
          ].map((x) => (
            <li key={x.e} className="relative">
              <span className="absolute -left-[21px] top-1.5 h-2 w-2 rounded-full bg-brand-mid" aria-hidden="true" />
              <p className="flex items-center gap-1.5 text-[12.5px] font-medium">
                <x.i size={12} className="text-brand-mid" aria-hidden="true" /> {x.e}
              </p>
              <p className="text-[11px] text-[var(--muted)]">{x.s}</p>
            </li>
          ))}
        </ol>
      </div>
    );
  }

  if (view === 'sync') {
    return (
      <div className="space-y-3 p-4">
        <p className="text-sm font-semibold">Connected PSA</p>
        <div className="flex items-center gap-3 rounded-lg border border-[var(--border)] px-3 py-2.5">
          <span className="h-2 w-2 rounded-full bg-emerald-500 dp-pulse" aria-hidden="true" />
          <span className="min-w-0 flex-1">
            <span className="block text-[12.5px] font-medium">Your PSA</span>
            <span className="block text-[11px] text-[var(--muted)]">Connected · syncing continuously</span>
          </span>
          <RefreshCw size={14} className="text-brand-mid" aria-hidden="true" />
        </div>
        <div className="grid grid-cols-2 gap-2.5">
          {['Requests', 'Conversations', 'Files', 'Status', 'Priority', 'Time'].map((x) => (
            <p key={x} className="flex items-center gap-1.5 rounded-lg border border-[var(--border)] px-3 py-2 text-[12px]">
              <CheckCircle2 size={12} className="text-brand-mid" aria-hidden="true" /> {x}
            </p>
          ))}
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-3 p-4">
      <div>
        <p className="text-sm font-semibold">Good morning</p>
        <p className="text-[12px] text-[var(--muted)]">Your support requests</p>
      </div>
      <div className="flex gap-1.5">
        {['Open', 'In progress', 'Resolved'].map((t, i) => (
          <span
            key={t}
            className={`rounded-full px-2.5 py-1 text-[11px] font-medium ${
              i === 1 ? 'bg-brand text-brand-fg' : 'border border-[var(--border)] text-[var(--muted)]'
            }`}
          >
            {t}
          </span>
        ))}
      </div>
      <div className="space-y-1.5">
        {REQUESTS.map((r) => (
          <div key={r.title} className="flex items-center gap-3 rounded-lg border border-[var(--border)] px-3 py-2.5">
            <span className="min-w-0 flex-1">
              <span className="block truncate text-[12.5px] font-medium">{r.title}</span>
              <span className="block text-[11px] text-[var(--muted)]">{r.meta}</span>
            </span>
            <Pill label={r.state} tone={r.tone} />
          </div>
        ))}
      </div>
      <button className="w-full rounded-lg bg-brand px-3 py-2.5 text-[12.5px] font-medium text-brand-fg">
        New request
      </button>
    </div>
  );
}
