'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Building2, Search } from 'lucide-react';
import { api } from '@/lib/api';
import type { ClientUserAccess } from '@/lib/types';

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

/**
 * Which companies a client user can see into besides their own.
 *
 * A client user belongs to one company. Some answer for more than one, and are given each further
 * company here, one at a time. Nothing else gives one: not an address, not a company's name. A
 * grant starts as the least it can be (the tickets that person raised there, and no changes) and
 * each thing more is ticked separately. It never makes anyone an administrator of the company.
 */
export default function ClientAccessPage() {
  const qc = useQueryClient();
  const [search, setSearch] = useState('');
  const [openId, setOpenId] = useState<string | null>(null);
  const users = useQuery({ queryKey: ['client-access-users', search], queryFn: () => api.clientAccessUsers(search), staleTime: 0 });
  const companies = useQuery({ queryKey: ['client-access-companies'], queryFn: api.clientAccessCompanies, staleTime: 60_000 });

  return (
    <div className="space-y-5">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Client access to more than one company</h1>
        <p className="mt-1 max-w-3xl text-sm text-[var(--muted)]">
          A client user sees their own company. Give someone a further company here when they answer for it too.
          Nothing else gives access: it is never worked out from an e-mail address or a company&apos;s name.
          A grant never makes anyone an administrator of the company, and every change is in the audit log.
        </p>
      </div>

      <label className="relative block max-w-md">
        <span className="sr-only">Search client users</span>
        <Search size={14} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-[var(--faint)]" aria-hidden="true" />
        <input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search by name, e-mail or company"
          className="w-full rounded-lg border border-[var(--border)] bg-[var(--surface)] py-2 pl-9 pr-3 text-sm outline-none focus:border-brand" />
      </label>

      {users.isError && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{message(users.error, 'The client users could not be read.')}</p>}
      {users.isLoading && <p className="text-sm text-[var(--muted)]">Reading client users…</p>}
      {users.data && users.data.length === 0 && (
        <p className="text-sm text-[var(--muted)]">{search.trim() ? 'No client user matches that.' : 'There are no client users yet. They are invited from a company’s own control panel.'}</p>
      )}

      <ul className="space-y-3">
        {(users.data ?? []).map((u) => (
          <li key={u.id} className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
            <button type="button" onClick={() => setOpenId(openId === u.id ? null : u.id)} aria-expanded={openId === u.id}
              className="flex w-full flex-wrap items-center gap-x-4 gap-y-1 px-4 py-3 text-left">
              <span className="min-w-0 flex-1">
                <span className="block text-sm font-medium">{u.displayName}{!u.isActive && <span className="ml-2 text-xs font-normal text-[var(--faint)]">switched off</span>}</span>
                <span className="block truncate text-xs text-[var(--muted)]">{u.email} · {u.homeCompanyName}</span>
              </span>
              <span className="text-xs text-[var(--muted)]">
                {u.grants.length === 0 ? 'Their own company only' : `Also: ${u.grants.map((g) => g.companyName).join(', ')}`}
              </span>
            </button>
            {openId === u.id && (
              <Grants user={u} companies={companies.data ?? []}
                onChanged={() => qc.invalidateQueries({ queryKey: ['client-access-users'] })} />
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

function Grants({ user, companies, onChanged }: { user: ClientUserAccess; companies: { id: string; name: string }[]; onChanged: () => void }) {
  const [companyId, setCompanyId] = useState('');
  const [seesAll, setSeesAll] = useState(false);
  const [canCreate, setCanCreate] = useState(false);
  const set = useMutation({
    mutationFn: (v: { companyId: string; seesAllTickets: boolean; canCreate: boolean }) =>
      api.setClientCompanyAccess(user.id, v.companyId, { seesAllTickets: v.seesAllTickets, canCreate: v.canCreate }),
    onSuccess: () => { setCompanyId(''); setSeesAll(false); setCanCreate(false); onChanged(); },
  });
  const remove = useMutation({ mutationFn: (id: string) => api.removeClientCompanyAccess(user.id, id), onSuccess: onChanged });
  const available = companies.filter((c) => c.id !== user.homeCompanyId && !user.grants.some((g) => g.companyId === c.id));
  const busy = set.isPending || remove.isPending;

  return (
    <div aria-label={`Companies ${user.displayName} can see`} role="group" className="space-y-3 border-t border-[var(--border)] px-4 py-3">
      <p className="flex items-center gap-2 text-sm">
        <Building2 size={14} className="text-[var(--muted)]" aria-hidden="true" />
        <span><span className="font-medium">{user.homeCompanyName}</span> <span className="text-[var(--muted)]">is their own company. What they can do there is set in that company&apos;s control panel.</span></span>
      </p>

      {user.grants.length > 0 && (
        <ul className="divide-y divide-[var(--border)] rounded-lg border border-[var(--border)]">
          {user.grants.map((g) => (
            <li key={g.companyId} className="flex flex-wrap items-center gap-x-4 gap-y-1.5 px-3 py-2 text-sm">
              <span className="min-w-0 flex-1 font-medium">{g.companyName}</span>
              <label className="flex items-center gap-1.5">
                <input type="checkbox" checked={g.seesAllTickets} disabled={busy} aria-label={`${g.companyName}: sees every ticket`}
                  onChange={(e) => set.mutate({ companyId: g.companyId, seesAllTickets: e.target.checked, canCreate: g.canCreate })} />
                Sees every ticket
              </label>
              <label className="flex items-center gap-1.5">
                <input type="checkbox" checked={g.canCreate} disabled={busy} aria-label={`${g.companyName}: may raise and reply`}
                  onChange={(e) => set.mutate({ companyId: g.companyId, seesAllTickets: g.seesAllTickets, canCreate: e.target.checked })} />
                May raise and reply
              </label>
              <button type="button" disabled={busy} onClick={() => remove.mutate(g.companyId)} aria-label={`Take ${g.companyName} away from ${user.displayName}`}
                className="rounded-lg border border-[var(--border)] px-2.5 py-1 text-xs font-medium hover:bg-[var(--bg)] disabled:opacity-50">
                Take away
              </button>
            </li>
          ))}
        </ul>
      )}
      {user.grants.some((g) => !g.seesAllTickets && !g.canCreate) && (
        <p className="text-xs text-[var(--muted)]">With neither ticked they see only the tickets they themselves raised in that company, and cannot change anything.</p>
      )}

      <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-sm">
        <label className="flex items-center gap-2">
          <span className="text-xs text-[var(--muted)]">Give a company</span>
          <select value={companyId} onChange={(e) => setCompanyId(e.target.value)} aria-label={`Company to give ${user.displayName}`}
            className="rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm">
            <option value="">Choose…</option>
            {available.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
        <label className="flex items-center gap-1.5"><input type="checkbox" checked={seesAll} onChange={(e) => setSeesAll(e.target.checked)} /> Sees every ticket</label>
        <label className="flex items-center gap-1.5"><input type="checkbox" checked={canCreate} onChange={(e) => setCanCreate(e.target.checked)} /> May raise and reply</label>
        <button type="button" disabled={companyId === '' || busy}
          onClick={() => set.mutate({ companyId, seesAllTickets: seesAll, canCreate })}
          className="rounded-lg bg-brand px-3 py-1.5 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
          {set.isPending ? 'Saving…' : 'Give access'}
        </button>
      </div>
      {available.length === 0 && companies.length > 0 && <p className="text-xs text-[var(--muted)]">There is no other company left to give.</p>}
      {(set.isError || remove.isError) && (
        <p role="alert" className="text-xs text-rose-600 dark:text-rose-400">{message(set.error ?? remove.error, 'That could not be saved.')}</p>
      )}
    </div>
  );
}
