'use client';

import { useEffect, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Building2 } from 'lucide-react';
import { api } from '@/lib/api';
import { isStaffPermissions } from '@/lib/staff';
import { ACTING_COMPANY_COOKIE, actingCompany, setActingCompany } from '@/lib/actingCompany';

/**
 * For a client user who has been given more than one company: which one they are looking at.
 *
 * Everything a client sees is one company's. The choice is kept in a cookie that the server
 * reads on every request and checks against what the person has actually been given; choosing
 * here gives nothing that the desk has not. Changing it loads the portal afresh, so nothing of
 * the company just left is still on the screen or in memory.
 *
 * Renders nothing for staff, and nothing for a client with one company, which is nearly everyone.
 */
export function CompanySwitcher() {
  // Asked only for a client: staff have no company of their own to switch from.
  const { data: me } = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000, retry: false });
  const isStaff = me ? isStaffPermissions(me.permissions) : true;
  const companies = useQuery({ queryKey: ['my-companies'], queryFn: api.myCompanies, staleTime: 60_000, retry: false, enabled: !!me && !isStaff });
  const list = useMemo(() => companies.data ?? [], [companies.data]);
  const chosen = actingCompany();
  const own = list.find((c) => c.isOwn);
  const current = list.find((c) => c.id === chosen) ?? own;

  // A company that was chosen and has since been taken away: every request would be refused
  // until the choice is dropped. Go back to their own.
  useEffect(() => {
    if (!companies.isSuccess || !chosen) return;
    if (!list.some((c) => c.id === chosen)) {
      setActingCompany(null);
      window.location.assign('/dashboard');
    }
  }, [companies.isSuccess, chosen, list]);

  if (list.length < 2 || !current) return null;

  return (
    <label className="flex min-w-0 items-center gap-1.5 text-sm" title="Which company you are looking at">
      <Building2 size={15} className="shrink-0 text-[var(--muted)]" aria-hidden="true" />
      <span className="sr-only">Company</span>
      <select
        aria-label="Company"
        data-cookie={ACTING_COMPANY_COOKIE}
        value={current.id}
        onChange={(e) => {
          const next = list.find((c) => c.id === e.target.value);
          if (!next) return;
          setActingCompany(next.isOwn ? null : next.id);
          // A full load, not a re-render: what was read for the other company is thrown away with the page.
          window.location.assign('/dashboard');
        }}
        className="max-w-[11rem] truncate rounded-lg border border-[var(--border)] bg-[var(--surface)] px-2 py-1.5 text-sm sm:max-w-[16rem]"
      >
        {list.map((c) => (
          <option key={c.id} value={c.id}>
            {c.name}{c.isOwn ? '' : c.canCreate ? '' : ' (view only)'}
          </option>
        ))}
      </select>
      {!current.isOwn && (
        <span className="hidden whitespace-nowrap rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-medium text-amber-900 dark:bg-amber-950 dark:text-amber-200 md:inline">
          {current.canCreate ? 'Not your own company' : 'View only'}
        </span>
      )}
    </label>
  );
}
