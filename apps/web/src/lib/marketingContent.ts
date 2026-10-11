import {
  Ticket, RefreshCw, MessageSquare, Paperclip, CircleDot, Bell, Wrench, Boxes,
  ShieldCheck, Server, Lock, ScrollText, Fingerprint, Building2, Mail, PhoneOff, Gauge, Eye,
  Users, Building, Headphones, Network, type LucideIcon,
} from 'lucide-react';

/**
 * Copy shared by more than one public page.
 *
 * It lives here rather than in a page because the home page teases a subset of the same lists the
 * platform and security pages show in full. Two copies of a feature blurb become two different
 * feature blurbs the first time one of them is edited.
 *
 * Nothing in this file is a customer count, a percentage, a testimonial or a performance figure.
 * Every claim describes how this build works.
 */
export type Blurb = { icon: LucideIcon; title: string; body: string };

export const FEATURES: Blurb[] = [
  { icon: Ticket, title: 'Client ticketing', body: 'A simple way for clients to submit and track support requests, in language written for them.' },
  { icon: RefreshCw, title: 'Two-way sync', body: 'Client communication stays in step with your PSA, continuously and in both directions.' },
  { icon: MessageSquare, title: 'Conversations', body: 'Support communication stays attached to the request. Internal notes remain internal.' },
  { icon: Paperclip, title: 'File sharing', body: 'Clients share screenshots and documents that land on the request, not in an inbox.' },
  { icon: CircleDot, title: 'Status visibility', body: 'Clear progress on every request, so nobody has to ask where something stands.' },
  { icon: Bell, title: 'Notifications', body: 'Clients stay informed without a chain of forwarded emails.' },
  { icon: Wrench, title: 'Technician workflow', body: 'Technicians carry on in the PSA they already use. Nothing new to learn or watch.' },
  { icon: Boxes, title: 'Multi-PSA architecture', body: 'One client experience across different PSA platforms, built to add more.' },
];

/** The four the home page leads with — the rest are on the platform page. */
export const HOME_FEATURES = FEATURES.slice(0, 4);

export const BENEFITS: Blurb[] = [
  { icon: Mail, title: 'Fewer inbound emails', body: 'Requests arrive in one place, in a shape your desk can act on immediately.' },
  { icon: Eye, title: 'Clients can see for themselves', body: 'Progress and responses are visible without anyone being asked.' },
  { icon: Wrench, title: 'No workflow change', body: 'Your team keeps its PSA, its boards and its habits.' },
  { icon: ShieldCheck, title: 'Your system of record is safe', body: 'The portal never becomes a second version of the truth.' },
  { icon: PhoneOff, title: 'Less chasing', body: '“Any update?” is answered by the portal, not by a technician.' },
  { icon: Gauge, title: 'An experience clients notice', body: 'A professional support portal is a visible difference at renewal.' },
];

export const SECURITY: Blurb[] = [
  { icon: Building2, title: 'Multi-tenant architecture', body: 'Each client company is isolated from every other, enforced on every request.' },
  { icon: Lock, title: 'Role-based access', body: 'Administrators, managers, technicians and client users each see only their own view.' },
  { icon: Fingerprint, title: 'SSO and MFA', body: 'Sign-in runs through your identity provider, so existing policy applies unchanged.' },
  { icon: ScrollText, title: 'Audit logging', body: 'Administrative activity is recorded and cannot be quietly altered afterwards.' },
  { icon: ShieldCheck, title: 'Secure credentials', body: 'PSA credentials are held in a secrets vault, never in the application database.' },
  { icon: Server, title: 'Deploy where you choose', body: 'Self-host the platform on infrastructure you control.' },
];

export const USE_CASES: Blurb[] = [
  { icon: Network, title: 'MSPs', body: 'Deliver a professional client experience without changing your PSA.' },
  { icon: Building, title: 'Growing IT service providers', body: 'Standardise client communication across every customer you serve.' },
  { icon: Boxes, title: 'Multi-PSA MSPs', body: 'One consistent client experience across different PSA environments.' },
  { icon: Headphones, title: 'IT support teams', body: 'Cut repetitive status requests and the chasing that comes with them.' },
  { icon: Users, title: 'Help desk teams', body: 'One front door instead of an inbox, a phone line and a chat window.' },
  { icon: Wrench, title: 'Service delivery leads', body: 'Keep technicians focused on resolving work rather than reporting on it.' },
];

export const PLATFORM_PILLARS: Blurb[] = [
  { icon: Boxes, title: 'A shared connector layer', body: 'Every platform reuses the same sync, mapping and portal, so a new one behaves like the last.' },
  { icon: Server, title: 'Deploy on your terms', body: 'Run the platform on infrastructure you control, alongside what you already host.' },
  { icon: Fingerprint, title: 'Your identity, your rules', body: 'Sign-in follows the policy your organisation already enforces.' },
];

/** What a client does in the portal, in the order they do it. */
export const CLIENT_JOURNEY = [
  'Submit a request in a form written for them, not for a technician.',
  'Attach the screenshot that explains it in one drag.',
  'Follow progress without asking anyone for an update.',
  'Reply in the same thread your team is already using.',
  'Get told when something changes, without an email chain.',
];

export const HOW_IT_WORKS = [
  { n: '01', title: 'Choose your PSA', body: 'Connect PioManage to the PSA your MSP already runs.' },
  { n: '02', title: 'Configure the experience', body: 'Decide what clients see, how statuses read, and who may do what.' },
  { n: '03', title: 'Invite your clients', body: 'Give each client access to their own support experience.' },
  { n: '04', title: 'Start working', body: 'Clients use the portal. Technicians carry on in the PSA.' },
];
