import type { Metadata } from 'next';
import Link from 'next/link';
import { Hero } from '@/components/marketing/Hero';
import { LegalDoc, LegalList, Fill, type LegalSection } from '@/components/marketing/Legal';
import { CONTACT_EMAIL } from '@/components/marketing/MarketingFooter';

export const metadata: Metadata = {
  title: 'Privacy policy — Desk Portal',
  description:
    'What Desk Portal collects, why, where it is held, and how to have it corrected or removed.',
};

const SECTIONS: LegalSection[] = [
  {
    id: 'who-we-are',
    heading: 'Who we are',
    body: (
      <>
        <p>
          Desk Portal is operated by TechPio (&ldquo;we&rdquo;, &ldquo;us&rdquo;), registered at{' '}
          <Fill>your registered address</Fill>. For anything in this policy, write to{' '}
          <a className="text-brand underline underline-offset-2" href={`mailto:${CONTACT_EMAIL}`}>
            {CONTACT_EMAIL}
          </a>
          .
        </p>
        <p>
          This policy covers the piomanage.com website and the Desk Portal service. Where a customer
          hosts Desk Portal on their own infrastructure, they are the controller of the data inside
          it and their own privacy notice governs it; this policy then covers only our website and
          any support we provide.
        </p>
      </>
    ),
  },
  {
    id: 'what-we-collect',
    heading: 'What we collect',
    body: (
      <>
        <p>We collect only what a specific purpose requires.</p>
        <LegalList
          items={[
            <>
              <strong className="text-[var(--fg)]">Enquiries.</strong> When you use the contact or
              booking form: your name, email address, and optionally company, phone number, the times
              you suggested, the page you sent it from, and whatever you wrote in the message.
            </>,
            <>
              <strong className="text-[var(--fg)]">Account details.</strong> If you have a portal
              account: your name, email address, and the identifier your identity provider issues.
              Sign-in itself is handled by that provider — we never see or store your password.
            </>,
            <>
              <strong className="text-[var(--fg)]">Service data.</strong> For customers using the
              product: tickets, messages, attachments and time entries synchronised with your PSA.
              Your PSA remains the system of record; the portal mirrors it.
            </>,
            <>
              <strong className="text-[var(--fg)]">Technical records.</strong> Web server logs (IP
              address, browser user agent, page requested, timestamp) and an audit log of
              administrative actions taken inside the product.
            </>,
          ]}
        />
      </>
    ),
  },
  {
    id: 'cookies',
    heading: 'Cookies',
    body: (
      <>
        <p>
          We use session cookies only, and only once you sign in. They hold your session so the
          application knows who you are between pages, and they are marked so that browser scripts
          cannot read them.
        </p>
        <p>
          We use no analytics, advertising, profiling or third-party tracking cookies of any kind.
          Nothing on this site reports your visit to anyone else, which is why you are not asked to
          dismiss a consent banner.
        </p>
      </>
    ),
  },
  {
    id: 'why',
    heading: 'Why we use it',
    body: (
      <LegalList
        items={[
          'To answer your enquiry or arrange the meeting you asked for.',
          'To provide the service: showing you your tickets, and synchronising them with your PSA.',
          'To keep the service secure and accountable — the audit log records who changed what.',
          'To meet legal or accounting obligations where they apply.',
        ]}
      />
    ),
  },
  {
    id: 'sharing',
    heading: 'Who else sees it',
    body: (
      <>
        <p>
          We do not sell your data, and we do not share it for advertising. It is disclosed only in
          these circumstances:
        </p>
        <LegalList
          items={[
            <>
              <strong className="text-[var(--fg)]">Your PSA provider.</strong> Datto Autotask or
              ConnectWise receive the ticket data you direct the portal to send them, under your own
              agreement with them.
            </>,
            <>
              <strong className="text-[var(--fg)]">Our hosting provider.</strong> The servers running
              our website are operated by Hostinger International Limited, who hold the data at
              rest on our behalf.
            </>,
            'Where the law requires it, or to establish or defend a legal claim.',
          ]}
        />
      </>
    ),
  },
  {
    id: 'where',
    heading: 'Where it is held',
    body: (
      <p>
        Our website and its data are hosted in the United States. Customers who run
        Desk Portal themselves choose where their own instance lives; the product is designed to be
        self-hosted precisely so that client data need not sit with a vendor.
      </p>
    ),
  },
  {
    id: 'retention',
    heading: 'How long we keep it',
    body: (
      <LegalList
        items={[
          <>
            Enquiries: kept until we delete them. We review them periodically and remove those
            we no longer need; ask us and we will delete yours.
          </>,
          <>
            Web server logs: 14 days, after which they are rotated away automatically.
          </>,
          <>
            Audit records: kept for the life of the account. These are append-only and cannot be
            edited, which is the point of them.
          </>,
          'Account and service data: for as long as the account is active, and then as set out in your contract with us.',
        ]}
      />
    ),
  },
  {
    id: 'security',
    heading: 'How we protect it',
    body: (
      <LegalList
        items={[
          'Traffic is encrypted in transit with TLS.',
          'PSA API credentials are held in a secrets vault, never in the application database, and are never displayed again after being entered.',
          'Each client company is isolated at the database level, so one customer cannot query another’s data.',
          'Access inside the product is governed by role, and administrative actions are written to an append-only audit log.',
          'Sign-in is delegated to an identity provider, so password policy and multi-factor are enforced centrally.',
        ]}
      />
    ),
  },
  {
    id: 'your-rights',
    heading: 'Your rights',
    body: (
      <>
        <p>
          Depending on where you live, you may have the right to ask for a copy of the personal data
          we hold about you, to have it corrected or deleted, to object to or restrict how we use it,
          and to receive it in a portable form. You may also complain to your local data protection
          authority.
        </p>
        <p>
          To exercise any of these, email{' '}
          <a className="text-brand underline underline-offset-2" href={`mailto:${CONTACT_EMAIL}`}>
            {CONTACT_EMAIL}
          </a>
          . We will respond within 30 days.
        </p>
      </>
    ),
  },
  {
    id: 'changes',
    heading: 'Changes to this policy',
    body: (
      <p>
        If we change this policy we will update the date at the top of this page. Where a change
        materially affects how we use your data, we will tell affected customers directly rather than
        relying on you to notice.
      </p>
    ),
  },
];

export default function PrivacyPage() {
  return (
    <>
      <Hero
        size="sm"
        eyebrow="Privacy policy"
        title={
          <>
            What we collect, and <span className="text-brand">what we don&rsquo;t.</span>
          </>
        }
        lead="No analytics, no advertising, no tracking. This page sets out exactly what is held, why, and how to have it removed."
      />
      <LegalDoc
        updated="14 August 2026"
        intro={
          <>
            <p>
              This policy explains how we handle personal data on the piomanage.com website and in
              the Desk Portal service. It is written to be read, not to be survived — if anything
              here is unclear, ask us and we will explain it plainly.
            </p>
            <p>
              See also our{' '}
              <Link className="text-brand underline underline-offset-2" href="/terms">
                terms of service
              </Link>
              .
            </p>
          </>
        }
        sections={SECTIONS}
      />
    </>
  );
}
