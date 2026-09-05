import { NextRequest, NextResponse } from 'next/server';

/**
 * Where the browser posts CSP violations.
 *
 * Without this, a violation exists only in whichever visitor's console happened to be open — so
 * during the report-only period nobody learns what the policy would have blocked, and once it is
 * enforcing a violation means a user's feature silently stopped working with nothing in any log.
 *
 * Deliberately unauthenticated, because the browser sends these with no credentials and often
 * while the page that caused them is broken. That makes it a public write endpoint, so it stores
 * nothing, answers 204 to everything, and only ever logs a handful of named fields.
 */

// A misconfigured policy can fire the same violation on every page load for every visitor. Only
// the FIRST of each distinct directive+resource is logged, so one bad directive costs one line
// rather than flooding out the others. Bounded, because the keys come from the network.
const seen = new Set<string>();
const MAX_DISTINCT = 500;

/** Report fields are attacker-controllable. Drop the query string, which is where a token would
 *  be if one ever appeared in a blocked URL, and cap the length. */
function safe(value: unknown, max = 200): string {
  if (typeof value !== 'string' || value.length === 0) return '';
  const noQuery = value.split('?')[0].split('#')[0];
  return noQuery.slice(0, max).replace(/[\r\n]+/g, ' ');
}

type Violation = { directive: string; blocked: string; document: string; source: string };

/** Browsers send report-uri and Reporting-API payloads in different shapes. */
function parse(payload: unknown): Violation[] {
  const out: Violation[] = [];

  // report-uri: { "csp-report": { ... } }
  const legacy = (payload as { 'csp-report'?: Record<string, unknown> })?.['csp-report'];
  if (legacy) {
    out.push({
      directive: safe(legacy['effective-directive'] ?? legacy['violated-directive'], 60),
      blocked: safe(legacy['blocked-uri']),
      document: safe(legacy['document-uri']),
      source: safe(legacy['source-file']),
    });
  }

  // Reporting API: [{ type: 'csp-violation', body: { ... } }]
  if (Array.isArray(payload)) {
    for (const r of payload) {
      const body = (r as { type?: string; body?: Record<string, unknown> })?.body;
      if (!body) continue;
      out.push({
        directive: safe(body.effectiveDirective, 60),
        blocked: safe(body.blockedURL),
        document: safe(body.documentURL),
        source: safe(body.sourceFile),
      });
    }
  }

  return out.filter((v) => v.directive || v.blocked);
}

export async function POST(req: NextRequest) {
  // Cap the read: this is an open endpoint and the body is never needed beyond a few fields.
  const raw = (await req.text()).slice(0, 64_000);

  let payload: unknown;
  try {
    payload = JSON.parse(raw);
  } catch {
    return new NextResponse(null, { status: 204 }); // malformed is not worth an error to a browser
  }

  for (const v of parse(payload)) {
    const key = `${v.directive}|${v.blocked}`;
    if (seen.has(key)) continue;
    if (seen.size < MAX_DISTINCT) seen.add(key);

    // eslint-disable-next-line no-console -- stdout is where the container's logs are read from
    console.warn(
      `[csp] ${v.directive} blocked ${v.blocked || '(inline)'} on ${v.document}` +
        (v.source ? ` via ${v.source}` : ''),
    );
  }

  // Always 204: a browser has nothing to do with a reply, and an error here would be noise in the
  // console of a visitor who did nothing wrong.
  return new NextResponse(null, { status: 204 });
}

/** Anything other than a POST is not a report. */
export async function GET() {
  return new NextResponse(null, { status: 405 });
}
