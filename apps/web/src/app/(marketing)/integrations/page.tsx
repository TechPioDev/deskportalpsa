import type { Metadata } from 'next';
import { Shell, Band, SectionHead, FeatureCard, PageHero, CtaBand } from '@/components/marketing/blocks';
import { Reveal } from '@/components/marketing/Reveal';
import { PsaEcosystem, PsaGrid } from '@/components/marketing/PsaEcosystem';
import { SyncLanes } from '@/components/marketing/IntegrationSync';
import { PSA_PLATFORMS } from '@/lib/psaPlatforms';
import { PLATFORM_PILLARS } from '@/lib/marketingContent';

export const metadata: Metadata = {
  title: 'Integrations — Desk Portal',
  description: `One client portal across ${PSA_PLATFORMS.length} PSA platforms. Connect the service management platform your MSP already runs and give every client the same experience.`,
  alternates: { canonical: '/integrations' },
};

export default function IntegrationsPage() {
  return (
    <>
      <PageHero
        eyebrow="Integrations"
        title="One portal. Multiple PSA platforms."
        lead="Connect your PSA environment to Desk Portal and give clients a consistent experience across your service desk ecosystem — without changing how your technicians work."
      />

      <Band>
        <Shell>
          <Reveal><PsaEcosystem /></Reveal>
        </Shell>
      </Band>

      <Band tone="raised">
        <Shell>
          <SectionHead
            eyebrow="Platforms"
            title="Choose the platform your MSP runs"
            lead="Every platform is built on the same portal and the same client experience."
            align="center"
          />
          <Reveal delay={80} className="mt-12"><PsaGrid /></Reveal>
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
            eyebrow="Built to grow"
            title="More PSAs. One client experience."
            lead="Every platform reuses the same connector layer, so the experience your clients get does not depend on which service desk sits behind it."
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

      <div className="pt-20 sm:pt-24">
        <CtaBand secondary={{ href: '/platform', label: 'See the platform' }} />
      </div>
    </>
  );
}
