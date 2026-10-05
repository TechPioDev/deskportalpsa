# Permissions and security

| Key | Scopes | Default holders | Allows |
|---|---|---|---|
| `schedule.view` | Own / Team / Department / All | Technician: Own. Manager: All. Administrator: All. | See schedules, skills, capacity, free time and exceptions of the people the scope reaches; search and conflict-check among them |
| `workforce.manage` | All | Administrator | Change schedules, breaks, time zones, "offered for planned work", the skill catalogue and who holds which skill |
| `availability.manage` (Phase 2) | Own / Team / Department / All | Manager: All. Administrator: All. Technician: none. | Record time unavailable and additional availability for the people the scope reaches |
| `schedule.manage` (Phase 3) | Own / Team / Department / All | Technician: **Own**. Manager: All. Administrator: All. | Put work into people's time and change or remove what is there. **Own** is planning your own work and nobody else's (and only work you can already see). A scope wider than Own makes the holder a *scheduler* for the people it reaches: place, fix, move, give away and take out their work. In the catalogue: "Plan work into people's time (Own = your own plan only)" |
| `schedule.override` (Phase 3) | All | Manager, Administrator. Technician: none. | Place work despite an overridable conflict (a double booking, outside the working window, over a break, over capacity), giving a reason of at least 5 characters that is kept with the work. Blocks cannot be overridden by anyone. In the catalogue: "Override a scheduling conflict, with a reason" |
| `workforce.analytics.export` (Phase 7) | All | Manager, Administrator. Technician: none. | Export the workforce analytics as a CSV: named people's capacity, planned and recorded time, utilization and completed work leave the system in a file. Reading the dashboard needs only `schedule.view` (its scope decides whose figures); taking it out is a separate right, audited on every export. In the catalogue: "Export workforce analytics (CSV)" |

No separate capacity permission was added: capacity is what a schedule means on a date, so it
follows `schedule.view` and its scope. `availability.manage` is separate because recording time
away is a different right from seeing it. To let technicians record their own, give the Technician
role `availability.manage` at scope **Own**.

Planning follows the same pattern. Reading a plan needs only `schedule.view` (and reaches the
people its scope reaches); changing one needs `schedule.manage`, whose scope decides whose. A
technician's default is Own: their own work, into their own time, no override. To let a team lead
schedule their team, give their role `schedule.manage` at scope **Team** (and `schedule.override` if
they may override conflicts). Fixing work in place, moving fixed work, taking scheduled work out and
giving work to someone else all need a scope wider than Own ([planned-work.md](planned-work.md)).

How each scope reaches people:
- **Team** reaches people who share a team with the caller.
- **Department** reaches people who share a department with the caller.
- **Own** reaches the caller only.
- Every scope includes the caller.

No client role holds any of these keys, and a pure client login's fixed claims don't include them
(tested per endpoint). The keys reach existing installations through the startup seeder, which only
adds.

## Internal only: client isolation

Client ticketing and workforce scheduling are separate security domains. A ticket may be visible to
a client; its planning never becomes visible because of that.

| Client attempt | Result | Proven by |
|---|---|---|
| Any `api/workforce/*` route: capacity, free slots, team capacity, the technician search, skills, exceptions, conflicts, plans, unscheduled work, placing or changing planned work | Refused (403): no client role or client login holds `schedule.view` | `No_client_account_can_reach_any_workforce_endpoint` walks every controller under `api/workforce` by route (and asserts `WorkforcePlanController` is among them), so a controller added later is covered |
| A guessed person, exception, ticket or allocation id | Refused before the id is looked at | Same |
| A workforce permission on a client role (`schedule.manage` and `schedule.override` included) | Not possible by default | `No_client_role_or_client_login_holds_a_workforce_permission` |
| Seeing planning through their own ticket | The ticket and client-portal response shapes carry no workforce field (`Allocat`, `Planned`, `MyPlan`, `Override`, …) | `Nothing_a_client_can_receive_carries_workforce_planning` fails if one is ever added |
| A signed-in account without the permission opening the pages by address | No menu entry; pages show an error; every endpoint answers 403 | `e2e/workforce-capacity.spec.ts`; for My plan, a person's Plan tab, the ticket's Planned work panel and every plan endpoint, `e2e/workforce-plan.spec.ts` |
| A sign-in that is not a staff account holding every planning claim | Every planning action refused ("Only staff accounts can use the workforce module.") | `WorkPlanTests.A_sign_in_that_is_not_a_staff_account_is_refused_by_every_planning_action` (the nine Phase 3 actions; the two Phase 4 reads share the same `Caller()` guard in code) |
| The team scheduler (Phase 4) by address, or its two reads `plan/team` and `plan/unscheduled/team`, with or without a `teamId` | Refused (403): the page shows an alert and no row, name or reference; both endpoints answer 403. For a staff account the rows are only the people its `schedule.view` reaches, so a technician gets one row, their own | `EndpointAuthorizationTests.Sensitive_endpoints_require_exactly_these_permissions` (the `Team` and `UnscheduledTeam` rows); `e2e/workforce-schedule.spec.ts`: "an account without the scheduling permission is refused the scheduler and its endpoints", "a technician has no team schedule entry and the page shows only themselves"; `WorkPlanTests.The_team_scheduler_lists_the_people_the_asker_may_see_with_their_days_and_what_is_planned` |

The ten planning-specific guarantees, each with its test, are listed in
[planned-work.md](planned-work.md#tenant-isolation-and-client-security), and the scheduler's six in
[team-scheduler.md](team-scheduler.md#tenant-isolation-and-client-security).

A second line of defence: every workforce action also requires a **staff** user id, so a principal
that is not a staff account is refused even if it somehow held the claim.

**Server-side enforcement:** the controller requires the claim, and the services check scope per
person through `WorkforceAccess`. Someone outside your scope, or in another organization, is "not
found", the same answer as a person who doesn't exist.

**Tenant isolation:**
- Another organization's person is "not found" for capacity, conflicts, and listing, adding,
  changing or removing an exception: the same words as for an id that names nobody, so nothing can
  be learned by probing. Naming them in a search finds nobody and counts nobody. A skill from
  another organization is refused with the same words as one that does not exist.
- An exception is looked up through the person it belongs to: another person's exception id on
  this person's route is "not found".
- Another organization's plan, ticket and allocation are "not found" for reading, placing, moving,
  giving away and cancelling, and its unscheduled list is empty; nothing is written
  (`Another_organizations_plans_cannot_be_read_written_or_detected`). Within an organization, a
  person outside the caller's `schedule.view` scope is "Person was not found." and a ticket outside
  their ticket scope is "Ticket was not found.", the same words as for an id that names nothing.
- All new tables are tenant entities with the global query filter.
- Staff accounts aren't tenant-filtered by EF, so the organization is applied explicitly.
- A skill from another organization isn't found.
- The database layer refuses a cross-tenant write outright.

**Validation:** every rule is enforced on the server, and all problems are reported in one answer.
Request bodies are explicit records, never entities (no mass assignment). A filter value that is
not a valid id is refused rather than ignored, so a malformed filter can never return more people
than were asked for. Notes are plain text: stored as typed and shown as text, never as markup.

**Limits against expensive requests:** 31 days of capacity or plan, 14 days of search or of the
team scheduler, 1,000 people, 200 matches, 20 skills, work of 5 minutes to 12 hours (search) or at
most 24 hours (planned work), 100 unscheduled tickets (own or the group's), 50 allocations listed on
a ticket. The API's per-user and per-organization
rate limits apply as everywhere else. Nothing is cached, so there is no cache to leak between tenants.

**Planned work and the ticket.** Planning never changes what the PSA says, and taking work out of a
plan never touches the ticket. The one portal-side effect: someone scheduled by someone else on a
ticket nobody in the portal holds becomes its holder (as "Take it" does), audited as
`ticket.assigned.portal` with `viaPlanning`. Nobody is ever planned on a ticket they cannot open:
the scheduler is told to hand it over first ([planned-work.md](planned-work.md#allocation-is-not-assignment)).

**Concurrency.** A person's plan is changed by one request at a time (a row lock on the person on
PostgreSQL, held across API containers), and every change to an allocation carries the version the
screen showed; a stale one is refused ([planned-work.md](planned-work.md#concurrency)).

**When, not why.** Everyone who may see a person's capacity sees *when* they are unavailable;
planning needs that. The reason ("Sick"), the note ("Dentist") and who recorded it are personal, so
the API returns them only to the person themselves and to whoever's `availability.manage` scope
reaches them. For anyone else those fields are null on every route that carries an exception
(person capacity, team capacity, the exception list).

**Error messages** never say whether a person exists in another organization or which rule kept
someone out of view; conflicts never carry a ticket's title, client or number
([conflicts.md](conflicts.md)). A plan carries a ticket's reference, title, client and status only
to an asker who may see that ticket; to anyone else the piece of work is "Work you cannot open" and
only its time is known.

**Audit:** every entry carries the request's correlation id. This phase made PIO fill that column for
every audit entry; it was always empty before.

| Area | Events |
|---|---|
| Schedules | `workforce.schedule.saved` (before and after summary), `workforce.schedule.removed`, `workforce.schedule.copied` |
| Offered for planned work | `workforce.schedulable.changed` |
| Skills | `skill.created`, `skill.updated` (before/after), `skill.assigned`, `skill.level_changed`, `skill.removed` |
| Capacity exceptions | `workforce.exception.added`, `workforce.exception.updated` (before/after), `workforce.exception.removed` |
| Planned work (Phase 3, Phase 4) | `workforce.allocation.created`, `workforce.allocation.moved` (before/after, when the start changes), `workforce.allocation.resized` (Phase 4: before/after, when the start is kept and only the end changes, as dragging a block's edge on the team scheduler does), `workforce.allocation.changed` (a note-only or fixed-only change), `workforce.allocation.reassigned` (from/to, before/after), `workforce.allocation.cancelled` (with the reason), `workforce.allocation.released` (the worker, when the ticket finished first; one entry per pass listing every release); `ticket.assigned.portal` with `viaPlanning: true` when planning made someone the holder. Override reasons are written; planning notes are not. The team scheduler adds no event of its own: a drop within a row is a `moved`, on another row a `reassigned`, a resize a `resized` |

| Advanced planning (Phase 5) | `workforce.allocation.created` now carries `status` (Planned / Tentative) and the warnings; `workforce.allocation.confirmed` (tentative → planned, with any override reason and types), `workforce.allocation.made_tentative` (planned → tentative), `workforce.planning.requirement_set` (before/after), `workforce.allocation.plan_confirmed` (a preview confirmed: the pieces with their ids, allocated minutes, the plan token) beside one `created` per piece |

| Work execution (Phase 6) | `workforce.session.started` / `paused` (with the reason) / `resumed` / `stopped` (seconds, hours, the entry) / `cancelled` (discarded or under a minute); `ticket.time.logged` carries `context` { sessionId, activeSeconds, allocationId } for a clock's entry; `ticket.time.edited` now carries `reason`, `byUserId`, `forUserId` (a lead changing someone else's time must give the reason) |
| Workforce analytics (Phase 7) | `workforce.analytics.exported` (the report, the period with its zone, the filters, the row count, the people count) on every successful export; dashboard views are not audited |

**Phase 7 reads through `schedule.view` and adds one key for exporting.** The dashboard, a person's detail and My analytics show, for the people the caller's `schedule.view` scope reaches, nothing the caller could not already read for them through capacity, the plan and My day; so reading needs no new key, and a technician at Own sees their own figures and nobody else's (no peer comparison exists for them). `workforce.analytics.export` is required by the controller and checked again by the service. Filters cannot widen the scope: a person outside it is "Person was not found.", a foreign team or department yields nobody, a client or connection id is checked against the organization. The client-shape guard also forbids `Heatmap`, `Reactive` and `Analytics`. The ten guarantees and their tests are in [analytics.md](analytics.md#permissions-client-isolation-tenant-isolation).

**Phase 6 adds no permission key.** The clock is logging time (`tickets.time.log` on the ticket, through the ticket scope); one's own day is `schedule.view` at Own; Team today is `schedule.view` beyond Own; correcting someone else's time is `boards.manage`, with a reason. The client-shape guard also forbids `Session`, `Segment`, `ActualSeconds`, `MyDay`, `TeamToday` and `Variance`. Nothing about presence is collected ([work-execution.md](work-execution.md#permissions-client-isolation-tenant-isolation-privacy)).

**Phase 5 shapes stay internal.** The client-shape guard (`Nothing_a_client_can_receive_carries_workforce_planning`)
now also forbids `RequiredMinutes`, `WaitingReason`, `PlanToken` and `Shortage` in any ticket,
control-panel, knowledge or attachment DTO, so a requirement, a queue item, a preview or a shortage
figure can never grow onto something a client receives. The planning requirement is its own row
beside the ticket, never a column on it ([advanced-planning.md](advanced-planning.md#permissions-client-isolation-tenant-isolation)).

View as is read-only, so it can't change a schedule.
