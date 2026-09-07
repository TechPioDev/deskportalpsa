# -*- coding: utf-8 -*-
"""Results of an actual execution of the plan, kept separate from the plan itself.

The cases in qa_plan_part1..4 are the template and do not change when a run happens; this file
is the overlay for one run. Both generators read it, so the PDF carries a stamp per case and the
workbook opens with those rows already filled in.

Status vocabulary, chosen because "not run" hides three different situations:
  Pass      executed against the live system, with the evidence named
  Partial   the mechanism was proven but the full case needs a session or a second environment
  Blocked   needs something this run did not have — a login, a PSA write, a second tenant
  N/A       cannot arise here, and saying so is a result rather than an omission
"""

RUN = {
    'when': '2026-09-05 / 06 / 07',
    'environment': 'https://piomanage.com (production) + prod database and worker logs',
    'tester': 'Claude (agent), unattended - no interactive session available',
    'scope': 'Every case reachable without a login: modules 1-6, 9, 10, 16, 22, 23. '
             'Modules 7, 8, 11-21 are session-bound and were not attempted.',
}

# case id -> (status, evidence)
RESULTS = {
    # --- 1. Environment, build, deployment -------------------------------------------------
    'ENV-01': ('Pass', 'VPS at the intended commit; api/worker images built after it. main was 4 '
                       'commits ahead but git diff --name-only showed docs and CI only, no '
                       'apps/ packages/ tests/ change, so no deploy was owed.'),
    'ENV-02': ('Pass', 'api, worker, web up; postgres reports (healthy); keycloak up.'),
    'ENV-03': ('Pass', '"Database migrated and built-in roles/templates/departments seeded", app '
                       'started, zero errors since boot.'),
    'ENV-04': ('Partial', '/version returns a buildId matching the id embedded in the served HTML, '
                          'so the watchdog has something true to compare. The reload toast itself '
                          'needs a browser open across a deploy.'),
    'ENV-05': ('Pass', '200 over TLS, certificate verifies, no redirect to a co-hosted site, '
                       'http->https 301.'),

    # --- 2. Authentication and session -----------------------------------------------------
    'AUTH-01': ('Pass', 'PKCE S256 + state; desk_pkce/desk_state are Secure, HttpOnly, '
                        'SameSite=lax, 10 minutes; session cookies set httpOnly/secure/lax; no '
                        'token material in the HTML and no auth data in browser storage.'),
    'AUTH-02': ('Blocked', 'Needs a Keycloak user with a temporary password.'),
    'AUTH-03': ('Pass', 'Every /dashboard route 307s to login; an authenticated API endpoint '
                        'returns 401 anonymously.'),
    'AUTH-04': ('Blocked', 'Needs a session held past the 5-minute access-token lifetime.'),
    'AUTH-05': ('Pass', 'Clears desk_at, desk_rt and desk_it AND redirects to Keycloak end-session '
                        '- a real RP-initiated logout, not just local cookie clearing.'),
    'AUTH-06': ('Blocked', 'Needs a Keycloak user with no portal record.'),
    'AUTH-07': ('Blocked', 'Needs client-portal credentials.'),

    # --- 3. Tenant isolation and security --------------------------------------------------
    'SEC-01': ('N/A', 'Production holds ONE tenant, so there is nothing to cross into. Covered by '
                      'SecurityIsolationTests, which shares one in-memory root between two tenants '
                      'so an unscoped reader would really leak. Real proof needs a second org.'),
    'SEC-02': ('N/A', 'As SEC-01.'),
    'SEC-03': ('Partial', 'Invariants hold: zero tenant-scoped rows with a null organization across '
                          'tickets, notes, companies, mappings and events; zero tickets whose '
                          'company belongs to another org. The endpoint itself needs a session.'),
    'SEC-04': ('N/A', 'As SEC-01.'),
    'SEC-05': ('Blocked', 'Needs a client session. The server-side filter is unit-tested in both '
                          'directions.'),
    'SEC-06': ('Blocked', 'Needs a client session.'),
    'SEC-07': ('Blocked', 'Needs an admin session to read the endpoint.'),
    'SEC-08': ('Blocked', 'Needs Connectors:BlockPrivateEgress enabled and a connection pointed at '
                          'a private address.'),
    'SEC-09': ('Pass', 'A next-page URL on another host is refused by name; covered by '
                       'A_next_page_url_on_another_host_is_refused.'),
    'SEC-10': ('N/A', 'The webhook endpoint is not reachable from outside: the API publishes no '
                      'ports and nginx has no route to it, so a provider cannot deliver one. Owner '
                      'chose to keep sync on polling, so this stays not-applicable rather than '
                      'blocked.'),
    'SEC-11': ('Blocked', 'Needs a session to upload. EICAR handling is unit-tested.'),
    'SEC-12': ('Pass', 'Exactly five 202s then 429. Driven through the honeypot field so nothing '
                       'was stored - confirmed zero new enquiry rows.'),

    # --- 4. Roles and permissions ----------------------------------------------------------
    'ROLE-01': ('Pass', 'All seven built-ins present, IsSystemRole, cross-tenant; zero custom roles.'),
    'ROLE-02': ('Pass', 'Twenty grants, matching Permissions.ForRole exactly.'),
    'ROLE-03': ('Pass', 'Holds connections.view and mappings.view but neither .manage variant, and '
                        'no users.manage or roles.manage.'),
    'ROLE-04': ('Pass', 'tickets.view.assigned at Assigned(30) scope.'),
    'ROLE-05': ('Pass', 'note.public.add, time.log and update are all at All(0) scope while the '
                        'ticket view is Assigned - the asymmetry is deliberate; confirm it still '
                        'matches policy.'),
    'ROLE-06': ('Pass', 'Only audit.view, integration.health.view and security.config.view. No '
                        'write of any kind.'),
    'ROLE-07': ('Blocked', 'Needs an admin session.'),
    'ROLE-08': ('Blocked', 'Needs an admin session.'),
    'ROLE-09': ('Pass', 'StaffAssignable restricts assignment to four staff built-ins or a role '
                        'this org owns, applied at list, picker AND assignment time - the last was '
                        'the actual hole. 34 RBAC and golden-matrix tests green.'),
    'ROLE-10': ('Blocked', 'Needs an admin session.'),
    'ROLE-11': ('Blocked', 'Needs an admin session.'),
    'ROLE-12': ('Pass', 'All eight system templates store only the difference from their base role; '
                        'three carry zero entries because they ARE their base role.'),
    'ROLE-13': ('Blocked', 'Needs an admin session.'),
    'ROLE-14': ('Blocked', 'Needs an admin session.'),

    # --- 5. Staff users and technician registration ----------------------------------------
    'USER-01': ('Blocked', 'Needs an admin session.'),
    'USER-02': ('Pass', 'Enforced case-insensitively on create and on update-excluding-self, and '
                        'now by the database too: a unique index over (org, lower(email)) was '
                        'added this run and proven to reject a case-variant duplicate.'),
    'USER-03': ('Blocked', 'Needs an admin session.'),
    'USER-04': ('Blocked', 'Needs an admin session.'),
    'USER-05': ('Pass', 'A technician with no email is surfaced as not-in-portal with the reason, '
                        'never auto-created.'),
    'USER-06': ('Blocked', 'Needs an admin session.'),
    'USER-07': ('Pass', 'The one staff user maps to Autotask resource 29682885; no ConnectWise '
                        'mapping, which is correct because mappings are per connection.'),
    'USER-08': ('Blocked', 'Needs a session to log time.'),
    'USER-09': ('Pass', 'An unmapped actor resolves to null rather than a guess, in production and '
                        'in test.'),
    'USER-10': ('Blocked', 'Needs an admin session.'),
    'USER-11': ('Blocked', 'Needs an admin session.'),
    'USER-12': ('Blocked', 'Needs an admin session.'),
    'USER-13': ('N/A', 'No board grants exist, so there is nothing to restrict.'),
    'USER-14': ('Blocked', 'Needs an admin session.'),

    # --- 6. Departments and teams ----------------------------------------------------------
    'ORG-01': ('Pass', 'Exactly the seven documented defaults, in sort order, all system defaults '
                       'and active.'),
    'ORG-02': ('Pass', 'Zero duplicates after a dozen-plus API restarts. Non-resurrection holds by '
                       'construction: an org holding ANY departments is skipped entirely.'),
    'ORG-03': ('Blocked', 'Needs an admin session.'),
    'ORG-04': ('N/A', 'No teams exist.'),
    'ORG-05': ('Pass', 'Every department endpoint carries RequirePermission(UsersManage), reads '
                       'included.'),
    'ORG-06': ('Blocked', 'Needs an admin session.'),

    # --- 9. Field mapping ------------------------------------------------------------------
    'MAP-01': ('Blocked', 'Needs an admin session.'),
    'MAP-02': ('Pass', 'Zero raw statuses and zero raw priorities on both connections - PsaStatus '
                       'differs from PortalStatus everywhere a rule exists.'),
    'MAP-03': ('Pass', 'Unmapped values are named in the worker log; zero outstanding after the '
                       'mappings were completed this run.'),
    'MAP-04': ('Pass', 'An unmapped value passes through and the ticket still imports.'),
    'MAP-05': ('Pass', 'Zero rows from the inbound ambiguity query, and zero from its outbound '
                       'mirror.'),
    'MAP-06': ('Blocked', 'Needs a portal-side push.'),
    'MAP-07': ('Pass', 'Three provider values are each claimed by two portal values, and in every '
                       'case exactly one rule is inbound-eligible.'),
    'MAP-08': ('Blocked', 'Needs a portal-side status change.'),
    'MAP-09': ('Blocked', 'Needs a portal-side status change.'),
    'MAP-10': ('Blocked', 'Needs the mapping page.'),
    'MAP-11': ('Blocked', 'Needs the mapping page.'),
    'MAP-12': ('Pass', 'Re-pointing two queue rules moved 124 tickets once the queue joined the '
                       'update hash - which is how that gap was found.'),
    'MAP-13': ('Blocked', 'Needs an admin session.'),
    'MAP-14': ('Pass', 'Every direct edit this run was scoped by PsaConnectionId and its row count '
                       'checked against what was expected.'),
    'MAP-15': ('Blocked', 'Needs a session to log time.'),
    'MAP-16': ('Pass', 'Rules edited in the database took effect on the next tick with no restart.'),

    # --- 10. Ticket sync engine ------------------------------------------------------------
    'SYNC-01': ('Pass', 'Seven Autotask and four ConnectWise companies auto-created, all carrying '
                        'tickets.'),
    'SYNC-02': ('Pass', 'A full re-sync fetched 135 across two pages plus a next-page call. Before '
                        'the fix it stopped at 100 and reported success.'),
    'SYNC-03': ('Pass', 'Zero duplicate (connection, external id) pairs.'),
    'SYNC-04': ('Pass', 'Zero page-cap warnings; headroom is 2.7% of the ceiling.'),
    'SYNC-05': ('Pass', 'Quiet cycles report no work and cost about three requests.'),
    'SYNC-06': ('Blocked', 'Needs a field-only edit in the PSA the connector actually reads. Two '
                           'attempts this run never reached the instance in question.'),
    'SYNC-07': ('Blocked', 'Needs a portal-side write.'),
    'SYNC-08': ('Pass', 'Autotask 135/135 raise dates, 135 SLA, 113 closures; ConnectWise 13/13 '
                        'raise and SLA.'),
    'SYNC-09': ('Pass', 'Raise dates span July to September - the PSA date, not the import date.'),
    'SYNC-10': ('Pass', 'ConnectWise sends no closure date because no board status is flagged as '
                        'closing - confirmed by closedFlag being false on all six tickets sitting '
                        'in Completed. Absent, not mis-mapped.'),
    'SYNC-11': ('Pass', '355 notes imported, 216 internal and 139 public.'),
    'SYNC-12': ('Pass', 'Time-entry notes import as internal.'),
    'SYNC-13': ('Blocked', 'Needs a note deleted in the PSA.'),
    'SYNC-14': ('Pass', 'Every ticket carries a synced status; none failed.'),
    'SYNC-15': ('Pass', 'Zero sync failures across the run; a failing connection was never induced '
                        'deliberately on production.'),
    'SYNC-16': ('Pass', 'LastSuccessfulSyncAt advancing is the reliable signal; the summary log '
                        'line is written only when something changed.'),

    # --- 16. Activity events and rollup ----------------------------------------------------
    'ROLL-01': ('Pass', 'Events captured from both sources.'),
    'ROLL-02': ('Pass', 'Events dated 13 Jul to 3 Sep - when they happened, not when they arrived.'),
    'ROLL-03': ('Pass', '68 events, 13 daily facts, all 68 aggregated.'),
    'ROLL-04': ('Blocked', 'Needs a backdated event; injecting one would corrupt real analytics.'),
    'ROLL-05': ('Pass', 'Three consecutive hourly passes identical - the backfill settled.'),
    'ROLL-06': ('Blocked', 'As ROLL-04.'),
    'ROLL-07': ('Blocked', 'As ROLL-04.'),
    'ROLL-08': ('N/A', 'Nothing is old enough to expire: the oldest event is 54 days against a '
                       '396-day horizon. Zero rows past the horizon is no violation, not proof '
                       'that expiry works. The deletion path is unit-tested only.'),

    # --- 22. Performance and scale ---------------------------------------------------------
    'PERF-01': ('Pass', 'A full re-sync of 135 tickets takes about 8 minutes and 279 requests, '
                        'after two efficiencies landed this run.'),
    'PERF-02': ('Pass', 'A quiet cycle costs about three outbound requests.'),
    'PERF-03': ('Pass', '2.7% and 0.3% of the 5,000-ticket ceiling.'),
    'PERF-04': ('Blocked', 'Needs a session.'),
    'PERF-05': ('Blocked', 'Needs several concurrent testers.'),
    'PERF-06': ('Blocked', 'Would mean deliberately breaking production.'),

    # --- 23. Regression traps ---------------------------------------------------------------
    'REG-01': ('Pass', '135 imported with no duplicates.'),
    'REG-02': ('Pass', 'Dates and queue all populated; both gaps were found and closed this run.'),
    'REG-03': ('Pass', 'Zero raw statuses or priorities on either connection.'),
    'REG-04': ('Pass', 'Discovery now offers the value tickets arrive with, held by certification '
                       'tests on both providers.'),
    'REG-05': ('Pass', 'No 405 on the Autotask next page; the fake now refuses a GET as the live '
                       'API does.'),
    'REG-06': ('Blocked', 'Needs a portal-side note.'),
    'REG-07': ('Blocked', 'Needs a PSA time entry with internal-only text.'),
    'REG-08': ('Blocked', 'Needs a picklist value retired in the PSA.'),
    'REG-09': ('Pass', 'Facts cover July to September, not just the rolling window.'),
    'REG-10': ('Pass', '540 unit tests green; five defects this run were each hidden at least once '
                       'by a fake more permissive than the real API, and each fake was tightened.'),
}


def status_of(case_id):
    return RESULTS.get(case_id, ('Not run', ''))


def tally():
    counts = {}
    for status, _ in RESULTS.values():
        counts[status] = counts.get(status, 0) + 1
    return counts
