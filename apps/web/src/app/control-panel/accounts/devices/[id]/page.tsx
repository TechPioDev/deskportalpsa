'use client';

import Link from 'next/link';
import { use } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ArrowLeft, Server } from 'lucide-react';
import { api } from '@/lib/api';
import { warrantyState, WARRANTY_TONE } from '@/lib/devices';
import { AccessError } from '../../../_ui';

const day = (iso: string) => new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });

/**
 * One device and every ticket raised about it - "ACME-SRV01 has had four tickets this year, two
 * about the same disk" is the question this page exists to answer. For the client's administrators,
 * like the device list it is opened from.
 */
export default function DevicePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const { data, error, isLoading } = useQuery({ queryKey: ['cp-device', id], queryFn: () => api.cpDevice(id), retry: false });

  if (error) return <AccessError label="this device" />;
  if (isLoading || !data) return <p className="text-sm text-[var(--muted)]">Loading…</p>;

  const d = data.device;
  const warranty = warrantyState(d.warrantyExpiresAt);
  const open = data.tickets.filter((t) => t.isOpen);

  return (
    <div className="space-y-5">
      <Link href="/control-panel/accounts" className="inline-flex items-center gap-1.5 text-sm text-[var(--muted)] hover:text-brand">
        <ArrowLeft size={14} /> Accounts &amp; Devices
      </Link>

      <section className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
        <div className="flex items-start gap-3">
          <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-brand-tint text-brand dark:bg-brand/15">
            <Server size={18} aria-hidden="true" />
          </span>
          <div className="min-w-0">
            <h1 className="flex flex-wrap items-center gap-2 text-lg font-semibold">
              {d.name}
              {!d.isActive && <span className="rounded bg-[var(--bg)] px-1.5 py-0.5 text-xs font-medium text-[var(--muted)]">Retired</span>}
            </h1>
            <p className="text-sm text-[var(--muted)]">{[d.type, d.fromPsa ? 'From your PSA' : 'Added by hand'].filter(Boolean).join(' · ')}</p>
          </div>
        </div>
        <dl className="mt-4 grid gap-3 text-sm sm:grid-cols-3">
          <div>
            <dt className="text-[10px] uppercase tracking-wide text-[var(--faint)]">Serial / tag</dt>
            <dd className="mt-0.5 font-medium">{d.identifier ?? '—'}</dd>
          </div>
          <div>
            <dt className="text-[10px] uppercase tracking-wide text-[var(--faint)]">Warranty</dt>
            <dd className={`mt-0.5 font-medium ${warranty ? WARRANTY_TONE[warranty.tone] : ''}`}>{warranty?.text ?? 'Not recorded'}</dd>
          </div>
          <div>
            <dt className="text-[10px] uppercase tracking-wide text-[var(--faint)]">Tickets</dt>
            <dd className="mt-0.5 font-medium">{data.tickets.length} in total{open.length > 0 ? `, ${open.length} open` : ''}</dd>
          </div>
        </dl>
        {d.notes && <p className="mt-3 rounded-lg bg-[var(--bg)] px-3 py-2 text-sm">{d.notes}</p>}
        {d.lastSyncedAt && <p className="mt-3 text-xs text-[var(--faint)]">Last checked with your PSA {day(d.lastSyncedAt)}.</p>}
      </section>

      <section aria-labelledby="device-tickets" className="rounded-xl border border-[var(--border)] bg-[var(--surface)]">
        <h2 id="device-tickets" className="border-b border-[var(--border)] px-5 py-3.5 text-sm font-semibold">Tickets about this device</h2>
        {data.tickets.length === 0 ? (
          <p className="px-5 py-8 text-center text-sm text-[var(--muted)]">No tickets have been raised about this device.</p>
        ) : (
          <ul className="divide-y divide-[var(--border)]">
            {data.tickets.map((t) => (
              <li key={t.id}>
                <Link href={`/dashboard/tickets/${t.id}`} className="flex items-center gap-3 px-5 py-3 hover:bg-[var(--bg)]">
                  <span className="w-28 shrink-0 font-mono text-xs text-[var(--muted)]">{t.reference}</span>
                  <span className="min-w-0 flex-1 truncate text-sm font-medium">{t.title}</span>
                  <span className={`shrink-0 rounded-full px-2 py-0.5 text-[11px] font-medium ${t.isOpen
                    ? 'bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300'
                    : 'bg-[var(--bg)] text-[var(--muted)]'}`}>{t.status.replace(/_/g, ' ')}</span>
                  <span className="hidden w-24 shrink-0 text-right text-xs text-[var(--muted)] sm:block">{day(t.raisedAt)}</span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
