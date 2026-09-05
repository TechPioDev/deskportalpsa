/**
 * Content Security Policy.
 *
 * Everything this app loads is same-origin — fonts are self-hosted at build time by next/font,
 * and nothing is pulled from a CDN — so the only awkward parts are the two 'unsafe-inline's.
 *
 * script-src needs it because the App Router streams its payload through dozens of inline
 * <script> blocks per page. The alternative is a per-request nonce from middleware, which works
 * but forces every page to render dynamically and would throw away the prerendering the
 * marketing pages currently rely on. Even with it, this policy still blocks the thing that
 * matters most: a script loaded from somewhere else.
 *
 * style-src needs it for inline style attributes, which React writes for anything computed.
 *
 * img-src allows any https origin because client branding takes a logo URL the customer types
 * in, and blob: because attachments and report downloads are read back as object URLs.
 *
 * Shipped as report-only first: a CSP that is wrong does not fail loudly, it silently blocks a
 * script and leaves a dead page, and this cannot be proven safe from outside a logged-in
 * session. Set CSP_ENFORCE=true once the console is clean through the dashboard.
 */
const csp = [
  "default-src 'self'",
  "script-src 'self' 'unsafe-inline'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data: blob: https:",
  "font-src 'self'",
  "connect-src 'self'",
  "object-src 'none'",
  "base-uri 'self'",
  "form-action 'self'",
  "frame-ancestors 'none'",
  // No upgrade-insecure-requests: HSTS already forces https for these hosts for a year, and a
  // report-only policy cannot honour it anyway — the browser logs an error about it on every
  // page load, which is exactly the noise that would bury a real violation while the policy is
  // still being proven.
].join('; ');

const cspHeaderName =
  process.env.CSP_ENFORCE === 'true'
    ? 'Content-Security-Policy'
    : 'Content-Security-Policy-Report-Only';

/** @type {import('next').NextConfig} */
const nextConfig = {
  // Self-contained server bundle for the Docker image: node_modules are traced in, so the runtime
  // stage copies one folder instead of reinstalling dependencies.
  output: 'standalone',
  reactStrictMode: true,
  poweredByHeader: false,
  async headers() {
    return [{
      source: '/:path*',
      headers: [
        { key: 'X-Content-Type-Options', value: 'nosniff' },
        { key: 'X-Frame-Options', value: 'DENY' },
        { key: 'Referrer-Policy', value: 'no-referrer' },
        { key: cspHeaderName, value: csp },
      ],
    }];
  },
};
export default nextConfig;
