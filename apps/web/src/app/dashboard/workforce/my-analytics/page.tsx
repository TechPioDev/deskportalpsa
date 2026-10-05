'use client';

import { Suspense } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Activity } from 'lucide-react';
import { api } from '@/lib/api';
import { WorkforceNav } from '@/components/WorkforceCapacity';
import { TechnicianAnalyticsView } from '@/components/WorkforceAnalytics';

/** My analytics: the signed-in person's own capacity, planned and recorded work over a period. Nobody else's figures appear here. */
export default function MyAnalyticsPage() {
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><Activity size={22} className="text-brand" aria-hidden="true" /> My analytics</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">Your capacity, what was planned for you, what you recorded, and how the two compare, over a period. Your own facts only: not attendance, not a score, no comparison with anyone.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account.</p>}
      {me?.userId && (
        <Suspense fallback={<div className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}>
          <TechnicianAnalyticsView appUserId={me.userId} self />
        </Suspense>
      )}
    </div>
  );
}
