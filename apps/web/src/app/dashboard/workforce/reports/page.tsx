'use client';

import { useQuery } from '@tanstack/react-query';
import { FileSpreadsheet } from 'lucide-react';
import { api } from '@/lib/api';
import { WorkforceNav } from '@/components/WorkforceCapacity';
import { ReportCatalogue } from '@/components/WorkforceReports';

/** The report center: the workforce reports the viewer may run, each previewed before it is exported. */
export default function WorkforceReportsPage() {
  const { data: me, isLoading } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight"><FileSpreadsheet size={22} className="text-brand" aria-hidden="true" /> Reports</h1>
        <p className="max-w-prose text-sm text-[var(--muted)]">Workforce, capacity, client, delivery and quality reports for the people you may see. Open one to preview it under your filters, then export it as CSV or XLSX. Facts from records: not attendance, not a performance score.</p>
      </div>
      <WorkforceNav />
      {isLoading && <p className="text-sm text-[var(--muted)]">Loading…</p>}
      {me && !me.userId && <p className="text-sm text-[var(--muted)]">This sign-in is not a staff account.</p>}
      {me?.userId && <ReportCatalogue />}
    </div>
  );
}
