/*
 * The portal's service worker. It does two things only: show a push notification, and open the page it
 * points to when tapped. It caches nothing - a ticket list served from a cache would be yesterday's
 * truth, and ticket data has no business sitting in a phone's storage.
 */
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

self.addEventListener('push', (event) => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch { data = { title: 'piomanage', body: event.data ? event.data.text() : '' }; }
  event.waitUntil(self.registration.showNotification(data.title || 'piomanage', {
    body: data.body || '',
    icon: '/icons/icon-192.png',
    badge: '/icons/icon-192.png',
    // One notification per ticket and kind: a second "due soon" replaces the first rather than stacking.
    tag: data.tag || undefined,
    renotify: !!data.tag,
    data: { url: data.url || '/dashboard' },
  }));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  // Only ever a path on this site: a notification can never send someone elsewhere.
  const path = (event.notification.data && event.notification.data.url) || '/dashboard';
  const target = new URL(path.startsWith('/') ? path : '/dashboard', self.location.origin).href;
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of windows) {
      if (new URL(client.url).origin === self.location.origin) {
        await client.focus();
        return client.navigate(target);
      }
    }
    return self.clients.openWindow(target);
  })());
});
