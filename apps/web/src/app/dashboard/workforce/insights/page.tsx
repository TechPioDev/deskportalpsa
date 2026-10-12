'use client';

import { Suspense } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Gauge } from 'lucide-react';
import { api } from '@/lib/api';
import { WorkforceNav } from '@/components/WorkforceCapacity';
import { WorkforceInsightsView } from '@/components/WorkforceInsights';

/**
 * Management insights: the capacity forecast, what changed against the period before, the quality
 * signals the records support and what needs attention, for the people the viewer may see. Sums of
 * records with their rules shown; not a prediction and not a score. Filters live in the query
 * string, hence Suspense.
 */
export default function WorkforceInsightsPage() {
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><Gauge size={22} className="text-brand" aria-hidden="true" /> Management insights</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">Capacity ahead against confirmed, tentative and unscheduled work, what changed against the period before, and what needs attention. Every figure opens the records that make it, and every statement shows its rule. Facts for planning: not a prediction, not a performance score.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account.</p>}
      {me?.userId && (
        <Suspense fallback={<div className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}>
          <WorkforceInsightsView />
        </Suspense>
      )}
    </div>
  );
}
