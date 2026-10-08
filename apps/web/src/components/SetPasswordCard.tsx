'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery } from '@tanstack/react-query';
import { KeyRound, CheckCircle2, AlertTriangle, LogIn } from 'lucide-react';
import { api } from '@/lib/api';
import type { InvitationLookup } from '@/lib/types';

const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);
const when = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: 'full', timeStyle: 'short' });

/**
 * The page at an invitation or a password-reset link: who it is for, and a password to choose.
 *
 * The person has no sign-in yet (or has lost it), so this runs without one. It shows no more than
 * the mail did; a link that is unknown, used, expired or revoked says so in words that give
 * nothing else away. On success it hands over to the real sign-in, with the address filled in.
 */
export function SetPasswordCard({ token, purpose }: { token: string; purpose: 'invite' | 'reset' }) {
  const lookup = useQuery({
    queryKey: ['invitation', purpose, token],
    queryFn: () => (purpose === 'invite' ? api.invitationLookup(token) : api.passwordResetLookup(token)),
    retry: false,
  });
  const [password, setPassword] = useState('');
  const [again, setAgain] = useState('');
  const accept = useMutation({
    mutationFn: () => (purpose === 'invite' ? api.invitationAccept(token, password) : api.passwordReset(token, password)),
  });
  const localMode = process.env.NEXT_PUBLIC_LOCAL_MODE === 'true';

  if (lookup.isLoading) return <Card><p className="text-sm text-[var(--muted)]">Checking your link…</p></Card>;
  if (lookup.isError || !lookup.data) {
    return (
      <Card>
        <h2 className="text-xl font-semibold tracking-tight">This link is not valid</h2>
        <p className="mt-2 text-sm text-[var(--muted)]">
          It may have been replaced by a newer one, or withdrawn. Ask your administrator for a new invitation, or{' '}
          <Link href="/reset-password" className="text-brand underline underline-offset-2">ask for a password reset</Link> if you already have a sign-in.
        </p>
      </Card>
    );
  }
  const info: InvitationLookup = lookup.data;
  const isInvite = info.purpose === 'Invite';

  if (accept.isSuccess) {
    return (
      <Card>
        <p className="inline-flex items-center gap-2 text-sm font-medium text-green-700 dark:text-green-400"><CheckCircle2 size={16} /> Your password is set.</p>
        <p className="mt-2 text-sm text-[var(--muted)]">Sign in with <span className="font-medium text-[var(--fg)]">{accept.data.email}</span> and the password you chose.</p>
        <a href={localMode ? '/dashboard' : accept.data.signInPath}
          className="mt-6 inline-flex w-full items-center justify-center gap-2 rounded-lg bg-brand px-4 py-2.5 font-medium text-brand-fg hover:opacity-90">
          <LogIn size={16} aria-hidden="true" /> Sign in
        </a>
      </Card>
    );
  }

  if (info.state !== 'Open' || !info.canBeAccepted) {
    const why = info.state === 'Used'
      ? 'This link has already been used. Sign in with the password you chose, or ask for a password reset.'
      : info.state === 'Expired'
        ? `This link expired on ${when(info.expiresAt)}. Ask your administrator for a new invitation.`
        : 'This link cannot be used right now. Ask your administrator.';
    return (
      <Card>
        <h2 className="text-xl font-semibold tracking-tight">{info.state === 'Used' ? 'Already used' : info.state === 'Expired' ? 'This link has expired' : 'Not available'}</h2>
        <p className="mt-2 text-sm text-[var(--muted)]">{why}</p>
        {info.state === 'Used' && (
          <Link href="/login" className="mt-6 inline-flex w-full items-center justify-center gap-2 rounded-lg bg-brand px-4 py-2.5 font-medium text-brand-fg hover:opacity-90">
            <LogIn size={16} aria-hidden="true" /> Go to sign in
          </Link>
        )}
      </Card>
    );
  }

  const tooShort = password.length > 0 && password.length < 12;
  const mismatch = again.length > 0 && again !== password;
  const canSubmit = password.length >= 12 && again === password && !accept.isPending;

  return (
    <Card>
      <h2 className="text-xl font-semibold tracking-tight">{isInvite ? `Welcome, ${info.displayName.split(' ')[0]}` : 'Choose a new password'}</h2>
      <p className="mt-1.5 text-sm text-[var(--muted)]">
        {isInvite
          ? <>{info.organizationName} has invited you to PioManage. Choose a password for <span className="font-medium text-[var(--fg)]">{info.email}</span>.</>
          : <>For <span className="font-medium text-[var(--fg)]">{info.email}</span> at {info.organizationName}.</>}
      </p>
      <form className="mt-5 space-y-3" onSubmit={(e) => { e.preventDefault(); if (canSubmit) accept.mutate(); }}>
        <label className="block text-xs font-medium text-[var(--muted)]">
          Password
          <input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} minLength={12} required
            className="mt-1 w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm text-[var(--fg)] outline-none focus:border-brand" />
        </label>
        <label className="block text-xs font-medium text-[var(--muted)]">
          The same again
          <input type="password" autoComplete="new-password" value={again} onChange={(e) => setAgain(e.target.value)} required
            className="mt-1 w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm text-[var(--fg)] outline-none focus:border-brand" />
        </label>
        <p className="text-xs text-[var(--faint)]">
          {tooShort ? 'At least 12 characters.' : mismatch ? 'The two do not match yet.' : 'At least 12 characters. A sentence you will remember works well.'}
        </p>
        {accept.isError && <p role="alert" className="inline-flex items-start gap-1.5 text-xs text-red-600 dark:text-red-400"><AlertTriangle size={13} className="mt-0.5 shrink-0" /> {message(accept.error, 'That could not be saved.')}</p>}
        <button type="submit" disabled={!canSubmit}
          className="inline-flex w-full items-center justify-center gap-2 rounded-lg bg-brand px-4 py-2.5 font-medium text-brand-fg hover:opacity-90 disabled:opacity-40">
          <KeyRound size={16} aria-hidden="true" /> {accept.isPending ? 'Saving…' : isInvite ? 'Set my password and sign in' : 'Save the new password'}
        </button>
      </form>
      <p className="mt-4 text-center text-xs text-[var(--muted)]">This link works until {when(info.expiresAt)}.</p>
    </Card>
  );
}

function Card({ children }: { children: React.ReactNode }) {
  return <div className="rounded-2xl border border-[var(--border)] bg-[var(--surface)] p-8">{children}</div>;
}

/** The form that asks for a reset link. It answers the same whatever the address, on purpose. */
export function RequestResetCard() {
  const [email, setEmail] = useState('');
  const ask = useMutation({ mutationFn: () => api.passwordResetRequest(email.trim()) });
  if (ask.isSuccess) {
    return (
      <Card>
        <p className="inline-flex items-center gap-2 text-sm font-medium text-green-700 dark:text-green-400"><CheckCircle2 size={16} /> If that address has a sign-in, a link is on its way.</p>
        <p className="mt-2 text-sm text-[var(--muted)]">It works for an hour. Nothing has changed until you use it.</p>
      </Card>
    );
  }
  return (
    <Card>
      <h2 className="text-xl font-semibold tracking-tight">Forgot your password?</h2>
      <p className="mt-1.5 text-sm text-[var(--muted)]">Enter the e-mail address you sign in with, and we will send you a link to choose a new one.</p>
      <form className="mt-5 space-y-3" onSubmit={(e) => { e.preventDefault(); if (email.includes('@')) ask.mutate(); }}>
        <label className="block text-xs font-medium text-[var(--muted)]">
          E-mail address
          <input type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} required
            className="mt-1 w-full rounded-lg border border-[var(--border)] bg-[var(--bg)] px-3 py-2 text-sm text-[var(--fg)] outline-none focus:border-brand" />
        </label>
        {ask.isError && <p role="alert" className="text-xs text-red-600 dark:text-red-400">{message(ask.error, 'That could not be sent.')}</p>}
        <button type="submit" disabled={!email.includes('@') || ask.isPending}
          className="inline-flex w-full items-center justify-center gap-2 rounded-lg bg-brand px-4 py-2.5 font-medium text-brand-fg hover:opacity-90 disabled:opacity-40">
          {ask.isPending ? 'Sending…' : 'Send me a link'}
        </button>
      </form>
    </Card>
  );
}
