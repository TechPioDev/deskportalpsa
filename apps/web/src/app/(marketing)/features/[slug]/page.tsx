import type { Metadata } from 'next';
import Link from 'next/link';
import { notFound } from 'next/navigation';
import { ArrowLeft, ArrowRight, Check } from 'lucide-react';
import { Shell, Band, PageHero, CtaBand } from '@/components/marketing/blocks';
import { Reveal } from '@/components/marketing/Reveal';
import { FEATURE_DOCS, featureHref, findFeature } from '@/lib/featureDocs';

type Params = { params: Promise<{ slug: string }> };

export function generateStaticParams() {
  return FEATURE_DOCS.map((d) => ({ slug: d.slug }));
}

export async function generateMetadata({ params }: Params): Promise<Metadata> {
  const doc = findFeature((await params).slug);
  if (!doc) return {};
  return {
    title: `${doc.name} — PioManage`,
    description: doc.summary,
    alternates: { canonical: featureHref(doc) },
  };
}

export default async function FeatureDocPage({ params }: Params) {
  const doc = findFeature((await params).slug);
  if (!doc) notFound();

  const index = FEATURE_DOCS.indexOf(doc);
  const prev = FEATURE_DOCS[index - 1];
  const next = FEATURE_DOCS[index + 1];

  return (
    <>
      <PageHero eyebrow="Feature documentation" title={doc.name} lead={doc.tagline} />

      <Band>
        <Shell className="max-w-4xl">
          <Reveal>
            <p className="text-[15.5px] leading-relaxed text-[var(--muted)]">{doc.summary}</p>
          </Reveal>

          <div className="mt-10 space-y-10">
            {doc.sections.map((section, i) => (
              <Reveal key={section.heading} delay={Math.min(i, 3) * 60}>
                <section>
                  <h2 className="text-lg font-semibold tracking-tight">{section.heading}</h2>
                  <p className="mt-2 text-[14.5px] leading-relaxed text-[var(--muted)]">{section.body}</p>
                  {section.points && (
                    <ul className="mt-3 space-y-2">
                      {section.points.map((point) => (
                        <li key={point} className="flex items-start gap-2.5 text-[14px] leading-relaxed text-[var(--muted)]">
                          <Check size={15} aria-hidden="true" className="mt-1 shrink-0 text-brand-mid" />
                          {point}
                        </li>
                      ))}
                    </ul>
                  )}
                </section>
              </Reveal>
            ))}
          </div>

          {/* Sequential reading path: the docs form one continuous walk through the product. */}
          <nav aria-label="More features" className="mt-14 flex flex-wrap items-center justify-between gap-3 border-t border-[var(--border)] pt-6">
            {prev ? (
              <Link href={featureHref(prev)} className="group inline-flex items-center gap-1.5 text-sm font-medium text-brand-deep hover:underline dark:text-brand-soft">
                <ArrowLeft size={15} aria-hidden="true" className="transition-transform group-hover:-translate-x-0.5" />
                {prev.name}
              </Link>
            ) : <span />}
            <Link href="/features" className="text-sm text-[var(--muted)] hover:text-[var(--fg)]">All features</Link>
            {next ? (
              <Link href={featureHref(next)} className="group inline-flex items-center gap-1.5 text-sm font-medium text-brand-deep hover:underline dark:text-brand-soft">
                {next.name}
                <ArrowRight size={15} aria-hidden="true" className="transition-transform group-hover:translate-x-0.5" />
              </Link>
            ) : <span />}
          </nav>
        </Shell>
      </Band>

      <CtaBand />
    </>
  );
}
