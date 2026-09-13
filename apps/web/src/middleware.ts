import { NextRequest, NextResponse } from 'next/server';
import { cookies as ck } from '@/lib/authConfig';

// Guards the dashboard: with no session at all, send the user to the login flow. If only the access
// token has expired (refresh still present), let the request through — the BFF refreshes on the next
// API call.
export function middleware(req: NextRequest) {
  // Local preview runs without Keycloak; only enforce the session guard in production.
  if (process.env.NODE_ENV !== 'production') return NextResponse.next();

  const hasAccess = Boolean(req.cookies.get(ck.access)?.value);
  const hasRefresh = Boolean(req.cookies.get(ck.refresh)?.value);
  if (!hasAccess && !hasRefresh) {
    return NextResponse.redirect(new URL('/api/auth/login', req.url));
  }
  return NextResponse.next();
}

export const config = {
  // The Control Panel too: without it a signed-out visitor got the panel's empty shell first, and
  // only reached sign-in after the page's own data requests came back 401.
  matcher: ['/dashboard/:path*', '/control-panel/:path*'],
};
