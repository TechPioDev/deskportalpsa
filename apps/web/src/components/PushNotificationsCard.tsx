'use client';

import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Bell, BellOff, Smartphone, Trash2 } from 'lucide-react';
import { api, type PushStatus } from '@/lib/api';

type Support = 'checking' | 'ok' | 'unsupported' | 'iphone';

function base64UrlToBytes(value: string): Uint8Array {
  const b64 = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=');
  return Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
}

async function endpointHash(endpoint: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(endpoint));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('').slice(0, 16);
}

/** "Chrome on Android" - enough to tell your phone from your laptop in the list, nothing more. */
function deviceLabel(): string {
  const ua = navigator.userAgent;
  const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
  const os = /Android/.test(ua) ? 'Android' : /iPhone|iPad|iPod/.test(ua) ? 'iPhone' : /Windows/.test(ua) ? 'Windows' : /Mac OS X/.test(ua) ? 'Mac' : /Linux/.test(ua) ? 'Linux' : 'this device';
  return `${browser} on ${os}`;
}

const EVENTS: { key: keyof PushStatus['preferences']; label: string; example: string }[] = [
  { key: 'assigned', label: 'A ticket is assigned to me', example: '"INT-000014 assigned to you"' },
  { key: 'clientReplied', label: 'A client replies on my ticket', example: '"Priya replied on T20260925.0045"' },
  { key: 'slaAtRisk', label: 'My ticket is about to breach its SLA', example: '"INT-000009 is due in 1h 30m"' },
];

/**
 * Push notifications on this device, for staff: turn them on or off, choose the events, send a test,
 * and see every device signed up. iPhone is not offered yet; the card says so instead of offering a
 * button that would not work there.
 */
export function PushNotificationsCard() {
  const qc = useQueryClient();
  const { data: status } = useQuery({ queryKey: ['push-status'], queryFn: api.pushStatus, retry: false });
  const [support, setSupport] = useState<Support>('checking');
  const [thisHash, setThisHash] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  // What this browser can do, read after mount: the server has no navigator to ask.
  useEffect(() => {
    const ios = /iPhone|iPad|iPod/.test(navigator.userAgent);
    const capable = 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
    // iPhone is not offered yet (owner's call, 29 Sep 2026): it needs the Home Screen install and
    // iOS 16.4+, and it has not been tried on a device. Android and computers only for now.
    setSupport(ios ? 'iphone' : capable ? 'ok' : 'unsupported');
    if (capable) {
      navigator.serviceWorker.getRegistration('/').then(async (reg) => {
        const sub = await reg?.pushManager.getSubscription();
        setThisHash(sub ? await endpointHash(sub.endpoint) : null);
      }).catch(() => setThisHash(null));
    }
  }, []);

  const refresh = () => qc.invalidateQueries({ queryKey: ['push-status'] });
  const onHere = !!thisHash && !!status?.devices.some((d) => d.endpointHash === thisHash);

  const turnOn = useMutation({
    mutationFn: async () => {
      if (!status) throw new Error('Still loading - try again in a moment.');
      const reg = await navigator.serviceWorker.register('/sw.js', { scope: '/' });
      await navigator.serviceWorker.ready;
      const permission = await Notification.requestPermission();
      if (permission !== 'granted')
        throw new Error(permission === 'denied'
          ? 'Notifications are blocked for this site. Allow them in the browser\'s site settings, then try again.'
          : 'Notifications were not allowed.');
      const sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: base64UrlToBytes(status.publicKey) });
      const json = sub.toJSON() as { endpoint: string; keys: { p256dh: string; auth: string } };
      await api.pushSubscribe({ endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth, deviceLabel: deviceLabel() });
      setThisHash(await endpointHash(json.endpoint));
    },
    onSuccess: () => { setMessage('Notifications are on for this device.'); refresh(); },
    onError: (e) => setMessage((e as Error).message),
  });

  const turnOff = useMutation({
    mutationFn: async () => {
      const reg = await navigator.serviceWorker.getRegistration('/');
      const sub = await reg?.pushManager.getSubscription();
      const device = status?.devices.find((d) => d.endpointHash === thisHash);
      if (device) await api.pushRemoveDevice(device.id);
      await sub?.unsubscribe();
      setThisHash(null);
    },
    onSuccess: () => { setMessage('Notifications are off for this device.'); refresh(); },
    onError: (e) => setMessage((e as Error).message),
  });

  const remove = useMutation({ mutationFn: (id: string) => api.pushRemoveDevice(id), onSuccess: refresh });
  const prefs = useMutation({ mutationFn: api.pushSavePreferences, onSuccess: refresh });
  const test = useMutation({
    mutationFn: api.pushTest,
    onSuccess: (r) => setMessage(r.delivered > 0 ? `Test sent to ${r.delivered} device${r.delivered === 1 ? '' : 's'} - it should appear in a few seconds.` : 'The push service did not accept the test. Try turning notifications off and on again.'),
    onError: (e) => setMessage((e as Error).message),
  });

  if (!status) return null;

  return (
    <section aria-labelledby="push-heading" className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-6">
      <h2 id="push-heading" className="flex items-center gap-2 text-base font-semibold"><Bell size={18} aria-hidden="true" /> Notifications</h2>
      <p className="mt-1 text-sm text-[var(--muted)]">Get a notification on your phone or computer when work comes your way - even when the portal is closed.</p>

      <div className="mt-4 rounded-lg bg-[var(--bg)] p-4">
        {support === 'iphone' && (
          <p className="text-sm text-[var(--muted)]">
            Notifications on iPhone are not available yet. Turn them on from an Android phone or a computer.
          </p>
        )}
        {support === 'unsupported' && <p className="text-sm text-[var(--muted)]">This browser cannot receive notifications. Chrome, Edge, Firefox and Safari can.</p>}
        {support === 'ok' && (
          <div className="flex flex-wrap items-center justify-between gap-3">
            <p className="flex items-center gap-2 text-sm">
              {onHere ? <Bell size={16} className="text-brand" aria-hidden="true" /> : <BellOff size={16} className="text-[var(--muted)]" aria-hidden="true" />}
              {onHere ? 'On for this device' : 'Off for this device'}
            </p>
            <div className="flex flex-wrap gap-2">
              {onHere ? (
                <>
                  <button type="button" onClick={() => test.mutate()} disabled={test.isPending}
                    className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--surface)] disabled:opacity-50">
                    {test.isPending ? 'Sending…' : 'Send a test'}
                  </button>
                  <button type="button" onClick={() => turnOff.mutate()} disabled={turnOff.isPending}
                    className="rounded-lg border border-[var(--border)] px-3 py-1.5 text-sm font-medium hover:bg-[var(--surface)] disabled:opacity-50">Turn off</button>
                </>
              ) : (
                <button type="button" onClick={() => turnOn.mutate()} disabled={turnOn.isPending}
                  className="rounded-lg bg-brand px-3 py-1.5 text-sm font-medium text-brand-fg hover:opacity-90 disabled:opacity-50">
                  {turnOn.isPending ? 'Turning on…' : 'Turn on for this device'}
                </button>
              )}
            </div>
          </div>
        )}
        {message && <p role="status" className="mt-2 text-sm text-[var(--muted)]">{message}</p>}
      </div>

      <fieldset className="mt-4 space-y-2">
        <legend className="text-sm font-medium">Tell me when</legend>
        {EVENTS.map((e) => (
          <label key={e.key} className="flex items-start gap-2 text-sm">
            <input type="checkbox" aria-label={e.label} className="mt-0.5" checked={status.preferences[e.key]}
              onChange={(ev) => prefs.mutate({ ...status.preferences, [e.key]: ev.target.checked })} />
            <span>{e.label} <span className="text-xs text-[var(--muted)]">- {e.example}</span></span>
          </label>
        ))}
        {prefs.isError && <p role="alert" className="text-sm text-red-600 dark:text-red-400">{(prefs.error as Error).message}</p>}
      </fieldset>

      {status.devices.length > 0 && (
        <div className="mt-4">
          <h3 className="text-sm font-medium">Your devices</h3>
          <ul className="mt-2 divide-y divide-[var(--border)] rounded-lg border border-[var(--border)]">
            {status.devices.map((d) => (
              <li key={d.id} className="flex items-center gap-3 px-3 py-2 text-sm">
                <Smartphone size={15} className="shrink-0 text-[var(--muted)]" aria-hidden="true" />
                <span className="min-w-0 flex-1 truncate">
                  {d.label ?? 'A device'}{d.endpointHash === thisHash && <span className="text-xs text-brand"> (this one)</span>}
                  <span className="block text-xs text-[var(--muted)]">
                    Added {new Date(d.addedAt).toLocaleDateString()}{d.lastDeliveredAt ? ` · last notified ${new Date(d.lastDeliveredAt).toLocaleString()}` : ''}
                  </span>
                </span>
                <button type="button" aria-label={`Remove ${d.label ?? 'device'}`} onClick={() => remove.mutate(d.id)}
                  className="rounded-md p-1.5 text-[var(--muted)] hover:bg-red-50 hover:text-red-600 dark:hover:bg-red-950/50"><Trash2 size={15} /></button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}
