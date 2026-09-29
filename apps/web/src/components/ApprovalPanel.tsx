'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { BadgeCheck, CheckCircle2, Clock, XCircle } from 'lucide-react';
import { api, type StaffApprovals, type TicketApproval } from '@/lib/api';

const when = (iso: string) =>
  new Date(iso).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

const HOW: Record<string, string> = { Portal: 'in the portal', Phone: 'by phone', Email: 'by email', InPerson: 'in person' };

const field = 'w-full rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 py-2 text-sm font-normal text-[var(--fg)] outline-none focus:border-brand';

/** One settled request, the same line for staff and client. */
function Settled({ a }: { a: TicketApproval }) {
  const approved = a.state === 'Approved';
  if (a.state === 'Cancelled') {
    return (
      <li className="text-xs text-[var(--muted)]">
        Request to {a.approverName} withdrawn{a.recordedByName ? ` by ${a.recordedByName}` : ''} · &ldquo;{a.request}&rdquo;
      </li>
    );
  }
  return (
    <li className="flex gap-2 text-sm">
      {approved
        ? <CheckCircle2 size={16} className="mt-0.5 shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />
        : <XCircle size={16} className="mt-0.5 shrink-0 text-red-600 dark:text-red-400" aria-hidden="true" />}
      <span>
        <strong>{approved ? 'Approved' : 'Rejected'}</strong> by {a.approverName} {HOW[a.channel ?? 'Portal']}
        {a.decidedAt ? `, ${when(a.decidedAt)}` : ''}
        {a.recordedByName && a.channel !== 'Portal' ? <span className="text-[var(--muted)]"> (recorded by {a.recordedByName})</span> : null}
        <span className="block text-xs text-[var(--muted)]">&ldquo;{a.request}&rdquo;{a.decisionComment ? ` — ${a.decisionComment}` : ''}</span>
      </span>
    </li>
  );
}

/**
 * Approvals on a ticket. Staff ask one of the client's named approvers and, when the answer comes by
 * phone, write it down; the approver answers here in the portal. While a request is open the ticket
 * waits on the customer, so its SLA clock is paused — said on the form, because it is the part a
 * technician would otherwise wonder about.
 */
export function ApprovalPanel({ ticketId, isStaff, canUpdate }: { ticketId: string; isStaff: boolean; canUpdate: boolean }) {
  return isStaff ? <StaffApprovals ticketId={ticketId} canUpdate={canUpdate} /> : <ClientApprovals ticketId={ticketId} />;
}

function useRefresh(ticketId: string) {
  const qc = useQueryClient();
  return () => {
    qc.invalidateQueries({ queryKey: ['approvals', ticketId] });
    qc.invalidateQueries({ queryKey: ['ticket', ticketId] });
    qc.invalidateQueries({ queryKey: ['tickets'] });
    qc.invalidateQueries({ queryKey: ['my-approvals'] });
  };
}

function StaffApprovals({ ticketId, canUpdate }: { ticketId: string; canUpdate: boolean }) {
  const refresh = useRefresh(ticketId);
  const { data: view } = useQuery({
    queryKey: ['approvals', ticketId], queryFn: () => api.ticketApprovals(ticketId), retry: false,
  });
  const [asking, setAsking] = useState(false);
  const [recording, setRecording] = useState(false);

  if (!view || !view.applies) return null;
  // Nothing asked and nothing askable - a finished ticket, say - is not worth a panel on every
  // ticket. The one reason kept is the actionable one: the client has nobody on their list yet.
  if (view.approvals.length === 0 && !view.canAsk && !view.missingApprovers) return null;
  const pending = view.approvals.find((a) => a.state === 'Pending');
  const settled = view.approvals.filter((a) => a.state !== 'Pending');

  return (
    <section aria-labelledby="approval-heading" className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
      <div className="flex items-center justify-between gap-2">
        <h2 id="approval-heading" className="flex items-center gap-2 text-sm font-semibold">
          <BadgeCheck size={15} aria-hidden="true" /> Client approval
        </h2>
        {canUpdate && view.canAsk && !asking && (
          <button type="button" onClick={() => setAsking(true)}
            className="rounded-lg border border-[var(--border)] px-2.5 py-1 text-xs font-medium hover:bg-[var(--bg)]">
            Ask for approval
          </button>
        )}
      </div>

      {pending && (
        <div className="mt-3 rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm dark:border-amber-800 dark:bg-amber-950/40">
          <p className="flex items-start gap-2">
            <Clock size={15} className="mt-0.5 shrink-0 text-amber-700 dark:text-amber-400" aria-hidden="true" />
            <span>
              Waiting for <strong>{pending.approverName}</strong> since {when(pending.requestedAt)}
              <span className="block text-[var(--muted)]">&ldquo;{pending.request}&rdquo; · asked by {pending.requestedByName}</span>
            </span>
          </p>
          <p className="mt-1 text-xs text-[var(--muted)]">The ticket waits on the customer until they answer, so its SLA clock is paused.</p>
          {canUpdate && !recording && (
            <div className="mt-2 flex flex-wrap gap-2">
              <button type="button" onClick={() => setRecording(true)}
                className="rounded-lg bg-brand px-2.5 py-1 text-xs font-medium text-brand-fg hover:opacity-90">Record their answer</button>
              <WithdrawButton approvalId={pending.id} onDone={refresh} />
            </div>
          )}
          {recording && <RecordForm approvalId={pending.id} name={pending.approverName}
            onDone={() => { setRecording(false); refresh(); }} onCancel={() => setRecording(false)} />}
        </div>
      )}

      {asking && <AskForm ticketId={ticketId} view={view}
        onDone={() => { setAsking(false); refresh(); }} onCancel={() => setAsking(false)} />}

      {!pending && !asking && !view.canAsk && view.reason && (
        <p className="mt-2 text-xs text-[var(--muted)]">{view.reason}</p>
      )}
      {!pending && !asking && view.canAsk && settled.length === 0 && (
        <p className="mt-2 text-xs text-[var(--muted)]">
          Need the client to agree first — a purchase, a licence, an out-of-hours reboot? Ask one of their approvers.
        </p>
      )}

      {settled.length > 0 && <ul className="mt-3 space-y-2">{settled.map((a) => <Settled key={a.id} a={a} />)}</ul>}
    </section>
  );
}

function AskForm({ ticketId, view, onDone, onCancel }: {
  ticketId: string; view: StaffApprovals; onDone: () => void; onCancel: () => void;
}) {
  const [approverId, setApproverId] = useState(view.approvers[0]?.id ?? '');
  const [text, setText] = useState('');
  const chosen = view.approvers.find((a) => a.id === approverId);
  const send = useMutation({ mutationFn: () => api.requestApproval(ticketId, approverId, text.trim()), onSuccess: onDone });

  return (
    <form className="mt-3 space-y-3" onSubmit={(e) => { e.preventDefault(); if (approverId && text.trim()) send.mutate(); }}>
      <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
        Who should approve
        <select value={approverId} onChange={(e) => setApproverId(e.target.value)} className={field}>
          {view.approvers.map((a) => (
            <option key={a.id} value={a.id}>{a.name}{a.scope ? ` — ${a.scope}` : ''}</option>
          ))}
        </select>
        {chosen && !chosen.canAnswerInPortal && (
          <span className="block font-normal text-amber-700 dark:text-amber-400">
            {chosen.name} has no portal login{chosen.email ? ` as ${chosen.email}` : ''}, so they cannot answer here. Ask them by phone or email and record their answer.
          </span>
        )}
      </label>
      <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
        What needs approving
        <textarea required rows={3} maxLength={1000} value={text} onChange={(e) => setText(e.target.value)}
          placeholder="Adobe Acrobat licence for Priya — ₹18,000 a year" className={field} />
      </label>
      <p className="text-xs text-[var(--muted)]">The ticket will wait on the customer (SLA paused) until they answer, and a note goes into the ticket&rsquo;s thread.</p>
      {send.isError && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(send.error as Error).message}</p>}
      <div className="flex gap-2">
        <button type="submit" disabled={!approverId || !text.trim() || send.isPending}
          className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
          {send.isPending ? 'Sending…' : 'Send request'}
        </button>
        <button type="button" onClick={onCancel}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
      </div>
    </form>
  );
}

function RecordForm({ approvalId, name, onDone, onCancel }: {
  approvalId: string; name: string; onDone: () => void; onCancel: () => void;
}) {
  const [approved, setApproved] = useState<boolean | null>(null);
  const [channel, setChannel] = useState<'Phone' | 'Email' | 'InPerson'>('Phone');
  const [comment, setComment] = useState('');
  const save = useMutation({
    mutationFn: () => api.recordApproval(approvalId, approved!, channel, comment.trim() || null), onSuccess: onDone,
  });

  return (
    <form className="mt-3 space-y-3" onSubmit={(e) => { e.preventDefault(); if (approved !== null) save.mutate(); }}>
      <fieldset className="space-y-1">
        <legend className="text-xs font-medium text-[var(--muted)]">What did {name} say?</legend>
        <div className="flex gap-2">
          {[{ v: true, label: 'Approved' }, { v: false, label: 'Rejected' }].map((o) => (
            <label key={o.label} className={`cursor-pointer rounded-lg border px-3 py-1.5 text-sm ${approved === o.v ? 'border-brand bg-[var(--surface)] font-medium' : 'border-[var(--border)]'}`}>
              <input type="radio" name="decision" value={o.label} aria-label={o.label} className="sr-only"
                checked={approved === o.v} onChange={() => setApproved(o.v)} />
              {o.label}
            </label>
          ))}
        </div>
      </fieldset>
      <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
        How did the answer reach you
        <select value={channel} onChange={(e) => setChannel(e.target.value as typeof channel)} className={field}>
          <option value="Phone">By phone</option>
          <option value="Email">By email</option>
          <option value="InPerson">In person</option>
        </select>
      </label>
      <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
        Note (optional)
        <textarea rows={2} maxLength={1000} value={comment} onChange={(e) => setComment(e.target.value)}
          placeholder="Rahul approved on a call at 3 pm" className={field} />
      </label>
      {save.isError && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(save.error as Error).message}</p>}
      <div className="flex gap-2">
        <button type="submit" disabled={approved === null || save.isPending}
          className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
          {save.isPending ? 'Saving…' : 'Save answer'}
        </button>
        <button type="button" onClick={onCancel}
          className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--bg)]">Cancel</button>
      </div>
    </form>
  );
}

function WithdrawButton({ approvalId, onDone }: { approvalId: string; onDone: () => void }) {
  const withdraw = useMutation({ mutationFn: () => api.cancelApproval(approvalId), onSuccess: onDone });
  return (
    <>
      <button type="button" disabled={withdraw.isPending}
        onClick={() => { if (window.confirm('Withdraw this approval request? The ticket goes back to in progress.')) withdraw.mutate(); }}
        className="rounded-lg border border-[var(--border)] px-2.5 py-1 text-xs font-medium hover:bg-[var(--surface)] disabled:opacity-50">
        Withdraw request
      </button>
      {withdraw.isError && <p role="alert" className="w-full text-xs text-red-600 dark:text-red-400">{(withdraw.error as Error).message}</p>}
    </>
  );
}

function ClientApprovals({ ticketId }: { ticketId: string }) {
  const refresh = useRefresh(ticketId);
  const { data: approvals } = useQuery({
    queryKey: ['approvals', ticketId], queryFn: () => api.clientTicketApprovals(ticketId), retry: false,
  });
  // A withdrawn request is the team's bookkeeping, not something the client needs to read.
  const shown = (approvals ?? []).filter((a) => a.state !== 'Cancelled');
  if (shown.length === 0) return null;
  const pending = shown.find((a) => a.state === 'Pending');
  const settled = shown.filter((a) => a.state !== 'Pending');

  return (
    <section aria-labelledby="client-approval-heading" className="rounded-xl border border-brand/30 bg-brand-tint p-5 dark:bg-brand/10">
      <h2 id="client-approval-heading" className="flex items-center gap-2 text-sm font-semibold">
        <BadgeCheck size={15} aria-hidden="true" /> Approval
      </h2>
      {pending && (pending.canAnswer
        ? <DecideForm approval={pending} onDone={refresh} />
        : (
          <p className="mt-2 text-sm">
            Waiting for approval from <strong>{pending.approverName}</strong>
            <span className="block text-xs text-[var(--muted)]">&ldquo;{pending.request}&rdquo; · asked {when(pending.requestedAt)}</span>
          </p>
        ))}
      {settled.length > 0 && <ul className="mt-3 space-y-2">{settled.map((a) => <Settled key={a.id} a={a} />)}</ul>}
    </section>
  );
}

/** The approver's own answer: shared by the ticket page and the waiting-for-you list. */
export function DecideForm({ approval, onDone, compact = false }: {
  approval: { id: string; request: string; requestedByName: string; requestedAt: string };
  onDone: () => void; compact?: boolean;
}) {
  const [comment, setComment] = useState('');
  const decide = useMutation({
    mutationFn: (approved: boolean) => api.decideApproval(approval.id, approved, comment.trim() || null),
    onSuccess: onDone,
  });

  return (
    <div className="mt-2 space-y-2">
      {!compact && (
        <p className="text-sm">
          <strong>Your approval is needed.</strong> {approval.requestedByName} asked {when(approval.requestedAt)}:
          <span className="mt-1 block rounded-lg bg-[var(--surface)] px-3 py-2">&ldquo;{approval.request}&rdquo;</span>
        </p>
      )}
      <label className="block space-y-1 text-xs font-medium text-[var(--muted)]">
        Comment (optional)
        <textarea rows={2} maxLength={1000} value={comment} onChange={(e) => setComment(e.target.value)} className={field} />
      </label>
      {decide.isError && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(decide.error as Error).message}</p>}
      <div className="flex gap-2">
        <button type="button" disabled={decide.isPending} onClick={() => decide.mutate(true)}
          className="inline-flex items-center gap-1.5 rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
          <CheckCircle2 size={15} aria-hidden="true" /> Approve
        </button>
        <button type="button" disabled={decide.isPending} onClick={() => decide.mutate(false)}
          className="inline-flex items-center gap-1.5 rounded-lg border border-red-300 px-3 py-1.5 text-sm font-medium text-red-700 hover:bg-red-50 disabled:opacity-50 dark:border-red-800 dark:text-red-300 dark:hover:bg-red-950/40">
          <XCircle size={15} aria-hidden="true" /> Reject
        </button>
      </div>
    </div>
  );
}
