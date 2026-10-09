import type { Metadata } from 'next';
import { Container } from '@/components/marketing/ui';
import { Hero } from '@/components/marketing/Hero';
import { SetPasswordCard } from '@/components/SetPasswordCard';

export const metadata: Metadata = { title: 'Choose a new password — PioManage', robots: { index: false, follow: false } };

export default async function ResetPasswordPage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;
  return (
    <>
      <Hero size="sm" eyebrow="Password" title={<>A new <span className="text-brand">password.</span></>}
        lead="Choose it here; it takes effect at once." />
      <Container className="flex items-center justify-center py-14">
        <div className="w-full max-w-sm"><SetPasswordCard token={token} purpose="reset" /></div>
      </Container>
    </>
  );
}
