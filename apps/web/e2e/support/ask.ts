import type { APIResponse } from '@playwright/test';

/**
 * Sends a request the way every spec here needs it sent.
 *
 * Two things can go wrong that are not what a test is about:
 *
 * 1. Every test signs in as the same user, and the API allows each user a number of requests a
 *    minute, so a busy run can be told 429 "too many requests". That is the limiter working: wait
 *    for the window to reset and ask again, up to eight times.
 *
 * 2. The connection is reset before the server has read the request ("read ECONNRESET"). The
 *    requests a test sends itself (page.request) go over connections that are kept open and
 *    reused. The development web server closes one that has been idle for five seconds; when a
 *    test spends about that long in the browser (viewing the portal as someone, say) and then
 *    sends its next request, the request can leave on a connection the server is closing at that
 *    moment. Seen in CI on 11 October 2026 (Firefox, workforce-planning, both attempts) and on a
 *    development PC on 12 October (Chromium, workforce-insights, under a full run): each time the
 *    first request after several seconds of browser-only steps, each time absent from the web
 *    server's log, each time passing when run alone. It could not be reproduced on demand, so
 *    this is a guard against a cause that fits every observation, not a proven fix. A request
 *    reset this way was not carried out, so it is sent again, up to three times.
 */
export async function ask(send: () => Promise<APIResponse>, wait: (ms: number) => Promise<void> = pause): Promise<APIResponse> {
  let limited = 0;
  let reset = 0;
  for (;;) {
    let res: APIResponse;
    try {
      res = await send();
    } catch (error) {
      if (!connectionWasReset(error) || reset >= 3) throw error;
      reset++;
      await wait(250 * reset);
      continue;
    }
    if (res.status() !== 429 || limited >= 8) return res;
    limited++;
    await wait(10_000);
  }
}

const pause = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** Whether a request failed because its connection went away, not because of what was asked. */
export function connectionWasReset(error: unknown): boolean {
  return /ECONNRESET|socket hang up|EPIPE/.test(String((error as Error | undefined)?.message ?? error));
}
