import { z } from 'zod';

export const TicketListItemSchema = z.object({
  id: z.string(),
  externalTicketId: z.string().nullable(),
  provider: z.union([z.string(), z.number()]),
  title: z.string(),
  portalStatus: z.string(),
  portalPriority: z.string(),
  queueOrBoard: z.string().nullable(),
  createdAt: z.string(),
  lastSyncedAt: z.string().nullable(),
  customerName: z.string().nullable().optional(),
  connectionName: z.string().nullable().optional(),
  // Defaulted: a response from the previous build mid-deploy carries none of these, and throwing
  // the whole ticket list away over a missing column helps nobody.
  raisedAt: z.string().nullable().default(null),
  timeWorkedHours: z.number().default(0),
  billableHours: z.number().default(0),
  // Who holds the ticket and who logged time on it, keyed by the server's PersonKey. Staff lists
  // only; null on a client's list, which is what tells the page not to offer a technician filter.
  people: z.array(z.object({ key: z.string(), name: z.string(), holds: z.boolean() })).nullable().default(null),
});
export type TicketListItem = z.infer<typeof TicketListItemSchema>;

export const TicketNoteSchema = z.object({
  id: z.string(),
  authorName: z.string(),
  authoredByClient: z.boolean(),
  body: z.string(),
  createdAt: z.string(),
  // Staff-only detail responses carry false for internal notes; client responses never contain them.
  isPublic: z.boolean().default(true),
  // Set when the note is a time entry's notes — pairs it with the live entry list.
  timeEntryExternalId: z.string().nullable().default(null),
  // Carried directly for portal-logged entries (reply + time), so the thread can state the time
  // without waiting on the live entry list. Null for provider-side te- notes.
  timeEntryHours: z.number().nullable().default(null),
  timeEntryBillable: z.boolean().nullable().default(null),
});

export const AttachmentSchema = z.object({
  id: z.string(),
  fileName: z.string(),
  contentType: z.string(),
  sizeBytes: z.number(),
  scanStatus: z.union([z.string(), z.number()]),
  uploadedAt: z.string(),
  authorName: z.string().nullable().default(null),
  fromProvider: z.boolean().default(false),
  ticketNoteId: z.string().nullable().default(null),
});

export const TicketDetailSchema = z.object({
  id: z.string(),
  externalTicketId: z.string().nullable(),
  provider: z.union([z.string(), z.number()]),
  title: z.string(),
  description: z.string().nullable(),
  portalStatus: z.string(),
  portalPriority: z.string(),
  portalCategory: z.string().nullable(),
  queueOrBoard: z.string().nullable(),
  createdAt: z.string(),
  resolvedAt: z.string().nullable(),
  conversation: z.array(TicketNoteSchema),
  attachments: z.array(AttachmentSchema),
  customerName: z.string().nullable(),
  updatedAt: z.string(),
  connectionName: z.string().nullable().optional(),
  serviceInstructions: z.string().nullable().optional(),
  assignedTechnicianExternalId: z.string().nullable().default(null),
  assignedTechnicianName: z.string().nullable().default(null),
  // The portal's own assignee, distinct from the provider's above. Defaulted so an older API
  // response (or a cached one mid-deploy) parses rather than throwing the whole detail away.
  assignedAppUserId: z.string().nullable().default(null),
  assignedAppUserName: z.string().nullable().default(null),
  externalTicketUrl: z.string().nullable().default(null),
});
export type TicketDetail = z.infer<typeof TicketDetailSchema>;

export const NotificationSchema = z.object({
  ticketId: z.string(),
  title: z.string(),
  kind: z.string(),
  summary: z.string(),
  at: z.string(),
});
export type Notification = z.infer<typeof NotificationSchema>;

export const ScoreContributionSchema = z.object({
  component: z.string(),
  score: z.number(),
  weight: z.number(),
  weightedPoints: z.number(),
});

export const ProductivityScoreSchema = z.object({
  overall: z.number(),
  measuredWeightFraction: z.number(),
  breakdown: z.array(ScoreContributionSchema),
});

export const TechnicianMetricsSchema = z.object({
  technicianExternalId: z.string(),
  assigned: z.number(),
  resolved: z.number(),
  open: z.number(),
  overdue: z.number(),
  slaCompliancePct: z.number(),
  avgResolutionHours: z.number(),
  timeWorkedHours: z.number(),
  billableHours: z.number(),
  nonBillableHours: z.number(),
  score: ProductivityScoreSchema.nullable(),
});

export const TechnicianResponseSchema = z.object({
  metrics: TechnicianMetricsSchema,
  disclaimer: z.string(),
});

export const TeamRowSchema = z.object({
  technicianExternalId: z.string(),
  resolved: z.number(),
  slaCompliancePct: z.number(),
  score: z.number().nullable(),
  // Defaulted rather than required: a response served mid-deploy by the previous build carries
  // neither field, and throwing the whole team table away over a missing label helps nobody.
  technicianName: z.string().nullable().default(null),
  appUserId: z.string().nullable().default(null),
});
export const TeamResponseSchema = z.object({
  team: z.array(TeamRowSchema),
  disclaimer: z.string(),
});

export const TrendPointSchema = z.object({
  date: z.string(),
  created: z.number(),
  resolved: z.number(),
});
export type TrendPoint = z.infer<typeof TrendPointSchema>;

/// One technician's day. The series every productivity view is drawn from.
export const TechnicianDaySchema = z.object({
  date: z.string(),
  appUserId: z.string().nullable(),
  technicianExternalId: z.string().nullable(),
  name: z.string(),
  hours: z.number(),
  billableHours: z.number(),
  resolved: z.number(),
  ticketsTouched: z.number(),
});
export type TechnicianDay = z.output<typeof TechnicianDaySchema>;
export type TechnicianResponse = z.infer<typeof TechnicianResponseSchema>;
export type TeamResponse = z.infer<typeof TeamResponseSchema>;

export const ConnectionSummarySchema = z.object({
  id: z.string(),
  name: z.string(),
  provider: z.union([z.string(), z.number()]),
  apiEndpoint: z.string(),
  tenantIdentifier: z.string().nullable(),
  status: z.union([z.string(), z.number()]),
  isEnabled: z.boolean(),
  lastSuccessfulSyncAt: z.string().nullable(),
  lastError: z.string().nullable(),
  lastHealthCheckAt: z.string().nullable().default(null),
  ticketCount: z.number().default(0),
  customerCount: z.number().default(0),
  contactCount: z.number().default(0),
  logoUrl: z.string().nullable().default(null),
  // Names of the credential fields that currently hold a stored value — never the values (they
  // stay write-only). null = the endpoint didn't say (older responses), which is different from
  // [] = it said "nothing is stored".
  storedCredentialKeys: z.array(z.string()).nullable().default(null),
});
export type ConnectionSummary = z.infer<typeof ConnectionSummarySchema>;

export const MappingRuleSchema = z.object({
  id: z.string(),
  provider: z.union([z.string(), z.number()]),
  scope: z.union([z.string(), z.number()]),
  psaConnectionId: z.string().nullable(),
  portalField: z.string(),
  portalValue: z.string().nullable(),
  externalField: z.string(),
  externalValue: z.string().nullable(),
  direction: z.union([z.string(), z.number()]),
  isRequired: z.boolean(),
  fallbackValue: z.string().nullable(),
  isActive: z.boolean(),
  version: z.number(),
});
export type MappingRule = z.infer<typeof MappingRuleSchema>;

// value = what the PROVIDER is sent (import filters, queue reassignment).
// syncValue = what a synced ticket arrives CARRYING, and so the only thing a mapping rule can match.
// They differ wherever a field is filtered by id and reported by name; older payloads without
// syncValue fall back to value, which is what they always meant.
export const FieldOptionSchema = z.object({
  value: z.string(),
  label: z.string(),
  syncValue: z.string().optional(),
});
export const ConnectionFieldsSchema = z.object({
  queuesOrBoards: z.array(FieldOptionSchema),
  statuses: z.array(FieldOptionSchema),
  priorities: z.array(FieldOptionSchema),
  categories: z.array(FieldOptionSchema),
  workTypes: z.array(FieldOptionSchema).default([]),
  workRoles: z.array(FieldOptionSchema).default([]),
  technicians: z.array(FieldOptionSchema).default([]),
  technicianCoverage: z.array(z.object({
    technicianId: z.string(),
    roleId: z.string().nullable(),
    roleName: z.string().nullable(),
    queueOrBoardId: z.string().nullable(),
  })).default([]),
});
export type ConnectionFields = z.infer<typeof ConnectionFieldsSchema>;

export const HealthSchema = z.object({
  connectionId: z.string(),
  name: z.string(),
  provider: z.union([z.string(), z.number()]),
  status: z.union([z.string(), z.number()]),
  lastSuccessfulSyncAt: z.string().nullable(),
  lastHealthCheckAt: z.string().nullable(),
  pendingJobs: z.number(),
  deadLetterJobs: z.number(),
  failedSyncEvents: z.number(),
  lastError: z.string().nullable(),
});
export type Health = z.infer<typeof HealthSchema>;

export const JobSchema = z.object({
  id: z.string(),
  jobType: z.string(),
  status: z.union([z.string(), z.number()]),
  attempts: z.number(),
  maxAttempts: z.number(),
  nextAttemptAt: z.string().nullable(),
  lastError: z.string().nullable(),
  createdAt: z.string(),
});
export type Job = z.infer<typeof JobSchema>;

export const AuditEntrySchema = z.object({
  id: z.string(),
  action: z.string(),
  entityType: z.string(),
  entityId: z.string().nullable(),
  actorDisplayName: z.string().nullable(),
  correlationId: z.string().nullable(),
  createdAt: z.string(),
  detailJson: z.string().nullable(),
});
export type AuditEntry = z.infer<typeof AuditEntrySchema>;

export const ProfileSchema = z.object({
  /** "staff" (technician/manager/MSP admin) or "client" (portal user). */
  kind: z.enum(['staff', 'client']),
  displayName: z.string(),
  email: z.string(),
  roles: z.array(z.string()),
  memberSince: z.string(),
  companyName: z.string().nullable(),
  isCompanyAdministrator: z.boolean(),
  /** Sign-in is IdP-bound: editing the contact email does not change login. */
  signInManaged: z.boolean(),
});
export type Profile = z.infer<typeof ProfileSchema>;
