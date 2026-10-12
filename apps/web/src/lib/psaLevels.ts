/**
 * The levels a PSA files a ticket under, by the names that PSA gives them. ConnectWise has a type,
 * a subtype and an item; Autotask a ticket type, an issue type and a sub-issue type. The same words
 * the connection's own settings use for the defaults a new ticket is raised with.
 *
 * Only the levels the ticket has: a row of dashes on every ticket of a desk that does not use
 * sub-issues would be a question nobody asked.
 */
export type PsaClassification = {
  ticketType: string | null; issueType: string | null; subIssueType: string | null;
  workType?: string | null; subcategory?: string | null; mapped?: boolean | null;
};

const CONNECTWISE = 1;

/** The PSA's own names for its three levels. A provider arrives as its number on a ticket and as its name on a connection. */
export function psaLevelNames(provider: number | string | null): [string, string, string] {
  const connectWise = provider === CONNECTWISE || (typeof provider === 'string' && provider.toLowerCase().includes('connectwise'));
  return connectWise ? ['Type', 'Subtype', 'Item'] : ['Ticket type', 'Issue type', 'Sub-issue type'];
}

export function psaLevels(provider: number | null, c: PsaClassification | null): { label: string; value: string }[] {
  if (!c) return [];
  const names = psaLevelNames(provider);
  return [c.ticketType, c.issueType, c.subIssueType]
    .map((value, i) => ({ label: `${names[i]} (PSA)`, value: (value ?? '').trim() }))
    .filter((level) => level.value !== '');
}

/**
 * What the connection's classification rules make of those levels, in the portal's own words.
 *
 * A work type or a subcategory is listed only where a rule gave one. A ticket the rules do not
 * name says so, in so many words: "Unmapped" is not a blank that could be read as "none", and
 * nothing is shown in a rule's place. A connection with no rules at all says nothing here, because
 * a desk that has not taken the rules up has not asked the question.
 */
export function portalClassification(c: PsaClassification | null): { label: string; value: string }[] {
  if (!c) return [];
  const rows = [
    { label: 'Work type', value: (c.workType ?? '').trim() },
    { label: 'Subcategory', value: (c.subcategory ?? '').trim() },
  ].filter((row) => row.value !== '');
  if (c.mapped === false) rows.push({ label: 'Portal classification', value: 'Unmapped' });
  return rows;
}
