import { z } from 'zod';
import {
  TicketListItemSchema, TicketDetailSchema, NotificationSchema, ProfileSchema,
  TechnicianResponseSchema, TeamResponseSchema, TrendPointSchema,
  ConnectionSummarySchema, HealthSchema, JobSchema, AuditEntrySchema, AttachmentSchema,
  TechnicianDaySchema, type TechnicianDay,
  ConnectionFieldsSchema, FieldOptionSchema, type ConnectionFields,
  MappingRuleSchema, type MappingRule, MappingSnapshotStatusSchema, type MappingSnapshotStatus,
  type TicketDetail, type TicketListItem, type Notification, type Profile,
  type TechnicianResponse, type TeamResponse, type TrendPoint,
  type ConnectionSummary, type Health, type Job, type AuditEntry,
  TicketFollowerSchema, type TicketFollower,
  SavedViewSchema, type SavedView, type SavedViewFilters,
} from './types';

// All API calls go through the same-origin BFF proxy, which attaches the bearer token from the
// httpOnly session cookie server-side. No token is ever held in client JavaScript.
const BFF_BASE = '/api/bff';

/** The organization's own mail account. The password is write-only: `hasPassword` is all that comes back. */
const EmailSettingsSchema = z.object({
  hasOwnAccount: z.boolean(), host: z.string().nullable(), port: z.number(), security: z.string(),
  username: z.string().nullable(), hasPassword: z.boolean(), fromAddress: z.string().nullable(), fromName: z.string().nullable(),
  status: z.object({ configured: z.boolean(), fromAddress: z.string().nullable(), source: z.string() }),
  method: z.string().default('Smtp'), graphTenantId: z.string().nullable().default(null), graphClientId: z.string().nullable().default(null),
});
export type EmailSettings = z.infer<typeof EmailSettingsSchema>;
/** `password`: null keeps the stored one, '' removes it. */
export type EmailSettingsInput = {
  host: string; port: number; security: string; username: string | null; password: string | null; fromAddress: string; fromName: string | null;
  /** 'Smtp' or 'Graph' (Microsoft 365 via an app registration; password then carries the client secret). */
  method?: string; graphTenantId?: string | null; graphClientId?: string | null;
};

/** Report kind / frequency are enums serialized as numbers by the API. */
export const REPORT_KIND = { TechnicianProductivity: 0, ClientQbr: 1 } as const;
export const REPORT_FREQUENCY = { Daily: 0, Weekly: 1, Monthly: 2, Quarterly: 3 } as const;

const StaffReportScheduleSchema = z.object({
  id: z.string(), name: z.string(), kind: z.number(), frequency: z.number(),
  clientCompanyId: z.string().nullable(), clientName: z.string().nullable(),
  recipients: z.string().nullable(), isEnabled: z.boolean(),
  lastRunAt: z.string().nullable(), nextRunAt: z.string(),
});
export type StaffReportSchedule = z.infer<typeof StaffReportScheduleSchema>;
export type StaffReportScheduleInput = {
  id?: string | null; name: string; kind: number; frequency: number;
  clientCompanyId: string | null; recipients: string; isEnabled: boolean;
};

const StaffReportRunSchema = z.object({
  id: z.string(), scheduleId: z.string().nullable(), kind: z.number(), title: z.string(), summary: z.string(),
  periodStart: z.string(), periodEnd: z.string(), generatedAt: z.string(),
  delivered: z.boolean(), deliveryNote: z.string().nullable(),
});
export type StaffReportRun = z.infer<typeof StaffReportRunSchema>;

async function request<T>(path: string, schema: z.ZodType<T>, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BFF_BASE}${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      'X-Correlation-ID': crypto.randomUUID(),
      ...(init?.headers ?? {}),
    },
    cache: 'no-store',
  });
  if (res.status === 401) {
    // The session is over and the BFF has already cleared the dead cookies. Send the user back
    // through sign-in: without this they sit on a rendered page where every action fails.
    if (typeof window !== 'undefined') window.location.assign('/api/auth/login');
    throw new ApiError(401, 'Your session expired — signing you back in.');
  }
  if (!res.ok) {
    // Surface the API's message when present so users see the real reason (e.g. which statuses the
    // PSA's board actually allows), not just a status code. Three shapes, because the API has
    // three: problem-details `detail`/`title` from the framework, and `error` from the handful of
    // endpoints that word their own refusal. Omitting `error` meant those carefully written
    // sentences were built, sent, and then thrown away in favour of "POST /path → 400".
    let detail: string | null = null;
    try {
      const body = await res.json();
      detail = body?.detail ?? body?.title ?? body?.error ?? null;
    } catch { /* non-JSON body */ }
    throw new ApiError(res.status, detail ?? `${init?.method ?? 'GET'} ${path} → ${res.status}`);
  }
  if (res.status === 204 || res.headers.get('content-length') === '0') return undefined as T;
  return schema.parse(await res.json());
}

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

/** Per-row outcome of a staff import. 0 created, 1 already exists, 2 invalid. */
const ImportResultSchema = z.object({
  dryRun: z.boolean(),
  created: z.number(),
  alreadyExisted: z.number(),
  invalid: z.number(),
  rows: z.array(z.object({
    email: z.string(),
    displayName: z.string(),
    outcome: z.union([z.literal(0), z.literal(1), z.literal(2), z.string()]),
    reason: z.string().nullable(),
    userId: z.string().nullable(),
  })),
});
export type ImportResult = z.infer<typeof ImportResultSchema>;

export const EnquirySchema = z.object({
  id: z.string(),
  kind: z.union([z.literal('Contact'), z.literal('Meeting'), z.number()]),
  name: z.string(),
  email: z.string(),
  company: z.string().nullable(),
  phone: z.string().nullable(),
  message: z.string(),
  preferredTime: z.string().nullable(),
  status: z.union([z.literal('New'), z.literal('InProgress'), z.literal('Closed'), z.number()]),
  sourcePage: z.string().nullable(),
  createdAt: z.string(),
});
export const EnquiryListSchema = z.object({
  total: z.number(),
  newCount: z.number(),
  items: z.array(EnquirySchema),
});
export type Enquiry = z.infer<typeof EnquirySchema>;

export const MeSchema = z.object({
  subject: z.string().nullable(),
  // The caller's own portal user id and teams: what "my tickets" and "my team's queue" mean.
  // Defaulted so an API from before they existed still parses.
  userId: z.string().nullable().default(null),
  teamIds: z.array(z.string()).default([]),
  email: z.string().nullable(),
  displayName: z.string().nullable(),
  organizationId: z.string().nullable(),
  isPlatformScope: z.boolean(),
  permissions: z.array(z.string()),
  // What this installation has switched on. Defaulted so an older API still parses.
  features: z.object({
    internalBoards: z.boolean().default(true),
    // The workforce module (schedules, skills) is off unless an installation turns it on.
    workforce: z.boolean().default(false),
  }).default({ internalBoards: true, workforce: false }),
  // Set while an administrator views the portal as this person: whom, and who is really looking.
  viewAs: z.object({ name: z.string(), kind: z.string().nullable(), by: z.string().nullable() }).nullable().default(null),
});


// ---- Workforce: working schedules and skills -------------------------------------------------
const WorkBreakSchema = z.object({ start: z.string(), end: z.string() });
export type WorkBreak = z.infer<typeof WorkBreakSchema>;
// Day of week as .NET sends it: 0 = Sunday ... 6 = Saturday.
const WorkDaySchema = z.object({
  day: z.number(), start: z.string(), end: z.string(), breaks: z.array(WorkBreakSchema),
  crossesMidnight: z.boolean(), grossMinutes: z.number(), breakMinutes: z.number(), usableMinutes: z.number(),
});
export type WorkDay = z.infer<typeof WorkDaySchema>;
const WorkScheduleVersionSchema = z.object({
  effectiveFrom: z.string(), timeZone: z.string(), days: z.array(WorkDaySchema), weeklyUsableMinutes: z.number(),
  updatedAt: z.string(), updatedBy: z.string().nullable(),
});
export type WorkScheduleVersion = z.infer<typeof WorkScheduleVersionSchema>;
export const PersonScheduleSchema = z.object({
  appUserId: z.string(), displayName: z.string(), isActive: z.boolean(), isSchedulable: z.boolean(),
  organizationTimeZone: z.string(), current: WorkScheduleVersionSchema.nullable(), upcoming: WorkScheduleVersionSchema.nullable(),
  versions: z.array(z.string()), canManage: z.boolean(),
});
export type PersonSchedule = z.infer<typeof PersonScheduleSchema>;
export type WorkScheduleInput = {
  effectiveFrom?: string | null; timeZone: string;
  days: { day: number; start: string; end: string; breaks: WorkBreak[] }[];
};
const SkillLevelSchema = z.union([z.literal(1), z.literal(2), z.literal(3)]);
export const SkillSchema = z.object({
  id: z.string(), name: z.string(), description: z.string().nullable(), isActive: z.boolean(), holderCount: z.number(),
});
export type Skill = z.infer<typeof SkillSchema>;
export const StaffSkillSchema = z.object({ skillId: z.string(), name: z.string(), level: SkillLevelSchema, skillIsActive: z.boolean() });
export type StaffSkill = z.infer<typeof StaffSkillSchema>;
export const WorkforcePersonSchema = z.object({
  appUserId: z.string(), displayName: z.string(), email: z.string(), isActive: z.boolean(), isSchedulable: z.boolean(),
  teams: z.array(z.string()), departments: z.array(z.string()), timeZone: z.string().nullable(),
  weeklyUsableMinutes: z.number().nullable(), scheduleSummary: z.string().nullable(), skills: z.array(StaffSkillSchema),
});
export type WorkforcePerson = z.infer<typeof WorkforcePersonSchema>;

// ---- Workforce: capacity, availability and free time (internal only) -----------------------------
// Instants are ISO strings in UTC; each carries the zone to show it in. Never format one with the
// browser's own zone by accident - a planner in London is looking at a technician's day in Kolkata.
const SlotSchema = z.object({ start: z.string(), end: z.string(), minutes: z.number() });
export type Slot = z.infer<typeof SlotSchema>;
/**
 * kind: 1 unavailable, 2 extra availability. reason: 0 other, 1 meeting, 2 training, 3 appointment,
 * 4 time off, 5 sick, 6 internal event. The reason, the note and who recorded it are null unless the
 * viewer is that person or manages their availability: others learn when, not why.
 */
export const CapacityExceptionSchema = z.object({
  id: z.string(), appUserId: z.string(), kind: z.number(), allDay: z.boolean(),
  fromDate: z.string(), toDate: z.string(), startTime: z.string().nullable(), endTime: z.string().nullable(),
  startsAt: z.string().nullable(), endsAt: z.string().nullable(), timeZone: z.string(),
  reason: z.number().nullable(), note: z.string().nullable(), updatedBy: z.string().nullable(), updatedAt: z.string(),
});
export type CapacityException = z.infer<typeof CapacityExceptionSchema>;
export type CapacityExceptionInput = {
  kind: number; allDay: boolean; fromDate: string; toDate?: string | null;
  startTime?: string | null; endTime?: string | null; reason: number; note?: string | null;
};
export const DayCapacitySchema = z.object({
  date: z.string(), timeZone: z.string(), isWorkingDay: z.boolean(),
  windowStart: z.string().nullable(), windowEnd: z.string().nullable(),
  grossMinutes: z.number(), breakMinutes: z.number(), unavailableMinutes: z.number(), additionalMinutes: z.number(), usableMinutes: z.number(),
  confirmedMinutes: z.number(), tentativeMinutes: z.number(), remainingConfirmedMinutes: z.number(), projectedRemainingMinutes: z.number(),
  unavailableAllDay: z.boolean(),
  breaks: z.array(SlotSchema), freeSlots: z.array(SlotSchema), projectedFreeSlots: z.array(SlotSchema),
  exceptions: z.array(CapacityExceptionSchema), holiday: z.string().nullable(),
});
export type DayCapacity = z.infer<typeof DayCapacitySchema>;
export const PersonCapacitySchema = z.object({
  appUserId: z.string(), displayName: z.string(), isActive: z.boolean(), isSchedulable: z.boolean(), hasSchedule: z.boolean(),
  timeZone: z.string(), today: z.string(), days: z.array(DayCapacitySchema), canManageExceptions: z.boolean(),
});
export type PersonCapacity = z.infer<typeof PersonCapacitySchema>;
export const TeamCapacitySchema = z.object({
  date: z.string(),
  people: z.array(z.object({
    appUserId: z.string(), displayName: z.string(), isSchedulable: z.boolean(), hasSchedule: z.boolean(),
    teams: z.array(z.string()), skills: z.array(StaffSkillSchema), day: DayCapacitySchema,
  })),
  usableMinutes: z.number(), confirmedMinutes: z.number(), tentativeMinutes: z.number(), remainingConfirmedMinutes: z.number(),
});
export type TeamCapacity = z.infer<typeof TeamCapacitySchema>;
const WorkforceGroupSchema = z.object({ id: z.string(), name: z.string() });
export const WorkforceGroupsSchema = z.object({ teams: z.array(WorkforceGroupSchema), departments: z.array(WorkforceGroupSchema) });
export type WorkforceGroups = z.infer<typeof WorkforceGroupsSchema>;
export const AvailabilityResultSchema = z.object({
  timeZone: z.string(), durationMinutes: z.number(), matchAllSkills: z.boolean(),
  matches: z.array(z.object({
    appUserId: z.string(), displayName: z.string(), timeZone: z.string(), teams: z.array(z.string()),
    matchingSkills: z.array(StaffSkillSchema), date: z.string(), recommended: SlotSchema, windows: z.array(SlotSchema), freeMinutes: z.number(),
  })),
  peopleConsidered: z.number(), withoutRequiredSkills: z.number(), notOfferedForWork: z.number(),
  withoutASchedule: z.number(), withNoFittingSlot: z.number(),
});
export type AvailabilityResult = z.infer<typeof AvailabilityResultSchema>;
export type AvailabilitySearch = {
  from: string; to?: string | null; duration: number; earliest?: string | null; latest?: string | null;
  teamId?: string | null; departmentId?: string | null; skills?: string[]; matchAll?: boolean;
};
/** type: 1 hard, 2 tentative, 3 break, 4 unavailable, 5 outside hours, 6 over capacity, 7 skill, 8 not schedulable. severity: 1 warning, 2 overridable, 3 block. */
export const ConflictResultSchema = z.object({
  canSchedule: z.boolean(), canOverride: z.boolean(),
  conflicts: z.array(z.object({
    type: z.number(), severity: z.number(), start: z.string(), end: z.string(), message: z.string(), blockingWorkId: z.string().nullable(),
  })),
});
export type ConflictResult = z.infer<typeof ConflictResultSchema>;

export const ViewAsPersonSchema = z.object({
  key: z.string(), kind: z.string(), name: z.string(), email: z.string(),
  detail: z.string().nullable(), available: z.boolean(), reason: z.string().nullable(),
});
export type ViewAsPerson = z.infer<typeof ViewAsPersonSchema>;

export const ConnectionSettingsSchema = z.object({
  twoWaySync: z.boolean(),
  autoImportNewTickets: z.boolean(),
  importNotes: z.boolean(),
  importSystemNotes: z.boolean(),
  syncAttachments: z.boolean(),
  importOpenTickets: z.boolean(),
  importClosedTickets: z.boolean(),
  filterCompanyIds: z.string().nullable(),
  filterQueueIds: z.string().nullable(),
  filterResourceIds: z.string().nullable(),
  filterActiveWithinDays: z.number().nullable(),
  defaultQueueOrBoardId: z.string().nullable(),
  defaultTicketType: z.string().nullable(),
  defaultIssueType: z.string().nullable(),
  defaultSubIssueType: z.string().nullable(),
  defaultTimeEntryResourceId: z.string().nullable(),
  defaultTimeEntryRoleId: z.string().nullable(),
});
export type ConnectionSettings = z.infer<typeof ConnectionSettingsSchema>;

export const api = {
  listTickets: () => request('/api/tickets', z.array(TicketListItemSchema)) as Promise<TicketListItem[]>,
  /** The tickets on one of the team's own boards. */
  tickets: (boardId: string) =>
    request(`/api/tickets?boardId=${boardId}`, z.array(TicketListItemSchema)) as Promise<TicketListItem[]>,
  getTicket: (id: string) => request(`/api/tickets/${id}`, TicketDetailSchema) as Promise<TicketDetail>,

  /**
   * Free-text search, run in the database. The list page filters what it has already loaded, which
   * cannot reach a phrase three replies down a conversation — this can, when asked to.
   */
  searchTickets: (params: {
    q?: string; boardId?: string; departmentId?: string; teamId?: string; status?: string;
    priority?: string; openness?: string; mine?: boolean; following?: boolean; unassigned?: boolean;
    overdue?: boolean; withinDays?: number; notes?: boolean; take?: number;
  }) => {
    const qs = new URLSearchParams();
    for (const [k, v] of Object.entries(params)) {
      if (v === undefined || v === null || v === '' || v === false) continue;
      qs.set(k, String(v));
    }
    return request(`/api/tickets/search?${qs}`, z.object({
      items: z.array(TicketListItemSchema),
      total: z.number(),
      // Whether the limit cut the result: a set silently truncated reads as "there is no more", and
      // somebody then concludes the ticket does not exist.
      truncated: z.boolean(),
    })) as Promise<{ items: TicketListItem[]; total: number; truncated: boolean }>;
  },

  /** One page of the ticket list, filtered in the database; hours cover the whole filtered set. */
  ticketPage: (params: TicketPageParams) => {
    const qs = new URLSearchParams();
    for (const [k, v] of Object.entries(params)) {
      if (v === undefined || v === null || v === '' || v === false) continue;
      qs.set(k, String(v));
    }
    return request(`/api/tickets/page?${qs}`, TicketPageSchema) as Promise<TicketPage>;
  },
  /** What the list's filters can be set to, across every ticket I can see. */
  ticketFacets: () => request('/api/tickets/facets', TicketFacetsSchema) as Promise<TicketFacets>,
  /** Tickets raised since a date (or ever), counted: total, open, by priority and queue. */
  ticketBreakdown: (from?: string) =>
    request(`/api/tickets/breakdown${from ? `?from=${encodeURIComponent(from)}` : ''}`, TicketBreakdownSchema) as Promise<TicketBreakdown>,
  /** Open work counted; mine=true for what I hold (My Work). Staff only. */
  ticketSummary: (mine = false) =>
    request(`/api/tickets/summary${mine ? '?mine=true' : ''}`, TicketSummarySchema) as Promise<TicketSummary>,
  /** Open work per person, for balancing the team. */
  teamWorkload: () => request('/api/tickets/workload', TeamWorkloadSchema) as Promise<TeamWorkload>,

  /** The ids of the tickets I follow — enough for the list to mark and filter them. */
  followingTicketIds: () => request('/api/tickets/following', z.array(z.string())),
  ticketFollowers: (id: string) =>
    request(`/api/tickets/${id}/followers`, z.array(TicketFollowerSchema)) as Promise<TicketFollower[]>,
  /** No user id follows the ticket yourself, which is what the Follow button asks for. */
  addTicketFollower: (id: string, appUserId?: string) =>
    request(`/api/tickets/${id}/followers`, z.array(TicketFollowerSchema),
      { method: 'POST', body: JSON.stringify({ appUserId: appUserId ?? null }) }) as Promise<TicketFollower[]>,
  removeTicketFollower: (id: string, appUserId: string) =>
    request(`/api/tickets/${id}/followers/${appUserId}`, z.array(TicketFollowerSchema),
      { method: 'DELETE' }) as Promise<TicketFollower[]>,

  ticketViews: (boardId?: string) =>
    request(`/api/tickets/views${boardId ? `?boardId=${boardId}` : ''}`, z.array(SavedViewSchema)) as Promise<SavedView[]>,
  saveTicketView: (body: { id?: string; name: string; shared?: boolean; boardId?: string; filters: SavedViewFilters }) =>
    request('/api/tickets/views', SavedViewSchema, { method: 'POST', body: JSON.stringify(body) }) as Promise<SavedView>,
  deleteTicketView: (id: string) =>
    request(`/api/tickets/views/${id}`, z.unknown(), { method: 'DELETE' }),
  createTicket: (body: { title: string; description?: string; priority?: string; queueOrBoard?: string; deviceId?: string }) =>
    request('/api/tickets', z.object({ id: z.string(), externalTicketId: z.string().nullable() }), {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  addComment: (id: string, body: string, isPublic?: boolean,
               recipients?: { emailContact: boolean; emailCc: string[] }) =>
    request(`/api/tickets/${id}/comments`, TicketNoteResponse, {
      method: 'POST',
      body: JSON.stringify({ body, isPublic, emailContact: recipients?.emailContact, emailCc: recipients?.emailCc }),
    }),
  refreshTicketContact: (id: string) =>
    request(`/api/tickets/${id}/contact/refresh`, z.object({ hasReachableContact: z.boolean() }), { method: 'POST' }),
  ticketRecipients: (id: string) =>
    request(`/api/tickets/${id}/recipients`, z.object({
      companyName: z.string(),
      canChooseRecipients: z.boolean(),
      contacts: z.array(z.object({ externalId: z.string(), name: z.string(), email: z.string() })),
    })),
  // Cast to the schema's OUTPUT type: portalTechnicians carries a .default([]), which makes it
  // optional on the way in and guaranteed on the way out. Without this the caller sees the input
  // type and has to null-check a field the parser has already filled.
  ticketAssignees: (id: string) =>
    request(`/api/tickets/${id}/assignees`, AssigneeOptionsSchema) as Promise<AssigneeOptions>,
  assignTicket: (id: string, body: {
    technicianExternalId?: string; queueOrBoardId?: string; roleId?: string; appUserId?: string;
    /** What the person handing over wants the next person to read first. */
    handoverNote?: string;
    /** A team to route it to. Independent of appUserId — a ticket can sit with Level 2 AND with Basit. */
    teamId?: string;
    /** Take it off the team it is on, without naming another. */
    clearTeam?: boolean;
  }) =>
    request(`/api/tickets/${id}/assignment`,
      z.object({
        assignedAppUserId: z.string().nullable().default(null),
        assignedTechnicianExternalId: z.string().nullable(),
        assignedTechnicianName: z.string().nullable(),
        assignedTeamId: z.string().nullable().default(null),
        assignedTeamName: z.string().nullable().default(null),
        queueOrBoard: z.string().nullable(),
      }).passthrough(),
      { method: 'PUT', body: JSON.stringify(body) }),
  updateTicketStatus: (id: string, status: string, resolution?: string | null) =>
    request(`/api/tickets/${id}/status`, z.object({ portalStatus: z.string() }), { method: 'POST', body: JSON.stringify({ status, resolution: resolution || null }) }),
  /** A board ticket's details, sent whole. */
  editBoardTicket: (id: string, body: BoardTicketEdit) =>
    request(`/api/boards/tickets/${id}`, z.unknown(), { method: 'PUT', body: JSON.stringify(body) }),
  /** Approve resolved work (it closes) or send it back with a note. Board leads only. */
  reviewTicket: (id: string, body: { approve: boolean; note?: string | null }) =>
    request(`/api/tickets/${id}/review`, z.unknown(), { method: 'POST', body: JSON.stringify(body) }),
  ticketLinks: (id: string) => request(`/api/tickets/${id}/links`, z.array(TicketLinkSchema)) as Promise<TicketLink[]>,
  addTicketLink: (id: string, otherTicketId: string, kind: number) =>
    request(`/api/tickets/${id}/links`, TicketLinkSchema, { method: 'POST', body: JSON.stringify({ otherTicketId, kind }) }),
  removeTicketLink: (id: string, linkId: string) =>
    request(`/api/tickets/${id}/links/${linkId}`, z.unknown(), { method: 'DELETE' }),
  ticketHistory: (id: string) =>
    request(`/api/tickets/${id}/history`, z.array(TicketHistoryEntrySchema)) as Promise<TicketHistoryEntry[]>,
  // Same aggregate response as update/retry/delete — the endpoint recomputes the ticket's totals
  // from the PSA and returns those, not the created entry. The old schema here demanded an
  // externalId the response never carried, so every SUCCESSFUL log threw at the parse and showed
  // as a failure while the entry quietly landed in the PSA.
  logTime: (id: string, body: { hours: number; billable: string; notes?: string; workType?: string; workRole?: string; noteId?: string; workedAt?: string }) =>
    request(`/api/tickets/${id}/time`, TimeAggregateSchema, { method: 'POST', body: JSON.stringify(body) }),
  ticketTimeOptions: (id: string) =>
    request(`/api/tickets/${id}/time-options`,
      z.object({ workTypes: z.array(FieldOptionSchema), workRoles: z.array(FieldOptionSchema) })),
  listTimeEntries: (id: string) =>
    request(`/api/tickets/${id}/time`, z.array(TimeEntrySchema)) as Promise<TimeEntry[]>,
  updateTimeEntry: (id: string, entryId: string, body: { hours?: number; billable?: string; notes?: string }) =>
    request(`/api/tickets/${id}/time/${entryId}`, TimeAggregateSchema, { method: 'PUT', body: JSON.stringify(body) }),
  retryTimeEntry: (id: string, entryId: string) =>
    request(`/api/tickets/${id}/time/${entryId}/retry`, TimeAggregateSchema, { method: 'POST' }),
  deleteTimeEntry: (id: string, entryId: string) =>
    request(`/api/tickets/${id}/time/${entryId}`, TimeAggregateSchema, { method: 'DELETE' }),
  uploadAttachment: async (ticketId: string, file: File, noteId?: string) => {
    const fd = new FormData();
    fd.append('file', file);
    const query = noteId ? `?noteId=${noteId}` : '';
    const res = await fetch(`${BFF_BASE}/api/tickets/${ticketId}/attachments${query}`, {
      method: 'POST',
      headers: { 'X-Correlation-ID': crypto.randomUUID() }, // no Content-Type — browser sets multipart boundary
      body: fd,
    });
    if (!res.ok) throw new ApiError(res.status, `upload → ${res.status}`);
    return AttachmentSchema.parse(await res.json());
  },
  uploadConnectionLogo: async (connectionId: string, file: File) => {
    const fd = new FormData();
    fd.append('file', file);
    const res = await fetch(`${BFF_BASE}/api/admin/connections/${connectionId}/logo`, {
      method: 'POST',
      headers: { 'X-Correlation-ID': crypto.randomUUID() }, // no Content-Type — the browser sets the boundary
      body: fd,
    });
    if (!res.ok) {
      let detail: string | null = null;
      try { detail = (await res.json())?.detail ?? null; } catch { /* non-JSON */ }
      throw new ApiError(res.status, detail ?? 'Could not upload the logo.');
    }
    return ConnectionSummarySchema.parse(await res.json());
  },
  removeConnectionLogo: (connectionId: string) =>
    request(`/api/admin/connections/${connectionId}/logo`, z.void(), { method: 'DELETE' }),
  attachmentDownloadUrl: (ticketId: string, attachmentId: string) =>
    request(`/api/tickets/${ticketId}/attachments/${attachmentId}/download`, z.object({ url: z.string() })),
  // Assistant. Availability is asked first so the rail can explain itself rather than fail on click.
  assistantAvailability: () =>
    request('/api/assistant/availability', z.object({ enabled: z.boolean(), reason: z.string().nullable() })),
  assistantAsk: (ticketId: string, action: string, draft?: string, question?: string) =>
    request(`/api/assistant/tickets/${ticketId}`, z.object({ text: z.string(), isDraft: z.boolean() }),
      { method: 'POST', body: JSON.stringify({ action, draft, question }) }),
  assistantSettings: () =>
    request('/api/assistant/settings', z.object({
      isEnabled: z.boolean(), model: z.string(), includeInternalNotes: z.boolean(), hasKey: z.boolean(),
    })),
  saveAssistantSettings: (body: { isEnabled: boolean; includeInternalNotes: boolean; model?: string; apiKey?: string }) =>
    request('/api/assistant/settings', z.object({
      isEnabled: z.boolean(), model: z.string(), includeInternalNotes: z.boolean(), hasKey: z.boolean(),
    }), { method: 'PUT', body: JSON.stringify(body) }),

  notifications: () => request('/api/notifications', z.array(NotificationSchema)) as Promise<Notification[]>,
  notificationHistory: () =>
    request('/api/notifications/history', z.array(z.object({
      ticketId: z.string(),
      ticketTitle: z.string(),
      kind: z.enum(['ticket-created', 'client-reply', 'staff-reply', 'ticket-resolved']),
      actor: z.string().nullable(),
      at: z.string(),
    }))),
  me: () => request('/api/me', MeSchema),
  workforcePeople: (q: { teamId?: string; departmentId?: string; skills?: string[]; matchAll?: boolean; includeInactive?: boolean }) => {
    const qs = new URLSearchParams();
    if (q.teamId) qs.set('teamId', q.teamId);
    if (q.departmentId) qs.set('departmentId', q.departmentId);
    if (q.skills?.length) qs.set('skills', q.skills.join(','));
    if (q.matchAll) qs.set('matchAll', 'true');
    if (q.includeInactive) qs.set('includeInactive', 'true');
    return request(`/api/workforce/people?${qs}`, z.array(WorkforcePersonSchema)) as Promise<WorkforcePerson[]>;
  },
  workSchedule: (userId: string) => request(`/api/workforce/people/${userId}/schedule`, PersonScheduleSchema) as Promise<PersonSchedule>,
  saveWorkSchedule: (userId: string, body: WorkScheduleInput) =>
    request(`/api/workforce/people/${userId}/schedule`, PersonScheduleSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<PersonSchedule>,
  removeUpcomingSchedule: (userId: string, effectiveFrom: string) =>
    request(`/api/workforce/people/${userId}/schedule/${effectiveFrom}`, PersonScheduleSchema, { method: 'DELETE' }) as Promise<PersonSchedule>,
  copyWorkSchedule: (userId: string, toUserIds: string[], effectiveFrom?: string | null) =>
    request(`/api/workforce/people/${userId}/schedule/copy`, z.object({ copied: z.number() }),
      { method: 'POST', body: JSON.stringify({ toUserIds, effectiveFrom: effectiveFrom || null }) }),
  setSchedulable: (userId: string, schedulable: boolean) =>
    request(`/api/workforce/people/${userId}/schedulable`, PersonScheduleSchema, { method: 'PUT', body: JSON.stringify({ schedulable }) }) as Promise<PersonSchedule>,
  skills: (includeInactive = false) => request(`/api/workforce/skills?includeInactive=${includeInactive}`, z.array(SkillSchema)) as Promise<Skill[]>,
  createSkill: (name: string, description?: string | null) =>
    request('/api/workforce/skills', SkillSchema, { method: 'POST', body: JSON.stringify({ name, description: description || null }) }) as Promise<Skill>,
  updateSkill: (id: string, body: { name: string; description: string | null; isActive: boolean }) =>
    request(`/api/workforce/skills/${id}`, SkillSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<Skill>,
  personSkills: (userId: string) => request(`/api/workforce/people/${userId}/skills`, z.array(StaffSkillSchema)) as Promise<StaffSkill[]>,
  assignSkill: (userId: string, skillId: string, level: 1 | 2 | 3) =>
    request(`/api/workforce/people/${userId}/skills`, z.array(StaffSkillSchema), { method: 'POST', body: JSON.stringify({ skillId, level }) }) as Promise<StaffSkill[]>,
  removeSkill: (userId: string, skillId: string) =>
    request(`/api/workforce/people/${userId}/skills/${skillId}`, z.array(StaffSkillSchema), { method: 'DELETE' }) as Promise<StaffSkill[]>,
  /** One person's capacity and free slots per date (their today when no dates are given; 31 days at most). */
  personCapacity: (userId: string, from?: string | null, to?: string | null) => {
    const qs = new URLSearchParams();
    if (from) qs.set('from', from);
    if (to) qs.set('to', to);
    return request(`/api/workforce/people/${userId}/capacity?${qs}`, PersonCapacitySchema) as Promise<PersonCapacity>;
  },
  /** Everyone the caller may see, for one date. Several skills mean ALL of them unless matchAll is false. */
  teamCapacity: (q: { date?: string | null; teamId?: string | null; departmentId?: string | null; skills?: string[]; matchAll?: boolean }) => {
    const qs = new URLSearchParams();
    if (q.date) qs.set('date', q.date);
    if (q.teamId) qs.set('teamId', q.teamId);
    if (q.departmentId) qs.set('departmentId', q.departmentId);
    if (q.skills?.length) qs.set('skills', q.skills.join(','));
    if (q.matchAll === false) qs.set('matchAll', 'false');
    return request(`/api/workforce/capacity?${qs}`, TeamCapacitySchema) as Promise<TeamCapacity>;
  },
  /** Teams and departments the caller can filter capacity by. */
  workforceGroups: () => request('/api/workforce/groups', WorkforceGroupsSchema) as Promise<WorkforceGroups>,
  /** Who has one continuous free slot long enough for the work. A read - nothing is reserved. */
  findAvailable: (q: AvailabilitySearch) => {
    const qs = new URLSearchParams({ from: q.from, duration: String(q.duration) });
    if (q.to) qs.set('to', q.to);
    if (q.earliest) qs.set('earliest', q.earliest);
    if (q.latest) qs.set('latest', q.latest);
    if (q.teamId) qs.set('teamId', q.teamId);
    if (q.departmentId) qs.set('departmentId', q.departmentId);
    if (q.skills?.length) qs.set('skills', q.skills.join(','));
    if (q.matchAll === false) qs.set('matchAll', 'false');
    return request(`/api/workforce/availability?${qs}`, AvailabilityResultSchema) as Promise<AvailabilityResult>;
  },
  /** Whether a piece of work would fit in someone's time, and what is in the way if not. */
  evaluateConflicts: (userId: string, q: { start: string; end: string; tentative?: boolean; skills?: string[] }) => {
    const qs = new URLSearchParams({ start: q.start, end: q.end });
    if (q.tentative) qs.set('tentative', 'true');
    if (q.skills?.length) qs.set('skills', q.skills.join(','));
    return request(`/api/workforce/people/${userId}/conflicts?${qs}`, ConflictResultSchema) as Promise<ConflictResult>;
  },
  capacityExceptions: (userId: string, from?: string | null, to?: string | null) => {
    const qs = new URLSearchParams();
    if (from) qs.set('from', from);
    if (to) qs.set('to', to);
    return request(`/api/workforce/people/${userId}/exceptions?${qs}`, z.array(CapacityExceptionSchema)) as Promise<CapacityException[]>;
  },
  addCapacityException: (userId: string, body: CapacityExceptionInput) =>
    request(`/api/workforce/people/${userId}/exceptions`, CapacityExceptionSchema, { method: 'POST', body: JSON.stringify(body) }) as Promise<CapacityException>,
  updateCapacityException: (userId: string, id: string, body: CapacityExceptionInput) =>
    request(`/api/workforce/people/${userId}/exceptions/${id}`, CapacityExceptionSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<CapacityException>,
  removeCapacityException: (userId: string, id: string) =>
    request(`/api/workforce/people/${userId}/exceptions/${id}`, z.void(), { method: 'DELETE' }),
  /** People an administrator may view the portal as: staff and client portal users. */
  viewAsPeople: (q: string) =>
    request(`/api/view-as/people?${new URLSearchParams({ q })}`, z.array(ViewAsPersonSchema)) as Promise<ViewAsPerson[]>,
  /** Starts viewing as someone. The proxy keeps the choice in an httpOnly cookie once the API agrees. */
  viewAsStart: (key: string) =>
    request('/api/view-as/start', z.object({ key: z.string(), name: z.string(), kind: z.string() }),
      { method: 'POST', body: JSON.stringify({ key }) }),
  viewAsStop: () => request('/api/view-as/stop', z.unknown(), { method: 'POST', body: JSON.stringify({}) }),
  storageUsage: () =>
    request('/api/admin/storage', z.object({ usedBytes: z.number(), fileCount: z.number(), ticketCount: z.number() })),
  profile: () => request('/api/profile', ProfileSchema) as Promise<Profile>,
  updateProfile: (body: { displayName: string; email: string }) =>
    request('/api/profile', ProfileSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<Profile>,

  /** A lead gets the desk's figures; anyone else is pinned to their own on the server. */
  technicianMetrics: (from?: string) =>
    request(`/api/dashboard/technician${from ? `?from=${encodeURIComponent(from)}` : ''}`, TechnicianResponseSchema) as Promise<TechnicianResponse>,
  teamMetrics: (fromIso?: string) =>
    request(`/api/dashboard/team${fromIso ? `?from=${encodeURIComponent(fromIso)}` : ''}`, TeamResponseSchema) as Promise<TeamResponse>,
  trend: (fromIso?: string) =>
    request(`/api/dashboard/trend${fromIso ? `?from=${encodeURIComponent(fromIso)}` : ''}`, z.array(TrendPointSchema)) as Promise<TrendPoint[]>,
  teamExportUrl: `${BFF_BASE}/api/dashboard/team/export`,
  /**
   * Per technician, per day, for an arbitrary window. `to` is what makes a custom range possible;
   * the presets are the same call with computed bounds, so there is one code path to be wrong in
   * rather than four.
   */
  dailyMetrics: (fromIso: string, toIso?: string, appUserId?: string, companyId?: string) => {
    const q = new URLSearchParams({ from: fromIso });
    if (toIso) q.set('to', toIso);
    if (appUserId) q.set('appUserId', appUserId);
    if (companyId) q.set('companyId', companyId);
    return request(`/api/dashboard/daily?${q}`, z.array(TechnicianDaySchema)) as Promise<TechnicianDay[]>;
  },

  // Admin
  connections: () => request('/api/admin/connections', z.array(ConnectionSummarySchema)) as Promise<ConnectionSummary[]>,
  /** Public site forms. No auth: the endpoint is anonymous by design and writes only. */
  submitEnquiry: (
    kind: 'contact' | 'meeting',
    body: {
      name: string; email: string; company?: string; phone?: string;
      message: string; preferredTime?: string; sourcePage?: string; website?: string;
    },
  ) => request(`/api/public/enquiries/${kind}`, z.object({ received: z.boolean() }), {
    method: 'POST', body: JSON.stringify(body),
  }),
  enquiries: (status?: 'New' | 'InProgress' | 'Closed') =>
    request(`/api/admin/enquiries${status ? `?status=${status}` : ''}`, EnquiryListSchema),
  setEnquiryStatus: (id: string, status: 'New' | 'InProgress' | 'Closed') =>
    request(`/api/admin/enquiries/${id}/status`, z.void(), {
      method: 'POST', body: JSON.stringify({ status }),
    }),
  createConnection: (body: {
    name: string; provider: number; apiEndpoint: string; tenantIdentifier?: string;
    credentials: Record<string, string>; timeZone?: string; logoUrl?: string;
  }) => request('/api/admin/connections', ConnectionSummarySchema, { method: 'POST', body: JSON.stringify(body) }),
  testConnection: (id: string) =>
    request(`/api/admin/connections/${id}/test`,
      z.object({ success: z.boolean(), message: z.string().nullable(), latencyMs: z.number() }),
      { method: 'POST' }),
  syncConnection: (id: string, full = false) =>
    request(`/api/admin/connections/${id}/sync${full ? '?full=true' : ''}`,
      z.object({ fetched: z.number(), created: z.number(), updated: z.number(), skipped: z.number(), pages: z.number() }),
      { method: 'POST' }),
  updateConnection: (id: string, body: {
    name: string; apiEndpoint: string; tenantIdentifier?: string; timeZone?: string;
    isEnabled: boolean; credentials?: Record<string, string>; logoUrl?: string;
  }) => request(`/api/admin/connections/${id}`, ConnectionSummarySchema, { method: 'PUT', body: JSON.stringify(body) }),
  connectionFields: (id: string) =>
    request(`/api/admin/connections/${id}/fields`, ConnectionFieldsSchema) as Promise<ConnectionFields>,
  // Read-only pre-flight so a time-entry misconfiguration is found here, not when a technician's
  // logged hour is rejected by the PSA.
  checkTimeEntry: (id: string) =>
    request(`/api/admin/connections/${id}/check-time-entry`, z.object({
      ready: z.boolean(),
      summary: z.string(),
      remedies: z.array(z.string()).default([]),
      availableRoles: z.array(z.string()).default([]),
    }), { method: 'POST' }),
  connectionSettings: (id: string) =>
    request(`/api/admin/connections/${id}/settings`, ConnectionSettingsSchema) as Promise<ConnectionSettings>,
  saveConnectionSettings: (id: string, body: ConnectionSettings) =>
    request(`/api/admin/connections/${id}/settings`, ConnectionSettingsSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<ConnectionSettings>,
  refreshConnectionFields: (id: string) =>
    request(`/api/admin/connections/${id}/fields/refresh`, ConnectionFieldsSchema, { method: 'POST' }) as Promise<ConnectionFields>,
  listMappings: (provider: number) =>
    request(`/api/admin/mappings?provider=${provider}`, z.array(MappingRuleSchema)) as Promise<MappingRule[]>,
  upsertMapping: (
    body: {
      id?: string; provider: number; scope: number; psaConnectionId: string;
      portalField: string; portalValue: string; externalField: string; externalValue: string;
      direction: number; isRequired: boolean; fallbackValue: string | null;
    },
    note?: string,
  ) => request(`/api/admin/mappings${note ? `?note=${encodeURIComponent(note)}` : ''}`,
    MappingRuleSchema, { method: 'POST', body: JSON.stringify(body) }),
  deleteMapping: (ruleId: string) =>
    request(`/api/admin/mappings/${ruleId}`, z.unknown(), { method: 'DELETE' }),
  mappingSnapshotStatus: (provider: number, connectionId: string) =>
    request(`/api/admin/mappings/versions/status?provider=${provider}&connectionId=${connectionId}`,
      MappingSnapshotStatusSchema) as Promise<MappingSnapshotStatus>,
  saveMappingSnapshot: (provider: number, connectionId: string) =>
    request(`/api/admin/mappings/versions?provider=${provider}&connectionId=${connectionId}`,
      MappingSnapshotStatusSchema, { method: 'POST' }) as Promise<MappingSnapshotStatus>,
  health: () => request('/api/admin/health', z.array(HealthSchema)) as Promise<Health[]>,
  // MSP staff reports: scheduled technician productivity (and client QBRs), emailed as PDF + CSV.
  reportSettings: () => request('/api/reports/settings', z.object({ timeZone: z.string() })),
  saveReportSettings: (timeZone: string) =>
    request('/api/reports/settings', z.object({ timeZone: z.string() }), { method: 'PUT', body: JSON.stringify({ timeZone }) }),
  reportClients: () => request('/api/reports/clients', z.array(z.object({ id: z.string(), name: z.string() }))),
  staffReportSchedules: () => request('/api/reports/schedules', z.array(StaffReportScheduleSchema)),
  saveStaffReportSchedule: (input: StaffReportScheduleInput) =>
    request('/api/reports/schedules', StaffReportScheduleSchema, { method: 'POST', body: JSON.stringify(input) }),
  deleteStaffReportSchedule: (id: string) => request(`/api/reports/schedules/${id}`, z.void(), { method: 'DELETE' }),
  runStaffReport: (id: string) => request(`/api/reports/schedules/${id}/run`, StaffReportRunSchema, { method: 'POST' }),
  staffReportRuns: () => request('/api/reports/runs?take=50', z.array(StaffReportRunSchema)),
  staffReportFileUrl: (id: string, format: 'pdf' | 'csv') => `${BFF_BASE}/api/reports/runs/${id}/${format}`,
  clientQbrPdfUrl: (companyId: string, year: number, quarter: number) =>
    `${BFF_BASE}/api/reports/qbr.pdf?${new URLSearchParams({ companyId, year: String(year), quarter: String(quarter) })}`,
  technicianPdfUrl: (fromIso: string, toIso: string, label: string, companyId?: string) => {
    const q = new URLSearchParams({ from: fromIso, to: toIso, label });
    if (companyId) q.set('companyId', companyId);
    return `${BFF_BASE}/api/reports/technician-productivity.pdf?${q}`;
  },
  // The team's own boards. Reading them needs only tickets.create; configuring them needs boards.manage.
  boards: (includeInactive = false) =>
    request(`/api/boards?includeInactive=${includeInactive}`, z.array(BoardSchema)) as Promise<Board[]>,
  createBoard: (input: BoardInput) =>
    request('/api/boards', BoardSchema, { method: 'POST', body: JSON.stringify(input) }) as Promise<Board>,
  updateBoard: (id: string, input: BoardInput) =>
    request(`/api/boards/${id}`, BoardSchema, { method: 'PUT', body: JSON.stringify(input) }) as Promise<Board>,
  setBoardActive: (id: string, active: boolean) =>
    request(`/api/boards/${id}/active`, z.unknown(), { method: 'PUT', body: JSON.stringify({ active }) }),
  boardMembers: (id: string) =>
    request(`/api/boards/${id}/members`, z.array(BoardMemberSchema)) as Promise<BoardMember[]>,
  setBoardMembers: (id: string, appUserIds: string[]) =>
    request(`/api/boards/${id}/members`, z.array(BoardMemberSchema),
      { method: 'PUT', body: JSON.stringify({ appUserIds }) }) as Promise<BoardMember[]>,
  // Monitoring tools allowed to open tickets. The key comes back once, on create and regenerate.
  /** The organization's active staff, for a lead choosing a board's members. */
  boardPeople: () => request('/api/boards/people', z.array(BoardMemberSchema)) as Promise<BoardMember[]>,
  alertSources: () => request('/api/boards/sources', z.array(AlertSourceSchema)) as Promise<AlertSource[]>,
  createAlertSource: (input: AlertSourceInput) =>
    request('/api/boards/sources', AlertSourceCreatedSchema, { method: 'POST', body: JSON.stringify(input) }),
  updateAlertSource: (id: string, input: AlertSourceInput) =>
    request(`/api/boards/sources/${id}`, AlertSourceSchema, { method: 'PUT', body: JSON.stringify(input) }),
  setAlertSourceActive: (id: string, active: boolean) =>
    request(`/api/boards/sources/${id}/active`, z.unknown(), { method: 'PUT', body: JSON.stringify({ active }) }),
  regenerateAlertSourceKey: (id: string) =>
    request(`/api/boards/sources/${id}/key`, AlertSourceCreatedSchema, { method: 'POST' }),
  boardTopics: (boardId: string, includeInactive = false) =>
    request(`/api/boards/${boardId}/topics?includeInactive=${includeInactive}`, z.array(BoardTopicSchema)) as Promise<BoardTopic[]>,
  addBoardTopic: (boardId: string, input: BoardTopicInput) =>
    request(`/api/boards/${boardId}/topics`, BoardTopicSchema, { method: 'POST', body: JSON.stringify(input) }) as Promise<BoardTopic>,
  updateBoardTopic: (topicId: string, input: BoardTopicInput) =>
    request(`/api/boards/topics/${topicId}`, BoardTopicSchema, { method: 'PUT', body: JSON.stringify(input) }) as Promise<BoardTopic>,

  slaPlans: (includeInactive = false) =>
    request(`/api/boards/sla-plans?includeInactive=${includeInactive}`, z.array(SlaPlanSchema)) as Promise<SlaPlan[]>,
  saveSlaPlan: (id: string | null, input: SlaPlanInput) =>
    request(id ? `/api/boards/sla-plans/${id}` : '/api/boards/sla-plans', SlaPlanSchema,
      { method: id ? 'PUT' : 'POST', body: JSON.stringify(input) }) as Promise<SlaPlan>,
  setSlaPlanActive: (id: string, active: boolean) =>
    request(`/api/boards/sla-plans/${id}/active`, z.unknown(), { method: 'PUT', body: JSON.stringify({ active }) }),

  // ── Approvals ── staff ask the client's approver; the approver answers in the portal.
  ticketApprovals: (ticketId: string) =>
    request(`/api/tickets/${ticketId}/approvals`, StaffApprovalsSchema) as Promise<StaffApprovals>,
  requestApproval: (ticketId: string, approverId: string, requestText: string) =>
    request(`/api/tickets/${ticketId}/approvals`, TicketApprovalSchema,
      { method: 'POST', body: JSON.stringify({ approverId, request: requestText }) }) as Promise<TicketApproval>,
  recordApproval: (approvalId: string, approved: boolean, channel: 'Phone' | 'Email' | 'InPerson', comment: string | null) =>
    request(`/api/approvals/${approvalId}/record`, TicketApprovalSchema,
      { method: 'POST', body: JSON.stringify({ approved, channel, comment }) }) as Promise<TicketApproval>,
  cancelApproval: (approvalId: string) =>
    request(`/api/approvals/${approvalId}/cancel`, TicketApprovalSchema, { method: 'POST' }) as Promise<TicketApproval>,
  clientTicketApprovals: (ticketId: string) =>
    request(`/api/client/tickets/${ticketId}/approvals`, z.array(TicketApprovalSchema)) as Promise<TicketApproval[]>,
  myApprovals: () => request('/api/client/approvals', z.array(MyApprovalSchema)) as Promise<MyApproval[]>,
  decideApproval: (approvalId: string, approved: boolean, comment: string | null) =>
    request(`/api/client/approvals/${approvalId}/decide`, TicketApprovalSchema,
      { method: 'POST', body: JSON.stringify({ approved, comment }) }) as Promise<TicketApproval>,

  satisfactionState: (ticketId: string) =>
    request(`/api/tickets/${ticketId}/satisfaction`, SatisfactionStateSchema) as Promise<SatisfactionState>,
  rateTicket: (ticketId: string, rating: number, comment: string | null) =>
    request(`/api/tickets/${ticketId}/satisfaction`, SatisfactionStateSchema,
      { method: 'POST', body: JSON.stringify({ rating, comment }) }) as Promise<SatisfactionState>,
  satisfactionSummary: (from: Date, to: Date) =>
    request(`/api/dashboard/satisfaction?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`,
      SatisfactionSummarySchema) as Promise<SatisfactionSummary>,

  deskHolidays: (from?: string) =>
    request(`/api/boards/holidays${from ? `?from=${from}` : ''}`, z.array(DeskHolidaySchema)) as Promise<DeskHoliday[]>,
  addDeskHoliday: (date: string, name: string) =>
    request('/api/boards/holidays', DeskHolidaySchema, { method: 'POST', body: JSON.stringify({ date, name }) }) as Promise<DeskHoliday>,
  removeDeskHoliday: (id: string) =>
    request(`/api/boards/holidays/${id}`, z.unknown(), { method: 'DELETE' }),

  recurringTickets: (includeInactive = true) =>
    request(`/api/boards/recurring?includeInactive=${includeInactive}`, z.array(RecurringTicketSchema)) as Promise<RecurringTicket[]>,
  saveRecurringTicket: (id: string | null, input: RecurringTicketInput) =>
    request(id ? `/api/boards/recurring/${id}` : '/api/boards/recurring', RecurringTicketSchema,
      { method: id ? 'PUT' : 'POST', body: JSON.stringify(input) }) as Promise<RecurringTicket>,
  setRecurringTicketActive: (id: string, active: boolean) =>
    request(`/api/boards/recurring/${id}/active`, z.unknown(), { method: 'PUT', body: JSON.stringify({ active }) }),
  deleteRecurringTicket: (id: string) =>
    request(`/api/boards/recurring/${id}`, z.unknown(), { method: 'DELETE' }),
  runRecurringTicket: (id: string) =>
    request(`/api/boards/recurring/${id}/run`,
      z.object({ outcome: z.string(), ticketId: z.string().nullable(), number: z.string().nullable() }), { method: 'POST' }),

  /** Every canned response, for the page that manages them. */
  cannedResponses: (includeInactive = false) =>
    request(`/api/canned-responses?includeInactive=${includeInactive}`, z.array(CannedResponseSchema)) as Promise<CannedResponse[]>,
  /** The responses offered while replying on this ticket: everywhere's, plus its board's. */
  cannedResponsesFor: (ticketId: string) =>
    request(`/api/tickets/${ticketId}/canned-responses`, z.array(CannedResponseSchema)) as Promise<CannedResponse[]>,
  saveCannedResponse: (id: string | null, input: CannedResponseInput) =>
    request(id ? `/api/canned-responses/${id}` : '/api/canned-responses', CannedResponseSchema,
      { method: id ? 'PUT' : 'POST', body: JSON.stringify(input) }) as Promise<CannedResponse>,
  setCannedResponseActive: (id: string, active: boolean) =>
    request(`/api/canned-responses/${id}/active`, z.unknown(), { method: 'PUT', body: JSON.stringify({ active }) }),

  ticketTasks: (ticketId: string) =>
    request(`/api/tickets/${ticketId}/tasks`, z.array(TicketTaskSchema)) as Promise<TicketTask[]>,
  addTicketTask: (ticketId: string, title: string, assignedAppUserId: string | null) =>
    request(`/api/tickets/${ticketId}/tasks`, z.array(TicketTaskSchema),
      { method: 'POST', body: JSON.stringify({ title, assignedAppUserId }) }) as Promise<TicketTask[]>,
  updateTicketTask: (taskId: string, title: string, assignedAppUserId: string | null) =>
    request(`/api/tickets/tasks/${taskId}`, z.array(TicketTaskSchema),
      { method: 'PUT', body: JSON.stringify({ title, assignedAppUserId }) }) as Promise<TicketTask[]>,
  setTicketTaskDone: (taskId: string, done: boolean) =>
    request(`/api/tickets/tasks/${taskId}/done`, z.array(TicketTaskSchema),
      { method: 'PUT', body: JSON.stringify({ done }) }) as Promise<TicketTask[]>,
  moveTicketTask: (taskId: string, offset: -1 | 1) =>
    request(`/api/tickets/tasks/${taskId}/move`, z.array(TicketTaskSchema),
      { method: 'PUT', body: JSON.stringify({ offset }) }) as Promise<TicketTask[]>,
  deleteTicketTask: (taskId: string) =>
    request(`/api/tickets/tasks/${taskId}`, z.array(TicketTaskSchema), { method: 'DELETE' }) as Promise<TicketTask[]>,
  setBoardTopicActive: (topicId: string, active: boolean) =>
    request(`/api/boards/topics/${topicId}/active`, z.unknown(), { method: 'PUT', body: JSON.stringify({ active }) }),
  ticketSources: () => request('/api/boards/sources/options', z.array(z.string())) as Promise<string[]>,
  /** Departments a ticket can be routed to. Readable by anyone who may raise one. */
  boardDepartments: () =>
    request('/api/boards/departments', z.array(z.object({
      id: z.string(), name: z.string(), isActive: z.boolean(),
    }))),
  createInternalTicket: (input: InternalTicketInput) =>
    request('/api/boards/tickets', z.object({
      ticketId: z.string(), number: z.string(), title: z.string(), boardId: z.string(),
    }), { method: 'POST', body: JSON.stringify(input) }),
  attention: () => request('/api/admin/attention', AttentionSchema) as Promise<Attention>,
  saveAttentionDigest: (recipients: string) =>
    request('/api/admin/attention/digest', z.object({
      recipients: z.string().nullable(), lastSentOn: z.string().nullable(), invalid: z.array(z.string()),
    }), { method: 'PUT', body: JSON.stringify({ recipients }) }),
  sendAttentionDigest: () =>
    request('/api/admin/attention/digest/send', z.object({ sent: z.boolean(), message: z.string() }), { method: 'POST' }),
  emailStatus: () => request('/api/admin/email', z.object({ configured: z.boolean(), from: z.string().nullable(), source: z.string() })),
  emailSettings: () => request('/api/admin/email/settings', EmailSettingsSchema),
  saveEmailSettings: (input: EmailSettingsInput) =>
    request('/api/admin/email/settings', EmailSettingsSchema, { method: 'PUT', body: JSON.stringify(input) }),
  removeEmailSettings: () => request('/api/admin/email/settings', EmailSettingsSchema, { method: 'DELETE' }),
  sendTestEmail: (to?: string) => request('/api/admin/email/test', z.object({ sent: z.boolean(), message: z.string() }),
    { method: 'POST', body: JSON.stringify({ to: to || null }) }),
  staffUsers: (params: UserListParams = {}) => {
    const qs = new URLSearchParams();
    if (params.search) qs.set('search', params.search);
    if (params.roleId) qs.set('roleId', params.roleId);
    if (params.departmentId) qs.set('departmentId', params.departmentId);
    if (params.teamId) qs.set('teamId', params.teamId);
    if (params.boardName) qs.set('boardName', params.boardName);
    if (params.isActive !== undefined) qs.set('isActive', String(params.isActive));
    qs.set('page', String(params.page ?? 1));
    qs.set('pageSize', String(params.pageSize ?? 25));
    return request(`/api/admin/users?${qs.toString()}`, UserListResultSchema);
  },
  staffUser: (id: string) => request(`/api/admin/users/${id}`, UserSummarySchema),
  staffRoles: () => request('/api/admin/roles', z.array(RoleOptionSchema)),
  rolesCatalog: () => request('/api/admin/roles/catalog', z.array(PermissionDefinitionSchema)),
  rolesDetailed: () => request('/api/admin/roles/detailed', z.array(RoleDetailSchema)),
  createRole: (body: { name: string; grants: { permissionKey: string; scope: number }[] }) =>
    request('/api/admin/roles', RoleDetailSchema, { method: 'POST', body: JSON.stringify(body) }),
  updateRole: (id: string, body: { name: string; grants: { permissionKey: string; scope: number }[] }) =>
    request(`/api/admin/roles/${id}`, RoleDetailSchema, { method: 'PUT', body: JSON.stringify(body) }),
  deleteRole: (id: string) =>
    request(`/api/admin/roles/${id}`, z.void(), { method: 'DELETE' }),
  effectivePermissionHolders: (key: string) =>
    request(`/api/admin/effective-permissions?key=${encodeURIComponent(key)}`, z.array(UserEffectivePermissionSchema)),
  staffDepartments: () => request('/api/admin/departments', z.array(DepartmentWithTeamsSchema)),
  staffBoards: () => request('/api/admin/boards', z.array(BoardOptionSchema)),
  permissionTemplates: () => request('/api/admin/permission-templates', z.array(PermissionTemplateOptionSchema)),
  orgStructure: () => request('/api/admin/org-structure', z.array(DepartmentManageSchema)),
  createDepartment: (body: { name: string; description?: string | null }) =>
    request('/api/admin/departments', DepartmentManageSchema, { method: 'POST', body: JSON.stringify(body) }),
  updateDepartment: (id: string, body: { name: string; description?: string | null }) =>
    request(`/api/admin/departments/${id}`, DepartmentManageSchema, { method: 'PUT', body: JSON.stringify(body) }),
  setDepartmentActive: (id: string, active: boolean) =>
    request(`/api/admin/departments/${id}/active`, z.void(), { method: 'PUT', body: JSON.stringify(active) }),
  deleteDepartment: (id: string) =>
    request(`/api/admin/departments/${id}`, z.void(), { method: 'DELETE' }),
  createTeam: (body: { departmentId: string; name: string }) =>
    request('/api/admin/teams', TeamManageSchema, { method: 'POST', body: JSON.stringify(body) }),
  updateTeam: (id: string, body: { name: string }) =>
    request(`/api/admin/teams/${id}`, TeamManageSchema, { method: 'PUT', body: JSON.stringify(body) }),
  setTeamActive: (id: string, active: boolean) =>
    request(`/api/admin/teams/${id}/active`, z.void(), { method: 'PUT', body: JSON.stringify(active) }),
  deleteTeam: (id: string) =>
    request(`/api/admin/teams/${id}`, z.void(), { method: 'DELETE' }),
  /**
   * Bulk staff import. Call with dryRun first and show the caller the per-row outcome: forty rows
   * is past what anyone checks by eye, and a half-finished import cannot be told from a complete
   * one by looking at the result.
   */
  importStaffUsers: (body: {
    rows: { displayName: string; email: string; department: string | null }[];
    roleIds: string[];
    dryRun: boolean;
  }) => request('/api/admin/users/import', ImportResultSchema, {
    method: 'POST', body: JSON.stringify(body),
  }) as Promise<ImportResult>,

  createStaffUser: (body: { displayName: string; email: string; roleIds: string[] }) =>
    request('/api/admin/users', UserSummarySchema, { method: 'POST', body: JSON.stringify(body) }),
  updateStaffUser: (id: string, body: { displayName: string; email: string; phoneNumber?: string | null; location?: string | null; managerId?: string | null }) =>
    request(`/api/admin/users/${id}`, UserSummarySchema, { method: 'PUT', body: JSON.stringify(body) }),
  setUserActive: (id: string, active: boolean) =>
    request(`/api/admin/users/${id}/active`, z.void(), { method: 'PUT', body: JSON.stringify(active) }),
  deleteStaffUser: (id: string) =>
    request(`/api/admin/users/${id}`, z.void(), { method: 'DELETE' }),
  assignUserRole: (id: string, roleId: string) =>
    request(`/api/admin/users/${id}/roles/${roleId}`, z.void(), { method: 'POST' }),
  removeUserRole: (id: string, roleId: string) =>
    request(`/api/admin/users/${id}/roles/${roleId}`, z.void(), { method: 'DELETE' }),
  setUserDepartment: (id: string, departmentId: string, isPrimary: boolean) =>
    request(`/api/admin/users/${id}/departments/${departmentId}?isPrimary=${isPrimary}`, z.void(), { method: 'POST' }),
  removeUserDepartment: (id: string, departmentId: string) =>
    request(`/api/admin/users/${id}/departments/${departmentId}`, z.void(), { method: 'DELETE' }),
  assignUserTeam: (id: string, teamId: string) =>
    request(`/api/admin/users/${id}/teams/${teamId}`, z.void(), { method: 'POST' }),
  removeUserTeam: (id: string, teamId: string) =>
    request(`/api/admin/users/${id}/teams/${teamId}`, z.void(), { method: 'DELETE' }),
  setUserBoardAccessMode: (id: string, mode: number) =>
    request(`/api/admin/users/${id}/board-access`, z.void(), { method: 'PUT', body: JSON.stringify({ mode }) }),
  clientWorkload: (params?: { from?: string; to?: string }) => {
    const qs = new URLSearchParams();
    if (params?.from) qs.set('from', params.from);
    if (params?.to) qs.set('to', params.to);
    const suffix = qs.toString() ? `?${qs}` : '';
    return request(`/api/dashboard/clients${suffix}`, z.object({
      clients: z.array(z.object({
        clientCompanyId: z.string(),
        clientName: z.string(),
        totalTickets: z.number(),
        openTickets: z.number(),
        closedTickets: z.number(),
        hoursWorked: z.number(),
        billableHours: z.number(),
        techniciansInvolved: z.number(),
        avgResolutionHours: z.number().nullable(),
        resolutionSample: z.number(),
        slaCompliancePct: z.number().nullable(),
        slaEligible: z.number(),
        // The people behind techniciansInvolved. Defaulted so a response from the previous build
        // mid-deploy still renders the table; the count then simply has nothing to open.
        people: z.array(z.object({
          // Null only from a build that predates it; the name is then shown without a link.
          key: z.string().nullable().default(null),
          appUserId: z.string().nullable(),
          technicianExternalId: z.string().nullable(),
          name: z.string(),
          assignedTickets: z.number(),
          hoursLogged: z.number(),
        })).default([]),
      })),
      ticketsWithoutRaiseDate: z.number(),
      ticketsWithoutClosure: z.number(),
      importWindows: z.array(z.object({
        connectionName: z.string(),
        importsClosedTickets: z.boolean(),
        activeWithinDays: z.number().nullable(),
        ticketsHeld: z.number(),
      })),
    }));
  },
  portalCoverage: (params?: { from?: string }) => {
    const qs = new URLSearchParams();
    if (params?.from) qs.set('from', params.from);
    const suffix = qs.toString() ? `?${qs}` : '';
    return request(`/api/dashboard/coverage${suffix}`, z.object({
      technicians: z.array(z.object({
        technicianExternalId: z.string(),
        technicianName: z.string().nullable(),
        psaHours: z.number(),
        psaEntries: z.number(),
        corroboratedEntries: z.number(),
        coveragePct: z.number().nullable(),
        portalEvents: z.number(),
      })),
      totalPsaHours: z.number(),
      totalPsaEntries: z.number(),
      totalCorroborated: z.number(),
      overallCoveragePct: z.number().nullable(),
      activityRecordedSince: z.string().nullable(),
      rangeStartsBeforeRecording: z.boolean(),
    }));
  },
  psaTechnicians: (psaConnectionId: string) =>
    request(`/api/admin/psa-technicians/${psaConnectionId}`, z.array(z.object({
      externalId: z.string(),
      name: z.string(),
      email: z.string(),
      isActive: z.boolean(),
      link: z.number(),            // 0 not in portal, 1 matched by email, 2 linked
      portalUserId: z.string().nullable(),
      canProvision: z.boolean(),
      blocker: z.string().nullable(),
    }))),
  provisionTechnician: (psaConnectionId: string, externalTechnicianId: string) =>
    request(`/api/admin/psa-technicians/${psaConnectionId}/${encodeURIComponent(externalTechnicianId)}`,
      z.object({ id: z.string(), email: z.string(), displayName: z.string() }).passthrough(),
      { method: 'POST' }),
  userPsaIdentities: (id: string) =>
    request(`/api/admin/users/${id}/psa-identities`, z.array(z.object({
      psaConnectionId: z.string(),
      connectionName: z.string(),
      externalTechnicianId: z.string().nullable(),
      externalTechnicianName: z.string().nullable(),
      technicians: z.array(z.object({ value: z.string(), label: z.string() })),
    }))),
  setUserPsaIdentity: (id: string, psaConnectionId: string, externalTechnicianId: string | null) =>
    request(`/api/admin/users/${id}/psa-identities/${psaConnectionId}`, z.void(),
      { method: 'PUT', body: JSON.stringify({ externalTechnicianId }) }),
  setUserBoardGrant: (id: string, body: { psaConnectionId: string; boardName: string; actions: number }) =>
    request(`/api/admin/users/${id}/board-grants`, z.void(), { method: 'PUT', body: JSON.stringify(body) }),
  removeUserBoardGrant: (id: string, psaConnectionId: string, boardName: string) =>
    request(`/api/admin/users/${id}/board-grants?psaConnectionId=${psaConnectionId}&boardName=${encodeURIComponent(boardName)}`, z.void(), { method: 'DELETE' }),
  applyPermissionTemplate: (id: string, templateId: string) =>
    request(`/api/admin/users/${id}/apply-template/${templateId}`, z.void(), { method: 'POST' }),
  userEffectivePermissions: (id: string) =>
    request(`/api/admin/users/${id}/permissions`, z.array(EffectivePermissionSchema)),
  uploadUserPhoto: async (userId: string, file: File) => {
    const fd = new FormData();
    fd.append('file', file);
    const res = await fetch(`${BFF_BASE}/api/admin/users/${userId}/photo`, {
      method: 'POST',
      headers: { 'X-Correlation-ID': crypto.randomUUID() }, // no Content-Type — the browser sets the boundary
      body: fd,
    });
    if (!res.ok) {
      let detail: string | null = null;
      try { detail = (await res.json())?.detail ?? null; } catch { /* non-JSON */ }
      throw new ApiError(res.status, detail ?? 'Could not upload the photo.');
    }
    return UserSummarySchema.parse(await res.json());
  },
  removeUserPhoto: (id: string) =>
    request(`/api/admin/users/${id}/photo`, z.void(), { method: 'DELETE' }),
  bulkUsers: (input: BulkUserActionInput) =>
    request('/api/admin/users/bulk', BulkUserActionResultSchema, {
      method: 'POST',
      body: JSON.stringify({ ...input, action: BulkUserActionValue[input.action] }),
    }),
  userAuditLog: (userId: string) =>
    request(`/api/admin/audit?entityId=${userId}&take=50`, z.array(AuditEntrySchema)) as Promise<AuditEntry[]>,
  unsyncedTickets: (connectionId?: string) =>
    request(`/api/admin/tickets/unsynced${connectionId ? `?connectionId=${connectionId}` : ''}`, UnsyncedTicketsSchema),
  resyncTicket: (ticketId: string) =>
    request(`/api/admin/tickets/${ticketId}/resync`, ResyncResultSchema, { method: 'POST' }),
  jobs: (status?: number) =>
    request(`/api/admin/jobs${status != null ? `?status=${status}` : ''}`, z.array(JobSchema)) as Promise<Job[]>,
  reprocessJob: (id: string) =>
    request(`/api/admin/jobs/${id}/reprocess`, z.unknown(), { method: 'POST' }),
  audit: () => request('/api/admin/audit', z.array(AuditEntrySchema)) as Promise<AuditEntry[]>,

  // Client control panel (CP-1)
  cpCapabilities: () => request('/api/control-panel/capabilities', CapabilitiesSchema) as Promise<Capabilities>,
  cpInstructions: () => request('/api/control-panel/instructions', InstructionsViewSchema) as Promise<InstructionsView>,
  cpSaveInstruction: (clientCompanyId: string | null, body: string) =>
    request('/api/control-panel/instructions', InstructionSchema,
      { method: 'PUT', body: JSON.stringify({ clientCompanyId, body }) }) as Promise<Instruction>,
  cpUsers: () => request('/api/control-panel/users', z.array(ClientUserSchema)) as Promise<ClientUser[]>,
  cpInviteUser: (body: { email: string; displayName: string; isCompanyAdministrator: boolean }) =>
    request('/api/control-panel/users', ClientUserSchema, { method: 'POST', body: JSON.stringify(body) }) as Promise<ClientUser>,
  cpSetUserActive: (id: string, active: boolean) =>
    request(`/api/control-panel/users/${id}/active`, z.unknown(), { method: 'POST', body: JSON.stringify({ active }) }),
  cpSetUserAccess: (id: string, body: { isCompanyAdministrator: boolean; grants: { section: string; clientCompanyId: string | null }[] }) =>
    request(`/api/control-panel/users/${id}/access`, ClientUserSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<ClientUser>,

  // Control panel — per-account settings (CP-2)
  cpAccount: () => request('/api/control-panel/account', AccountSchema) as Promise<Account>,
  cpApprovers: () => request('/api/control-panel/approvers', z.array(ApproverSchema)) as Promise<Approver[]>,
  cpSaveApprover: (body: ApproverInput) => request('/api/control-panel/approvers', ApproverSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<Approver>,
  cpDeleteApprover: (id: string) => request(`/api/control-panel/approvers/${id}`, z.unknown(), { method: 'DELETE' }),
  cpEscalation: () => request('/api/control-panel/escalation', z.array(EscalationSchema)) as Promise<EscalationLevel[]>,
  cpSaveEscalation: (body: EscalationInput) => request('/api/control-panel/escalation', EscalationSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<EscalationLevel>,
  cpDeleteEscalation: (id: string) => request(`/api/control-panel/escalation/${id}`, z.unknown(), { method: 'DELETE' }),
  cpHolidays: () => request('/api/control-panel/holidays', z.array(HolidaySchema)) as Promise<Holiday[]>,
  cpSaveHoliday: (body: HolidayInput) => request('/api/control-panel/holidays', HolidaySchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<Holiday>,
  cpDeleteHoliday: (id: string) => request(`/api/control-panel/holidays/${id}`, z.unknown(), { method: 'DELETE' }),
  cpImportHolidaysFromPsa: () =>
    request('/api/control-panel/holidays/import-from-psa',
      z.object({ supported: z.boolean(), created: z.number(), skipped: z.number() }),
      { method: 'POST' }),
  cpImportFromPsa: () =>
    request('/api/control-panel/import-from-psa',
      z.object({ usersCreated: z.number(), usersUpdated: z.number(), devicesCreated: z.number(), devicesUpdated: z.number() }),
      { method: 'POST' }),
  // The PSA's live view of this account: agreements/contracts + the queues its tickets flow through.
  cpPsaView: () =>
    request('/api/control-panel/psa-view', z.object({
      agreementsSupported: z.boolean(),
      agreements: z.array(z.object({
        name: z.string(),
        type: z.string().nullable(),
        status: z.string().nullable(),
        startDate: z.string().nullable(),
        endDate: z.string().nullable(),
      })),
      monitoredQueues: z.array(z.string()),
      agreementsUnavailable: z.boolean().default(false),
    })),
  cpDevices: () => request('/api/control-panel/devices', z.array(DeviceSchema)) as Promise<Device[]>,
  // ── Push notifications ── a staff member's own devices and choices.
  pushStatus: () => request('/api/me/push', PushStatusSchema) as Promise<PushStatus>,
  pushSubscribe: (body: { endpoint: string; p256dh: string; auth: string; deviceLabel: string }) =>
    request('/api/me/push/devices', z.unknown(), { method: 'POST', body: JSON.stringify(body) }),
  pushRemoveDevice: (id: string) => request(`/api/me/push/devices/${id}`, z.unknown(), { method: 'DELETE' }),
  pushSavePreferences: (body: PushStatus['preferences']) =>
    request('/api/me/push/preferences', z.unknown(), { method: 'PUT', body: JSON.stringify(body) }),
  pushTest: () => request('/api/me/push/test', z.object({ delivered: z.number() }), { method: 'POST' }),

  // ── Knowledge base ── the team writes and reads every article; a client reads theirs on the Help page.
  kbList: (search?: string) =>
    request(`/api/kb${search ? `?search=${encodeURIComponent(search)}` : ''}`, z.array(KbArticleSummarySchema)) as Promise<KbArticleSummary[]>,
  kbGet: (id: string) => request(`/api/kb/${id}`, KbArticleSchema) as Promise<KbArticle>,
  kbSave: (id: string | null, body: KbArticleInput) =>
    request(id ? `/api/kb/${id}` : '/api/kb', KbArticleSchema, { method: id ? 'PUT' : 'POST', body: JSON.stringify(body) }) as Promise<KbArticle>,
  kbDelete: (id: string) => request(`/api/kb/${id}`, z.unknown(), { method: 'DELETE' }),
  kbStats: (days = 30) => request(`/api/kb/stats?days=${days}`, KbStatsSchema) as Promise<KbStats>,
  help: (search?: string) =>
    request(`/api/client/help${search ? `?search=${encodeURIComponent(search)}` : ''}`, z.array(HelpSummarySchema)) as Promise<HelpSummary[]>,
  helpArticle: (source: string, id: string) =>
    request(`/api/client/help/${source}/${id}`, HelpArticleSchema) as Promise<HelpArticle>,
  helpSuggest: (q: string) =>
    request(`/api/client/help/suggest?q=${encodeURIComponent(q)}`, z.array(HelpSummarySchema)) as Promise<HelpSummary[]>,
  helpSolved: (source: string, id: string, query: string) =>
    request(`/api/client/help/${source}/${id}/solved`, z.unknown(), { method: 'POST', body: JSON.stringify({ query }) }),

  // ── Ticket devices ── staff choose from the ticket's client's devices; a client names one when raising a ticket.
  ticketDeviceChoices: (ticketId: string) =>
    request(`/api/tickets/${ticketId}/device-choices`, z.array(DeviceChoiceSchema)) as Promise<DeviceChoice[]>,
  setTicketDevice: (ticketId: string, deviceId: string | null) =>
    request(`/api/tickets/${ticketId}/device`, z.object({ device: z.unknown().nullable() }),
      { method: 'PUT', body: JSON.stringify({ deviceId }) }),
  clientDeviceChoices: () =>
    request('/api/client/device-choices', z.array(z.object({ id: z.string(), name: z.string(), type: z.string().nullable() }))),
  cpDevice: (id: string) => request(`/api/control-panel/devices/${id}`, DeviceDetailSchema) as Promise<DeviceDetail>,
  cpSaveDevice: (body: DeviceInput) => request('/api/control-panel/devices', DeviceSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<Device>,
  cpDeleteDevice: (id: string) => request(`/api/control-panel/devices/${id}`, z.unknown(), { method: 'DELETE' }),
  cpBusinessHours: () => request('/api/control-panel/business-hours', BusinessHoursSchema) as Promise<BusinessHours>,
  cpSaveBusinessHours: (body: BusinessHours) => request('/api/control-panel/business-hours', BusinessHoursSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<BusinessHours>,

  // Control panel — CP-3 content
  cpAnnouncements: () => request('/api/control-panel/announcements', z.array(AnnouncementSchema)) as Promise<Announcement[]>,
  cpSaveAnnouncement: (body: AnnouncementInput) => request('/api/control-panel/announcements', AnnouncementSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<Announcement>,
  cpDeleteAnnouncement: (id: string) => request(`/api/control-panel/announcements/${id}`, z.unknown(), { method: 'DELETE' }),
  cpBranding: () => request('/api/control-panel/branding', BrandingSchema) as Promise<Branding>,
  cpSaveBranding: (body: Branding) => request('/api/control-panel/branding', BrandingSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<Branding>,
  cpReport: () => request('/api/control-panel/report', ReportSchema) as Promise<AccountReport>,
  cpFaq: () => request('/api/control-panel/faq', z.array(FaqSchema)) as Promise<FaqArticle[]>,
  cpSaveFaq: (body: FaqInput) => request('/api/control-panel/faq', FaqSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<FaqArticle>,
  cpDeleteFaq: (id: string) => request(`/api/control-panel/faq/${id}`, z.unknown(), { method: 'DELETE' }),

  // Reports — export + scheduling
  cpReportSchedules: () => request('/api/control-panel/report/schedules', z.array(ReportScheduleSchema)) as Promise<ReportSchedule[]>,
  cpSaveReportSchedule: (body: ReportScheduleInput) => request('/api/control-panel/report/schedules', ReportScheduleSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<ReportSchedule>,
  cpDeleteReportSchedule: (id: string) => request(`/api/control-panel/report/schedules/${id}`, z.unknown(), { method: 'DELETE' }),
  cpRunReportSchedule: (id: string) => request(`/api/control-panel/report/schedules/${id}/run`, ReportRunSchema, { method: 'POST' }) as Promise<ReportRun>,
  cpReportRuns: () => request('/api/control-panel/report/runs', z.array(ReportRunSchema)) as Promise<ReportRun[]>,
  // CSV downloads stream a file, so they go through the BFF directly (see downloadCsv in the page).
  cpReportExportPath: `${BFF_BASE}/api/control-panel/report/export`,
  cpReportRunDownloadPath: (id: string) => `${BFF_BASE}/api/control-panel/report/runs/${id}/download`,
};

const ReportScheduleSchema = z.object({
  id: z.string(), name: z.string(), frequency: z.string(), recipients: z.string().nullable(),
  isEnabled: z.boolean(), lastRunAt: z.string().nullable(), nextRunAt: z.string(),
});
export type ReportSchedule = z.infer<typeof ReportScheduleSchema>;
export type ReportScheduleInput = { id?: string; name: string; frequency: string; recipients?: string | null; isEnabled: boolean };

const ReportRunSchema = z.object({
  id: z.string(), reportScheduleId: z.string().nullable(), generatedAt: z.string(), format: z.string(),
  summary: z.string(), delivered: z.boolean(), deliveryNote: z.string().nullable(),
});
export type ReportRun = z.infer<typeof ReportRunSchema>;

const FaqSchema = z.object({
  id: z.string(), question: z.string(), answer: z.string(), category: z.string().nullable(),
  isPublished: z.boolean(), sortOrder: z.number(),
});
export type FaqArticle = z.infer<typeof FaqSchema>;
export type FaqInput = { id?: string; question: string; answer?: string; category?: string | null; isPublished: boolean; sortOrder: number };

// ---- CP-3 schemas ----
const AnnouncementSchema = z.object({
  id: z.string(), title: z.string(), body: z.string(), isPinned: z.boolean(), isPublished: z.boolean(),
  publishedAt: z.string().nullable(), authorName: z.string().nullable(),
});
export type Announcement = z.infer<typeof AnnouncementSchema>;
export type AnnouncementInput = { id?: string; title: string; body?: string; isPinned: boolean; isPublished: boolean };

const BrandingSchema = z.object({ displayName: z.string().nullable(), logoUrl: z.string().nullable(), accentColor: z.string().nullable() });
export type Branding = z.infer<typeof BrandingSchema>;

const ReportSchema = z.object({
  totalTickets: z.number(),
  openTickets: z.number(),
  byStatus: z.array(z.object({ status: z.string(), count: z.number() })),
  hoursLogged: z.number(),
  billableHours: z.number(),
  recent: z.array(z.object({ id: z.string(), externalTicketId: z.string().nullable(), title: z.string(), portalStatus: z.string(), createdAt: z.string() })),
});
export type AccountReport = z.infer<typeof ReportSchema>;

// ---- Control panel account-settings schemas (CP-2) ----
const AccountSchema = z.object({ id: z.string(), name: z.string(), externalCompanyId: z.string(), connectionName: z.string().nullable(), isActive: z.boolean() });
export type Account = z.infer<typeof AccountSchema>;

const ApproverSchema = z.object({ id: z.string(), name: z.string(), email: z.string().nullable(), phone: z.string().nullable(), scope: z.string().nullable(), sortOrder: z.number() });
export type Approver = z.infer<typeof ApproverSchema>;
export type ApproverInput = { id?: string; name: string; email?: string | null; phone?: string | null; scope?: string | null; sortOrder: number };

const EscalationSchema = z.object({ id: z.string(), level: z.number(), name: z.string(), contact: z.string().nullable(), condition: z.string().nullable() });
export type EscalationLevel = z.infer<typeof EscalationSchema>;
export type EscalationInput = { id?: string; level: number; name: string; contact?: string | null; condition?: string | null };

const HolidaySchema = z.object({ id: z.string(), date: z.string(), name: z.string() });
export type Holiday = z.infer<typeof HolidaySchema>;
export type HolidayInput = { id?: string; date: string; name: string };

const DeviceSchema = z.object({
  id: z.string(), name: z.string(), type: z.string().nullable(), identifier: z.string().nullable(), notes: z.string().nullable(),
  /** Synced from the PSA: name, type, serial and warranty are the PSA's; only the notes are editable here. */
  fromPsa: z.boolean(),
  isActive: z.boolean(),
  warrantyExpiresAt: z.string().nullable(),
  lastSyncedAt: z.string().nullable(),
  openTickets: z.number(),
  totalTickets: z.number(),
});
export const PushStatusSchema = z.object({
  publicKey: z.string(),
  devices: z.array(z.object({ id: z.string(), label: z.string().nullable(), addedAt: z.string(), lastDeliveredAt: z.string().nullable(), endpointHash: z.string() })),
  preferences: z.object({ assigned: z.boolean(), clientReplied: z.boolean(), slaAtRisk: z.boolean() }),
});
export type PushStatus = z.infer<typeof PushStatusSchema>;

export const KbArticleSummarySchema = z.object({
  id: z.string(), title: z.string(), category: z.string().nullable(),
  audience: z.enum(['Staff', 'AllClients', 'SelectedClients']), clientNames: z.array(z.string()),
  isPublished: z.boolean(), updatedAt: z.string(), updatedByName: z.string().nullable(), solved: z.number(),
});
export type KbArticleSummary = z.infer<typeof KbArticleSummarySchema>;
export const KbArticleSchema = z.object({
  id: z.string(), title: z.string(), body: z.string(), category: z.string().nullable(),
  audience: z.enum(['Staff', 'AllClients', 'SelectedClients']), clientIds: z.array(z.string()),
  isPublished: z.boolean(), authorName: z.string().nullable(), updatedByName: z.string().nullable(), updatedAt: z.string(),
});
export type KbArticle = z.infer<typeof KbArticleSchema>;
export type KbArticleInput = {
  title: string; body: string; category: string | null; audience: KbArticle['audience']; clientIds: string[]; isPublished: boolean;
};
export const KbStatsSchema = z.object({
  days: z.number(), ticketsAvoided: z.number(),
  topArticles: z.array(z.object({ source: z.string(), id: z.string(), title: z.string(), solved: z.number() })),
  recent: z.array(z.object({ title: z.string(), query: z.string().nullable(), clientName: z.string().nullable(), occurredAt: z.string() })),
});
export type KbStats = z.infer<typeof KbStatsSchema>;
export const HelpSummarySchema = z.object({
  source: z.enum(['Team', 'ClientFaq']), id: z.string(), title: z.string(), category: z.string().nullable(), excerpt: z.string(),
});
export type HelpSummary = z.infer<typeof HelpSummarySchema>;
export const HelpArticleSchema = z.object({
  source: z.enum(['Team', 'ClientFaq']), id: z.string(), title: z.string(), body: z.string(), category: z.string().nullable(), updatedAt: z.string(),
});
export type HelpArticle = z.infer<typeof HelpArticleSchema>;

const DeviceChoiceSchema = z.object({
  id: z.string(), name: z.string(), type: z.string().nullable(), identifier: z.string().nullable(),
  isActive: z.boolean(), fromPsa: z.boolean(),
  /** Why this device cannot be this ticket's, when it cannot. */
  unavailable: z.string().nullable(),
});
export type DeviceChoice = z.infer<typeof DeviceChoiceSchema>;
const DeviceDetailSchema = z.object({
  device: DeviceSchema,
  tickets: z.array(z.object({
    id: z.string(), reference: z.string(), title: z.string(), status: z.string(), isOpen: z.boolean(), raisedAt: z.string(),
  })),
});
export type DeviceDetail = z.infer<typeof DeviceDetailSchema>;
export type Device = z.infer<typeof DeviceSchema>;
export type DeviceInput = { id?: string; name: string; type?: string | null; identifier?: string | null; notes?: string | null };

const BusinessHoursSchema = z.object({ timeZone: z.string().nullable(), scheduleJson: z.string(), notes: z.string().nullable() });
export type BusinessHours = z.infer<typeof BusinessHoursSchema>;

// ---- Control panel schemas ----
const CapabilitiesSchema = z.object({
  isCompanyAdministrator: z.boolean(),
  clientCompanyId: z.string(),
  companyName: z.string(),
  sections: z.array(z.string()),
});
export type Capabilities = z.infer<typeof CapabilitiesSchema>;

const InstructionSchema = z.object({
  clientCompanyId: z.string().nullable(),
  scope: z.string(),
  accountName: z.string(),
  body: z.string(),
  lastEditedBy: z.string().nullable(),
  updatedAt: z.string().nullable(),
});
export type Instruction = z.infer<typeof InstructionSchema>;

const InstructionsViewSchema = z.object({
  global: InstructionSchema,
  accounts: z.array(InstructionSchema),
});
export type InstructionsView = z.infer<typeof InstructionsViewSchema>;

const AccessGrantSchema = z.object({ section: z.string(), clientCompanyId: z.string().nullable() });
const ClientUserSchema = z.object({
  id: z.string(),
  email: z.string(),
  displayName: z.string(),
  isCompanyAdministrator: z.boolean(),
  isActive: z.boolean(),
  grants: z.array(AccessGrantSchema),
});
export type ClientUser = z.infer<typeof ClientUserSchema>;

const AssigneeOptionsSchema = z.object({
  queueOrBoardId: z.string().nullable(),
  filteredByRole: z.boolean(),
  filteredByQueue: z.boolean(),
  queuesOrBoards: z.array(z.object({ value: z.string(), label: z.string() })),
  technicians: z.array(z.object({
    id: z.string(),
    name: z.string(),
    roles: z.array(z.string()),
    roleOptions: z.array(z.object({ id: z.string(), name: z.string() })),
  })),
  // Portal staff, listed separately from the PSA's technicians rather than merged with them.
  // Assigning one of these does not touch the provider and their name never appears there.
  portalTechnicians: z.array(z.object({
    id: z.string(),
    name: z.string(),
    email: z.string(),
  })).default([]),
  // The teams a ticket can be routed to, named with their department: "Level 2" means little on its
  // own when two departments each have one.
  teams: z.array(z.object({ id: z.string(), name: z.string(), department: z.string() })).default([]),
});
export type AssigneeOptions = z.infer<typeof AssigneeOptionsSchema>;

const RoleOptionSchema = z.object({ id: z.string(), name: z.string() });
export type RoleOption = z.infer<typeof RoleOptionSchema>;

const DepartmentOptionSchema = z.object({ id: z.string(), name: z.string() });
export type DepartmentOption = z.infer<typeof DepartmentOptionSchema>;

const TeamOptionSchema = z.object({ id: z.string(), name: z.string(), departmentId: z.string() });
export type TeamOption = z.infer<typeof TeamOptionSchema>;

const DepartmentWithTeamsSchema = z.object({ id: z.string(), name: z.string(), teams: z.array(TeamOptionSchema) });
export type DepartmentWithTeams = z.infer<typeof DepartmentWithTeamsSchema>;

// A board is a PSA-synced queue/board name, not a stored entity — grouped by connection because
// the same board name under two connections doesn't mean the same board.
const BoardOptionSchema = z.object({ psaConnectionId: z.string(), connectionName: z.string(), boardName: z.string() });
export type BoardOption = z.infer<typeof BoardOptionSchema>;

// baseRoleType is a raw enum (no JsonStringEnumConverter registered on the API), so it serializes
// as its underlying number, not a name.
const PermissionTemplateOptionSchema = z.object({
  id: z.string(), name: z.string(), description: z.string().nullable(), baseRoleType: z.number(),
});
export type PermissionTemplateOption = z.infer<typeof PermissionTemplateOptionSchema>;

const TeamManageSchema = z.object({
  id: z.string(), departmentId: z.string(), name: z.string(), isActive: z.boolean(), sortOrder: z.number(), userCount: z.number(),
});
export type TeamManage = z.infer<typeof TeamManageSchema>;

const DepartmentManageSchema = z.object({
  id: z.string(), name: z.string(), description: z.string().nullable(), isActive: z.boolean(), isSystemDefault: z.boolean(),
  sortOrder: z.number(), teams: z.array(TeamManageSchema), primaryUserCount: z.number(), secondaryUserCount: z.number(),
});
export type DepartmentManage = z.infer<typeof DepartmentManageSchema>;

const PermissionDefinitionSchema = z.object({
  key: z.string(), module: z.string(), displayName: z.string(),
  supportedScopes: z.array(z.number()), defaultScope: z.number(), isBoardAware: z.boolean(),
});
export type PermissionDefinition = z.infer<typeof PermissionDefinitionSchema>;

const RoleGrantSchema = z.object({ permissionKey: z.string(), scope: z.number() });
export type RoleGrant = z.infer<typeof RoleGrantSchema>;

const RoleDetailSchema = z.object({
  id: z.string(), name: z.string(), isSystemRole: z.boolean(), builtInType: z.number().nullable(),
  userCount: z.number(), heldByCaller: z.boolean(), grants: z.array(RoleGrantSchema),
});
export type RoleDetail = z.infer<typeof RoleDetailSchema>;

const UserEffectivePermissionSchema = z.object({
  userId: z.string(), displayName: z.string(), email: z.string(), photoUrl: z.string().nullable(),
  isActive: z.boolean(), scope: z.number(), source: z.string(), boardAccessMode: z.string(),
  viaRoles: z.array(z.string()),
});
export type UserEffectivePermission = z.infer<typeof UserEffectivePermissionSchema>;

// BoardAccessMode: All = 0, Selected = 1, None = 2.
export const BoardAccessMode = { All: 0, Selected: 1, None: 2 } as const;

// BoardAction: a [Flags] bitmask — combine with | when granting more than one.
export const BoardAction = { View: 1, Create: 2, Edit: 4, Assign: 8, Close: 16, Delete: 32, Manage: 64 } as const;

// PermissionScope, for reading EffectivePermission.scope back out.
export const PermissionScope = { All: 0, Department: 10, Team: 20, Assigned: 30, Own: 40, Selected: 50, None: 60 } as const;

const UserSummarySchema = z.object({
  id: z.string(),
  email: z.string(),
  displayName: z.string(),
  isActive: z.boolean(),
  signInLinked: z.boolean(),
  roles: z.array(RoleOptionSchema),
  phoneNumber: z.string().nullable(),
  location: z.string().nullable(),
  photoUrl: z.string().nullable(),
  managerId: z.string().nullable(),
  managerName: z.string().nullable(),
  primaryDepartment: DepartmentOptionSchema.nullable(),
  secondaryDepartments: z.array(DepartmentOptionSchema),
  teams: z.array(TeamOptionSchema),
  boardAccessMode: z.number(),
  boardGrants: z.array(BoardOptionSchema),
  lastActiveAt: z.string().nullable(),
  createdAt: z.string(),
});
export type UserSummary = z.infer<typeof UserSummarySchema>;

const UserSummaryCountsSchema = z.object({ total: z.number(), active: z.number(), pending: z.number(), administrators: z.number() });
export type UserSummaryCounts = z.infer<typeof UserSummaryCountsSchema>;

const UserListResultSchema = z.object({
  users: z.array(UserSummarySchema), totalMatching: z.number(), page: z.number(), pageSize: z.number(),
  summary: UserSummaryCountsSchema,
});
export type UserListResult = z.infer<typeof UserListResultSchema>;

export type UserListParams = {
  search?: string; roleId?: string; departmentId?: string; teamId?: string; boardName?: string;
  isActive?: boolean; page?: number; pageSize?: number;
};

// scope is a raw PermissionScope enum (number); source/boardAccessMode were already .ToString()'d
// server-side, so those two are plain strings.
const EffectivePermissionSchema = z.object({
  permissionKey: z.string(), module: z.string(), displayName: z.string(),
  scope: z.number(), source: z.string(), isBoardAware: z.boolean(), boardAccessMode: z.string(),
});
export type EffectivePermission = z.infer<typeof EffectivePermissionSchema>;

const BulkUserRowResultSchema = z.object({ userId: z.string(), success: z.boolean(), reason: z.string().nullable() });
const BulkUserActionResultSchema = z.object({ rows: z.array(BulkUserRowResultSchema) });
export type BulkUserActionResult = z.infer<typeof BulkUserActionResultSchema>;
export type BulkUserRowResult = z.infer<typeof BulkUserRowResultSchema>;

export type BulkUserActionName =
  'AssignRole' | 'RemoveRole' | 'AssignDepartment' | 'AssignTeam' | 'Activate' | 'Deactivate' | 'Delete';

// Order must match the C# BulkUserAction enum exactly — sent as a number, no string converter on the API.
const BulkUserActionValue: Record<BulkUserActionName, number> = {
  AssignRole: 0, RemoveRole: 1, AssignDepartment: 2, AssignTeam: 3, Activate: 4, Deactivate: 5, Delete: 6,
};

export type BulkUserActionInput = {
  action: BulkUserActionName;
  userIds: string[];
  roleId?: string;
  departmentId?: string;
  teamId?: string;
};

const UnsyncedTicketSchema = z.object({
  ticketId: z.string(),
  psaConnectionId: z.string(),
  connectionName: z.string(),
  title: z.string(),
  customerName: z.string().nullable(),
  syncStatus: z.string(),
  syncError: z.string().nullable(),
  createdAt: z.string(),
  lastAttemptAt: z.string().nullable(),
});
const UnsyncedTicketsSchema = z.object({ count: z.number(), tickets: z.array(UnsyncedTicketSchema) });
export type UnsyncedTicket = z.infer<typeof UnsyncedTicketSchema>;

const ResyncResultSchema = z.object({
  success: z.boolean(),
  ticketId: z.string(),
  externalTicketId: z.string().nullable(),
  error: z.string().nullable(),
});

export type TicketPageParams = {
  q?: string; boardId?: string; status?: string; priority?: string; openness?: string | null;
  mine?: boolean; following?: boolean; unassigned?: boolean; overdue?: boolean; dueSoon?: boolean;
  company?: string; queue?: string; source?: string; person?: string; from?: string; kind?: string;
  skip?: number; take?: number;
  /** "pending": resolved work waiting for a board lead's review. */
  review?: string;
};
const TicketPageSchema = z.object({
  items: z.array(TicketListItemSchema), total: z.number(), skip: z.number(), take: z.number(),
  hoursWorked: z.number(), hoursBillable: z.number(),
});
export type TicketPage = z.infer<typeof TicketPageSchema>;
const TicketFacetsSchema = z.object({
  statuses: z.array(z.string()), priorities: z.array(z.string()), companies: z.array(z.string()),
  queues: z.array(z.string()), sources: z.array(z.string()),
  people: z.array(z.object({ key: z.string(), name: z.string(), holds: z.boolean() })),
});
export type TicketFacets = z.infer<typeof TicketFacetsSchema>;
const LabelCountSchema = z.object({ label: z.string(), count: z.number() });
export type LabelCount = z.infer<typeof LabelCountSchema>;
const TicketBreakdownSchema = z.object({
  total: z.number(), open: z.number(), byPriority: z.array(LabelCountSchema), byQueue: z.array(LabelCountSchema),
});
export type TicketBreakdown = z.infer<typeof TicketBreakdownSchema>;
const TicketSummarySchema = z.object({
  open: z.number(), overdue: z.number(), dueToday: z.number(), dueSoon: z.number(), waiting: z.number(),
  highPriority: z.number(), unassigned: z.number(), resolvedLast7Days: z.number(),
  openByPriority: z.array(LabelCountSchema), openBySource: z.array(LabelCountSchema),
  hoursLoggedThisWeek: z.number().nullable(),
});
export type TicketSummary = z.infer<typeof TicketSummarySchema>;
const TeamWorkloadSchema = z.object({
  people: z.array(z.object({
    key: z.string(), name: z.string(), open: z.number(), overdue: z.number(), highPriority: z.number(),
    stale: z.number(), oldestRaisedAt: z.string().nullable(),
  })),
  unassigned: z.number(), unassignedOverdue: z.number(), stale: z.number(), staleDays: z.number(),
  awaitingReview: z.number().default(0),
});
export type TeamWorkload = z.infer<typeof TeamWorkloadSchema>;

const TimeEntrySchema = z.object({
  externalId: z.string(),
  hours: z.number(),
  billable: z.boolean(),
  entryDate: z.string(),
  notes: z.string().nullable(),
  technician: z.string().nullable().default(null),
  technicianName: z.string().nullable().default(null),
  workType: z.string().nullable().default(null),
  billableOption: z.string().default('Billable'),
  // Which system the entry was logged in, and whether it actually reached the PSA.
  source: z.string().default('Provider'),
  syncStatus: z.string().default('Synced'),
  syncError: z.string().nullable().default(null),
  // Whether this person may edit or delete it: their own time, or they lead the boards.
  mayChange: z.boolean().default(false),
});
export type TimeEntry = z.infer<typeof TimeEntrySchema>;

export const TicketLinkSchema = z.object({
  id: z.string(), relation: z.string(), otherTicketId: z.string(), otherReference: z.string().nullable(),
  otherTitle: z.string(), otherStatus: z.string(), otherOrigin: z.number(),
});
export type TicketLink = z.infer<typeof TicketLinkSchema>;

export const TicketHistoryEntrySchema = z.object({
  at: z.string(), who: z.string().nullable(), kind: z.string(), summary: z.string(), note: z.string().nullable().default(null),
});
export type TicketHistoryEntry = z.infer<typeof TicketHistoryEntrySchema>;
export type BoardTicketEdit = {
  title: string; description: string | null; priority: string; dueAt: string | null;
  boardTopicId: string | null; category: string | null; departmentId: string | null; clientCompanyId: string | null;
};

const TimeAggregateSchema = z.object({
  count: z.number(),
  timeWorkedHours: z.number(),
  billableHours: z.number(),
  nonBillableHours: z.number(),
});

const TicketNoteResponse = z.object({
  id: z.string(), authorName: z.string(), authoredByClient: z.boolean(),
  body: z.string(), createdAt: z.string(),
});

export const AttentionItemSchema = z.object({
  kind: z.string(),
  severity: z.enum(['critical', 'warning']),
  title: z.string(),
  detail: z.string(),
  count: z.number(),
  link: z.string().nullable(),
});
export type AttentionItem = z.infer<typeof AttentionItemSchema>;
export const AttentionSchema = z.object({
  items: z.array(AttentionItemSchema),
  digest: z.object({ recipients: z.string().nullable(), lastSentOn: z.string().nullable() }),
  checkedAt: z.string(),
});
export type Attention = z.infer<typeof AttentionSchema>;

/** A board the team works on that is not a PSA queue. Kind 0 is the team's own work, 1 is monitoring. */
export const BoardSchema = z.object({
  id: z.string(),
  name: z.string(),
  key: z.string(),
  description: z.string().nullable(),
  kind: z.number(),
  clientVisible: z.boolean(),
  isActive: z.boolean(),
  sortOrder: z.number(),
  memberCount: z.number(),
  openTickets: z.number(),
  defaultSlaPlanId: z.string().nullable().default(null),
  defaultSlaPlanName: z.string().nullable().default(null),
  requireResolution: z.boolean().default(false),
  requireReview: z.boolean().default(false),
});
export type Board = z.infer<typeof BoardSchema>;
export const BoardMemberSchema = z.object({
  appUserId: z.string(), displayName: z.string(), email: z.string(),
});
export type BoardMember = z.infer<typeof BoardMemberSchema>;
export type BoardInput = {
  name: string; key: string; description: string | null;
  kind?: number; clientVisible?: boolean; sortOrder?: number;
  /** The SLA plan a ticket gets when its topic names none. Sent on every save: omitting it clears it. */
  defaultSlaPlanId?: string | null;
  /** Resolving a ticket here needs a written resolution. Sent on every save: omitting it switches it off. */
  requireResolution?: boolean;
  /** Resolved work here is reviewed by a board lead before it closes. Sent on every save. */
  requireReview?: boolean;
};
export type InternalTicketInput = {
  boardId: string; title: string; description: string | null;
  priority?: string | null; category?: string | null;
  clientCompanyId?: string | null; assignedAppUserId?: string | null; dueAt?: string | null;
  /** What it is about; fills in department, priority, assignee and due date unless stated. */
  boardTopicId?: string | null; departmentId?: string | null; source?: string | null;
};

export const BoardTopicSchema = z.object({
  id: z.string(),
  boardId: z.string(),
  name: z.string(),
  defaultDepartmentId: z.string().nullable(),
  defaultDepartmentName: z.string().nullable(),
  defaultPriority: z.string().nullable(),
  defaultAssigneeUserId: z.string().nullable(),
  defaultAssigneeName: z.string().nullable(),
  dueInHours: z.number().nullable(),
  isActive: z.boolean(),
  sortOrder: z.number(),
  slaPlanId: z.string().nullable().default(null),
  slaPlanName: z.string().nullable().default(null),
  requireReview: z.boolean().default(false),
});
export type BoardTopic = z.infer<typeof BoardTopicSchema>;
export type BoardTopicInput = {
  name: string; defaultDepartmentId?: string | null; defaultPriority?: string | null;
  defaultAssigneeUserId?: string | null; dueInHours?: number | null; sortOrder?: number;
  slaPlanId?: string | null;
  /** This kind of work is reviewed before it closes. Sent on every save: omitting it switches it off. */
  requireReview?: boolean;
};

/** How quickly board work is owed. Working days are a bitmask with Sunday as bit 0. */
export const SlaPlanSchema = z.object({
  id: z.string(),
  name: z.string(),
  resolveWithinHours: z.number(),
  firstResponseWithinHours: z.number().nullable(),
  businessHoursOnly: z.boolean(),
  workdayStartHour: z.number(),
  workdayEndHour: z.number(),
  workingDays: z.number(),
  isActive: z.boolean(),
  sortOrder: z.number(),
  usedBy: z.number(),
  skipHolidays: z.boolean().default(true),
  pauseWhileWaiting: z.boolean().default(true),
});
export type SlaPlan = z.infer<typeof SlaPlanSchema>;
export type SlaPlanInput = {
  name: string; resolveWithinHours: number; firstResponseWithinHours: number | null;
  businessHoursOnly: boolean; workdayStartHour: number; workdayEndHour: number; workingDays: number;
  sortOrder?: number; skipHolidays: boolean; pauseWhileWaiting: boolean;
};

/** One approval request on a ticket, as staff and the client both see it. */
export const TicketApprovalSchema = z.object({
  id: z.string(),
  approverName: z.string(),
  request: z.string(),
  requestedByName: z.string(),
  requestedAt: z.string(),
  state: z.enum(['Pending', 'Approved', 'Rejected', 'Cancelled']),
  decidedAt: z.string().nullable(),
  decisionComment: z.string().nullable(),
  /** "Portal" when the approver clicked; otherwise how the technician says the answer came. */
  channel: z.enum(['Portal', 'Phone', 'Email', 'InPerson']).nullable(),
  recordedByName: z.string().nullable(),
  canAnswer: z.boolean(),
});
export type TicketApproval = z.infer<typeof TicketApprovalSchema>;

export const StaffApprovalsSchema = z.object({
  applies: z.boolean(),
  canAsk: z.boolean(),
  reason: z.string().nullable(),
  approvals: z.array(TicketApprovalSchema),
  approvers: z.array(z.object({
    id: z.string(), name: z.string(), email: z.string().nullable(), scope: z.string().nullable(),
    canAnswerInPortal: z.boolean(),
  })),
  missingApprovers: z.boolean().default(false),
});
export type StaffApprovals = z.infer<typeof StaffApprovalsSchema>;

export const MyApprovalSchema = z.object({
  id: z.string(), ticketId: z.string(), reference: z.string(), ticketTitle: z.string(),
  request: z.string(), requestedByName: z.string(), requestedAt: z.string(),
});
export type MyApproval = z.infer<typeof MyApprovalSchema>;

/** What a client sees under a finished ticket: whether they can rate it, and their answer. */
export const SatisfactionStateSchema = z.object({
  canRate: z.boolean(),
  reason: z.string().nullable(),
  rating: z.number().nullable(),
  comment: z.string().nullable(),
  ratedAt: z.string().nullable(),
  openUntil: z.string().nullable(),
});
export type SatisfactionState = z.infer<typeof SatisfactionStateSchema>;

const SatisfactionGroupSchema = z.object({
  key: z.string(), name: z.string(), ratings: z.number(), satisfied: z.number(),
  csatPct: z.number().nullable(), average: z.number(),
});
/** CSAT is the share of ratings that are 4 or 5; null when nobody rated anything. */
export const SatisfactionSummarySchema = z.object({
  ratings: z.number(),
  satisfied: z.number(),
  csatPct: z.number().nullable(),
  average: z.number().nullable(),
  distribution: z.array(z.number()),
  byTechnician: z.array(SatisfactionGroupSchema),
  byClient: z.array(SatisfactionGroupSchema),
  recent: z.array(z.object({
    ticketId: z.string(), reference: z.string(), title: z.string(), rating: z.number(),
    comment: z.string().nullable(), clientName: z.string().nullable(), technicianName: z.string().nullable(),
    ratedAt: z.string(),
  })),
});
export type SatisfactionSummary = z.infer<typeof SatisfactionSummarySchema>;

/** A day the desk is closed. Working-hours SLA plans step over these. */
export const DeskHolidaySchema = z.object({ id: z.string(), date: z.string(), name: z.string() });
export type DeskHoliday = z.infer<typeof DeskHolidaySchema>;

/** Frequency: 0 daily, 1 weekdays, 2 weekly, 3 monthly. dayOfMonth 0 means the last day. */
export const RecurringTicketSchema = z.object({
  id: z.string(),
  boardId: z.string(),
  boardName: z.string(),
  title: z.string(),
  description: z.string().nullable(),
  boardTopicId: z.string().nullable(),
  topicName: z.string().nullable(),
  priority: z.string().nullable(),
  departmentId: z.string().nullable(),
  assignedAppUserId: z.string().nullable(),
  assignedName: z.string().nullable(),
  clientCompanyId: z.string().nullable(),
  checklist: z.string().nullable(),
  frequency: z.number(),
  dayOfWeek: z.number(),
  dayOfMonth: z.number(),
  hour: z.number(),
  skipIfOpen: z.boolean(),
  isActive: z.boolean(),
  nextRunAt: z.string(),
  lastRunAt: z.string().nullable(),
  lastTicketId: z.string().nullable(),
  lastTicketNumber: z.string().nullable(),
  lastOutcome: z.string().nullable(),
  schedule: z.string(),
  createdByName: z.string().nullable(),
});
export type RecurringTicket = z.infer<typeof RecurringTicketSchema>;
export type RecurringTicketInput = {
  boardId: string; title: string; description: string | null; boardTopicId: string | null;
  priority: string | null; assignedAppUserId: string | null; checklist: string | null;
  frequency: number; dayOfWeek: number; dayOfMonth: number; hour: number; skipIfOpen: boolean;
};

/** A reply the desk keeps. boardId null means it is offered on every ticket, PSA ones included. */
export const CannedResponseSchema = z.object({
  id: z.string(),
  name: z.string(),
  body: z.string(),
  boardId: z.string().nullable(),
  boardName: z.string().nullable(),
  isActive: z.boolean(),
  sortOrder: z.number(),
});
export type CannedResponse = z.infer<typeof CannedResponseSchema>;
export type CannedResponseInput = { name: string; body: string; boardId: string | null; sortOrder?: number };

/** One step of the work inside a ticket. Staff only; never sent to a PSA. */
export const TicketTaskSchema = z.object({
  id: z.string(),
  title: z.string(),
  isDone: z.boolean(),
  doneAt: z.string().nullable(),
  doneByName: z.string().nullable(),
  assignedAppUserId: z.string().nullable(),
  assignedName: z.string().nullable(),
  sortOrder: z.number(),
  createdAt: z.string(),
});
export type TicketTask = z.infer<typeof TicketTaskSchema>;

/** A monitoring tool allowed to open tickets on a board. Vendor 0 generic, 1 NinjaOne, 2 Datto RMM. */
export const AlertSourceSchema = z.object({
  id: z.string(),
  name: z.string(),
  boardId: z.string(),
  boardName: z.string(),
  vendor: z.number(),
  keyHint: z.string(),
  isActive: z.boolean(),
  closeOnClear: z.boolean(),
  lastReceivedAt: z.string().nullable(),
  receivedCount: z.number(),
  lastError: z.string().nullable(),
  // The one client this tool reports on; null means the client is taken from each alert's text.
  clientCompanyId: z.string().nullable().optional(),
  clientName: z.string().nullable().optional(),
});
export type AlertSource = z.infer<typeof AlertSourceSchema>;
/** The key is here once and never again: it is not stored in a form anyone can read back. */
export const AlertSourceCreatedSchema = z.object({ source: AlertSourceSchema, key: z.string() });
export type AlertSourceInput = { name: string; boardId: string; vendor?: number; closeOnClear?: boolean; clientCompanyId?: string | null };
