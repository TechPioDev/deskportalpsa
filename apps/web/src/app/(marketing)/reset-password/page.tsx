import type { Metadata } from 'next';
import { Container } from '@/components/marketing/ui';
import { Hero } from '@/components/marketing/Hero';
import { RequestResetCard } from '@/components/SetPasswordCard';

export const metadata: Metadata = { title: 'Reset your password — PioManage', robots: { index: false, follow: false } };

export default function ResetPasswordRequestPage() {
  return (
    <>
      <Hero size="sm" eyebrow="Password" title={<>A new <span className="text-brand">password.</span></>}
        lead="We send a one-time link to the address you sign in with. It works for an hour." />
      <Container className="flex items-center justify-center py-14">
        <div className="w-full max-w-sm"><RequestResetCard /></div>
      </Container>
    </>
  );
}
