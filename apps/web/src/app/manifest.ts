import type { MetadataRoute } from 'next';

/**
 * Makes the portal installable: "Add to Home Screen" on a phone opens it full-screen at the dashboard,
 * with the owl as its icon. It is the same site, not a copy - there is nothing to update separately.
 */
export default function manifest(): MetadataRoute.Manifest {
  return {
    name: 'PioManage',
    short_name: 'PioManage',
    description: 'Your tickets, boards and alerts from PioManage, on your phone.',
    start_url: '/dashboard',
    scope: '/',
    display: 'standalone',
    background_color: '#FDF6E3',
    theme_color: '#14532D',
    icons: [
      { src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png', purpose: 'any' },
      { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png', purpose: 'any' },
      { src: '/icons/maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
    ],
  };
}
