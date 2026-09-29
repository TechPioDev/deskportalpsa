'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * Point a ticket at one of its client's devices, or at none. The list is read when the picker opens,
 * not with every ticket page. A device that cannot be this ticket's - hand-added on a PSA ticket,
 * retired - is shown disabled with the reason, rather than hidden, so nobody wonders where it went.
 */
export function TicketDevicePicker({ ticketId, currentId, onClose }: {
  ticketId: string; currentId: string | null; onClose: () => void;
}) {
  const qc = useQueryClient();
  const [choice, setChoice] = useState(currentId ?? '');
  const { data: devices, isLoading } = useQuery({
    queryKey: ['ticket-device-choices', ticketId], queryFn: () => api.ticketDeviceChoices(ticketId), retry: false,
  });
  const save = useMutation({
    mutationFn: () => api.setTicketDevice(ticketId, choice || null),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['ticket', ticketId] });
      qc.invalidateQueries({ queryKey: ['tickets'] });
      onClose();
    },
  });
  const chosen = devices?.find((d) => d.id === choice);

  return (
    <div className="mt-1 space-y-2">
      <select aria-label="Device" value={choice} onChange={(e) => setChoice(e.target.value)} disabled={isLoading}
        className="w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-2 py-1.5 text-sm font-normal outline-none focus:border-brand">
        <option value="">No device</option>
        {devices?.map((d) => (
          <option key={d.id} value={d.id} disabled={!!d.unavailable && d.id !== currentId}>
            {d.name}{d.type ? ` — ${d.type}` : ''}{d.unavailable ? ' (not available)' : ''}
          </option>
        ))}
      </select>
      {devices && devices.length === 0 && (
        <p className="text-xs font-normal text-[var(--muted)]">This client has no devices yet. They arrive from the PSA once a day.</p>
      )}
      {chosen?.unavailable && <p className="text-xs font-normal text-amber-700 dark:text-amber-400">{chosen.unavailable}</p>}
      {devices?.some((d) => d.unavailable && d.isActive) && !chosen?.unavailable && (
        <p className="text-xs font-normal text-[var(--muted)]">Greyed-out devices were added by hand, so the PSA does not know them.</p>
      )}
      {save.isError && <p role="alert" className="text-xs font-normal text-red-600 dark:text-red-400">{(save.error as Error).message}</p>}
      <div className="flex gap-2">
        <button type="button" onClick={() => save.mutate()} disabled={save.isPending || (choice || null) === currentId}
          className="rounded-lg bg-brand px-2.5 py-1 text-xs font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
          {save.isPending ? 'Saving…' : 'Save'}
        </button>
        <button type="button" onClick={onClose}
          className="rounded-lg border border-[var(--border)] px-2.5 py-1 text-xs font-medium hover:bg-[var(--bg)]">Cancel</button>
      </div>
    </div>
  );
}
