import type { Metadata } from 'next';
import { Container } from '@/components/marketing/ui';
import { Hero } from '@/components/marketing/Hero';
import { SetPasswordCard } from '@/components/SetPasswordCard';

export const metadata: Metadata = { title: 'Your invitation — PioManage', robots: { index: false, follow: false } };

/** The page an invitation mail leads to. The token is in the address and nowhere else. */
export default async function InvitePage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;
  return (
    <>
      <Hero size="sm" eyebrow="Invitation" title={<>You&rsquo;re <span className="text-brand">invited.</span></>}
        lead="Choose a password and you are in: your tickets, your conversations, and the people who work on them." />
      <Container className="flex items-center justify-center py-14">
        <div className="w-full max-w-sm"><SetPasswordCard token={token} purpose="invite" /></div>
      </Container>
    </>
  );
}
