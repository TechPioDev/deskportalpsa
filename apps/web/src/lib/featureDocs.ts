import {
  Ticket, MessagesSquare, Clock, Users, PanelsTopLeft, ShieldCheck,
  Plug, Paperclip, BarChart3, KeyRound, type LucideIcon,
} from 'lucide-react';

/**
 * The feature documentation the marketing site publishes at /features/{slug}.
 *
 * Every statement here describes THIS build — shipped, deployed behavior — in the same spirit as
 * the footer's PROOF list: no invented metrics, no roadmap dressed up as product. When a feature
 * changes, its document changes in the same pull request.
 */

export type FeatureSection = {
  heading: string;
  body: string;
  points?: string[];
};

export type FeatureDoc = {
  slug: string;
  name: string;
  icon: LucideIcon;
  tagline: string;
  summary: string;
  sections: FeatureSection[];
};

export const FEATURE_DOCS: FeatureDoc[] = [
  {
    slug: 'ticket-management',
    name: 'Ticket Management & Two-Way Sync',
    icon: Ticket,
    tagline: 'One ticket, two systems, always in step.',
    summary:
      'Tickets flow both ways between PioManage and your PSA. A client raises a request in the portal and it becomes a real ticket in ConnectWise or Autotask; a technician updates the ticket in the PSA and the portal reflects it. Neither side is a copy — the PSA remains the system of record, and the portal is the client-friendly window onto it.',
    sections: [
      {
        heading: 'How the sync works',
        body:
          'A background worker polls each connected PSA on a short interval and reconciles tickets, conversation notes, time entries and attachments. Changes made in the portal are pushed to the PSA first and only then recorded locally, so the portal can never show a state the PSA rejected.',
        points: [
          'Incremental sync picks up only what changed since the last successful run; a full sync can rebuild from scratch.',
          'Echo suppression: a reply pushed from the portal is recognized when it comes back in the next sync, so nothing duplicates.',
          'Notes deleted in the PSA are removed from the portal thread; portal-authored replies are never deleted by reconciliation.',
          'A failed sync marks the connection Degraded with the real error — never a stale "Healthy" badge.',
        ],
      },
      {
        heading: 'Status, priority and queue mapping',
        body:
          'Your PSA speaks in its own status and priority ids; the portal speaks in a neutral vocabulary clients understand (New, In Progress, Waiting Customer, Resolved, Closed). The field-mapping engine translates between the two in both directions, per connection, with versioned mappings you can roll back.',
      },
      {
        heading: 'Working a ticket from the portal',
        body:
          'Staff can change status, assign or reassign a technician, move a ticket between queues or boards, reply, attach files and log time — every one of those actions lands in the PSA, attributed and timestamped. Clients see a clean detail page with the public conversation and their own reply box.',
      },
      {
        heading: 'Import controls',
        body:
          'Each connection decides what enters the portal: which companies, boards and technicians to import, whether closed tickets come along, whether notes and attachments sync, and whether brand-new PSA tickets are auto-imported or only tracked once known.',
      },
    ],
  },
  {
    slug: 'conversations',
    name: 'Conversation Threads',
    icon: MessagesSquare,
    tagline: 'A support thread you can actually read.',
    summary:
      'The conversation view lays the whole exchange out like a modern chat: client messages on the left, your team on the right, with color telling you at a glance what kind of post each one is. Notes written in the PSA, replies sent from the portal, internal remarks and time entries all land in one thread — each on the correct side, each visible only to the people allowed to see it.',
    sections: [
      {
        heading: 'Sides and colors that mean something',
        body:
          'Every card is colored by what it is, not who typed it: client messages are neutral, staff replies carry the brand tint, internal notes are amber, time entries are blue. The author side comes from the PSA’s own attribution — a note written by a customer contact in ConnectWise arrives on the client side of the thread, not dressed up as the MSP’s words.',
      },
      {
        heading: 'Internal notes stay internal',
        body:
          'Technicians see the whole thread, internal analysis included. Clients never receive internal notes — they are filtered on the server before the response is built, not hidden by the browser. The same rule covers time-entry notes and the hours attached to replies: billing data never crosses to the client side.',
      },
      {
        heading: 'One reply, one action',
        body:
          'The composer does in one send what used to take three screens: write the reply, optionally log time against it, optionally change the ticket status, attach files — and for staff, flip the toggle to post an internal note instead of a public reply. The note always posts first; if a side-step fails, you get a precise warning instead of a duplicated reply.',
        points: [
          'Public reply / internal note toggle (staff only, enforced by the API — a client cannot request an internal note).',
          'Set status with the reply: "Send reply + IN PROGRESS" in a single action.',
          'Log time with the reply; the reply text becomes the entry’s notes and the hours appear on the reply card.',
          'Ctrl+Enter sends. Attachments ride along with the note they belong to.',
        ],
      },
      {
        heading: 'Notes render like documents, not dumps',
        body:
          'PSA notes arrive as markdown-ish text full of formatting and enormous pre-signed URLs. The portal renders a safe subset — bold, code, links, lists, headings and inline images — without ever injecting raw HTML. Long notes collapse behind a "Show more" control so one pasted knowledge-base article cannot swallow the thread, and expired image links degrade to a labeled chip instead of a broken-image icon.',
      },
    ],
  },
  {
    slug: 'time-tracking',
    name: 'Time Tracking & Billing',
    icon: Clock,
    tagline: 'Hours logged where the work happened, recorded where the money is.',
    summary:
      'Technicians log time in the portal and it lands in the PSA as a real time entry — with work type, work role and billable status. Time logged in the PSA flows back the other way. The thread itself answers the question every time-and-materials reader has: how long did this take, and does it bill?',
    sections: [
      {
        heading: 'Three ways to log time',
        body:
          'Use the Log Time panel on a ticket, attach hours to a reply as you send it, or run the global timer from the header and log the tracked minutes against any ticket. Free-form durations are accepted — 0.30h and 18 minutes are as valid as a tidy quarter hour.',
      },
      {
        heading: 'The time panel reconciles both systems',
        body:
          'The entries list shows every entry the PSA holds plus anything the portal logged that has not landed yet. Each row states which system it came from, its sync status, and — when the PSA rejected it — the provider’s own error verbatim, with a retry that re-sends the original entry rather than asking anyone to re-type it.',
        points: [
          'Totals count only time the PSA actually holds, so the figure reconciles with the provider’s summary.',
          'Edit and delete synced entries from the portal; aggregates are recomputed from the PSA after every change.',
          'A rejected entry is kept, flagged, and retryable — logged work is never silently lost.',
        ],
      },
      {
        heading: 'Setting it up: a worked example',
        body:
          'Time logging needs one piece of setup per PSA connection, because providers disagree about who may own an entry. Open Dashboard → PSA Connections → your connection → Sync settings → Time entry defaults, then:',
        points: [
          'Time entry technician — the person portal-logged time is filed under. Pick a real technician (for example "Jane Cooper", an engineer who works tickets), NOT the API or administrator account: Autotask refuses to let its own integration user own time, and rejects the entry outright.',
          'Default work role — leave it on "the technician\'s own role" unless you have a reason not to. Autotask only accepts a role that technician actually holds, so a role picked from the full list can be one that pairing does not exist for.',
          'If a configured role turns out not to belong to that technician, the portal uses a role they do hold rather than losing the entry — and if they hold none at all, it says so plainly instead of returning the provider\'s raw error.',
          'ConnectWise is less strict: it needs a member, and the work role is optional.',
        ],
      },
      {
        heading: 'When the PSA says no',
        body:
          'A rejected entry is kept, flagged, and retryable — the technician\'s work is never discarded because a provider was unhappy. The row carries the provider\'s own message, refreshed on every attempt, so a retry that fails for a new reason shows the new reason rather than the one from last time.',
      },
      {
        heading: 'Time on the reply itself',
        body:
          'When a reply logs time, the entry is linked to that exact note and the thread shows the duration and billable status directly on the reply card. PSA-side time entries appear in the thread the same way, so the conversation and the timesheet tell one story. Clients never see any of it — hours are billing data and are stripped server-side from the client view.',
      },
    ],
  },
  {
    slug: 'client-portal',
    name: 'Client Portal & Client Logins',
    icon: PanelsTopLeft,
    tagline: 'Your clients get a portal. Your data model knows they are clients.',
    summary:
      'Client users sign in through the same identity provider as your staff but come out the other side with a genuinely different product: their company’s tickets only, the public conversation only, and exactly the actions a client should have — raise a ticket, reply, attach a file. Not a staff dashboard with buttons that error; a client experience with no staff tooling to trip over.',
    sections: [
      {
        heading: 'Scoped by company, enforced by the server',
        body:
          'A client user belongs to one client company. Company administrators see all of that company’s tickets; regular users see their own. The scoping is applied in every query on the server — there is no client-side filter to bypass, and a ticket outside the caller’s company is indistinguishable from one that does not exist.',
      },
      {
        heading: 'Least privilege by construction',
        body:
          'A client login carries exactly the permissions client-reachable endpoints need — create a ticket, add a public note — and nothing else. Staff-only controls (status changes, assignment, time tracking, internal notes) are absent from the client’s pages and refused by the API if requested anyway.',
      },
      {
        heading: 'Provisioning clients',
        body:
          'Invite client users by email — the account binds to their identity on first verified sign-in — or import your PSA’s existing contacts for a company in one action, matched on the PSA contact id so re-imports update rather than duplicate.',
      },
    ],
  },
  {
    slug: 'control-panel',
    name: 'Client Control Panel',
    icon: KeyRound,
    tagline: 'Let client admins run their own account.',
    summary:
      'The Control Panel gives each client company a self-service surface: service instructions for technicians, their own user management with per-section delegation, operational settings like business hours and holidays, announcements, reports and branding. Access is granted per section and per account, so a client admin can delegate exactly as much as they mean to.',
    sections: [
      {
        heading: 'What a client admin can manage',
        body: 'Nine functional sections, each individually delegable to non-admin users:',
        points: [
          'Ticket instructions — global and per-account guidance technicians see on every ticket.',
          'Users — invite, deactivate and grant sectional access to their own people.',
          'Accounts & devices — account details, devices imported from the PSA, the agreements and contracts the PSA holds for the account (read live, never cached), and the service queues their tickets flow through.',
          'Approvers and escalation levels.',
          'Business hours (weekly schedule) and a holiday calendar — importable in one click from the PSA’s own holiday lists, with re-imports never duplicating a date.',
          'Announcements — pinned, published client-authored notices.',
          'Reports — live account reports, CSV export, and scheduled reports with a run history.',
          'Notification history — a dated feed of everything that happened on their tickets (opened, replied to, resolved), derived from real records.',
          'Branding — display name, logo and accent color for their account.',
          'Knowledge base — per-account FAQ articles, drafted and published by category.',
        ],
      },
      {
        heading: 'Instructions that reach the technician',
        body:
          'Service instructions written by the client surface as a highlighted panel on the ticket detail page your technicians work from — the account-specific override when one exists, the client’s global default otherwise.',
      },
      {
        heading: 'Reports without a spreadsheet ritual',
        body:
          'Account reports are computed from live ticket data — status counts, hours logged and billable, recent activity. Any report exports to CSV on demand, and scheduled reports generate on their own cadence with each run downloadable from the history.',
      },
    ],
  },
  {
    slug: 'users-access',
    name: 'Users & Access Management',
    icon: Users,
    tagline: 'Who can do what, answered precisely.',
    summary:
      'Staff access is managed through roles, scoped permissions, per-user overrides and permission templates — with an Effective Permissions screen that answers the question audits actually ask: for this permission, who has it, and why? Departments, teams and board access give structure to larger service desks.',
    sections: [
      {
        heading: 'The users directory',
        body:
          'A full staff directory with search, filters (role, department, team, board, status), bulk actions, and a detail view covering profile, departments and teams, board access, permissions, security and activity. "Last active" is a real signal derived from authenticated requests — not an invented statistic.',
      },
      {
        heading: 'Roles and permissions',
        body:
          'Built-in roles are shared and read-only; custom tenant roles are yours to create — including duplicating a built-in as a starting point. Assignment is guarded: the roles a user can hand out are exactly the assignable set, an administrator cannot edit a role they themselves hold, and a role in use cannot be deleted out from under its holders.',
        points: [
          'Per-user overrides grant or deny individual permissions on top of roles — a Deny wins at the endpoint gate, not just in fine-grained checks.',
          'Permission templates (Standard Technician, Dispatcher, Billing User, Auditor and more) apply a curated set of overrides in one action.',
          'Every change is written to the audit log.',
        ],
      },
      {
        heading: 'Effective permissions, resolved by the engine',
        body:
          'Pick any permission and see every user’s actual access as the enforcement engine resolves it — which roles contribute, which overrides intervene, and what scope applies. What the screen shows is what the API enforces, because both ask the same engine.',
      },
    ],
  },
  {
    slug: 'psa-connections',
    name: 'PSA Connections & Field Mapping',
    icon: Plug,
    tagline: 'Connect a PSA in minutes. Know its health at a glance.',
    summary:
      'Each PSA connection is configured, tested and monitored from one screen: credentials go straight into an encrypted store, a live test proves the connection against the real API, and field discovery pulls the boards, statuses, priorities, work types and technicians you map the portal onto.',
    sections: [
      {
        heading: 'Credentials handled like credentials',
        body:
          'Secrets are encrypted with AES-256-GCM and stored by reference — connection records and API responses carry no secret material, ever. The edit screen shows which credential fields have a stored value without revealing them, merges partial updates over what is stored, and re-tests the connection automatically after a save.',
      },
      {
        heading: 'Field mapping with versions',
        body:
          'Map portal statuses, priorities, queues and categories onto the ids your PSA actually uses, per connection, from live-discovered options. Every mapping change snapshots a version; rollback restores the previous one.',
      },
      {
        heading: 'Health you can trust',
        body:
          'Connections report Healthy, Pending or Degraded from real outcomes — a live test on save, and the result of every sync run. When something breaks, the connection carries the actual provider error, and fixing credentials clears it back to Pending for the next run to confirm.',
      },
    ],
  },
  {
    slug: 'attachments',
    name: 'Attachments & File Sharing',
    icon: Paperclip,
    tagline: 'Files travel with the conversation — scanned, scoped, synced.',
    summary:
      'Clients and technicians attach files to tickets and replies; attachments sync both ways with the PSA. Every upload is scanned before it is stored, executables are blocked, and downloads are authorized against the same ticket-access rules as everything else.',
    sections: [
      {
        heading: 'Upload pipeline',
        body:
          'Validate, scan, then store: files failing malware screening are quarantined and clearly flagged rather than silently dropped. Uploads are capped at 25 MB per file, and a file attached to a reply carries its note id all the way to the PSA so both systems show it in context.',
      },
      {
        heading: 'Both directions',
        body:
          'Files added in ConnectWise or Autotask appear on the portal ticket; files uploaded in the portal land in the PSA. Attachments deleted in the PSA are reconciled away — with dated sweeps or per-ticket reads depending on what the provider supports.',
      },
      {
        heading: 'In the thread, not a separate pile',
        body:
          'Attachments belonging to a reply are shown with that reply; everything else sits in an "Other files" section. Inline images referenced in notes render in place, bounded, with a graceful fallback when a provider’s pre-signed link has expired.',
      },
    ],
  },
  {
    slug: 'analytics',
    name: 'Analytics & Productivity',
    icon: BarChart3,
    tagline: 'Measured from your tickets — and honest about what is not measured.',
    summary:
      'Dashboards cover the service desk’s pulse: ticket volumes and trends, team workload, SLA attainment, resolution times and logged hours, plus a per-technician productivity score built from configurable weighted components. Every scored view states its coverage — what was measured and what was not — instead of dressing partial data up as a verdict.',
    sections: [
      {
        heading: 'The productivity score',
        body:
          'A weighted blend of SLA attainment, resolution speed, worklog discipline and documentation habits, renormalized over the components that actually have data for the period. Components without data are excluded and disclosed, never guessed.',
      },
      {
        heading: 'Team and trend views',
        body:
          'A team table for comparing load and outcomes across technicians, trend charts for volumes over time, and CSV export for anything you want to take into a spreadsheet or a QBR deck.',
      },
    ],
  },
  {
    slug: 'security-audit',
    name: 'Security & Audit',
    icon: ShieldCheck,
    tagline: 'Multi-tenant isolation, real RBAC, and a log that does not forget.',
    summary:
      'PioManage is self-hosted and multi-tenant by construction: tenant isolation is enforced in the data layer on every query, authentication is delegated to your own identity provider, permissions are enforced server-side, and every administrative and access-relevant action lands in an append-only audit log.',
    sections: [
      {
        heading: 'Isolation in depth',
        body:
          'Tenant scoping is applied by a global query filter at the database context — endpoints cannot forget it — with write guards on top. Client-company scoping narrows further within a tenant. Requests without an established scope fail closed to zero rows.',
      },
      {
        heading: 'Identity and sessions',
        body:
          'Sign-in runs through Keycloak with OIDC and PKCE; the browser holds tokens in httpOnly cookies behind a backend-for-frontend proxy, and permissions are resolved from the database on every request — access changes take effect without waiting for tokens to expire.',
      },
      {
        heading: 'Audit log',
        body:
          'User management, role and permission changes, connection changes, control-panel actions and ticket-affecting operations are recorded append-only with actor, entity and timestamp — and with secrets redacted before anything is written.',
      },
      {
        heading: 'Your infrastructure, your data',
        body:
          'The platform, its PostgreSQL database and its encrypted secret store run on servers you control. PSA credentials never appear in configuration files, API responses or logs.',
      },
    ],
  },
];

export const featureHref = (doc: Pick<FeatureDoc, 'slug'>) => `/features/${doc.slug}`;

export const findFeature = (slug: string) => FEATURE_DOCS.find((d) => d.slug === slug);
