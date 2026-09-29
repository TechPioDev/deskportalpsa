'use client';

import Link from 'next/link';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { BadgeCheck } from 'lucide-react';
import { api } from '@/lib/api';
import { DecideForm } from '@/components/ApprovalPanel';
import { isStaffPermissions } from '@/lib/staff';

/**
 * Requests waiting on the signed-in client user, answerable right here. An approver is often not the
 * person who raised the ticket - the finance head approving a licence - so the question has to find
 * them rather than wait to be found inside somebody else's ticket. Staff never see this: they ask,
 * they do not answer.
 */
export function MyApprovalsBanner() {
  const qc = useQueryClient();
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const isStaff = me ? isStaffPermissions(me.permissions) : true;
  const { data: waiting } = useQuery({
    queryKey: ['my-approvals'], queryFn: api.myApprovals, enabled: !!me && !isStaff, retry: false,
  });
  if (isStaff || !waiting || waiting.length === 0) return null;

  const done = (ticketId: string) => {
    qc.invalidateQueries({ queryKey: ['my-approvals'] });
    qc.invalidateQueries({ queryKey: ['approvals', ticketId] });
    qc.invalidateQueries({ queryKey: ['tickets'] });
  };

  return (
    <section aria-labelledby="my-approvals-heading" className="rounded-xl border border-amber-300 bg-amber-50 p-4 dark:border-amber-800 dark:bg-amber-950/40">
      <h2 id="my-approvals-heading" className="flex items-center gap-2 text-sm font-semibold">
        <BadgeCheck size={16} aria-hidden="true" />
        {waiting.length === 1 ? '1 request is waiting for your approval' : `${waiting.length} requests are waiting for your approval`}
      </h2>
      <ul className="mt-3 space-y-4">
        {waiting.map((a) => (
          <li key={a.id} className="rounded-lg bg-[var(--surface)] p-3">
            <p className="text-sm">
              <Link href={`/dashboard/tickets/${a.ticketId}`} className="font-medium text-brand hover:underline">
                {a.reference} · {a.ticketTitle}
              </Link>
              <span className="block text-[var(--muted)]">
                {a.requestedByName} asks: &ldquo;{a.request}&rdquo;
              </span>
            </p>
            <DecideForm approval={a} compact onDone={() => done(a.ticketId)} />
          </li>
        ))}
      </ul>
    </section>
  );
}
