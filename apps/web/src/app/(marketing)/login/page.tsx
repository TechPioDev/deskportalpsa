import Link from 'next/link';
import type { Metadata } from 'next';
import { LogIn, HelpCircle } from 'lucide-react';
import { Container } from '@/components/marketing/ui';
import { Hero } from '@/components/marketing/Hero';
import { CONTACT_EMAIL } from '@/components/marketing/MarketingFooter';

export const metadata: Metadata = { title: 'Sign in — Desk Portal' };

/**
 * Sign-in entry (Keycloak OIDC, auth-code + PKCE; local demo mode skips straight in).
 *
 * Lives inside the marketing group so it carries the site header and footer: it is the page a
 * prospect most often lands on from a bookmark or a link, and without them it was a dead end with
 * no route to what the product even is.
 */
export default function LoginPage() {
  const localMode = process.env.NEXT_PUBLIC_LOCAL_MODE === 'true';
  return (
    <>
      <Hero
        size="sm"
        eyebrow="Sign in"
        title={<>Welcome <span className="text-brand">back.</span></>}
        lead="Your tickets, your clients’ conversations, and the time logged against them — all in one place."
      />
      <Container className="flex items-center justify-center py-14">
      <div className="w-full max-w-sm">
        <div className="rounded-2xl border border-[var(--border)] bg-[var(--surface)] p-8">
          <h2 className="text-xl font-semibold tracking-tight">Sign in</h2>
          <p className="mt-1.5 text-sm text-[var(--muted)]">
            {localMode ? 'Local demo mode' : 'Continue to your ticket portal.'}
          </p>

          {localMode ? (
            <Link
              href="/dashboard"
              className="mt-6 block w-full rounded-lg bg-brand px-4 py-2.5 text-center font-medium text-brand-fg transition-opacity hover:opacity-90"
            >
              Enter portal
            </Link>
          ) : (
            // A link, not a form: /api/auth/login answers with a redirect to the sign-in host, and a
            // form submission that lands off-origin is what the CSP's form-action refuses. The only
            // violation the report-only policy ever recorded was this button.
            <a
              href="/api/auth/login"
              className="mt-6 inline-flex w-full items-center justify-center gap-2 rounded-lg bg-brand px-4 py-2.5 font-medium text-brand-fg transition-opacity hover:opacity-90"
            >
              <LogIn size={16} aria-hidden="true" /> Continue with SSO
            </a>
          )}

          <p className="mt-6 text-center text-xs leading-relaxed text-[var(--muted)]">
            {localMode
              ? 'Demo mode signs you in automatically — no identity provider needed.'
              : 'Authentication is handled by your organisation’s identity provider.'}
          </p>
        </div>

        <div className="mt-4 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4">
          <p className="flex items-start gap-2 text-xs leading-relaxed text-[var(--muted)]">
            <HelpCircle size={14} className="mt-0.5 shrink-0 text-brand" aria-hidden="true" />
            <span>
              Cannot get in? Your account is created by your provider and links to your sign-in the
              first time you use it. Ask them to check the email address matches, or write to{' '}
              <a className="text-brand underline underline-offset-2" href={`mailto:${CONTACT_EMAIL}`}>
                {CONTACT_EMAIL}
              </a>
              .
            </span>
          </p>
        </div>

        <p className="mt-4 text-center text-xs text-[var(--muted)]">
          <a href="/user-guide.pdf" target="_blank" rel="noopener noreferrer" className="hover:underline">
            User guide (PDF)
          </a>
        </p>
      </div>
      </Container>
    </>
  );
}
