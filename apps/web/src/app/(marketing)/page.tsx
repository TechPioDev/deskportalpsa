import Link from 'next/link';
import type { Metadata } from 'next';
import { ArrowRight, Play, ShieldCheck, Lock, Building2, Server } from 'lucide-react';
import { Shell, Band, SectionHead, FeatureCard, Step, CtaBand } from '@/components/marketing/blocks';
import { Reveal } from '@/components/marketing/Reveal';
import { HeroStage } from '@/components/marketing/HeroStage';
import { FlowRail } from '@/components/marketing/FlowRail';
import { IntegrationBadge } from '@/components/marketing/IntegrationSync';
import { PsaStrip } from '@/components/marketing/PsaEcosystem';
import { PSA_PLATFORMS } from '@/lib/psaPlatforms';
import { HOME_FEATURES, HOW_IT_WORKS } from '@/lib/marketingContent';

export const metadata: Metadata = {
  title: 'PioManage — one modern client portal for the PSA your MSP already runs',
  description:
    'Give clients a modern way to submit requests, follow updates and share files, while your technicians keep working in the PSA they already use. Multi-PSA by design.',
  alternates: { canonical: '/' },
  openGraph: {
    title: 'PioManage — one modern client portal. Any PSA.',
    description:
      'A client experience platform that sits on top of your existing PSA. Two-way sync, multi-tenant, self-hosted.',
    type: 'website',
    siteName: 'PioManage',
  },
};

/**
 * The home page answers four questions and then gets out of the way: what this is, which PSA
 * platforms it covers, how it works, and is it safe. Everything that used to sit below — the
 * product tour, the full feature grid, the security detail, the platform pages — now has its own
 * route, because a visitor who wants that depth is willing to click for it, and one who does not
 * should never have to scroll past it.
 */
export default function HomePage() {
  return (
    <>
      <section className="relative isolate overflow-hidden border-b border-[var(--border)]">
        <div aria-hidden="true" className="pointer-events-none absolute inset-0 -z-10">
          <div className="dp-aurora h-[30rem] w-[30rem] bg-brand/20" style={{ top: '-12rem', left: '-8rem' }} />
          <div className="dp-aurora h-[26rem] w-[26rem] bg-brand-soft/35" style={{ top: '-6rem', right: '-6rem', animationDelay: '-6s' }} />
          <div
            className="dp-grid-bg absolute inset-0 opacity-60 dark:opacity-25"
            style={{
              maskImage: 'radial-gradient(ellipse 75% 55% at 50% 0%, #000 40%, transparent 100%)',
              WebkitMaskImage: 'radial-gradient(ellipse 75% 55% at 50% 0%, #000 40%, transparent 100%)',
            }}
          />
        </div>

        <Shell className="grid items-center gap-14 pt-16 pb-20 sm:pt-24 lg:grid-cols-[1fr_1.05fr] lg:gap-16 lg:pb-28">
          <div>
            <div className="dp-rise"><IntegrationBadge /></div>
            <h1
              className="dp-rise mt-5 text-[2.4rem] font-semibold leading-[1.06] tracking-tight sm:text-[3.1rem]"
              style={{ animationDelay: '80ms' }}
            >
              The client portal that works with{' '}
              <span className="text-brand dark:text-brand-soft">your PSA.</span>
            </h1>
            <p
              className="dp-rise mt-5 max-w-xl text-[17px] leading-relaxed text-[var(--muted)]"
              style={{ animationDelay: '150ms' }}
            >
              Give clients a modern way to submit requests, follow updates, share files and talk to
              your support team — while your technicians continue working in the PSA they already
              use. You should not have to replace your PSA to improve the client experience.
            </p>
            <div className="dp-rise mt-8 flex flex-wrap gap-3" style={{ animationDelay: '220ms' }}>
              <Link
                href="/book"
                className="inline-flex items-center gap-2 rounded-xl bg-brand px-6 py-3.5 text-sm font-medium text-brand-fg shadow-[0_10px_30px_-12px_rgba(20,83,45,0.8)] transition-transform hover:-translate-y-0.5"
              >
                Book a demo <ArrowRight size={15} aria-hidden="true" />
              </Link>
              <Link
                href="/platform"
                className="inline-flex items-center gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] px-6 py-3.5 text-sm font-medium transition-transform hover:-translate-y-0.5"
              >
                <Play size={14} aria-hidden="true" /> See the platform
              </Link>
            </div>
            <p className="dp-rise mt-5 text-[12.5px] text-[var(--faint)]" style={{ animationDelay: '280ms' }}>
              {PSA_PLATFORMS.length} PSA platforms · self-hosted · your PSA stays the system of record
            </p>
          </div>

          <div className="dp-rise" style={{ animationDelay: '320ms' }}>
            <HeroStage />
          </div>
        </Shell>
      </section>

      <Band tone="raised" className="!py-14">
        <Shell>
          <p className="text-center text-xs font-semibold uppercase tracking-[0.18em] text-[var(--faint)]">
            Built for the PSA platforms MSPs run
          </p>
          <Reveal delay={60} className="mt-8"><PsaStrip /></Reveal>
          <p className="mt-8 text-center">
            <Link
              href="/integrations"
              className="inline-flex items-center gap-1.5 text-sm font-medium text-brand hover:underline dark:text-brand-soft"
            >
              Explore all integrations <ArrowRight size={14} aria-hidden="true" />
            </Link>
          </p>
        </Shell>
      </Band>

      <Band>
        <Shell>
          <SectionHead
            eyebrow="How it works"
            title="One request. Two experiences. One system of record."
            lead="The client gets a portal. The technician keeps the PSA. PioManage keeps the two in step."
            align="center"
          />
          <Reveal delay={80} className="mt-12"><FlowRail /></Reveal>

          <div className="mt-12 grid gap-5 sm:grid-cols-2 lg:grid-cols-4">
            {HOW_IT_WORKS.map((s, i) => (
              <Reveal key={s.n} delay={i * 70}>
                <Step n={s.n} title={s.title}>{s.body}</Step>
              </Reveal>
            ))}
          </div>
        </Shell>
      </Band>

      <Band tone="raised">
        <Shell>
          <SectionHead
            eyebrow="What clients get"
            title="Everything your clients need. Nothing your technicians need to relearn."
            align="center"
          />
          <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {HOME_FEATURES.map((f, i) => (
              <Reveal key={f.title} delay={(i % 4) * 60}>
                <FeatureCard icon={f.icon} title={f.title}>{f.body}</FeatureCard>
              </Reveal>
            ))}
          </div>
          <p className="mt-8 text-center">
            <Link
              href="/platform"
              className="inline-flex items-center gap-1.5 text-sm font-medium text-brand hover:underline dark:text-brand-soft"
            >
              See the full platform <ArrowRight size={14} aria-hidden="true" />
            </Link>
          </p>
        </Shell>
      </Band>

      <Band tone="ink">
        <Shell className="grid items-center gap-10 lg:grid-cols-[1.1fr_1fr]">
          <div>
            <SectionHead
              eyebrow="Security"
              title="You are holding other companies' data."
              lead="Isolation between client companies, role-based access, sign-in through your own identity provider, and an audit trail that cannot be quietly altered."
              onInk
            />
            <Reveal delay={100}>
              <Link
                href="/security"
                className="mt-8 inline-flex items-center gap-2 rounded-xl border border-white/20 px-5 py-3 text-sm font-medium text-white transition-colors hover:bg-white/10"
              >
                How the platform is secured <ArrowRight size={15} aria-hidden="true" />
              </Link>
            </Reveal>
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            {[
              { icon: Building2, label: 'Multi-tenant isolation' },
              { icon: Lock, label: 'Role-based access' },
              { icon: ShieldCheck, label: 'Credentials in a vault' },
              { icon: Server, label: 'Self-hosted deployment' },
            ].map(({ icon: Icon, label }, i) => (
              <Reveal key={label} delay={i * 70}>
                <div className="flex h-full items-center gap-3 rounded-2xl border border-white/10 bg-white/[0.04] p-5 backdrop-blur">
                  <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-brand-soft/15 text-brand-soft">
                    <Icon size={18} aria-hidden="true" />
                  </span>
                  <span className="text-[14px] font-medium text-white">{label}</span>
                </div>
              </Reveal>
            ))}
          </div>
        </Shell>
      </Band>

      <div className="pt-20 sm:pt-24">
        <CtaBand />
      </div>
    </>
  );
}
