'use client';

import { Suspense } from 'react';
import Link from 'next/link';
import { useParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { ChevronLeft, UserRound } from 'lucide-react';
import { api } from '@/lib/api';
import { WorkforceNav } from '@/components/WorkforceCapacity';
import { TechnicianAnalyticsView } from '@/components/WorkforceAnalytics';

/** One person's work analytics over a period: their figures, their days and the work behind them. Facts, not a score. */
export default function TechnicianAnalyticsPage() {
  const params = useParams<{ id: string }>();
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const id = params?.id ?? '';
  return (
    <div className="space-y-4">
      <div>
        <Link href="/dashboard/workforce/analytics" className="inline-flex items-center gap-1 text-sm text-[var(--muted)] hover:text-[var(--fg)]"><ChevronLeft size={14} aria-hidden="true" /> Workforce analytics</Link>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><UserRound size={22} className="text-brand" aria-hidden="true" /> Technician work analytics</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">Capacity, planned and recorded work, utilization and the work items behind them, for one person over a period. Operational facts: not attendance, not a performance score.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account.</p>}
      {me?.userId && id && (
        <Suspense fallback={<div className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}>
          <TechnicianAnalyticsView appUserId={id} self={id === me.userId} />
        </Suspense>
      )}
    </div>
  );
}
