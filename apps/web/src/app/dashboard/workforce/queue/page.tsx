'use client';

import { useQuery } from '@tanstack/react-query';
import { ListTodo } from 'lucide-react';
import { api } from '@/lib/api';
import { WorkforceNav } from '@/components/WorkforceCapacity';
import { PlanningQueueView } from '@/components/WorkforcePlanning';

/**
 * The planning queue: open work in the group's hands that is in nobody's plan, why it waits, how
 * urgent it is, what it needs, and what the group is short over the next fortnight. Internal only:
 * the server decides whose work and whose capacity the viewer gets; a client never reaches it.
 */
export default function PlanningQueuePage() {
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><ListTodo size={22} className="text-brand" aria-hidden="true" /> Planning queue</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">Work that waits to be planned, why it waits and what it needs, against the time the group has. Due dates are the tickets&rsquo; own; planning never moves them.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account.</p>}
      {me?.userId && <PlanningQueueView viewerId={me.userId} canPlan={me.permissions.includes('schedule.manage')} />}
    </div>
  );
}
