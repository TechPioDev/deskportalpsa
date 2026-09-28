/**
 * A load probe with nothing to install.
 *
 * tests/load/k6-smoke.js states the performance targets but needs k6, a full stack and a token, so
 * it had never been run and the targets had never been measured. This hits the same endpoints with
 * plain fetch, reports the percentiles those targets are written in, and says plainly which ones
 * were met.
 *
 * It is a probe, not a substitute for the k6 run against production-like infrastructure: run
 * locally it measures this machine and whichever database the API is configured with.
 *
 *   node tests/load/probe.mjs --base http://localhost:5400 --vus 25 --seconds 30
 *   node tests/load/probe.mjs --token "<jwt>"        # when the API requires one
 */

const args = Object.fromEntries(
  process.argv.slice(2).join(' ').split('--').filter(Boolean)
    .map((part) => part.trim().split(/\s+/))
    .map(([k, ...v]) => [k, v.join(' ') || 'true']));

const BASE = args.base ?? 'http://localhost:5400';
const VUS = Number(args.vus ?? 25);
const SECONDS = Number(args.seconds ?? 30);
const TOKEN = args.token ?? '';

/** The targets from the spec, in the same words as the k6 thresholds. */
const ROUTES = [
  { name: 'health', path: '/health/ready', p95: 500 },
  { name: 'ticket_list', path: '/api/tickets', p95: 2000 },
  { name: 'dashboard', path: '/api/dashboard/daily?from=2026-01-01T00:00:00Z', p95: 3000 },
  { name: 'client_workload', path: '/api/dashboard/clients', p95: 3000 },
];

const headers = TOKEN ? { Authorization: `Bearer ${TOKEN}` } : {};
const samples = new Map(ROUTES.map((r) => [r.name, []]));
let failures = 0;
let requests = 0;
let limited = 0;

const percentile = (values, p) => {
  if (values.length === 0) return NaN;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))];
};

async function worker(deadline) {
  while (Date.now() < deadline) {
    for (const route of ROUTES) {
      const started = performance.now();
      let status = 0;
      try {
        const response = await fetch(BASE + route.path, { headers });
        status = response.status;
        await response.arrayBuffer();
      } catch {
        failures++;
      }
      requests++;
      // A 429 is the rate limiter doing its job, not the server failing or being slow. Counted
      // separately, and kept out of the timings: a rejection is fast and would flatter the figures.
      if (status === 429) { limited++; continue; }
      if (status !== 0 && (status < 200 || status >= 400)) failures++;
      samples.get(route.name).push(performance.now() - started);
    }
  }
}

console.log(`probing ${BASE} with ${VUS} concurrent callers for ${SECONDS}s\n`);
const deadline = Date.now() + SECONDS * 1000;
await Promise.all(Array.from({ length: VUS }, () => worker(deadline)));

const pad = (s, n) => String(s).padEnd(n);
console.log(`${pad('ROUTE', 18)}${pad('SAMPLES', 10)}${pad('p50', 10)}${pad('p95', 10)}${pad('p99', 10)}${pad('TARGET p95', 12)}VERDICT`);
let allMet = true;
for (const route of ROUTES) {
  const values = samples.get(route.name);
  const p95 = percentile(values, 95);
  const met = p95 <= route.p95;
  allMet &&= met;
  console.log(
    pad(route.name, 18) + pad(values.length, 10) +
    pad(`${percentile(values, 50).toFixed(0)}ms`, 10) +
    pad(`${p95.toFixed(0)}ms`, 10) +
    pad(`${percentile(values, 99).toFixed(0)}ms`, 10) +
    pad(`${route.p95}ms`, 12) + (met ? 'met' : 'MISSED'));
}
const errorRate = requests === 0 ? 0 : failures / requests;
console.log(`\n${requests} requests, ${failures} failed (${(errorRate * 100).toFixed(2)}%), target under 1.00%`);
if (!allMet || errorRate >= 0.01) {
  console.log('\nOne or more targets were missed.');
  process.exitCode = 1;
}
