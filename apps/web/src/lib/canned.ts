/**
 * The placeholders a canned response may use, and how they are filled. One list, read by both the
 * page that manages responses (to show what is available) and the reply box (to fill them in), so
 * the two can never disagree about what {requester} means.
 *
 * A placeholder with nothing to fill it is left visible rather than blanked: "Hi {requester}," is an
 * obvious gap to fix before sending, "Hi ," is a mistake that goes out.
 */
export const PLACEHOLDERS: { token: string; meaning: string }[] = [
  { token: '{ticket.number}', meaning: 'The ticket number — INT-000123, or the PSA reference' },
  { token: '{ticket.title}', meaning: 'The ticket subject' },
  { token: '{customer}', meaning: 'The client company' },
  { token: '{requester}', meaning: 'Who the ticket is for' },
  { token: '{assignee}', meaning: 'Who is working it' },
  { token: '{me}', meaning: 'Your own name' },
];

export type PlaceholderValues = Partial<Record<string, string | null | undefined>>;

export function fillPlaceholders(body: string, values: PlaceholderValues): string {
  return body.replace(/\{(ticket\.number|ticket\.title|customer|requester|assignee|me)\}/g, (whole, key: string) => {
    const value = values[key];
    return value && value.trim() ? value : whole;
  });
}
