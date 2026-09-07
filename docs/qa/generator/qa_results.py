# -*- coding: utf-8 -*-
"""Results of an actual execution of the plan, kept separate from the plan itself.

The cases in qa_plan_part1..4 are the template and do not change when a run happens; this file
is the overlay for one run. Both generators read it, so the PDF carries a stamp per case and the
workbook opens with those rows already filled in.

Status vocabulary, chosen because "not run" hides three different situations:
  Pass      executed against the live system, with the evidence named
  Partial   the mechanism was proven but the full case needs a session or a second environment
  Blocked   needs something this run did not have — a login, a PSA write, a second tenant
  Fail      executed, and the system did not do what the case says it should
  N/A       cannot arise here, and saying so is a result rather than an omission
"""

RUN = {
    'when': '2026-09-05 / 06 / 07',
    'environment': 'https://piomanage.com (production) + prod database and worker logs',
    'tester': 'Claude (agent), unattended - no interactive session available',
    'scope': 'Every case reachable without a login: modules 1-10, 16, 21, 22, 23. Modules 11-20 '
             'are session-bound throughout and were not attempted. Module 21 is the exception '
             'among the high-numbered modules - the public site is anonymous by design, so most '
             'of it could be exercised for real.',
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

    # --- 7. Client users and the client portal ----------------------------------------------
    'CLIENT-01': ('Blocked', 'Needs a client session.'),
    'CLIENT-02': ('Blocked', 'Needs an admin session. Worth noting for whoever runs it: the one '
                             'client user has a null ExternalContactId, so he was invited by hand '
                             'and the contact-import path has never been exercised here.'),
    'CLIENT-03': ('Blocked', 'Needs a client session.'),
    'CLIENT-04': ('Blocked', 'Needs a client session.'),
    'CLIENT-05': ('Partial', 'All 355 notes carry AuthoredByClient = false - 17 portal-authored, '
                             '338 imported. That is the shape of the PR #66 bug, but it is not a '
                             'regression: both connectors populate the flag and the heal loop '
                             'rewrites it on every sync, of which many ran. No client has ever '
                             'written a note in either PSA, so the mechanism is verified and the '
                             'rendering is not - two different claims.'),
    'CLIENT-06': ('N/A', 'Zero section grants exist, so there is nothing to delegate.'),
    'CLIENT-07': ('Blocked', 'Needs a client session. Last-admin protection is unit-tested.'),
    'CLIENT-08': ('Blocked', 'Needs a client session.'),

    # --- 8. PSA connections -----------------------------------------------------------------
    'CONN-01': ('Pass', 'The connection row holds a 32-character opaque id; the material lives in '
                        'secret_blobs."Ciphertext" as bytea in a separate table. Structural, which '
                        'is what the case asks - an attempt to measure how printable the ciphertext '
                        'was returned 257% and meant nothing, because encode(escape) expands bytes.'),
    'CONN-02': ('Blocked', 'Needs a session. Read-only and safe to run - one call to the PSA.'),
    'CONN-03': ('Blocked', 'Needs credentials typed into a form, which is the tester\'s to do and '
                           'not the agent\'s. Merge-not-clobber is unit-tested.'),
    'CONN-04': ('Blocked', 'Needs a session. Display only, safe.'),
    'CONN-05': ('Blocked', 'Needs a session AND stops a live integration syncing. Reversible in '
                           'seconds, but it is a deliberate outage on production - staging is the '
                           'honest place for it.'),
    'CONN-06': ('Blocked', 'Needs a session. Read-only against the PSA, safe.'),
    'CONN-07': ('Pass', 'Map() filters o.IsActive before options reach the mapping UI.'),
    'CONN-08': ('Pass', 'Options carry Value and SyncValue, held by certification tests on both '
                        'providers.'),
    'CONN-09': ('Pass', 'Rests on the An_import_filter_restricted_to_one_company_excludes_the_others '
                        'test written this run - production filters are empty, so no live data '
                        'proves it. The test exists because the fake used to match everything.'),
    'CONN-10': ('Blocked', 'The one to avoid on production: turning the toggle off and re-syncing '
                           'could reconcile away the 113 already-imported closed Autotask tickets, '
                           'and recovering them means another full re-sync. Real data movement to '
                           'prove a toggle.'),
    'CONN-11': ('Blocked', 'Needs a session. Creates no time entry, so safe to run.'),
    'CONN-12': ('Blocked', 'Needs a session, but arguably already covered: the button calls the '
                           'same ConnectionSyncRunner invoked a dozen times this run by resetting '
                           'the watermark. Only the HTTP entry point is untested.'),
    'CONN-13': ('Pass', 'Both connections healthy, enabled, synced within the minute, no error.'),
    'CONN-14': ('Pass', 'A failed run sets Status = Degraded and LastError, cleared on success. '
                        'Corroborated by the Autotask 405 incident this run, which exercised the '
                        'path for real - LastError reads empty now precisely because the next '
                        'success cleared it.'),

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

    # --- 21. Public site, enquiries and legal ------------------------------------------------
    'PUB-01': ('Pass', 'All 17 public pages return 200 with real content, not a shell.'),
    'PUB-02': ('Pass', 'Stored with Kind = 0. The test rows were deleted afterwards; the one '
                       'genuine enquiry on production was left alone.'),
    'PUB-03': ('Pass', 'Stored with Kind = 1, carrying the preferred time as the visitor worded it.'),
    'PUB-04': ('Pass', 'A filled honeypot answers 202 and stores nothing - confirmed by row count.'),
    'PUB-05': ('Pass', 'A defect this run, now fixed and verified in production. A 60,000-character '
                       'message used to return 202 and store exactly 4,000: the sender was thanked '
                       'and never told, and staff read a message ending mid-sentence with nothing '
                       'to mark the cut. Every visitor-typed field is now refused when oversized, '
                       'naming the field and the limit, and nothing is written on a refusal. '
                       'Live: 60,000 and 4,001 both 400, a normal message 202, no truncated rows. '
                       'SourcePage is still clipped on purpose - the site fills it in, and a lead '
                       'should not be lost to the length of our own URL.'),
    'PUB-06': ('Blocked', 'Needs an admin session to move an enquiry through its statuses.'),
    'PUB-07': ('Fail', 'Twelve unfilled placeholders are live and visible: seven on /privacy '
                       '(registered address, hosting provider, hosting region, and four retention '
                       'periods reading "e.g. 24 months" and the like) and five on /terms (two '
                       'notice periods, the liability cap "e.g. the fees paid in the preceding 12 '
                       'months", and governing law twice as "your jurisdiction"). A scan for '
                       'placeholder MARKERS - TBD, TODO, lorem ipsum - finds nothing, because the '
                       'slots hold ordinary English inside <mark> elements; only grepping "<Fill" '
                       'in the page sources revealed them. Needs the owner\'s real values.'),
    'PUB-08': ('Pass', 'Zero cookies are set on any public page, so no banner is owed. The session '
                       'cookies begin at sign-in and are strictly necessary.'),

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
    'REG-10': ('Pass', '549 unit tests green; five defects this run were each hidden at least once '
                       'by a fake more permissive than the real API, and each fake was tightened.'),
}


def status_of(case_id):
    return RESULTS.get(case_id, ('Not run', ''))


def tally():
    counts = {}
    for status, _ in RESULTS.values():
        counts[status] = counts.get(status, 0) + 1
    return counts
