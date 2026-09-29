/**
 * The two ticket views that make someone staff. An administrator holds tickets.view.all; a Standard
 * Technician holds tickets.view.assigned instead. Client roles hold neither - they have
 * view.own / view.own_company. Mirrors Permissions.StaffTicketViews on the API.
 */
export const STAFF_TICKET_VIEWS = ['tickets.view.all', 'tickets.view.assigned'] as const;

export function isStaffPermissions(permissions: readonly string[] | Set<string>): boolean {
  const has = (p: string) => (permissions instanceof Set ? permissions.has(p) : permissions.includes(p));
  return STAFF_TICKET_VIEWS.some(has);
}
