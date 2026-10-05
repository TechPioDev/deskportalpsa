'use client';

import { Suspense } from 'react';
import { useParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { FileSpreadsheet } from 'lucide-react';
import { api } from '@/lib/api';
import { WorkforceNav } from '@/components/WorkforceCapacity';
import { ReportView } from '@/components/WorkforceReports';

/** One report: its filters, a preview of what it holds, and the export. Filters live in the query string, hence Suspense. */
export default function WorkforceReportPage() {
  const params = useParams<{ key: string }>();
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const key = params?.key ?? '';
  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><FileSpreadsheet size={22} className="text-brand" aria-hidden="true" /> Reports</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">A preview under your filters. The export is built from the same rows, so its totals are the ones shown here.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account.</p>}
      {me?.userId && key && (
        <Suspense fallback={<div className="h-64 animate-pulse rounded-xl border border-[var(--border)] bg-[var(--surface)]" />}>
          <ReportView reportKey={key} />
        </Suspense>
      )}
    </div>
  );
}
