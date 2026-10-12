import Link from 'next/link';
import type { Metadata } from 'next';
import { Section, Card, CtaBand, Container } from '@/components/marketing/ui';
import { Hero } from '@/components/marketing/Hero';

export const metadata: Metadata = {
  title: 'About us — PioManage',
  description:
    'PioManage is built by TechPIO Services LLP for managed service providers, on top of the PSA they already run.',
};

export default function AboutPage() {
  return (
    <>
      <Hero
        eyebrow="About us"
        title={<>Built by people who <span className="text-brand">work in a PSA</span> every day.</>}
        lead="PioManage is made by TechPio. It exists because the gap between a managed service provider and its clients is usually filled with email, and email loses things."
        actions={
          <>
            <Link href="/book" className="rounded-lg bg-brand px-5 py-3 text-sm font-medium text-brand-fg transition-transform hover:-translate-y-0.5 hover:opacity-90">
              Talk to us
            </Link>
            <Link href="/faq" className="rounded-lg border border-[var(--border)] bg-[var(--surface)] px-5 py-3 text-sm font-medium transition-transform hover:-translate-y-0.5 hover:bg-[var(--bg)]">
              Read the FAQ
            </Link>
          </>
        }
      />

      <Section title="Why we built it">
        <div className="max-w-3xl space-y-4 text-[15px] leading-relaxed text-[var(--muted)]">
          <p>
            Every managed service provider already has a system of record. It holds the boards, the
            SLAs, the time entries and the history. What it rarely has is a front door a client can
            comfortably walk through.
          </p>
          <p>
            The usual answers are unsatisfying. Give clients a PSA login and they meet an interface
            built for technicians. Leave them on email and nobody can answer &ldquo;what is happening
            with my ticket?&rdquo; without going and looking. Move to a product that replaces the PSA
            and you have thrown away years of history and process.
          </p>
          <p>
            So PioManage takes the opposite position: your PSA stays exactly as it is and remains
            the source of truth. The portal is a clear, fast surface on top of it for the people who
            are not technicians — and it keeps itself honest by syncing continuously in both
            directions.
          </p>
        </div>
      </Section>

      <Section title="How we approach it" className="border-y border-[var(--border)] bg-[var(--surface)]">
        <div className="grid gap-4 sm:grid-cols-3">
          <Card title="Your PSA is not the enemy">
            We are not trying to replace your PSA, or to become a second place your team has to check.
            Whatever the PSA says, wins.
          </Card>
          <Card title="Self-hosted by default">
            You run it, on infrastructure you control, with your own identity provider. Your clients&apos;
            data does not need to sit with a vendor for the product to work.
          </Card>
          <Card title="Honest about limits">
            Providers differ. Where one supports something the other does not, the portal degrades
            gracefully and says so, rather than pretending parity and failing quietly.
          </Card>
        </div>
      </Section>

      <Section title="Who it is for">
        <div className="grid gap-4 sm:grid-cols-2">
          <Card title="Managed service providers">
            Whichever PSA you run, with clients who keep asking for status updates and a desk that
            would rather be fixing things than reporting on them.
          </Card>
          <Card title="Internal IT teams">
            Running a PSA for an organisation with several sites or departments, who need each one to
            see its own tickets without seeing everyone else&apos;s.
          </Card>
        </div>
      </Section>

      <Container className="pb-10">
        <div className="rounded-xl border border-[var(--border)] bg-[var(--surface)] p-6">
          <h2 className="text-[15px] font-semibold">A note on where the product is</h2>
          <p className="mt-2 max-w-3xl text-sm leading-relaxed text-[var(--muted)]">
            PioManage is built around the PSA ecosystem MSPs actually run, with two-way sync of
            conversation, attachments and time. Every platform reuses the same connector layer, so
            the client experience is the same whichever PSA sits behind it. If you have a specific
            requirement, ask us on a call and we will walk you through exactly how it works for
            your environment.
          </p>
        </div>
      </Container>

      <CtaBand
        title="Talk to the people who built it."
        lead="No sales team to get past — you will be speaking to someone who knows how the sync actually works."
      />
    </>
  );
}
