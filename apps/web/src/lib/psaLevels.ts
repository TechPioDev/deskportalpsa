/**
 * The levels a PSA files a ticket under, by the names that PSA gives them. ConnectWise has a type,
 * a subtype and an item; Autotask a ticket type, an issue type and a sub-issue type. The same words
 * the connection's own settings use for the defaults a new ticket is raised with.
 *
 * Only the levels the ticket has: a row of dashes on every ticket of a desk that does not use
 * sub-issues would be a question nobody asked.
 */
export type PsaClassification = { ticketType: string | null; issueType: string | null; subIssueType: string | null };

const CONNECTWISE = 1;

export function psaLevels(provider: number | null, c: PsaClassification | null): { label: string; value: string }[] {
  if (!c) return [];
  const names = provider === CONNECTWISE ? ['Type', 'Subtype', 'Item'] : ['Ticket type', 'Issue type', 'Sub-issue type'];
  return [c.ticketType, c.issueType, c.subIssueType]
    .map((value, i) => ({ label: `${names[i]} (PSA)`, value: (value ?? '').trim() }))
    .filter((level) => level.value !== '');
}
