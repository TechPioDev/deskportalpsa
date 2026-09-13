import Link from 'next/link';
import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { ArrowLeft, ArrowRight, RefreshCw, ShieldCheck, Wrench } from 'lucide-react';
import { Shell, Band, SectionHead, FeatureCard, Step, PageHero, CtaBand } from '@/components/marketing/blocks';
import { Reveal } from '@/components/marketing/Reveal';
import { SyncLanes } from '@/components/marketing/IntegrationSync';
import { BrowserFrame, ProductScreen } from '@/components/marketing/ProductUI';
import { PSA_PLATFORMS, findPlatform, platformHref } from '@/lib/psaPlatforms';
import { CLIENT_JOURNEY, HOW_IT_WORKS } from '@/lib/marketingContent';

type Params = { params: Promise<{ platform: string }> };

/** Eight known ids, rendered at build time. Anything else is a 404, not an improvised page. */
export function generateStaticParams() {
  return PSA_PLATFORMS.map((p) => ({ platform: p.id }));
}

export const dynamicParams = false;

export async function generateMetadata({ params }: Params): Promise<Metadata> {
  const platform = findPlatform((await params).platform);
  if (!platform) return {};
  return {
    title: `Desk Portal for ${platform.name}`,
    description: platform.hasConnector
      ? `Give your clients a modern support portal while your technicians keep working in ${platform.name}. Two-way sync, multi-tenant, self-hosted.`
      : `A modern client support portal, built for MSPs whose technicians work in ${platform.name}. Multi-tenant and self-hosted.`,
    alternates: { canonical: platformHref(platform) },
  };
}

/**
 * One page per PSA platform.
 *
 * The structure is shared on purpose. Every platform gets the same portal, the same sync and the
 * same client experience, so a page that promised something different for one of them would be
 * describing a product that does not exist. What differs is the name of the service desk your
 * team keeps working in — which is the whole point of the page.
 *
 * Nothing here labels where any platform sits in the build. Do not add a status badge, a launch
 * date, or copy that reads as one. What the page must not do either is promise what is not true:
 * where no connector ships yet (`hasConnector`), it drops the "connect your environment" steps
 * and present-tense claims about writing back, and invites a conversation instead.
 */
export default async function PlatformIntegrationPage({ params }: Params) {
  const platform = findPlatform((await params).platform);
  if (!platform) notFound();

  const others = PSA_PLATFORMS.filter((p) => p.id !== platform.id);
  const live = platform.hasConnector;

  return (
    <>
      <PageHero
        eyebrow="Integration"
        title={<>Desk Portal for <span className="text-brand dark:text-brand-soft">{platform.name}</span></>}
        lead={live
          ? `Give your clients a modern way to raise requests, follow progress and share files — while your technicians carry on in ${platform.name}. Your service desk stays the system of record.`
          : `Desk Portal is built to give your clients a modern way to raise requests, follow progress and share files — while your technicians carry on in ${platform.name}, and it stays the system of record.`}
      >
        <div className="flex flex-wrap items-center gap-3">
          <Link
            href="/book"
            className="inline-flex items-center gap-2 rounded-xl bg-brand px-6 py-3.5 text-sm font-medium text-brand-fg transition-transform hover:-translate-y-0.5"
          >
            {live ? 'Book a demo' : `Talk to us about ${platform.name}`} <ArrowRight size={15} aria-hidden="true" />
          </Link>
          <Link
            href="/integrations"
            className="inline-flex items-center gap-2 rounded-xl border border-[var(--border)] bg-[var(--bg)] px-6 py-3.5 text-sm font-medium transition-transform hover:-translate-y-0.5"
          >
            <ArrowLeft size={15} aria-hidden="true" /> All integrations
          </Link>
        </div>
      </PageHero>

      <Band>
        <Shell className="grid items-center gap-12 lg:grid-cols-2">
          <div>
            <SectionHead
              eyebrow="For your clients"
              title="A support experience written for them."
              lead={`No ${platform.name} login, no training, and no wondering where a request went.`}
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
        <Shell>
          <SectionHead
            eyebrow="For your technicians"
            title={`Your team keeps ${platform.name}.`}
            lead="Nobody learns a second system or watches a second queue. The portal is a surface on top of the service desk you already run, not a replacement for it."
            align="center"
          />
          <div className="mt-10 grid gap-4 lg:grid-cols-3">
            <Reveal>
              <FeatureCard icon={Wrench} title="No workflow change">
                Boards, queues and habits stay exactly as they are. Technicians work the same day they did before.
              </FeatureCard>
            </Reveal>
            <Reveal delay={80}>
              <FeatureCard icon={RefreshCw} title="No second inbox">
                Client replies land on the request already in front of your team. There is nothing extra to check.
              </FeatureCard>
            </Reveal>
            <Reveal delay={160}>
              <FeatureCard icon={ShieldCheck} title="No risk to the record">
                {live
                  ? <>Every change is written back, so {platform.name} stays the single source of truth.</>
                  : <>Designed so every change is written back and {platform.name} stays the single source of truth.</>}
              </FeatureCard>
            </Reveal>
          </div>
        </Shell>
      </Band>

      <Band>
        <Shell>
          <SectionHead
            eyebrow="Two-way sync"
            title="Everything stays in step, both directions."
            lead={live
              ? 'Requests, replies, files and status move continuously rather than on demand.'
              : 'How the sync works: requests, replies, files and status move continuously rather than on demand.'}
            align="center"
          />
          <Reveal delay={80} className="mt-10"><SyncLanes /></Reveal>
        </Shell>
      </Band>

      {live ? (
      <Band tone="raised">
        <Shell>
          <SectionHead
            eyebrow="Getting started"
            title={`Connecting ${platform.name}`}
            align="center"
          />
          <div className="mt-10 grid gap-5 sm:grid-cols-2 lg:grid-cols-4">
            {HOW_IT_WORKS.map((s, i) => (
              <Reveal key={s.n} delay={i * 70}>
                <Step n={s.n} title={s.title}>
                  {s.n === '01' ? `Connect Desk Portal to your ${platform.name} environment.` : s.body}
                </Step>
              </Reveal>
            ))}
          </div>
        </Shell>
      </Band>
      ) : (
        <Band tone="raised">
          <Shell>
            <SectionHead
              eyebrow="Getting started"
              title={`Bringing Desk Portal to ${platform.name}`}
              lead={`Tell us how your team runs ${platform.name} — boards, statuses and the clients you support. That is what shapes the connection, so we start with a conversation rather than a setup wizard.`}
              align="center"
            />
            <div className="mt-8 flex justify-center">
              <Link
                href="/book"
                className="inline-flex items-center gap-2 rounded-xl bg-brand px-6 py-3.5 text-sm font-medium text-brand-fg transition-transform hover:-translate-y-0.5"
              >
                Talk to us about {platform.name} <ArrowRight size={15} aria-hidden="true" />
              </Link>
            </div>
          </Shell>
        </Band>
      )}

      <Band>
        <Shell>
          <h2 className="text-center text-xs font-semibold uppercase tracking-[0.18em] text-[var(--faint)]">
            Other platforms
          </h2>
          <ul className="mt-7 flex flex-wrap items-center justify-center gap-2.5">
            {others.map((p) => (
              <li key={p.id}>
                <Link
                  href={platformHref(p)}
                  className="dp-lift flex items-center gap-2.5 rounded-xl border border-[var(--border)] bg-[var(--surface)] py-2.5 pl-2.5 pr-4 transition-colors hover:border-brand/40"
                >
                  <span
                    className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-brand text-[11px] font-bold tracking-wide text-brand-fg"
                    aria-hidden="true"
                  >
                    {p.initials}
                  </span>
                  <span className="text-[13.5px] font-medium">{p.name}</span>
                </Link>
              </li>
            ))}
          </ul>
        </Shell>
      </Band>

      <CtaBand
        title={`Keep ${platform.name}. Upgrade your client experience.`}
        secondary={{ href: '/platform', label: 'See the platform' }}
      />
    </>
  );
}
