import { NextRequest, NextResponse } from 'next/server';
import { authConfig, oidc, cookies as ck } from '@/lib/authConfig';

// Clears the session cookies and ends the Keycloak SSO session.
export async function GET(req: NextRequest) {
  const idToken = req.cookies.get(ck.idToken)?.value;

  const params = new URLSearchParams({
    post_logout_redirect_uri: authConfig.appUrl,
    client_id: authConfig.clientId,
  });
  if (idToken) params.set('id_token_hint', idToken);

  const res = NextResponse.redirect(`${oidc.logout}?${params.toString()}`);
  res.cookies.delete(ck.access);
  res.cookies.delete(ck.refresh);
  res.cookies.delete(ck.idToken);
  res.cookies.delete(ck.viewAs);
  return res;
}
