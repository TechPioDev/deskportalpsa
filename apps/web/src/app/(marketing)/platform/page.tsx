import type { Metadata } from 'next';
import { RefreshCw, ShieldCheck } from 'lucide-react';
import { Shell, Band, SectionHead, FeatureCard, PageHero, CtaBand } from '@/components/marketing/blocks';
import { Reveal } from '@/components/marketing/Reveal';
import { Compare } from '@/components/marketing/Compare';
import { ProductTabs } from '@/components/marketing/ProductTabs';
import { SyncLanes } from '@/components/marketing/IntegrationSync';
import { BrowserFrame, ProductScreen } from '@/components/marketing/ProductUI';
import { FEATURES, BENEFITS, USE_CASES, CLIENT_JOURNEY } from '@/lib/marketingContent';

export const metadata: Metadata = {
  title: 'Platform — PioManage',
  description:
    'A tour of PioManage: the client experience, the technician experience, and the two-way sync that keeps your PSA the system of record.',
  alternates: { canonical: '/platform' },
};

export default function PlatformPage() {
  return (
    <>
      <PageHero
        eyebrow="Platform"
        title={<>Your PSA works for your technicians. <span className="text-[var(--muted)]">Does it work for your clients?</span></>}
        lead="A PSA is built for the people who resolve work. Handing that interface to a client — or leaving them on email — is where visibility disappears. PioManage is the surface in between."
      />

      <Band>
        <Shell>
          <Reveal><Compare /></Reveal>
        </Shell>
      </Band>

      <Band tone="raised">
        <Shell>
          <SectionHead
            eyebrow="The product"
            title="See PioManage in action"
            lead="A look at the client experience: requests, conversation, shared files, progress, and the sync that keeps your PSA current."
          />
          <Reveal delay={80} className="mt-10"><ProductTabs /></Reveal>
        </Shell>
      </Band>

      <Band>
        <Shell className="grid items-center gap-12 lg:grid-cols-2">
          <div>
            <SectionHead
              eyebrow="For your clients"
              title="Make IT support feel simple."
              lead="No PSA login, no training, no wondering where a request went."
            />
            <ol className="mt-8 space-y-4">
              {CLIENT_JOURNEY.map((t, i) => (
                <Reveal key={t} delay={i * 60}>
                  <li className="flex gap-3 text-[14.5px] leading-relaxed text-[var(--muted)]">
                    <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-brand text-[11px] font-semibold text-brand-fg">
                      {i + 1}
                    </span>
                    {t}
                  </li>
                </Reveal>
              ))}
            </ol>
          </div>
          <Reveal delay={100}>
            <BrowserFrame><ProductScreen view="requests" /></BrowserFrame>
          </Reveal>
        </Shell>
      </Band>

      <Band tone="raised">
        <Shell className="grid items-center gap-12 lg:grid-cols-2">
          <Reveal className="order-2 lg:order-1">
            <BrowserFrame url="Connected PSA"><ProductScreen view="sync" /></BrowserFrame>
          </Reveal>
          <div className="order-1 lg:order-2">
            <SectionHead
              eyebrow="For your technicians"
              title="Technicians keep working where they already work."
              lead="Nobody learns a second system or watches a second queue. Replies written in your PSA reach the client, and client replies land on the request already in front of your team."
            />
            <div className="mt-8 grid gap-4 sm:grid-cols-2">
              <FeatureCard icon={RefreshCw} title="No second inbox">The portal is a surface, not a destination. There is nothing extra to check.</FeatureCard>
              <FeatureCard icon={ShieldCheck} title="No risk to the record">Every change is written back, so the PSA stays the single source of truth.</FeatureCard>
            </div>
          </div>
        </Shell>
      </Band>

      <Band>
        <Shell>
          <SectionHead
            eyebrow="Two-way sync"
            title="Everything stays in step, both directions."
            lead="Whichever PSA you connect, the same things move — and they move continuously rather than on demand."
            align="center"
          />
          <Reveal delay={80} className="mt-10"><SyncLanes /></Reveal>
        </Shell>
      </Band>

      <Band tone="raised">
        <Shell>
          <SectionHead
            eyebrow="Features"
            title="Everything your clients need. Nothing your technicians need to relearn."
          />
          <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {FEATURES.map((f, i) => (
              <Reveal key={f.title} delay={(i % 4) * 60}>
                <FeatureCard icon={f.icon} title={f.title}>{f.body}</FeatureCard>
              </Reveal>
            ))}
          </div>
        </Shell>
      </Band>

      <Band>
        <Shell>
          <SectionHead eyebrow="Who it is for" title="Where PioManage fits" align="center" />
          <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {USE_CASES.map((u, i) => (
              <Reveal key={u.title} delay={(i % 3) * 70}>
                <FeatureCard icon={u.icon} title={u.title}>{u.body}</FeatureCard>
              </Reveal>
            ))}
          </div>
        </Shell>
      </Band>

      <Band tone="raised">
        <Shell>
          <SectionHead eyebrow="Why MSPs use it" title="Designed around how your team actually works." align="center" />
          <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {BENEFITS.map((b, i) => (
              <Reveal key={b.title} delay={(i % 3) * 70}>
                <FeatureCard icon={b.icon} title={b.title}>{b.body}</FeatureCard>
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
