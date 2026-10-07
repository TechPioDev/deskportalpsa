/**
 * Which company a client user with more than one has chosen to look at.
 *
 * Kept in a cookie so that every request the browser makes carries it, uploads and downloads
 * included, without each caller having to remember to. The web app's proxy turns it into the
 * `X-Desk-Company` header; the browser cannot send that header itself.
 *
 * It is a choice and not a permission. The API checks it, on every request, against the companies
 * the person has been given, and refuses one that is not among them however it got here. Nothing
 * secret is in it and a page script may read and set it.
 */
export const ACTING_COMPANY_COOKIE = 'desk_company';

const ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The company id in a cookie header, or null: none chosen, or not an id. */
export function readActingCompany(cookieHeader: string | null | undefined): string | null {
  if (!cookieHeader) return null;
  for (const part of cookieHeader.split(';')) {
    const at = part.indexOf('=');
    if (at < 0 || part.slice(0, at).trim() !== ACTING_COMPANY_COOKIE) continue;
    const value = decodeURIComponent(part.slice(at + 1).trim());
    return ID.test(value) ? value.toLowerCase() : null;
  }
  return null;
}

export function actingCompany(): string | null {
  return typeof document === 'undefined' ? null : readActingCompany(document.cookie);
}

/** Chooses a company, or with null goes back to the person's own (the cookie is removed). */
export function setActingCompany(id: string | null): void {
  if (typeof document === 'undefined') return;
  const secure = window.location.protocol === 'https:' ? '; Secure' : '';
  document.cookie = id && ID.test(id)
    ? `${ACTING_COMPANY_COOKIE}=${encodeURIComponent(id.toLowerCase())}; Path=/; Max-Age=${60 * 60 * 24 * 30}; SameSite=Lax${secure}`
    : `${ACTING_COMPANY_COOKIE}=; Path=/; Max-Age=0; SameSite=Lax${secure}`;
}
