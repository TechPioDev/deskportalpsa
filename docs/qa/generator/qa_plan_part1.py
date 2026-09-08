# -*- coding: utf-8 -*-
"""Builds the Desk Portal / PIO Manage master test plan PDF."""
from xml.sax.saxutils import escape as _esc


def E(s):
    return _esc(str(s))


# Each case: (id, title, steps, expected, verify)
MODULES = []


def mod(name, intro, cases):
    MODULES.append((name, intro, cases))


mod("1. Environment, Build and Deployment",
    "Prove the thing under test is the thing you think it is. Every wasted QA hour this project "
    "has spent began with testing a build that predated the fix.",
    [
     ("ENV-01", "Deployed build matches the intended commit",
      "On the VPS run: cd /opt/deskportal && git log --oneline -1. Compare with the commit you "
      "expect. Then check the running image is newer than that commit: "
      "docker inspect -f '{{.Created}}' $(docker inspect -f '{{.Image}}' desk-portal-prod-api-1)",
      "Repo HEAD equals the intended commit AND the image was built after it.",
      "SSH + docker inspect. If the image predates the commit, the deploy did not rebuild - "
      "re-run compose up -d --build before testing anything else."),
     ("ENV-02", "All containers healthy",
      "docker ps --format '{{.Names}}\\t{{.Status}}' | grep desk-portal",
      "api, worker, web, postgres, keycloak all Up. Postgres reports (healthy).",
      "Any container restarting in a loop usually means a pending EF migration - check "
      "docker logs desk-portal-prod-api-1 for PendingModelChangesWarning."),
     ("ENV-03", "Database migrations are applied and current",
      "Restart the API and watch its log for migration output: docker logs desk-portal-prod-api-1 "
      "--since 5m | grep -i migrat",
      "Migrations apply cleanly, or report nothing pending. No PendingModelChangesWarning.",
      "API log. Local mode uses SQLite EnsureCreated and hides schema drift, so this must be "
      "checked against Postgres, never only locally."),
     ("ENV-04", "Stale browser tab is detected",
      "Open the dashboard, leave the tab open, deploy a new build, then focus the tab and wait "
      "up to 3 minutes.",
      "A Reload toast appears (UpdateWatchdog polls /version against BUILD_ID).",
      "Browser UI. If a tester reports a bug that 'came back', check for a stale bundle first - "
      "the tell is old UI details still on screen."),
     ("ENV-05", "TLS and vhost serve the right site",
      "curl -sI https://piomanage.com | head -3",
      "HTTP 200, valid certificate, no redirect to another site co-hosted on the same VPS.",
      "curl. This VPS also serves pioassets.com and piodeploy.com."),
    ])

mod("2. Authentication and Session",
    "Login is Keycloak OIDC (auth-code + PKCE) behind a BFF: tokens live in httpOnly cookies and "
    "the browser never holds a bearer token.",
    [
     ("AUTH-01", "Staff login round trip",
      "Open https://piomanage.com, click Sign in, authenticate as a staff user in the desk realm.",
      "Redirected back to /dashboard, header shows the user menu with the right name.",
      "Browser. Check DevTools > Application > Cookies: the session cookie is httpOnly and Secure, "
      "and there is NO token in localStorage."),
     ("AUTH-02", "First login forces a password change",
      "Create a Keycloak user with a temporary password, then log in as them.",
      "Keycloak forces a password update before the app loads.",
      "Keycloak admin console + browser."),
     ("AUTH-03", "Unauthenticated access is blocked",
      "In a private window open https://piomanage.com/dashboard/tickets directly.",
      "Redirected to login, not shown any ticket data.",
      "Browser. Repeat for /control-panel and /dashboard/users."),
     ("AUTH-04", "Expired access token refreshes silently",
      "Stay logged in past the access-token lifetime, then click through to another page.",
      "The BFF proxy refreshes on 401 and the page loads without bouncing to login.",
      "Browser network tab: a 401 followed by a successful retry."),
     ("AUTH-05", "Logout clears the session",
      "Click Sign out, then press Back and try to reach /dashboard.",
      "Session cookies cleared; the protected page redirects to login.",
      "Browser."),
     ("AUTH-06", "A user with no portal record cannot act",
      "Log in as a Keycloak user with no matching app_users / client_users row.",
      "No tenant scope is resolved: the user sees no data rather than another tenant's data.",
      "Browser + SQL: select * from app_users where \"IdpSubject\"='<keycloak sub>'."),
     ("AUTH-07", "Client login is separate from staff login",
      "Log in as a pure client-portal user (client_users row, no app_users row).",
      "Lands in the client experience with exactly tickets.create and tickets.note.public.add; "
      "no staff tooling appears on the ticket page.",
      "Browser. A user holding BOTH identities must resolve as staff - test that case too."),
    ])

mod("3. Tenant Isolation and Security",
    "Isolation is enforced in five layers. These are the tests where a pass must be proven, not "
    "assumed - a query that returns nothing looks identical to a filter that works.",
    [
     ("SEC-01", "Cross-tenant ticket read is refused",
      "As an admin of org A, note a ticket GUID belonging to org B (from SQL). Call "
      "GET /api/tickets/<org-B-ticket-id> with org A's session.",
      "404 or 403. Never org B's ticket body.",
      "API + SQL. Repeat for notes, attachments, time entries, users and audit rows."),
     ("SEC-02", "Cross-tenant write is refused",
      "As org A, POST a comment to an org B ticket id.",
      "Rejected. No row is created.",
      "API + SQL: confirm no ticket_notes row appeared for that ticket."),
     ("SEC-03", "Audit and user tables are scoped",
      "As org A call GET /api/admin/audit and GET /api/admin/users.",
      "Only org A rows. AuditLog and AppUser are NOT covered by the global query filter, so they "
      "are scoped in service code - they need their own explicit test.",
      "API + SQL count comparison."),
     ("SEC-04", "Client user sees only their own company",
      "Log in as a client user of company X and list tickets.",
      "Only company X tickets. Requester-level users see only their own.",
      "Browser + SQL: compare against select count(*) from tickets where \"ClientCompanyId\"=..."),
     ("SEC-05", "Internal notes never reach a client",
      "Add an internal note to a ticket as staff, then open the same ticket as a client user.",
      "The internal note is absent from the API response body, not merely hidden in the UI.",
      "Inspect the raw JSON from GET /api/tickets/{id} in the client session. This is the single "
      "most important confidentiality test in the product."),
     ("SEC-06", "Time and billing data are stripped for clients",
      "Reply with time logged as staff, then view the thread as a client.",
      "No duration, billable flag or time entry appears in the client payload.",
      "Raw JSON inspection."),
     ("SEC-07", "Credentials are never returned",
      "GET /api/admin/connections and the single-connection endpoint.",
      "No API key, secret or password field in any response. Only 'has a stored credential' style "
      "flags and names.",
      "Raw JSON. Also grep the API log for the secret value to prove it is not logged."),
     ("SEC-08", "SSRF egress guard",
      "With Connectors:BlockPrivateEgress enabled, configure a connection endpoint pointing at "
      "http://169.254.169.254 or a private IP and run Test connection.",
      "Blocked with a clear error, no outbound request.",
      "API response + log."),
     ("SEC-09", "Provider-supplied next-page URL cannot redirect the sync",
      "Unit-level: the Autotask connector refuses a nextPageUrl on a different host.",
      "ConnectorException naming the host. Covered by "
      "A_next_page_url_on_another_host_is_refused.",
      "dotnet test --filter A_next_page_url_on_another_host. This matters because the follow-up "
      "request carries the PSA credentials."),
     ("SEC-10", "Webhook signature validation",
      "POST to /api/webhooks/{connectionId} with a bad signature, then with a stale timestamp.",
      "Both rejected. A correctly signed, fresh payload is accepted exactly once (replay is "
      "de-duplicated).",
      "API. Send the same signed payload twice and confirm the second is a no-op."),
     ("SEC-11", "Attachment malware quarantine",
      "Upload the EICAR test string as a file to a ticket.",
      "Stored as quarantined, flagged in the UI, and NOT downloadable.",
      "Browser + SQL: select \"ScanStatus\" from attachments order by \"CreatedAt\" desc limit 1."),
     ("SEC-12", "Rate limiting on public forms",
      "POST /api/public/enquiries/contact six times within ten minutes from one IP.",
      "The sixth is rate limited (policy allows 5 per 10 minutes per IP).",
      "curl loop, observe 429."),
     ("SEC-13", "A file posted with an internal note never reaches a client",
      "As staff, post an INTERNAL note on a client's ticket with a file attached (name it "
      "obviously, e.g. INTERNAL-credentials.png). Then, as a client user of that company, open "
      "the ticket and read the API response, not just the page. Finally take the attachment id "
      "from the staff view and call the download endpoint as the client: "
      "GET /api/tickets/{ticketId}/attachments/{attachmentId}/download",
      "The client's detail payload contains neither the internal note NOR its attachment - no "
      "file name, size, uploader or id. The download returns 404 (not 403). A file attached to a "
      "PUBLIC note, and one attached to no note at all, are both still returned and downloadable "
      "- a rule that refuses those has been written too broadly.",
      "DevTools > Network on the ticket detail response, plus curl for the download. CHECK THE "
      "PAYLOAD, NOT THE PAGE: the UI hides an internal-note attachment by accident - it matches "
      "no rendered message, and the loose-files list takes only attachments with no note - so a "
      "leak here looks completely normal on screen. This is how it was missed."),
    ])

mod("4. Roles and Permissions",
    "Seven built-in roles, 27 permission keys, plus tenant-custom roles and 8 permission "
    "templates. Built-ins are shared system rows and must stay read-only.",
    [
     ("ROLE-01", "Built-in roles exist and are read-only",
      "Open /dashboard/roles as an MSP administrator. Try to edit a built-in role.",
      "PlatformSuperAdministrator, MspAdministrator, Manager, Technician, ClientAdministrator, "
      "ClientUser and Auditor are listed and cannot be edited.",
      "Browser. Built-ins are cross-tenant rows - editing one would affect every tenant."),
     ("ROLE-02", "MSP administrator permission set",
      "GET /api/admin/users/{id}/permissions for an MspAdministrator.",
      "Includes org.manage, connections.manage, mappings.manage, users.manage, roles.manage, "
      "tickets.view.all, audit.view, enquiries.view and productivity.team.view.",
      "API response against packages/domain/Authorization/Permissions.cs ForRole."),
     ("ROLE-03", "Manager cannot manage users or connections",
      "Log in as a Manager. Attempt /dashboard/users and /dashboard/connections edit.",
      "Manager has connections.view and mappings.view but NOT the manage variants, and no "
      "users.manage. Read allowed, write refused.",
      "Browser + API 403."),
     ("ROLE-04", "Technician sees assigned tickets only",
      "Log in as a Standard Technician and open the ticket list.",
      "Only tickets assigned to them (tickets.view.assigned at Assigned scope).",
      "Browser + SQL: compare with select count(*) from tickets where "
      "\"AssignedTechnicianExternalId\"='<their external id>'."),
     ("ROLE-05", "Technician can log time and add public notes on any ticket",
      "As a technician, open a ticket they can see and log time / add a note.",
      "Allowed - tickets.time.log and tickets.note.public.add are granted at All scope by design.",
      "Browser. This is deliberate, not a bug: confirm it still matches your policy."),
     ("ROLE-06", "Auditor is read-only",
      "Log in as an Auditor and attempt any write: change a status, save a mapping, edit a user.",
      "All writes refused; audit and security pages readable.",
      "Browser + API."),
     ("ROLE-07", "Custom role creation",
      "Create a tenant role 'Dispatcher QA' from the catalogue, grant tickets.view.all and "
      "tickets.update only, assign it to a test user.",
      "The user gains exactly those two capabilities and nothing else.",
      "/dashboard/roles then verify with /dashboard/permissions."),
     ("ROLE-08", "Duplicate-from-built-in",
      "Use Duplicate on a built-in role, then edit the copy.",
      "The copy is tenant-owned and editable; the built-in is unchanged.",
      "Browser + SQL: the new row has a non-null MspOrganizationId."),
     ("ROLE-09", "Privilege escalation is blocked",
      "As an MSP administrator, attempt to assign PlatformSuperAdministrator to another user, "
      "including by POSTing the role id directly to "
      "/api/admin/users/{id}/roles/{roleId}.",
      "Refused. Only the 4 staff built-ins plus tenant customs are assignable.",
      "API. This is a real escalation hole that was closed - it must stay closed."),
     ("ROLE-10", "Cannot edit a role you currently hold",
      "As a user holding custom role R, try to edit R.",
      "Forbidden.",
      "Browser + API 403."),
     ("ROLE-11", "Cannot delete a role still assigned",
      "Delete a custom role that a user holds.",
      "Refused with a clear message naming the holders.",
      "Browser."),
     ("ROLE-12", "Permission templates apply as a diff",
      "Apply the 'Senior Technician' template to a user via "
      "POST /api/admin/users/{id}/apply-template/{templateId}.",
      "The base role is assigned AND the template's override entries are materialised on top. "
      "Templates hold only the difference from their base role.",
      "GET /api/admin/users/{id}/permissions before and after."),
     ("ROLE-13", "Effective permission explorer is accurate",
      "Open /dashboard/permissions, pick tickets.view.all, and compare the holder list with a "
      "manual check of three users.",
      "Holders match, and each row names the roles that contributed the grant. An override-deny "
      "shows as 'Override - denied (role grant from: X)'.",
      "Browser. This page is engine-resolved, so it is also a test of the engine itself."),
     ("ROLE-14", "Override deny beats a role grant",
      "Give a user a role granting tickets.update, then add an explicit deny override.",
      "The user cannot update tickets, and the explorer explains why.",
      "Browser + API 403 on a status change."),
    ])

mod("5. Staff Users and Technician Registration",
    "Covers creating employees by hand, importing them from the PSA, and binding each portal "
    "user to their PSA technician identity so work is attributed to the right person.",
    [
     ("USER-01", "Create a staff user",
      "/dashboard/users > Add user. Supply name, email, role.",
      "User is created, appears in the list, and can be opened at /dashboard/users/{id}.",
      "Browser + SQL: select * from app_users order by \"CreatedAt\" desc limit 1."),
     ("USER-02", "Email is required and unique",
      "Attempt to create a second user with an existing email, and one with no email.",
      "Both refused with a clear message.",
      "Browser."),
     ("USER-03", "Bulk user creation",
      "POST /api/admin/users/bulk with three users, one of them invalid.",
      "Valid rows are created, the invalid one is reported. No partial-silent failure.",
      "API response + SQL count."),
     ("USER-04", "Import technicians from the PSA",
      "/dashboard/users > Import from PSA. Select the Autotask connection.",
      "Active PSA technicians are listed with their names and emails.",
      "GET /api/admin/psa-technicians/{psaConnectionId}."),
     ("USER-05", "A PSA technician with no email is refused",
      "Attempt to import a technician whose PSA record has no email address.",
      "Refused with a reason, not silently skipped and not created with a placeholder.",
      "API response. Provisioning deliberately will not invent an identity."),
     ("USER-06", "Importing a technician creates a usable portal user",
      "POST /api/admin/psa-technicians/{connectionId}/{externalTechnicianId}.",
      "A portal user is created AND bound to that PSA technician id.",
      "GET /api/admin/users/{id}/psa-identities."),
     ("USER-07", "Per-connection PSA identity mapping",
      "PUT /api/admin/users/{id}/psa-identities/{psaConnectionId} to map a user to an Autotask "
      "resource id, then map the same user on the ConnectWise connection to a member id.",
      "Both mappings coexist. They are per connection because an Autotask numeric resourceID and "
      "a ConnectWise member identifier are different kinds of thing.",
      "SQL: select * from user_psa_identities where \"AppUserId\"='<id>'."),
     ("USER-08", "Time is attributed to the mapped technician",
      "As a mapped user, log time on a ticket, then check the entry in the PSA.",
      "The PSA time entry is owned by that technician, not by the API integration user.",
      "PSA UI + portal time entry list."),
     ("USER-09", "An unmapped user is not guessed at",
      "Log activity as a portal user with no PSA identity, then run the rollup.",
      "The daily fact carries a null actor rather than an attributed guess.",
      "SQL: select \"ActorExternalId\" from activity_daily_facts order by \"Day\" desc limit 5."),
     ("USER-10", "Deactivate a user",
      "PUT /api/admin/users/{id}/active with false, then try to log in as them.",
      "Login is refused or lands with no access; the user remains in the list as inactive for "
      "audit history.",
      "Browser + SQL."),
     ("USER-11", "Delete a user",
      "DELETE /api/admin/users/{id} for a user with no history, and one with ticket history.",
      "Behaviour is consistent and explained; historical attribution is not silently orphaned.",
      "API + SQL."),
     ("USER-12", "Profile photo upload and removal",
      "POST then DELETE /api/admin/users/{id}/photo.",
      "Photo appears in the user list and detail, and removal restores the initials avatar.",
      "Browser."),
     ("USER-13", "Board / queue access grants",
      "PUT /api/admin/users/{id}/board-grants restricting a technician to one board.",
      "The technician sees only tickets on that board.",
      "Browser as that user + SQL cross-check."),
     ("USER-14", "User detail tabs load",
      "Open /dashboard/users/{id} and visit every tab.",
      "All tabs render with real data and no console errors.",
      "Browser DevTools console."),
    ])

mod("6. Departments and Teams",
    "Staff org structure, separate from client companies. Seeded with 7 defaults per organization.",
    [
     ("ORG-01", "Default departments are seeded",
      "Create a new organization and inspect its departments.",
      "IT Support, NOC, Projects, Sales, Billing, Administration, Security.",
      "SQL: select \"Name\" from departments where \"MspOrganizationId\"='<new org>'."),
     ("ORG-02", "Seeding is idempotent and non-resurrecting",
      "Delete one default department, then restart the API.",
      "The deleted department stays deleted. Seeding skips any org that already has ANY "
      "departments.",
      "SQL before and after restart."),
     ("ORG-03", "Create, rename, deactivate a department",
      "POST /api/admin/departments, PUT it, then PUT .../active false.",
      "Each step succeeds and is reflected in /dashboard/departments.",
      "Browser + API."),
     ("ORG-04", "Team membership",
      "Create a team, add and remove a user via POST/DELETE "
      "/api/admin/users/{id}/teams/{teamId}.",
      "Membership updates immediately and appears on the user detail page.",
      "Browser."),
     ("ORG-05", "Departments page is permission gated",
      "Open /dashboard/departments as a Technician.",
      "Refused - the page is gated on users.manage by design (no dedicated permission).",
      "Browser + API 403."),
     ("ORG-06", "Org structure endpoint",
      "GET /api/admin/org-structure.",
      "Returns the department/team tree with member counts matching the database.",
      "API + SQL counts."),
    ])
