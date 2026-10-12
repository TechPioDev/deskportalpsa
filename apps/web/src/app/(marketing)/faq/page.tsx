import Link from 'next/link';
import type { Metadata } from 'next';
import { ChevronDown } from 'lucide-react';
import { Container, CtaBand } from '@/components/marketing/ui';
import { Hero } from '@/components/marketing/Hero';

export const metadata: Metadata = {
  title: 'FAQ — PioManage',
  description:
    'Common questions about how PioManage works with your PSA — sync, supported platforms, hosting, security and access.',
};

const GROUPS: { heading: string; items: { q: string; a: string }[] }[] = [
  {
    heading: 'How it works',
    items: [
      {
        q: 'Does PioManage replace my PSA?',
        a: 'No. Your PSA stays the system of record and your technicians keep working in it. PioManage is a client-facing surface on top, kept current by two-way sync. Nothing is migrated out of your PSA.',
      },
      {
        q: 'Which PSA platforms are supported?',
        a: 'PioManage is built around the PSA ecosystem MSPs actually run: ConnectWise PSA, Autotask PSA, HaloPSA, Kaseya BMS, Syncro, SuperOps, N-able MSP Manager and Atera. The connector layer is shared, so every platform reuses the same sync, mapping and client experience. Tell us which PSA you run and we will walk you through it on a call.',
      },
      {
        q: 'What syncs, and in which direction?',
        a: 'Both directions: client replies and technician notes, attachments, time entries, status, priority and assignment. A note added in the PSA appears in the portal, and a client reply lands on the ticket your technician is already working on.',
      },
      {
        q: 'Will clients see our internal notes?',
        a: 'No. Internal notes are recognised as internal and are never shown on the client-facing thread — they are filtered on the server, not hidden in the interface.',
      },
      {
        q: 'How quickly do changes appear?',
        a: 'A background worker syncs on an interval you set, five minutes by default, and you can trigger a sync at any time. The interval is configurable because Autotask meters API requests per hour.',
      },
      {
        q: 'What if a ticket fails to reach the PSA?',
        a: 'It is kept, listed as unsynced with the reason the PSA gave, and can be pushed again once the cause is fixed. A rejected ticket is never silently discarded.',
      },
    ],
  },
  {
    heading: 'Setup and hosting',
    items: [
      {
        q: 'Where does it run?',
        a: 'On your own infrastructure. The whole stack — portal, API, background worker, database and sign-in — runs from a single Docker host. One modest virtual server is enough to start.',
      },
      {
        q: 'Can it run alongside our existing websites?',
        a: 'Yes. If the server already serves other sites, the portal publishes on local ports and your existing web server proxies to it. Your other sites are untouched.',
      },
      {
        q: 'How long does setup take?',
        a: 'The stack itself comes up in minutes. The real work is deciding how your statuses, priorities and boards map to the portal, and that depends on how your desk is organised. We do it with you on a call.',
      },
      {
        q: 'Can we use more than one PSA at once?',
        a: 'Yes. Connections are configured separately, so two different PSA tenants can run side by side, each with its own mappings and defaults.',
      },
    ],
  },
  {
    heading: 'Access and security',
    items: [
      {
        q: 'How do people sign in?',
        a: 'Through Keycloak, an identity provider that runs as part of the stack, so password policy and multi-factor are governed in one place. Accounts are invited by email and bind to that login the first time the person signs in.',
      },
      {
        q: 'Can one client see another client’s tickets?',
        a: 'No. Isolation is enforced at the database level on every query, not by a filter that a developer has to remember to apply, and it is covered by tests written specifically to try to break it.',
      },
      {
        q: 'Where are our PSA API credentials kept?',
        a: 'In a secrets vault, never in the application database, and they are never returned by the API or shown again in the interface after being entered.',
      },
      {
        q: 'Is there an audit trail?',
        a: 'Yes. Administrative actions are written to an append-only audit log with the actor and a correlation id. Log entries cannot be edited or deleted, including by an administrator.',
      },
    ],
  },
  {
    heading: 'Commercials',
    items: [
      {
        q: 'What does it cost?',
        a: 'It depends on how many client companies and technicians you need, and whether you host it yourself or want help doing so. Book a meeting and we will give you a straight number rather than a page of tiers.',
      },
      {
        q: 'Can we try it against our own PSA first?',
        a: 'Yes — that is the sensible way to evaluate it. Read-only credentials against a sandbox or a single board are enough to see your real tickets flowing through.',
      },
    ],
  },
];

export default function FaqPage() {
  return (
    <>
      <Hero
        eyebrow="FAQ"
        title={<>Questions we are <span className="text-brand">actually asked.</span></>}
        lead="Straight answers about which PSAs are supported, how the sync works, what it costs to run, and who can see what. If yours is not here, ask it."
        actions={
          <Link href="/contact" className="rounded-lg bg-brand px-5 py-3 text-sm font-medium text-brand-fg transition-transform hover:-translate-y-0.5 hover:opacity-90">
            Ask your question
          </Link>
        }
      />

      <Container className="pt-12 pb-4">
        {GROUPS.map((group) => (
          <section key={group.heading} className="mb-10">
            <h2 className="mb-4 text-xs font-semibold uppercase tracking-widest text-[var(--faint)]">
              {group.heading}
            </h2>
            <div className="divide-y divide-[var(--border)] overflow-hidden rounded-xl border border-[var(--border)] bg-[var(--surface)]">
              {group.items.map(({ q, a }) => (
                <details key={q} className="group">
                  <summary className="flex cursor-pointer list-none items-center justify-between gap-4 px-5 py-4 text-[15px] font-medium hover:bg-[var(--bg)]">
                    {q}
                    <ChevronDown
                      size={16}
                      aria-hidden="true"
                      className="shrink-0 text-[var(--faint)] transition-transform group-open:rotate-180"
                    />
                  </summary>
                  <p className="px-5 pb-5 text-sm leading-relaxed text-[var(--muted)]">{a}</p>
                </details>
              ))}
            </div>
          </section>
        ))}
      </Container>

      <CtaBand
        title="Still deciding?"
        lead="Bring the question that is actually blocking you — we will answer it on a call rather than in a brochure."
      />
    </>
  );
}
