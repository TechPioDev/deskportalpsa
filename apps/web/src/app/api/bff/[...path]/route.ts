import { NextRequest, NextResponse } from 'next/server';
import { authConfig, oidc, cookies as ck, isProd } from '@/lib/authConfig';

// Backend-for-frontend proxy. The browser calls same-origin /api/bff/*; this handler attaches the
// access token (from the httpOnly cookie) server-side and forwards to the .NET API. The token never
// reaches client JavaScript, and a 401 triggers a transparent refresh-token exchange + retry.

async function refresh(refreshToken: string) {
  const body = new URLSearchParams({
    grant_type: 'refresh_token',
    refresh_token: refreshToken,
    client_id: authConfig.clientId,
    ...(authConfig.clientSecret ? { client_secret: authConfig.clientSecret } : {}),
  });
  const r = await fetch(oidc.token, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body,
    cache: 'no-store',
  });
  if (!r.ok) return null;
  return (await r.json()) as { access_token: string; refresh_token?: string; expires_in?: number };
}

function upstreamHeaders(req: NextRequest, token: string | undefined): Headers {
  const h = new Headers();
  for (const key of ['content-type', 'accept', 'x-correlation-id']) {
    const v = req.headers.get(key);
    if (v) h.set(key, v);
  }
  if (token) h.set('authorization', `Bearer ${token}`);
  return h;
}

function passthroughHeaders(res: Response): Headers {
  const h = new Headers();
  for (const key of ['content-type', 'content-disposition', 'cache-control']) {
    const v = res.headers.get(key);
    if (v) h.set(key, v);
  }
  return h;
}

/** Errors meaning the request never reached the API, so repeating it cannot do anything twice. */
const NEVER_SENT = new Set(['ECONNREFUSED', 'ENOTFOUND', 'EAI_AGAIN', 'UND_ERR_CONNECT_TIMEOUT']);

/**
 * Rides out an API container swap during a deploy. While the new container takes over, a request
 * can hit the old one as it stops (connection refused, or a kept-alive socket closed under it).
 * Retried: anything that was never sent, and reads whatever went wrong. A write whose connection
 * dropped mid-flight is NOT retried — the API may already have done it.
 */
async function fetchWithRetry(send: () => Promise<Response>, method: string): Promise<Response> {
  const idempotent = method === 'GET' || method === 'HEAD';
  const waits = [250, 750, 1500];
  for (let attempt = 0; ; attempt++) {
    try {
      return await send();
    } catch (err) {
      const code = (err as { cause?: { code?: string } })?.cause?.code ?? '';
      if (attempt >= waits.length || !(NEVER_SENT.has(code) || idempotent)) throw err;
      await new Promise((r) => setTimeout(r, waits[attempt]));
    }
  }
}

async function proxy(req: NextRequest, ctx: { params: Promise<{ path: string[] }> }) {
  const { path } = await ctx.params;
  const target = `${authConfig.apiBase}/${path.join('/')}${new URL(req.url).search}`;

  const hasBody = req.method !== 'GET' && req.method !== 'HEAD';
  const bodyBuf = hasBody ? Buffer.from(await req.arrayBuffer()) : undefined;
  let access = req.cookies.get(ck.access)?.value;

  const call = (token?: string) =>
    fetchWithRetry(
      () => fetch(target, { method: req.method, headers: upstreamHeaders(req, token), body: bodyBuf, cache: 'no-store', redirect: 'manual' }),
      req.method);

  let upstream = await call(access);

  let refreshed: { access: string; refresh?: string; maxAge?: number } | null = null;
  let sessionDead = false;
  if (upstream.status === 401) {
    const rt = req.cookies.get(ck.refresh)?.value;
    const t = rt ? await refresh(rt) : null;
    if (t) {
      access = t.access_token;
      refreshed = { access: t.access_token, refresh: t.refresh_token, maxAge: t.expires_in };
      upstream = await call(access);
    }
    // The session cannot be revived — no refresh token, the IdP rejected it, or even the fresh
    // token was refused. The cookies must go: the dashboard guard admits anyone holding them, so
    // leaving them behind produces a zombie session where the UI renders but every call 401s.
    if (!t || upstream.status === 401) sessionDead = true;
  }

  // The Fetch spec forbids any body argument — even a zero-length one — on a null-body status.
  // The API returns 204 from every action route (activate/deactivate, delete, role/department/
  // team/board assignment, …), and passing an empty Buffer through here throws "Invalid response
  // status code 204" instead of proxying it, turning every one of those actions into a 500.
  const nullBodyStatus = upstream.status === 204 || upstream.status === 304;
  const out = new NextResponse(nullBodyStatus ? null : Buffer.from(await upstream.arrayBuffer()), {
    status: upstream.status,
    headers: passthroughHeaders(upstream),
  });
  if (sessionDead) {
    for (const name of [ck.access, ck.refresh, ck.idToken]) out.cookies.delete(name);
  } else if (refreshed) {
    out.cookies.set(ck.access, refreshed.access, {
      httpOnly: true, secure: isProd, sameSite: 'lax', path: '/', maxAge: refreshed.maxAge ?? 300,
    });
    if (refreshed.refresh)
      out.cookies.set(ck.refresh, refreshed.refresh, {
        httpOnly: true, secure: isProd, sameSite: 'lax', path: '/', maxAge: 60 * 60 * 24 * 7,
      });
  }
  return out;
}

export { proxy as GET, proxy as POST, proxy as PUT, proxy as PATCH, proxy as DELETE };
