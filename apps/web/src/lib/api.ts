import { z } from 'zod';
import {
  TicketListItemSchema, TicketDetailSchema, NotificationSchema, ProfileSchema,
  TechnicianResponseSchema, TeamResponseSchema, TrendPointSchema,
  ConnectionSummarySchema, HealthSchema, JobSchema, AuditEntrySchema, AttachmentSchema,
  TechnicianDaySchema, type TechnicianDay,
  ConnectionFieldsSchema, FieldOptionSchema, type ConnectionFields,
  MappingRuleSchema, type MappingRule,
  type TicketDetail, type TicketListItem, type Notification, type Profile,
  type TechnicianResponse, type TeamResponse, type TrendPoint,
  type ConnectionSummary, type Health, type Job, type AuditEntry,
} from './types';

// All API calls go through the same-origin BFF proxy, which attaches the bearer token from the
// httpOnly session cookie server-side. No token is ever held in client JavaScript.
const BFF_BASE = '/api/bff';

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
  email: z.string().nullable(),
  displayName: z.string().nullable(),
  organizationId: z.string().nullable(),
  isPlatformScope: z.boolean(),
  permissions: z.array(z.string()),
});


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
  getTicket: (id: string) => request(`/api/tickets/${id}`, TicketDetailSchema) as Promise<TicketDetail>,
  createTicket: (body: { title: string; description?: string; priority?: string; queueOrBoard?: string }) =>
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
  }) =>
    request(`/api/tickets/${id}/assignment`,
      z.object({
        assignedAppUserId: z.string().nullable().default(null),
        assignedTechnicianExternalId: z.string().nullable(),
        assignedTechnicianName: z.string().nullable(),
        queueOrBoard: z.string().nullable(),
      }).passthrough(),
      { method: 'PUT', body: JSON.stringify(body) }),
  updateTicketStatus: (id: string, status: string) =>
    request(`/api/tickets/${id}/status`, z.object({ portalStatus: z.string() }), { method: 'POST', body: JSON.stringify({ status }) }),
  // Same aggregate response as update/retry/delete — the endpoint recomputes the ticket's totals
  // from the PSA and returns those, not the created entry. The old schema here demanded an
  // externalId the response never carried, so every SUCCESSFUL log threw at the parse and showed
  // as a failure while the entry quietly landed in the PSA.
  logTime: (id: string, body: { hours: number; billable: string; notes?: string; workType?: string; workRole?: string; noteId?: string }) =>
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
  storageUsage: () =>
    request('/api/admin/storage', z.object({ usedBytes: z.number(), fileCount: z.number(), ticketCount: z.number() })),
  profile: () => request('/api/profile', ProfileSchema) as Promise<Profile>,
  updateProfile: (body: { displayName: string; email: string }) =>
    request('/api/profile', ProfileSchema, { method: 'PUT', body: JSON.stringify(body) }) as Promise<Profile>,

  technicianMetrics: () => request('/api/dashboard/technician', TechnicianResponseSchema) as Promise<TechnicianResponse>,
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
  health: () => request('/api/admin/health', z.array(HealthSchema)) as Promise<Health[]>,
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

const DeviceSchema = z.object({ id: z.string(), name: z.string(), type: z.string().nullable(), identifier: z.string().nullable(), notes: z.string().nullable() });
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
});
export type TimeEntry = z.infer<typeof TimeEntrySchema>;

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
