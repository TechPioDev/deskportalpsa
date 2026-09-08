import type { Metadata } from 'next';
import Link from 'next/link';
import { Hero } from '@/components/marketing/Hero';
import { LegalDoc, LegalList, type LegalSection } from '@/components/marketing/Legal';
import { CONTACT_EMAIL } from '@/components/marketing/MarketingFooter';

export const metadata: Metadata = {
  title: 'Terms of service — Desk Portal',
  description: 'The terms on which Desk Portal is provided: what we do, what you agree to, and who owns what.',
};

const SECTIONS: LegalSection[] = [
  {
    id: 'agreement',
    heading: 'This agreement',
    body: (
      <>
        <p>
          These terms govern your use of the piomanage.com website and the Desk Portal software
          provided by TechPio (&ldquo;we&rdquo;, &ldquo;us&rdquo;). By using either, you accept them.
        </p>
        <p>
          Where you have signed a separate written agreement or order form with us, that document
          takes precedence over these terms wherever the two conflict.
        </p>
      </>
    ),
  },
  {
    id: 'service',
    heading: 'What the service is',
    body: (
      <>
        <p>
          Desk Portal is a client-facing ticket portal that synchronises with a professional services
          automation system you already operate.
        </p>
        <p>
          Your PSA remains the system of record. The portal reads from it and writes back to it. We
          do not migrate your data out of your PSA, and the portal is not a backup of it.
        </p>
      </>
    ),
  },
  {
    id: 'your-account',
    heading: 'Your account and your people',
    body: (
      <LegalList
        items={[
          'You are responsible for the accounts you create and for what the people holding them do.',
          'Accounts are personal. Sharing one set of credentials between several people undermines the audit trail and is not permitted.',
          'Tell us promptly if you believe an account has been compromised.',
          'You must be authorised to enter into these terms on behalf of the organisation you represent.',
        ]}
      />
    ),
  },
  {
    id: 'psa-credentials',
    heading: 'PSA credentials and your data',
    body: (
      <>
        <p>
          To do its job the portal needs API credentials for your PSA. By supplying them you confirm
          you are entitled to grant that access, and that doing so does not breach your agreement
          with your PSA vendor.
        </p>
        <p>
          Your data stays yours. We claim no ownership over your tickets, your clients&rsquo;
          information, or anything else you put into the product. We use it only to provide the
          service to you, and to support you when you ask.
        </p>
      </>
    ),
  },
  {
    id: 'acceptable-use',
    heading: 'Acceptable use',
    body: (
      <>
        <p>You agree not to:</p>
        <LegalList
          items={[
            'Use the service unlawfully, or to store or transmit unlawful material.',
            'Attempt to access another customer’s data, or to circumvent the isolation between tenants.',
            'Probe, scan or load-test the service without our written permission.',
            'Resell or provide the service to a third party unless your agreement with us permits it.',
            'Reverse engineer the software except where the law expressly allows it.',
          ]}
        />
      </>
    ),
  },
  {
    id: 'availability',
    heading: 'Availability and support',
    body: (
      <>
        <p>
          Where you host Desk Portal yourself, availability is in your hands — we do not control your
          infrastructure and make no commitment about it.
        </p>
        <p>
          Where we host it for you, we aim for continuous availability but do not commit to a service
          level unless one is agreed in writing. Support is provided as set out in your agreement, or
          otherwise on a reasonable-endeavours basis at{' '}
          <a className="text-brand underline underline-offset-2" href={`mailto:${CONTACT_EMAIL}`}>
            {CONTACT_EMAIL}
          </a>
          .
        </p>
      </>
    ),
  },
  {
    id: 'fees',
    heading: 'Fees',
    body: (
      <p>
        Fees, billing period and payment terms are those set out in your order form or written
        agreement with us. Unless that document says otherwise, invoices are payable within{' '}
        30 days and fees are exclusive of any applicable taxes.
      </p>
    ),
  },
  {
    id: 'ownership',
    heading: 'Ownership',
    body: (
      <p>
        We own the Desk Portal software, its design and its documentation, together with all
        intellectual property in them. You are granted a non-exclusive, non-transferable right to use
        it for the term of your agreement. You own your data. Any feedback you give us we may use
        freely to improve the product, without obligation to you.
      </p>
    ),
  },
  {
    id: 'confidentiality',
    heading: 'Confidentiality',
    body: (
      <p>
        Each of us may learn confidential information about the other. Both of us agree to protect it
        with at least the care we apply to our own, to use it only for the purposes of this
        agreement, and not to disclose it — except where the law compels disclosure, and then only
        after telling the other party if we are permitted to.
      </p>
    ),
  },
  {
    id: 'warranties',
    heading: 'Warranties and disclaimers',
    body: (
      <>
        <p>
          We warrant that we will provide the service with reasonable skill and care. Beyond that, and
          to the fullest extent the law permits, the service is provided as it stands, without further
          warranty of any kind.
        </p>
        <p>
          In particular we do not warrant that the service will be uninterrupted or error-free, nor
          that it will be compatible with future changes a PSA vendor makes to their own API.
        </p>
      </>
    ),
  },
  {
    id: 'liability',
    heading: 'Limitation of liability',
    body: (
      <>
        <p>
          Nothing here limits liability for death or personal injury caused by negligence, for fraud,
          or for anything else that cannot lawfully be limited.
        </p>
        <p>
          Subject to that, neither party is liable for indirect or consequential loss, loss of profit,
          or loss of anticipated savings. Each party&rsquo;s total liability arising out of this
          agreement is limited to the fees paid in the 12 months before the claim.
        </p>
      </>
    ),
  },
  {
    id: 'term',
    heading: 'Term and termination',
    body: (
      <>
        <p>
          These terms apply for as long as you use the service. Either party may terminate for
          material breach that is not put right within 30 days of written notice.
        </p>
        <p>
          On termination your right to use the software ends. Because your PSA remains the system of
          record, your ticket history stays where it has always been — in your PSA. On request we will
          delete the data held in a portal we host for you.
        </p>
      </>
    ),
  },
  {
    id: 'governing-law',
    heading: 'Governing law',
    body: (
      <p>
        These terms are governed by the laws of India, and the courts at Mohali, Punjab have
        exclusive jurisdiction over any dispute arising from them.
      </p>
    ),
  },
  {
    id: 'changes',
    heading: 'Changes',
    body: (
      <p>
        We may update these terms as the product changes. The date at the top of this page shows when
        they were last revised, and we will give customers reasonable notice of any change that
        materially reduces what they receive.
      </p>
    ),
  },
];

export default function TermsPage() {
  return (
    <>
      <Hero
        size="sm"
        eyebrow="Terms of service"
        title={
          <>
            The terms, <span className="text-brand">in plain words.</span>
          </>
        }
        lead="What the service is, what you agree to, and who owns what. Written to be understood rather than to be impressive."
      />
      <LegalDoc
        updated="14 August 2026"
        intro={
          <>
            <p>
              These are the terms on which Desk Portal is provided. If you have a signed agreement
              with us, that document wins wherever it differs from this page.
            </p>
            <p>
              See also our{' '}
              <Link className="text-brand underline underline-offset-2" href="/privacy">
                privacy policy
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
