'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useState } from 'react';
import { Menu, X } from 'lucide-react';
import { BrandMark } from '@/components/BrandMark';

export const MARKETING_NAV = [
  { href: '/platform', label: 'Platform' },
  { href: '/features', label: 'Features' },
  { href: '/integrations', label: 'Integrations' },
  { href: '/security', label: 'Security' },
  { href: '/faq', label: 'FAQ' },
  { href: '/about', label: 'About' },
] as const;

/**
 * Public-site header. Sticky, because the two things a visitor needs — book a call, sign in —
 * should never be a scroll away. The nav collapses to a disclosure below lg rather than a drawer:
 * these links do not warrant an overlay, and at md they no longer fit beside the logo and both
 * calls to action once a scrollbar takes its share of the viewport.
 *
 * Every entry is a real route. They were anchors into one long home page, which meant the current
 * page could never be marked and the browser's back button did nothing useful.
 */
export function MarketingHeader() {
  const pathname = usePathname();
  const [open, setOpen] = useState(false);

  return (
    <header className="sticky top-0 z-40 border-b border-[var(--border)] bg-[var(--surface)]/75 backdrop-blur-xl">
      <div className="mx-auto flex max-w-shell items-center gap-3 px-5 py-3 sm:px-8">
        <Link href="/" className="flex shrink-0 items-center gap-3" onClick={() => setOpen(false)}>
          <BrandMark size={46} className="rounded-xl" />
          <span className="text-[19px] font-semibold tracking-tight">PioManage</span>
        </Link>

        <nav className="ml-6 hidden items-center gap-1 lg:flex">
          {MARKETING_NAV.map((item) => {
            // A section page stays marked while you are anywhere beneath it, e.g. an integration.
            const active = pathname === item.href || pathname.startsWith(`${item.href}/`);
            return (
              <Link
                key={item.href}
                href={item.href}
                aria-current={active ? 'page' : undefined}
                className={`rounded-lg px-3 py-2 text-sm transition-colors ${
                  active ? 'font-medium text-brand' : 'text-[var(--muted)] hover:text-[var(--fg)]'
                }`}
              >
                {item.label}
              </Link>
            );
          })}
        </nav>

        <div className="ml-auto flex items-center gap-2">
          <Link
            href="/login"
            className="hidden rounded-lg px-3 py-2 text-sm text-[var(--muted)] hover:text-[var(--fg)] sm:block"
          >
            Sign in
          </Link>
          <Link
            href="/book"
            className="rounded-lg bg-brand px-4 py-2 text-sm font-medium text-brand-fg transition-transform hover:-translate-y-0.5"
          >
            Book a demo
          </Link>
          <button
            type="button"
            onClick={() => setOpen((v) => !v)}
            aria-expanded={open}
            aria-label={open ? 'Close menu' : 'Open menu'}
            className="rounded-lg p-2 text-[var(--muted)] hover:bg-[var(--bg)] lg:hidden"
          >
            {open ? <X size={18} /> : <Menu size={18} />}
          </button>
        </div>
      </div>

      {open && (
        <nav className="border-t border-[var(--border)] px-5 py-2 lg:hidden">
          {[...MARKETING_NAV, { href: '/login', label: 'Sign in' }].map((item) => (
            <Link
              key={item.href}
              href={item.href}
              onClick={() => setOpen(false)}
              className="block rounded-lg px-3 py-2.5 text-sm text-[var(--muted)] hover:bg-[var(--bg)] hover:text-[var(--fg)]"
            >
              {item.label}
            </Link>
          ))}
        </nav>
      )}
    </header>
  );
}
