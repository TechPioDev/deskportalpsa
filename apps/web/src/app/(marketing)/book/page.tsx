import type { Metadata } from 'next';
import { Video, Check } from 'lucide-react';
import { Container } from '@/components/marketing/ui';
import { Hero } from '@/components/marketing/Hero';
import { EnquiryForm } from '@/components/marketing/EnquiryForm';

export const metadata: Metadata = {
  title: 'Book a meeting — PioManage',
  description: 'Half an hour against your own PSA, with the people who built it.',
};

const AGENDA = [
  'What your desk looks like now — boards, statuses, and where clients currently ask for updates.',
  'The portal running against a real PSA, not slideware.',
  'How your statuses and priorities would map, and what that means day to day.',
  'Where it would sit: your own server, alongside anything already running there.',
  'What it does not do yet, so nothing is a surprise later.',
];

export default function BookPage() {
  return (
    <>
      <Hero
        size="sm"
        eyebrow="Book a meeting"
        title={<>Thirty minutes, against <span className="text-brand">your own PSA.</span></>}
        lead="Not a scripted demo. Bring the PSA you run and the problem you are actually trying to solve."
      />

      <Container className="grid gap-8 pt-12 pb-10 lg:grid-cols-[1.4fr_1fr]">
        <EnquiryForm kind="meeting" sourcePage="/book" />

        <aside className="space-y-4">
          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
            <h2 className="flex items-center gap-2 text-[15px] font-semibold">
              <Video size={16} className="text-brand" aria-hidden="true" /> What we will cover
            </h2>
            <ul className="mt-3 space-y-2.5">
              {AGENDA.map((item) => (
                <li key={item} className="flex gap-2 text-sm leading-relaxed text-[var(--muted)]">
                  <Check size={15} className="mt-0.5 shrink-0 text-brand" aria-hidden="true" />
                  {item}
                </li>
              ))}
            </ul>
          </div>

          <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-5">
            <h2 className="text-[15px] font-semibold">Nothing to install first</h2>
            <p className="mt-2 text-sm leading-relaxed text-[var(--muted)]">
              You do not need to deploy anything or hand over credentials to have this conversation.
              If you want to see your own tickets flowing, read-only access to a sandbox or a single
              board is enough — and that is a decision for later, not for the call.
            </p>
          </div>
        </aside>
      </Container>
    </>
  );
}
