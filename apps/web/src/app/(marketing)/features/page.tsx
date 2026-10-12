import type { Metadata } from 'next';
import Link from 'next/link';
import { ArrowUpRight } from 'lucide-react';
import { Shell, Band, PageHero, CtaBand } from '@/components/marketing/blocks';
import { Reveal } from '@/components/marketing/Reveal';
import { FEATURE_DOCS, featureHref } from '@/lib/featureDocs';

export const metadata: Metadata = {
  title: 'Features — PioManage',
  description:
    'Full documentation of every PioManage feature: two-way PSA sync, conversation threads, time tracking, client logins, the client control panel, users and access management, PSA connections, attachments, analytics, and security.',
  alternates: { canonical: '/features' },
};

export default function FeaturesIndexPage() {
  return (
    <>
      <PageHero
        eyebrow="Features"
        title="Everything the platform does, documented."
        lead="One page per feature, describing this build — what it does, how it works, and where the boundaries are. When the product changes, these documents change with it."
      />

      <Band>
        <Shell>
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {FEATURE_DOCS.map((doc, i) => (
              <Reveal key={doc.slug} delay={(i % 3) * 70}>
                <Link
                  href={featureHref(doc)}
                  className="dp-lift group flex h-full flex-col rounded-2xl border border-[var(--border)] bg-[var(--surface)] p-5"
                >
                  <span className="mb-3 flex h-10 w-10 items-center justify-center rounded-xl bg-brand-tint text-brand-deep transition-colors group-hover:bg-brand group-hover:text-brand-fg dark:bg-brand/25 dark:text-brand-soft">
                    <doc.icon size={18} aria-hidden="true" />
                  </span>
                  <h2 className="text-[15px] font-semibold">{doc.name}</h2>
                  <p className="mt-1 text-[13px] font-medium text-brand-mid">{doc.tagline}</p>
                  <p className="mt-2 flex-1 text-[13.5px] leading-relaxed text-[var(--muted)]">{doc.summary}</p>
                  <span className="mt-4 inline-flex items-center gap-1 text-[13px] font-medium text-brand-deep dark:text-brand-soft">
                    Read the full document
                    <ArrowUpRight size={14} aria-hidden="true" className="transition-transform group-hover:translate-x-0.5" />
                  </span>
                </Link>
              </Reveal>
            ))}
          </div>
        </Shell>
      </Band>

      <CtaBand />
    </>
  );
}
