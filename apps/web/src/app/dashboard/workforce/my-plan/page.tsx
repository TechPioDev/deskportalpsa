'use client';

import { Suspense } from 'react';
import { useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { ClipboardList } from 'lucide-react';
import { api } from '@/lib/api';
import { WorkforceNav } from '@/components/WorkforceCapacity';
import { PlanAgenda, UnscheduledWorkList } from '@/components/WorkforcePlan';

/**
 * What am I planning to work on, when, for how long, what is still unscheduled, and where is my free
 * capacity. Wrapped in Suspense because the page reads the query string (a notification opens it on
 * a date).
 */
export default function MyPlanPage() {
  return (
    <Suspense fallback={<div className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}>
      <MyPlan />
    </Suspense>
  );
}

function MyPlan() {
  const query = useSearchParams();
  const date = /^\d{4}-\d{2}-\d{2}$/.test(query.get('date') ?? '') ? query.get('date') : null;
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const canPlan = !!me?.permissions.includes('schedule.manage');

  return (
    <div className="space-y-5">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><ClipboardList size={22} className="text-brand" aria-hidden="true" /> My plan</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">What you are planning to work on, when, and what is still unscheduled. Planned time, not actual time: log your time on the ticket as always.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account, so it has no plan.</p>}
      {me?.userId && (
        <>
          <PlanAgenda userId={me.userId} viewerId={me.userId} initialDate={date} />
          <UnscheduledWorkList viewerId={me.userId} canPlan={canPlan} />
        </>
      )}
    </div>
  );
}
