'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { useMutation, useQuery } from '@tanstack/react-query';
import { z } from 'zod';
import { ArrowLeft, ArrowRight, CheckCircle2, ClipboardList, Info } from 'lucide-react';
import Link from 'next/link';
import { api } from '@/lib/api';
import { isStaffPermissions } from '@/lib/staff';
import { HelpSuggestions } from '@/components/HelpSuggestions';

const FormSchema = z.object({
  title: z.string().min(3, 'Give your request a short title (3+ characters).'),
  description: z.string().optional(),
  priority: z.enum(['LOW', 'NORMAL', 'HIGH', 'CRITICAL']),
});

/**
 * "New ticket" means two different things, by who presses it.
 *
 * A client's own user asks the IT team for help: the request form below, which raises a ticket for
 * their company in its PSA. A member of staff raises work on one of the team's own boards. The
 * button used to lead both to the client's form, which the server accepts only from a client: a
 * member of staff filled it in and was told "This endpoint is for client portal users" (reported
 * by the owner on 12 October 2026). Staff are now asked which board the ticket belongs to.
 *
 * Someone who is both (the development identity; nobody in production) gets the boards first and
 * can still choose the client's form.
 */
export default function NewTicketPage() {
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const [asClient, setAsClient] = useState(false);
  // The same question the client's form asks anyway. It answers only for a client's own user, so
  // an answer says this person can use that form.
  const client = useQuery({ queryKey: ['client-device-choices'], queryFn: api.clientDeviceChoices, retry: false });
  // Until "me" has loaded nobody knows which form is theirs: showing either would be a guess.
  if (!me) return <div className="mx-auto max-w-xl" aria-busy="true" />;
  if (isStaffPermissions(me.permissions) && !asClient) {
    return <StaffNewTicket
      boardsOn={me.features?.internalBoards ?? true}
      mayRaise={me.permissions.includes('tickets.create')}
      mayManageBoards={me.permissions.includes('boards.manage')}
      onAsClient={client.isSuccess ? () => setAsClient(true) : null} />;
  }
  return <ClientRequestForm />;
}

function StaffNewTicket({ boardsOn, mayRaise, mayManageBoards, onAsClient }: {
  boardsOn: boolean; mayRaise: boolean; mayManageBoards: boolean; onAsClient: (() => void) | null;
}) {
  const usable = boardsOn && mayRaise;
  const { data: boards, isLoading, isError } = useQuery({
    queryKey: ['boards', false], queryFn: () => api.boards(false), enabled: usable, retry: false,
  });
  return (
    <div className="mx-auto max-w-xl space-y-5">
      <Link href="/dashboard/tickets" className="inline-flex items-center gap-1.5 text-sm text-[var(--muted)] hover:text-[var(--fg)]">
        <ArrowLeft size={15} /> Back to tickets
      </Link>
      <div>
        <h1 className="text-xl font-semibold">New ticket</h1>
        <p className="text-sm text-[var(--muted)]">Your team&rsquo;s own work is raised on an internal board. Choose the board this ticket belongs to.</p>
      </div>

      <section aria-label="Internal boards" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        {!boardsOn ? (
          <p className="p-5 text-sm text-[var(--muted)]">Internal boards are switched off on this installation, so there is nowhere here to raise the team&rsquo;s own work.</p>
        ) : !mayRaise ? (
          <p className="p-5 text-sm text-[var(--muted)]">Your role may not raise tickets. Ask an administrator to give you &ldquo;Create tickets&rdquo;.</p>
        ) : isLoading ? (
          <p className="p-5 text-sm text-[var(--muted)]">Loading the boards…</p>
        ) : isError ? (
          <p role="alert" className="p-5 text-sm text-red-600 dark:text-red-400">The boards could not be loaded. Try again in a moment.</p>
        ) : !boards || boards.length === 0 ? (
          <div className="space-y-2 p-5 text-sm">
            <p>There is no internal board yet.</p>
            {mayManageBoards
              ? <Link href="/dashboard/boards" className="inline-flex items-center gap-1 font-medium text-brand hover:underline">Create the first board <ArrowRight size={14} /></Link>
              : <p className="text-[var(--muted)]">Ask an administrator to create one.</p>}
          </div>
        ) : (
          <ul className="divide-y divide-[var(--border)]">
            {boards.map((b) => (
              <li key={b.id}>
                <Link href={`/dashboard/boards/${b.id}?new=1`}
                  className="flex items-center gap-3 px-5 py-3.5 hover:bg-[var(--bg)]">
                  <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-brand-tint text-brand dark:bg-brand/15"><ClipboardList size={17} aria-hidden="true" /></span>
                  <span className="min-w-0 flex-1">
                    <span className="block text-sm font-medium">{b.name}</span>
                    <span className="block text-xs text-[var(--muted)]">
                      Tickets numbered {b.key}-… · {b.openTickets} open{b.description ? ` · ${b.description}` : ''}
                    </span>
                  </span>
                  <span className="inline-flex shrink-0 items-center gap-1 text-xs font-medium text-brand">Raise here <ArrowRight size={13} aria-hidden="true" /></span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>

      <p className="flex items-start gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-4 py-3 text-xs text-[var(--muted)]">
        <Info size={14} className="mt-0.5 shrink-0" aria-hidden="true" />
        <span>
          A ticket for a client, in their PSA, cannot be raised from here yet. Raise it in the PSA itself and it appears in PioManage at the next sync.
          The request form on this address is for a client&rsquo;s own users.
          {onAsClient && <> You also have a client sign-in: <button type="button" onClick={onAsClient} className="font-medium text-brand hover:underline">raise a request as a client instead</button>.</>}
        </span>
      </p>
    </div>
  );
}

function ClientRequestForm() {
  const router = useRouter();
  const [form, setForm] = useState({ title: '', description: '', priority: 'NORMAL', deviceId: '' });
  const [error, setError] = useState<string | null>(null);
  // Set when an article answered the question: the form steps aside, and nothing is raised.
  const [solvedBy, setSolvedBy] = useState<string | null>(null);
  // The company's devices the PSA knows. Only shown when there are some: an empty "Which device?"
  // is a question nobody can answer.
  const { data: devices } = useQuery({ queryKey: ['client-device-choices'], queryFn: api.clientDeviceChoices, retry: false });

  const create = useMutation({
    mutationFn: (body: { title: string; description?: string; priority?: string; deviceId?: string }) => api.createTicket(body),
    onSuccess: (r) => router.push(`/dashboard/tickets/${r.id}`),
    // The server's own words: "That device is not one of your company's devices" is something the
    // person can act on; a generic "could not submit" is not.
    onError: (e) => setError((e as Error).message || 'Could not submit the ticket. Please try again.'),
  });

  function submit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    const parsed = FormSchema.safeParse(form);
    if (!parsed.success) {
      setError(parsed.error.issues[0].message);
      return;
    }
    create.mutate({ ...parsed.data, deviceId: form.deviceId || undefined });
  }

  return (
    <div className="mx-auto max-w-xl space-y-5">
      <Link href="/dashboard/tickets" className="inline-flex items-center gap-1.5 text-sm text-[var(--muted)] hover:text-[var(--fg)]">
        <ArrowLeft size={15} /> Back to tickets
      </Link>
      <div>
        <h1 className="text-xl font-semibold">New ticket</h1>
        <p className="text-sm text-[var(--muted)]">Describe what you need help with — it goes straight to your IT team.</p>
      </div>

      {solvedBy ? (
        <div role="status" className="space-y-2 rounded-xl border border-brand/30 bg-brand-tint p-6 dark:bg-brand/10">
          <p className="flex items-center gap-2 font-medium"><CheckCircle2 size={18} className="text-brand" aria-hidden="true" /> Glad that helped.</p>
          <p className="text-sm text-[var(--muted)]">&ldquo;{solvedBy}&rdquo; answered it, so no ticket was raised. Your IT team can see which articles are saving you time.</p>
          <div className="flex gap-3 pt-1 text-sm">
            <Link href="/dashboard/tickets" className="font-medium text-brand hover:underline">Back to tickets</Link>
            <button type="button" onClick={() => setSolvedBy(null)} className="text-[var(--muted)] hover:underline">I still need help</button>
          </div>
        </div>
      ) : (
      <form onSubmit={submit} className="space-y-4 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-6">
        <Field label="Title">
          <input
            value={form.title}
            onChange={(e) => setForm({ ...form, title: e.target.value })}
            placeholder="e.g. Can't connect to the VPN"
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand"
          />
        </Field>
        <HelpSuggestions query={form.title} onSolved={setSolvedBy} />
        <Field label="Priority">
          <select
            value={form.priority}
            onChange={(e) => setForm({ ...form, priority: e.target.value })}
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand"
          >
            <option value="LOW">Low</option>
            <option value="NORMAL">Normal</option>
            <option value="HIGH">High</option>
            <option value="CRITICAL">Critical</option>
          </select>
        </Field>
        {devices && devices.length > 0 && (
          <Field label="Which device? (optional)">
            <select
              value={form.deviceId}
              onChange={(e) => setForm({ ...form, deviceId: e.target.value })}
              className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand"
            >
              <option value="">Not about a particular device</option>
              {devices.map((d) => <option key={d.id} value={d.id}>{d.name}{d.type ? ` — ${d.type}` : ''}</option>)}
            </select>
          </Field>
        )}
        <Field label="Description">
          <textarea
            value={form.description}
            onChange={(e) => setForm({ ...form, description: e.target.value })}
            rows={5}
            placeholder="What's happening? Include any error messages."
            className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm outline-none focus:border-brand"
          />
        </Field>

        {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}

        <div className="flex justify-end">
          <button
            type="submit"
            disabled={create.isPending}
            className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50"
          >
            {create.isPending ? 'Submitting…' : 'Submit ticket'}
          </button>
        </div>
      </form>
      )}
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1.5 block text-sm font-medium">{label}</span>
      {children}
    </label>
  );
}
