import { z } from 'zod';

export const TicketListItemSchema = z.object({
  id: z.string(),
  externalTicketId: z.string().nullable(),
  // Null for a ticket that belongs to no PSA: the team's own board, or a monitoring alert.
  provider: z.union([z.string(), z.number()]).nullable(),
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
  // The team's own boards: which board, the number people quote, and who holds it.
  boardId: z.string().nullable().default(null),
  number: z.string().nullable().default(null),
  assignedToName: z.string().nullable().default(null),
  // The columns a desk organised by department reads its queue with.
  departmentName: z.string().nullable().default(null),
  topic: z.string().nullable().default(null),
  source: z.string().nullable().default(null),
  dueAt: z.string().nullable().default(null),
  replyCount: z.number().default(0),
  lastActivityAt: z.string().nullable().default(null),
  // The team it is routed to, and whether I am watching it — both shown beside the assignee, not
  // instead of it: "Level 2 · Basit" is a different fact from either half on its own.
  assignedTeamId: z.string().nullable().default(null),
  assignedTeamName: z.string().nullable().default(null),
  following: z.boolean().default(false),
  // The ticket's task list as a count, and the SLA's reply promise with whether it was kept.
  taskCount: z.number().default(0),
  tasksDone: z.number().default(0),
  firstResponseDueAt: z.string().nullable().default(null),
  firstRespondedAt: z.string().nullable().default(null),
  // Set while the SLA clock is stopped: waiting on the customer, or on hold.
  slaPausedAt: z.string().nullable().default(null),
  // 0 = a PSA, 1 = the team's own board, 2 = a monitoring alert. Drives the source badge.
  origin: z.number().default(0),
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

export const TicketFollowerSchema = z.object({
  appUserId: z.string(),
  name: z.string(),
  email: z.string().nullable(),
  isMe: z.boolean(),
  addedAt: z.string(),
});
export type TicketFollower = z.infer<typeof TicketFollowerSchema>;

/** A filter set somebody named. The built-in views are code, not rows, and never come back here. */
export const SavedViewFiltersSchema = z.object({
  search: z.string().nullable().default(null),
  status: z.string().nullable().default(null),
  priority: z.string().nullable().default(null),
  company: z.string().nullable().default(null),
  queue: z.string().nullable().default(null),
  connectionName: z.string().nullable().default(null),
  personKey: z.string().nullable().default(null),
  departmentId: z.string().nullable().default(null),
  teamId: z.string().nullable().default(null),
  openness: z.string().nullable().default(null),
  mineOnly: z.boolean().default(false),
  followingOnly: z.boolean().default(false),
  unassignedOnly: z.boolean().default(false),
  overdueOnly: z.boolean().default(false),
  dueSoonOnly: z.boolean().default(false),
  raisedWithinDays: z.number().nullable().default(null),
});
export type SavedViewFilters = z.infer<typeof SavedViewFiltersSchema>;

export const SavedViewSchema = z.object({
  id: z.string(),
  name: z.string(),
  shared: z.boolean(),
  isMine: z.boolean(),
  ownerName: z.string().nullable(),
  boardId: z.string().nullable(),
  filters: SavedViewFiltersSchema,
  sortOrder: z.number().default(0),
});
export type SavedView = z.infer<typeof SavedViewSchema>;

export const TicketDetailSchema = z.object({
  id: z.string(),
  externalTicketId: z.string().nullable(),
  // The number an internal board's ticket is quoted by, e.g. INT-000123.
  number: z.string().nullable().default(null),
  // Null for a ticket on one of the team's own boards: it belongs to no PSA.
  provider: z.union([z.string(), z.number()]).nullable(),
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
  // Who resolved it in the portal: the person productivity credits it to. Staff only.
  resolvedByName: z.string().nullable().default(null),
  assignedAppUserName: z.string().nullable().default(null),
  externalTicketUrl: z.string().nullable().default(null),
  contactName: z.string().nullable().default(null),
  hasReachableContact: z.boolean().default(false),
  assignedTeamId: z.string().nullable().default(null),
  assignedTeamName: z.string().nullable().default(null),
  // Who is watching without holding it. Empty on a client's detail, which never names the desk.
  followers: z.array(TicketFollowerSchema).nullable().default(null),
  // The SLA plan the due dates came from, and its reply promise. Staff only; null otherwise.
  slaPlanName: z.string().nullable().default(null),
  slaDueAt: z.string().nullable().default(null),
  firstResponseDueAt: z.string().nullable().default(null),
  firstRespondedAt: z.string().nullable().default(null),
  slaPausedAt: z.string().nullable().default(null),
  // The client's rating, on the staff detail only.
  rating: z.object({
    rating: z.number(), comment: z.string().nullable(), ratedAt: z.string(),
    ratedBy: z.string().nullable(), technicianName: z.string().nullable(),
  }).nullable().default(null),
  // The device the ticket is about. Serial and warranty arrive for staff only.
  device: z.object({
    id: z.string(), name: z.string(), type: z.string().nullable(), identifier: z.string().nullable(),
    isActive: z.boolean(), warrantyExpiresAt: z.string().nullable(),
  }).nullable().default(null),
  // A board ticket's own record, staff only: what the edit form shows, what fixed it, how often it
  // came back, and whether the board asks for a resolution.
  boardDetails: z.object({
    boardId: z.string(), boardTopicId: z.string().nullable(), departmentId: z.string().nullable(),
    clientCompanyId: z.string().nullable(), resolution: z.string().nullable(), reopenCount: z.number(),
    lastReopenedAt: z.string().nullable(), requireResolution: z.boolean(),
    requireReview: z.boolean().default(false),
    // 0 = no review, 1 = waiting for a lead, 2 = approved.
    reviewState: z.number().default(0),
    reviewedBy: z.string().nullable().default(null),
    reviewedAt: z.string().nullable().default(null),
    reviewSendBacks: z.number().default(0),
  }).nullable().default(null),
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
  // Quality, measured only where there is something to measure (null otherwise).
  firstResponseEligible: z.number().default(0),
  firstResponseMet: z.number().default(0),
  avgFirstResponseHours: z.number().nullable().default(null),
  firstResponseSample: z.number().default(0),
  reopened: z.number().default(0),
  reopenRatePct: z.number().nullable().default(null),
  rated: z.number().default(0),
  satisfied: z.number().default(0),
  reviewed: z.number().default(0),
  passedReviewFirstTime: z.number().default(0),
  // Where the work came from: each PSA connection, team boards, monitoring.
  bySource: z.array(z.object({ label: z.string(), assigned: z.number(), resolved: z.number(), hours: z.number() })).default([]),
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
  // One per person. technicianExternalId is not: two PSA accounts can each have a resource 42.
  key: z.string().nullable().default(null),
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
  // The PSA account a login belongs to, for someone with no portal account: part of who they are.
  psaConnectionId: z.string().nullable().default(null),
  name: z.string(),
  hours: z.number(),
  billableHours: z.number(),
  resolved: z.number(),
  ticketsTouched: z.number(),
  // Of the totals above, the part spent on the team's own boards. Defaulted so a response from the
  // previous build mid-deploy still parses.
  internalHours: z.number().default(0),
  resolvedInternal: z.number().default(0),
  // ...and the part opened by monitoring alerts, apart from the team's own boards.
  monitoringHours: z.number().default(0),
  resolvedMonitoring: z.number().default(0),
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
  // What the connection is doing now, worked out by the server from everything it knows (set up
  // or not, switched on, paused, rejected by the PSA, syncing). null = an older response.
  state: z.union([z.string(), z.number()]).nullable().default(null),
  syncPausedAt: z.string().nullable().default(null),
});
export type ConnectionSummary = z.infer<typeof ConnectionSummarySchema>;

/** A PSA the portal names. `available` false = named, with no connector in this build. */
export const ProviderCatalogEntrySchema = z.object({
  provider: z.union([z.string(), z.number()]),
  name: z.string(),
  available: z.boolean(),
  endpointExample: z.string().nullable().default(null),
  endpointHint: z.string().nullable().default(null),
  tenantIdentifierLabel: z.string().nullable().default(null),
  credentials: z.array(z.object({
    key: z.string(), label: z.string(), secret: z.boolean(), hint: z.string().nullable().default(null),
  })).default([]),
  // Two letters for a connection's tile where no logo has been uploaded. From the server, so a new
  // connector needs no entry here.
  mark: z.string().default(''),
});
export type ProviderCatalogEntry = z.infer<typeof ProviderCatalogEntrySchema>;

export const SyncRunSchema = z.object({
  id: z.string(), trigger: z.string(), status: z.string(), startedAt: z.string(), finishedAt: z.string().nullable(),
  fetched: z.number(), created: z.number(), updated: z.number(), skipped: z.number(), pages: z.number(),
  notes: z.number().default(0), attachments: z.number().default(0),
  failedRecords: z.number().default(0), retried: z.number().default(0), recovered: z.number().default(0),
  error: z.string().nullable().default(null), notice: z.string().nullable().default(null),
  requestedBy: z.string().nullable().default(null),
});
export type SyncRun = z.infer<typeof SyncRunSchema>;

export const SyncStateSchema = z.object({
  connectionId: z.string(),
  // Everything changed in the PSA before this moment has been read.
  watermark: z.string().nullable(),
  // A read that ran out of pages and is being carried on run by run.
  readInProgress: z.boolean(), pagesReadSoFar: z.number(),
  running: z.boolean(), openFailures: z.number(), needsReview: z.number(),
  runs: z.array(SyncRunSchema),
});
export type SyncState = z.infer<typeof SyncStateSchema>;

/**
 * One line of a connection's test. `outcome` is Pass, Fail, Warn, NotTested (nothing to try it on,
 * or a write, which a test never makes), Available or Unavailable.
 */
export const ConnectionCheckSchema = z.object({
  key: z.string(), name: z.string(), outcome: z.string(), detail: z.string().nullable().default(null), required: z.boolean().default(false),
});
export const ConnectionCheckReportSchema = z.object({
  passed: z.boolean(), checkedAt: z.string(), checks: z.array(ConnectionCheckSchema),
});
export type ConnectionCheckReport = z.infer<typeof ConnectionCheckReportSchema>;

/** A value the PSA lists and what it becomes in the portal. `mapsTo` null = nothing maps it. */
const MappedValueSchema = z.object({
  value: z.string(), label: z.string(), mapsTo: z.string().nullable(), byFallbackRule: z.boolean().default(false),
});
export const ConnectionMappingCoverageSchema = z.object({
  statuses: z.array(MappedValueSchema), priorities: z.array(MappedValueSchema), unmapped: z.number(),
});
export type ConnectionMappingCoverage = z.infer<typeof ConnectionMappingCoverageSchema>;

/** How much an import would bring in. A null is "the PSA could not say", never zero. */
export const ConnectionPreviewSchema = z.object({
  clients: z.number().nullable(), technicians: z.number().nullable(),
  openTickets: z.number().nullable(), allTickets: z.number().nullable(), ticketsToImport: z.number().nullable(),
  unmapped: z.number(), notes: z.array(z.string()).default([]),
});
export type ConnectionPreview = z.infer<typeof ConnectionPreviewSchema>;

/** What stands between a connection and being switched on. A Fail blocks; a Warn is only said. */
export const PreflightSchema = z.object({
  canEnable: z.boolean(),
  items: z.array(z.object({ key: z.string(), name: z.string(), outcome: z.string(), detail: z.string() })),
});
export type Preflight = z.infer<typeof PreflightSchema>;

/**
 * One value a PSA uses for a field and what the portal makes of it. `mapsTo` null = nothing maps
 * it. `treatedAs` says how the portal counts tickets in a status nothing maps: "Open" or "Finished".
 */
const MappingValueSchema = z.object({
  value: z.string(), mapsTo: z.string().nullable(), byFallbackRule: z.boolean().default(false),
  tickets: z.number(), unmappedTickets: z.number(), listedByPsa: z.boolean(), treatedAs: z.string().nullable().default(null),
});
const MappingFieldHealthSchema = z.object({
  field: z.string(), name: z.string(), values: z.number(), mapped: z.number(), mappedPct: z.number().nullable(),
  unmappedTickets: z.number(), items: z.array(MappingValueSchema),
});
export type MappingFieldHealth = z.infer<typeof MappingFieldHealthSchema>;

/** How well a connection's mapping covers what its PSA sends. `level`: Pass, Optional, Warning or Blocking. */
export const ConnectionMappingHealthSchema = z.object({
  connectionId: z.string(), connectionName: z.string(), checkedAt: z.string(), level: z.string(),
  fields: z.array(MappingFieldHealthSchema),
  outboundStatuses: z.array(z.object({ portalValue: z.string(), sendsAs: z.string().nullable(), problem: z.string().nullable() })),
  technicians: z.object({
    technicians: z.number(), linked: z.number(), linkedPct: z.number().nullable(),
    unlinked: z.array(z.object({ externalId: z.string(), name: z.string().nullable(), tickets: z.number() })),
    // Logins an administrator has said to leave alone. Not counted in the figures above.
    ignored: z.number().default(0),
  }),
  tickets: z.number(), unmappedTickets: z.number(), ticketsWithoutClient: z.number(), notes: z.array(z.string()).default([]),
});
export type ConnectionMappingHealth = z.infer<typeof ConnectionMappingHealthSchema>;

/** A sample ticket: what it arrived with and what the rules make of it. A null mapped value = nothing maps it. */
export const MappingPreviewRowSchema = z.object({
  reference: z.string(), title: z.string(), sourceStatus: z.string().nullable(), mappedStatus: z.string().nullable(),
  sourcePriority: z.string().nullable(), mappedPriority: z.string().nullable(), readFromPsa: z.boolean().default(false),
});
export type MappingPreviewRow = z.infer<typeof MappingPreviewRowSchema>;

export const MappingApplyResultSchema = z.object({
  statusesChanged: z.number(), prioritiesChanged: z.number(), changes: z.array(z.string()).default([]),
});
export type MappingApplyResult = z.infer<typeof MappingApplyResultSchema>;

/** A record the sync could not read or apply. Kept, and tried again until it goes through. */
export const SyncFailureSchema = z.object({
  id: z.string(), entity: z.string(), externalId: z.string(), operation: z.string(), category: z.string(),
  message: z.string(), attempts: z.number(), firstFailedAt: z.string(), lastFailedAt: z.string(),
  nextAttemptAt: z.string().nullable(), status: z.string(),
});
export type SyncFailure = z.infer<typeof SyncFailureSchema>;

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

/** Whether the newest mapping snapshot still holds the live rules (a rollback restores only what it holds). */
export const MappingSnapshotStatusSchema = z.object({
  latestVersion: z.number().nullable(),
  takenAt: z.string().nullable(),
  liveRules: z.number(),
  snapshotRules: z.number(),
  matchesLiveRules: z.boolean(),
});
export type MappingSnapshotStatus = z.infer<typeof MappingSnapshotStatusSchema>;

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
