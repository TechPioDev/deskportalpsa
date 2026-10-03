'use client';

import { Suspense } from 'react';
import { useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { CalendarRange } from 'lucide-react';
import { api } from '@/lib/api';
import { WorkforceNav } from '@/components/WorkforceCapacity';
import { TeamScheduleWorkspace } from '@/components/WorkforceSchedule';

/**
 * The team scheduler: who is working, what is planned and when, how much capacity is left and
 * where, what is still unscheduled, and the means to place, move, resize, give away and take out
 * work. Internal only: the server decides whose rows the viewer gets; a client never reaches it.
 */
export default function TeamSchedulePage() {
  return (
    <Suspense fallback={<div className="h-72 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}>
      <TeamSchedule />
    </Suspense>
  );
}

function TeamSchedule() {
  const query = useSearchParams();
  const asked = query.get('date') ?? '';
  const date = /^\d{4}-\d{2}-\d{2}$/.test(asked) && !Number.isNaN(Date.parse(`${asked}T00:00:00Z`)) ? asked : null;
  const view = query.get('view') === 'week' ? 'week' : query.get('view') === 'day' ? 'day' : null;
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });

  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><CalendarRange size={22} className="text-brand" aria-hidden="true" /> Team schedule</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">Everyone you may see, their working time and what is planned in it. Place, move and hand over work; planned time, never time logged.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account.</p>}
      {me?.userId && <TeamScheduleWorkspace viewerId={me.userId} initialDate={date} initialView={view} canPlanAtAll={me.permissions.includes('schedule.manage')} />}
    </div>
  );
}
