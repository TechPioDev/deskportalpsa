import { NextRequest, NextResponse } from 'next/server';
import { authConfig } from '@/lib/authConfig';

/**
 * Serves an attachment's bytes for a presigned URL.
 *
 * WHY THIS ROUTE EXISTS
 *
 * The API mints download links as `{Attachments:PublicBaseUrl}/api/attachments/blob?key=…`, and
 * PublicBaseUrl is the site's own origin — https://piomanage.com. That is the right link to hand a
 * browser, but in this deployment nginx has a single `location /` pointing at the Next app, and the
 * API container publishes no port at all. So the browser asked Next for `/api/attachments/blob`,
 * Next had no such route, and every attachment opened the 404 page instead of the file.
 *
 * The upload half worked throughout, which is what made it look like a storage bug: the bytes were
 * on disk the whole time under the exact key in the URL. Nothing was lost — it was unreachable.
 *
 * DELIBERATELY NOT THROUGH THE BFF
 *
 * The sibling proxy at /api/bff/* attaches the session bearer token. This endpoint must not use it.
 * The link is opened in a new tab, is anonymous by design, and is gated by an HMAC over key+expiry
 * that the API verifies. Routing it through the session proxy would quietly make an unauthenticated,
 * signature-gated URL depend on a cookie, and it would start failing the moment a link outlived the
 * session that produced it — the one case short-lived signed URLs exist to handle.
 *
 * Only key, exp and sig are forwarded, to a fixed host and a fixed path. That matters: a handler
 * that passed through a caller-supplied URL would be an open proxy sitting inside the trusted
 * origin, able to reach anything on the internal Docker network.
 */

export const dynamic = 'force-dynamic';

export async function GET(req: NextRequest) {
  const params = new URL(req.url).searchParams;
  const key = params.get('key');
  const exp = params.get('exp');
  const sig = params.get('sig');

  // A link missing a parameter was never a valid one; there is nothing to ask the API about.
  if (!key || !exp || !sig) {
    return new NextResponse('This download link is incomplete.', { status: 400 });
  }

  const target = new URL(`${authConfig.apiBase}/api/attachments/blob`);
  target.searchParams.set('key', key);
  target.searchParams.set('exp', exp);
  target.searchParams.set('sig', sig);

  let upstream: Response;
  try {
    upstream = await fetch(target, { cache: 'no-store', redirect: 'manual' });
  } catch {
    return new NextResponse('The file store is unreachable.', { status: 502 });
  }

  if (!upstream.ok) {
    // The API distinguishes a bad or expired signature (401) from a key it does not hold (404).
    // Both are repeated as-is, with wording of our own: an expired link is the ordinary case here
    // and the person holding it needs to know to go back and click download again.
    const message = upstream.status === 401
      ? 'This download link has expired. Open the ticket and download the file again.'
      : 'This file is no longer available.';
    return new NextResponse(message, { status: upstream.status });
  }

  const headers = new Headers();
  for (const name of ['content-type', 'content-disposition', 'content-length']) {
    const value = upstream.headers.get(name);
    if (value) headers.set(name, value);
  }
  // Signed, short-lived and per-recipient: it must not sit in a shared cache after the signature
  // that authorized it has expired.
  headers.set('cache-control', 'private, no-store');

  // Streamed rather than buffered — attachments run to 25 MB, and holding one per concurrent
  // download in the web container's memory is a cost with nothing to show for it.
  return new NextResponse(upstream.body, { status: 200, headers });
}
