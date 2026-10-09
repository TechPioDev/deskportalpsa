import type { Metadata } from 'next';
import { Shell, Band, SectionHead, FeatureCard, CapabilityTile, PageHero, CtaBand } from '@/components/marketing/blocks';
import { Reveal } from '@/components/marketing/Reveal';
import { SECURITY, PLATFORM_PILLARS } from '@/lib/marketingContent';

export const metadata: Metadata = {
  title: 'Security — PioManage',
  description:
    'How PioManage is built: multi-tenant isolation, role-based access, SSO and MFA through your own identity provider, audit logging, vaulted PSA credentials and self-hosted deployment.',
  alternates: { canonical: '/security' },
};

export default function SecurityPage() {
  return (
    <>
      <PageHero
        eyebrow="Security"
        title="Enterprise-grade control without enterprise-level complexity."
        lead="You are holding other companies' data. The platform is built that way from the first request rather than hardened later."
      />

      <Band tone="ink">
        <Shell>
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {SECURITY.map(({ icon: Icon, title, body }, i) => (
              <Reveal key={title} delay={(i % 3) * 70}>
                <div className="dp-lift h-full rounded-2xl border border-white/10 bg-white/[0.04] p-5 backdrop-blur">
                  <span className="mb-3 flex h-10 w-10 items-center justify-center rounded-xl bg-brand-soft/15 text-brand-soft">
                    <Icon size={18} aria-hidden="true" />
                  </span>
                  <h2 className="text-[15px] font-semibold text-white">{title}</h2>
                  <p className="mt-2 text-[13.5px] leading-relaxed text-white/60">{body}</p>
                </div>
              </Reveal>
            ))}
          </div>

          <Reveal delay={120} className="mt-12">
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
              <CapabilityTile value="24/7" label="Client self-service access" />
              <CapabilityTile value="Two-way" label="Continuous PSA sync" />
              <CapabilityTile value="Multi-PSA" label="One experience, many platforms" />
              <CapabilityTile value="Multi-tenant" label="Client isolation by design" />
            </div>
            <p className="mt-4 text-center text-[12px] text-white/40">
              Capability statements describing how the platform is built — not customer metrics.
            </p>
          </Reveal>
        </Shell>
      </Band>

      <Band>
        <Shell>
          <SectionHead
            eyebrow="Deployment"
            title="Run it where your data is allowed to live."
            lead="PioManage is self-hosted. The platform, its database and its secrets store sit on infrastructure you control, alongside whatever else you already run."
          />
          <div className="mt-10 grid gap-4 lg:grid-cols-3">
            {PLATFORM_PILLARS.map((p, i) => (
              <Reveal key={p.title} delay={i * 80}>
                <FeatureCard icon={p.icon} title={p.title}>{p.body}</FeatureCard>
              </Reveal>
            ))}
          </div>
        </Shell>
      </Band>

      <Band tone="raised">
        <Shell className="max-w-3xl">
          <SectionHead eyebrow="Data handling" title="What we do not do" />
          <ul className="mt-8 space-y-4 text-[15px] leading-relaxed text-[var(--muted)]">
            {[
              'We do not hold your PSA credentials in the application database. They live in a secrets vault.',
              'We do not make the portal a second system of record. Your PSA remains the source of truth.',
              'We do not expose one client company’s data to another. Isolation is enforced on every request, not by a filter in the interface.',
              'We do not publish security badges or certifications the platform has not earned.',
            ].map((t) => (
              <Reveal key={t}>
                <li className="flex gap-3">
                  <span aria-hidden="true" className="mt-2 h-1.5 w-1.5 shrink-0 rounded-full bg-brand" />
                  {t}
                </li>
              </Reveal>
            ))}
          </ul>
          <p className="mt-8 text-[14px] text-[var(--faint)]">
            Our{' '}
            <a href="/privacy" className="text-brand underline underline-offset-2 dark:text-brand-soft">privacy policy</a>
            {' '}and{' '}
            <a href="/terms" className="text-brand underline underline-offset-2 dark:text-brand-soft">terms of service</a>
            {' '}set out the rest in full.
          </p>
        </Shell>
      </Band>

      <div className="pt-20 sm:pt-24">
        <CtaBand secondary={{ href: '/platform', label: 'See the platform' }} />
      </div>
    </>
  );
}
