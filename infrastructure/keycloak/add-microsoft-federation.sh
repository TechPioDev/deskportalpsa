#!/usr/bin/env bash
# Points the desk realm at Microsoft Entra ID, so staff sign in with their work account and no
# portal passwords exist at all.
#
# YOU run this, on the VPS. It prompts for the Keycloak admin password and the Entra client secret,
# uses them for this one configuration call, and stores neither. Nothing here is committed with a
# credential in it.
#
#   bash add-microsoft-federation.sh
#
# BEFORE running, create the app registration in Entra (portal.azure.com -> Microsoft Entra ID ->
# App registrations -> New registration):
#
#   Name                 Desk Portal
#   Account types        Accounts in this organizational directory only
#   Redirect URI (Web)   https://auth.piomanage.com/realms/desk/broker/oidc/endpoint
#
# Then, still in that registration:
#
#   Certificates & secrets -> New client secret        (copy the VALUE, shown once)
#   Overview                                            (copy Application (client) ID and Directory
#                                                        (tenant) ID)
#   Token configuration -> Add optional claim -> ID -> email
#
# THAT LAST STEP IS NOT OPTIONAL HERE, whatever Microsoft calls it. The portal binds a person to
# their waiting account by matching the token's email against the row an administrator created. Many
# Entra tenants omit the email claim unless it is added explicitly - users then sign in successfully,
# arrive with no email, match nothing, and land in the portal as a stranger with no permissions. The
# symptom looks like a portal bug and is not one.
set -euo pipefail

REALM=desk
ALIAS=oidc                      # must match the /broker/<alias>/endpoint redirect URI above
CONTAINER=desk-portal-prod-keycloak-1
KC=/opt/keycloak/bin/kcadm.sh

read -rp  "Keycloak admin username: " KC_ADMIN
read -rsp "Keycloak admin password: " KC_PASS; echo
read -rp  "Entra directory (tenant) ID: " TENANT
read -rp  "Entra application (client) ID: " CLIENT_ID
read -rsp "Entra client secret: " CLIENT_SECRET; echo

kc() { docker exec -i "$CONTAINER" "$KC" "$@"; }

kc config credentials --server http://localhost:8081 --realm master \
   --user "$KC_ADMIN" --password "$KC_PASS" >/dev/null

if kc get "identity-provider/instances/$ALIAS" -r "$REALM" >/dev/null 2>&1; then
  echo "An identity provider '$ALIAS' already exists in realm '$REALM'."
  echo "Delete or edit it in the admin console rather than running this again - creating a second"
  echo "one would leave two sign-in buttons and no way to tell which is live."
  exit 1
fi

BASE="https://login.microsoftonline.com/$TENANT"

kc create identity-provider/instances -r "$REALM" \
  -s "alias=$ALIAS" \
  -s providerId=oidc \
  -s enabled=true \
  -s 'displayName=Sign in with Microsoft' \
  -s storeToken=false \
  -s addReadTokenRoleOnCreate=false \
  -s linkOnly=false \
  `# Entra has already verified the address; without this Keycloak treats the email as unverified` \
  `# and tries to verify it itself, which needs SMTP this deployment does not have - and would` \
  `# block every sign-in behind an email that never arrives.` \
  -s trustEmail=true \
  -s "config.clientId=$CLIENT_ID" \
  -s "config.clientSecret=$CLIENT_SECRET" \
  -s "config.issuer=$BASE/v2.0" \
  -s "config.authorizationUrl=$BASE/oauth2/v2.0/authorize" \
  -s "config.tokenUrl=$BASE/oauth2/v2.0/token" \
  -s "config.jwksUrl=$BASE/discovery/v2.0/keys" \
  -s config.useJwksUrl=true \
  -s config.validateSignature=true \
  -s 'config.defaultScope=openid profile email' \
  -s config.clientAuthMethod=client_secret_post \
  `# FORCE: re-read name and email from Entra on EVERY login, so a rename or an address change in` \
  `# the directory follows through instead of the portal keeping whatever it saw the first time.` \
  -s config.syncMode=FORCE >/dev/null

echo "Identity provider '$ALIAS' created."

# Explicit mappers rather than relying on defaults. The email one matters most: it is the field the
# portal matches on, and a silent default is a poor thing to stake sign-in for forty people on.
add_mapper() {
  kc create "identity-provider/instances/$ALIAS/mappers" -r "$REALM" \
    -s "name=$1" -s "identityProviderAlias=$ALIAS" \
    -s identityProviderMapper=oidc-user-attribute-idp-mapper \
    -s "config.claim=$2" -s "config.user.attribute=$3" -s config.syncMode=INHERIT >/dev/null
  echo "  mapper: $2 -> $3"
}
add_mapper email-from-entra      email       email
add_mapper first-name-from-entra given_name  firstName
add_mapper last-name-from-entra  family_name lastName

echo
echo "Done. A 'Sign in with Microsoft' button now appears at https://piomanage.com."
echo
echo "Verify with ONE person before telling the team:"
echo "  1. Sign in as a technician whose address is in the portal."
echo "  2. Check they land on the dashboard rather than an 'update account' prompt - that prompt"
echo "     means the email claim did not arrive, and the optional claim above is missing."
echo "  3. Confirm the portal bound them:"
echo "     docker exec -i desk-portal-prod-postgres-1 psql -U desk -d desk_portal \\"
echo "       -c 'select \"DisplayName\", \"IdpSubject\" is not null as bound from app_users"
echo "           where \"Email\" = '\''their.address@techpio.com'\'';'"
echo
echo "'bound' being true is the whole test: the invitation has been claimed and every later sign-in"
echo "resolves by subject, so a later address change cannot silently re-bind them to someone else."
