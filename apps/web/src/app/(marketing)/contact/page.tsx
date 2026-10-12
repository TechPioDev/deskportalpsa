import type { Metadata } from 'next';
import Link from 'next/link';
import { Mail, Clock, ShieldCheck } from 'lucide-react';
import { Container } from '@/components/marketing/ui';
import { Hero } from '@/components/marketing/Hero';
import { EnquiryForm } from '@/components/marketing/EnquiryForm';
import { CONTACT_EMAIL } from '@/components/marketing/MarketingFooter';

export const metadata: Metadata = {
  title: 'Contact us — PioManage',
  description: 'Ask a question about PioManage, or tell us what you need it to do.',
};

export default function ContactPage() {
  return (
    <>
      <Hero
        size="sm"
        eyebrow="Contact us"
        title={<>Tell us what you <span className="text-brand">need it to do.</span></>}
        lead="A real question gets a real answer. If it does not do the thing you need, we would rather say so than take you through a demo first."
      />

      <Container className="grid gap-8 pt-12 pb-10 lg:grid-cols-[1.4fr_1fr]">
        <EnquiryForm kind="contact" sourcePage="/contact" />

        <aside className="space-y-4">
          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
            <h2 className="flex items-center gap-2 text-[15px] font-semibold">
              <Mail size={16} className="text-brand" aria-hidden="true" /> Prefer email?
            </h2>
            <a
              href={`mailto:${CONTACT_EMAIL}`}
              className="mt-2 block text-sm text-brand underline underline-offset-2"
            >
              {CONTACT_EMAIL}
            </a>
          </div>

          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
            <h2 className="flex items-center gap-2 text-[15px] font-semibold">
              <Clock size={16} className="text-brand" aria-hidden="true" /> What happens next
            </h2>
            <p className="mt-2 text-sm leading-relaxed text-[var(--muted)]">
              We reply by email, usually within one business day. If a call would be quicker, you can{' '}
              <Link href="/book" className="text-brand underline underline-offset-2">
                book a meeting
              </Link>{' '}
              directly instead.
            </p>
          </div>

          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
            <h2 className="flex items-center gap-2 text-[15px] font-semibold">
              <ShieldCheck size={16} className="text-brand" aria-hidden="true" /> Your details
            </h2>
            <p className="mt-2 text-sm leading-relaxed text-[var(--muted)]">
              Used only to reply to you. Nothing is passed to a third party, and there is no mailing
              list to be added to.
            </p>
          </div>
        </aside>
      </Container>
    </>
  );
}
