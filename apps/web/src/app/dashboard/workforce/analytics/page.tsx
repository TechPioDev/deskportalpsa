'use client';

import { Suspense } from 'react';
import { useQuery } from '@tanstack/react-query';
import { BarChart3 } from 'lucide-react';
import { api } from '@/lib/api';
import { WorkforceNav } from '@/components/WorkforceCapacity';
import { WorkforceAnalyticsView } from '@/components/WorkforceAnalytics';

/**
 * Workforce analytics: how work is distributed, planned, executed and completed over a period, for
 * the people the viewer may see. Facts with their records behind them; not attendance and not a
 * score. Filters live in the query string, hence Suspense.
 */
export default function WorkforceAnalyticsPage() {
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><BarChart3 size={22} className="text-brand" aria-hidden="true" /> Workforce analytics</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">Capacity, planned and recorded work, utilization, planned against actual, reactive and completed work over a period. Every card opens the records that make it. Operational facts: not attendance, not a performance score.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account.</p>}
      {me?.userId && (
        <Suspense fallback={<div className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}>
          <WorkforceAnalyticsView />
        </Suspense>
      )}
    </div>
  );
}
