import Link from 'next/link';
import { ArrowRight, type LucideIcon } from 'lucide-react';
import { Reveal } from '@/components/marketing/Reveal';

/** One place decides how wide the site is and how far apart its sections sit. */
export function Shell({ children, className = '' }: { children: React.ReactNode; className?: string }) {
  return <div className={`mx-auto w-full max-w-shell px-5 sm:px-8 ${className}`}>{children}</div>;
}

export function Band({
  children, id, tone = 'plain', className = '',
}: {
  children: React.ReactNode;
  id?: string;
  tone?: 'plain' | 'raised' | 'ink';
  className?: string;
}) {
  const tones = {
    plain: '',
    raised: 'border-y border-[var(--border)] bg-[var(--surface)]',
    ink: 'bg-ink text-white',
  } as const;
  return (
    <section id={id} className={`scroll-mt-20 py-20 sm:py-28 ${tones[tone]} ${className}`}>
      {children}
    </section>
  );
}

export function SectionHead({
  eyebrow, title, lead, align = 'left', onInk = false,
}: {
  eyebrow?: string;
  title: React.ReactNode;
  lead?: string;
  align?: 'left' | 'center';
  onInk?: boolean;
}) {
  return (
    <Reveal className={align === 'center' ? 'mx-auto max-w-2xl text-center' : 'max-w-3xl'}>
      {eyebrow && (
        <p className={`mb-3 text-xs font-semibold uppercase tracking-[0.18em] ${onInk ? 'text-brand-soft' : 'text-brand-mid'}`}>
          {eyebrow}
        </p>
      )}
      <h2 className="text-[1.75rem] font-semibold leading-[1.15] tracking-tight sm:text-[2.25rem]">{title}</h2>
      {lead && (
        <p className={`mt-4 text-[17px] leading-relaxed ${onInk ? 'text-white/65' : 'text-[var(--muted)]'}`}>{lead}</p>
      )}
    </Reveal>
  );
}

/**
 * The opening block of every page that is not the home page.
 *
 * Deliberately quieter than the home hero — no aurora, no product mockup. A visitor who has landed
 * on an inner page has already been sold the headline; what they need here is to know where they
 * are and get to the content.
 */
export function PageHero({
  eyebrow, title, lead, children,
}: {
  eyebrow: string;
  title: React.ReactNode;
  lead: string;
  children?: React.ReactNode;
}) {
  return (
    <section className="relative isolate overflow-hidden border-b border-[var(--border)] bg-[var(--surface)]">
      <div aria-hidden="true" className="pointer-events-none absolute inset-0 -z-10">
        <div className="dp-aurora h-[22rem] w-[22rem] bg-brand/15" style={{ top: '-10rem', left: '-6rem' }} />
      </div>
      <Shell className="py-16 sm:py-20">
        <p className="dp-rise mb-3 text-xs font-semibold uppercase tracking-[0.18em] text-brand-mid">
          {eyebrow}
        </p>
        <h1
          className="dp-rise max-w-3xl text-[2.1rem] font-semibold leading-[1.1] tracking-tight sm:text-[2.75rem]"
          style={{ animationDelay: '60ms' }}
        >
          {title}
        </h1>
        <p
          className="dp-rise mt-5 max-w-2xl text-[17px] leading-relaxed text-[var(--muted)]"
          style={{ animationDelay: '120ms' }}
        >
          {lead}
        </p>
        {children && (
          <div className="dp-rise mt-8" style={{ animationDelay: '180ms' }}>
            {children}
          </div>
        )}
      </Shell>
    </section>
  );
}

/**
 * The closing call to action. One component rather than a copy per page, so the last thing a
 * visitor reads never drifts out of step with the rest of the site.
 */
export function CtaBand({
  title = 'Keep your PSA. Upgrade your client experience.',
  lead = 'PioManage gives your clients a modern support experience while your team continues working in the PSA they already know.',
  secondary = { href: '/integrations', label: 'Explore integrations' },
}: {
  title?: string;
  lead?: string;
  secondary?: { href: string; label: string };
}) {
  return (
    <Shell className="pb-24">
      <Reveal>
        <div className="relative isolate overflow-hidden rounded-3xl bg-brand px-6 py-14 text-brand-fg sm:px-12">
          <div aria-hidden="true" className="pointer-events-none absolute inset-0 -z-10">
            <div className="dp-aurora h-80 w-80 bg-brand-soft/30" style={{ top: '-6rem', right: '-4rem' }} />
            <div className="dp-aurora h-72 w-72 bg-brand-accent/20" style={{ bottom: '-8rem', left: '-3rem', animationDelay: '-5s' }} />
          </div>
          <h2 className="max-w-2xl text-[1.8rem] font-semibold leading-tight tracking-tight sm:text-[2.4rem]">
            {title}
          </h2>
          <p className="mt-4 max-w-xl text-[16px] leading-relaxed text-brand-fg/75">{lead}</p>
          <div className="mt-8 flex flex-wrap gap-3">
            <Link
              href="/book"
              className="inline-flex items-center gap-2 rounded-xl bg-brand-fg px-6 py-3.5 text-sm font-medium text-brand transition-transform hover:-translate-y-0.5"
            >
              Book a demo <ArrowRight size={15} aria-hidden="true" />
            </Link>
            <Link
              href={secondary.href}
              className="inline-flex items-center gap-2 rounded-xl border border-brand-fg/30 px-6 py-3.5 text-sm font-medium transition-colors hover:bg-brand-fg/10"
            >
              {secondary.label} <ArrowRight size={15} aria-hidden="true" />
            </Link>
          </div>
        </div>
      </Reveal>
    </Shell>
  );
}

export function FeatureCard({
  icon: Icon, title, children, visual,
}: {
  icon: LucideIcon;
  title: string;
  children: React.ReactNode;
  visual?: React.ReactNode;
}) {
  return (
    <div className="dp-lift group flex h-full flex-col rounded-2xl border border-[var(--border)] bg-[var(--surface)] p-5">
      <span className="mb-3 flex h-10 w-10 items-center justify-center rounded-xl bg-brand-tint text-brand-deep transition-colors group-hover:bg-brand group-hover:text-brand-fg dark:bg-brand/25 dark:text-brand-soft">
        <Icon size={18} aria-hidden="true" />
      </span>
      <h3 className="text-[15px] font-semibold">{title}</h3>
      <p className="mt-2 text-[13.5px] leading-relaxed text-[var(--muted)]">{children}</p>
      {visual && <div className="mt-4">{visual}</div>}
    </div>
  );
}

/**
 * Capability statements, not metrics. Nothing here is a customer count or a performance figure,
 * because inventing one would be the fastest way to lose a buyer who checks.
 */
export function CapabilityTile({ value, label }: { value: string; label: string }) {
  return (
    <div className="rounded-2xl border border-white/10 bg-white/[0.04] p-5 text-center backdrop-blur">
      <p className="text-2xl font-semibold tracking-tight text-brand-soft sm:text-3xl">{value}</p>
      <p className="mt-1.5 text-[13px] text-white/60">{label}</p>
    </div>
  );
}

export function Step({ n, title, children }: { n: string; title: string; children: React.ReactNode }) {
  return (
    <div className="relative rounded-2xl border border-[var(--border)] bg-[var(--surface)] p-6">
      <span className="text-[2.5rem] font-semibold leading-none tracking-tight text-brand/15 dark:text-brand-soft/20">
        {n}
      </span>
      <h3 className="mt-2 text-[15px] font-semibold">{title}</h3>
      <p className="mt-2 text-[13.5px] leading-relaxed text-[var(--muted)]">{children}</p>
    </div>
  );
}
