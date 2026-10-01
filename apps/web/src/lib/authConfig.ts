// Server-side OIDC configuration. None of these are NEXT_PUBLIC — secrets and the API base stay on
// the server (the BFF pattern), so the browser never sees a token.

export const authConfig = {
  issuer: process.env.KEYCLOAK_ISSUER ?? 'http://localhost:8081/realms/desk',
  clientId: process.env.KEYCLOAK_CLIENT_ID ?? 'desk-web',
  // desk-web is a PUBLIC client (PKCE, no secret). Set only if you switch it to confidential.
  clientSecret: process.env.KEYCLOAK_CLIENT_SECRET ?? '',
  appUrl: process.env.APP_URL ?? 'http://localhost:3000',
  apiBase: process.env.DESK_API_BASE ?? 'http://localhost:5080',
  scope: 'openid profile email',
} as const;

export const oidc = {
  authorize: `${authConfig.issuer}/protocol/openid-connect/auth`,
  token: `${authConfig.issuer}/protocol/openid-connect/token`,
  logout: `${authConfig.issuer}/protocol/openid-connect/logout`,
  redirectUri: `${authConfig.appUrl}/api/auth/callback`,
} as const;

export const cookies = {
  access: 'desk_at',
  refresh: 'desk_rt',
  idToken: 'desk_it',
  // Who an administrator is viewing the portal as ("u:<id>" / "c:<id>"). httpOnly: page scripts can
  // neither read nor set it. It grants nothing: the API re-checks the administrator on every request.
  viewAs: 'desk_view_as',
  verifier: 'desk_pkce',
  state: 'desk_state',
} as const;

export const isProd = process.env.NODE_ENV === 'production';
