# Internal Boards v1

Work the team does for itself, and alerts that arrive from monitoring, kept beside the PSA queues
without ever mixing with them.

## Why it exists

Most of what this team does never reaches a PSA, because most clients do not grant full access to
theirs. That work was invisible: raised in messages, remembered by the person doing it, and absent
from every figure management reads. Internal boards give it the same home as client work — one
ticket record, one place to log time, one set of productivity figures — while keeping the two
sides answerable separately.

## The shape of it

| Concept | What it is |
|---|---|
| **Origin** | Every ticket is PSA, Internal or Rmm. It decides whether the portal pushes anywhere, whether a client can see it, and which side of the reporting split it falls on. |
| **Board** | A place that is not a provider queue. Two kinds: the team's own work, and monitoring alerts. |
| **Prefix** | Each board's ticket numbers, e.g. `INT-000123`. Quoted between people, so it cannot change once tickets exist. |
| **Membership** | A board with no members belongs to the whole team. Naming members narrows it; the holder and the raiser keep sight of their own ticket regardless. |
| **Handover** | Every assignment records who passed it, to whom, who decided, and what they said. |

## The rules that must not bend

1. **A client never sees internal work.** Naming a client on an internal ticket records who the work
   was for; it does not publish it. Only an RMM board may be published, and only when someone turns
   that on deliberately. Asking for it on an internal board is ignored rather than obeyed.
2. **Nothing is pushed.** No provider call is made for an internal ticket's creation, notes, status
   or time. There is no provider to call.
3. **Internal tickets are not failures.** The "never reached the PSA" list and its critical alert
   cover tickets that belong in a PSA. An internal ticket is complete where it is.
4. **Client reports count client work only.** Business reviews and client report runs exclude
   internal tickets explicitly.
5. **Productivity is split, not blended.** Client work and internal work are reported side by side,
   per technician, for tickets and for hours. Internal work is self-raised, and a single blended
   number invites the question of who is marking their own homework; the split answers it, and the
   recorded assigner answers the rest.

## Permissions

| Permission | Who holds it | What it allows |
|---|---|---|
| `tickets.create` | Everyone on the desk | Raise a ticket on a board, assign it to anybody |
| `boards.manage` | Managers and administrators | Create, edit, open and close boards; set membership |

Deciding what boards exist is a lead's call. Handing work to a colleague is not, which is what a
team running day and night shifts needs.

## Switching it off

One setting covers the whole feature:

```
Features__InternalBoards=false
```

With it off the endpoints answer "not found" and the pages disappear from the interface. Nothing is
deleted: every board and ticket stays in the database and returns the moment it is switched back on.

## Rolling it back

| Step | Command |
|---|---|
| Code before the feature | `git checkout pre-internal-boards` |
| Schema | The migration `InternalBoards` is additive: it adds columns and three tables, and makes the ticket's PSA connection, provider and client company optional. Reverting it drops the boards, membership and handover tables, so export anything worth keeping first. |

The feature switch is the reversible option and should be tried first. The tag is there for the case
where the code itself must go.

## What a ticket carries

Beyond the title, detail, priority and status every ticket has:

| Field | What it is for |
|---|---|
| **Department** | Which part of the team owns the work. The desk is organised this way, so the board is filtered and sorted by it. |
| **Topic** | What the ticket is about — Patching, Access request, Site visit. Choosing one fills in the department, priority, assignee and due date that kind of work usually has. It is a shortcut, never a rule: everything it sets stays editable. |
| **Due by** | When it should be done. Overdue is shown in the list, and anything due within eight hours is marked "soon". |
| **How it reached us** | Phone, Email, Chat, Walk-in, Meeting, Monitoring or Other. A short fixed list, because the only reason to record it is to count it later and free text cannot be counted. |
| **Client** | Who the work was for, when it was for somebody. It records that; it never shows the ticket to them. |

Topics are managed per board by leads and administrators, under **Topics** on the board. Retiring a
topic leaves every ticket raised under it untouched and only stops new ones choosing it.

The board list shows the ticket number, when it last moved, the subject with its topic and source,
who holds it, the department, the priority, and when it is due — the columns a desk reads a queue
with, newest activity first.

## Finding work, and watching it

These apply to every ticket — PSA queues and the team's own boards alike — and answer as the caller:
a search can only find what that person's own list would show them.

| Feature | What it does |
|---|---|
| **Search** (header box, Ctrl+/) | Looks up ticket numbers, provider references, subjects, customers and requesters, and — once four characters have been typed — the conversation itself. Runs in the database, so it finds the phrase three replies down that no list filter can reach. Enter opens the full result as a list; the result says how many matched rather than implying the first eight were all there was. |
| **Views** | Above every list: All, Open, Mine, Unassigned, Overdue, Following, Closed. These are code rather than rows — they mean the same on every desk and cannot be deleted. **Mine** includes anything sitting with a team you are in. **Overdue** means past due *and* still open; a ticket closed late is history, not work. |
| **Saved views** | Any filter set can be saved under a name, privately or shared with the team. A shared view still belongs to whoever made it; only they can change or delete it. Date filters are saved as a window ("last 30 days"), never a fixed date, so a view does not silently become "everything" a few months later. |
| **Followers** | Anybody on the team can be put on a ticket to watch it without holding it. A follower is not accountable for the work, so following never changes who has it. Followers are staff only; a client's view of a ticket never lists them. |
| **Team** | A ticket can be routed to a team (Level 2, NOC…) as well as, or before, a person. The two are independent: "sits with Level 2, being worked by Basit" is one sentence. Teams stay in the portal and are never sent to a PSA. |

`GET /api/tickets/search`, `/api/tickets/views`, `/api/tickets/{id}/followers`; the team rides on the
existing `PUT /api/tickets/{id}/assignment` as `teamId` / `clearTeam`. Migration
`SearchViewsFollowers` adds `tickets.AssignedTeamId` and two tables, `ticket_followers` and
`saved_ticket_views`; it is additive and its `Down` drops exactly those.

## SLA plans, canned responses and tasks

| Feature | What it does |
|---|---|
| **SLA plans** | *Boards → SLA plans.* A plan promises a first reply within N hours and resolution within N hours, counted round the clock or in working hours only (working days and hours, in the organization's time zone). A topic names a plan, otherwise the board's default applies; a date typed on the ticket always wins, and a topic's fixed "due within" wins for the resolve date. Alerts from monitoring take their board's plan, timed from when the alert arrived. The first reply is met by the first note anyone on the team writes (recorded as `FirstRespondedAt`; alert repeats do not count). Changing a plan never re-dates tickets already raised; retiring one stops it being applied. PSA tickets keep their PSA's own SLA. Overnight working windows are refused — a night shift is a 24x7 desk. Holidays are not yet taken off the clock. |
| **Canned responses** | *Boards → Canned responses.* Replies the desk keeps, for every ticket (PSA ones included) or one board. Inserted from the reply box and filled in: `{ticket.number}`, `{ticket.title}`, `{customer}`, `{requester}`, `{assignee}`, `{me}`. A placeholder with no value is left visible rather than blanked. |
| **Tasks** | A checklist inside any ticket: add, tick (who and when is recorded), reorder, assign, remove. Staff only, never sent to a PSA. As in osTicket, a ticket with open tasks cannot be closed or resolved from the portal; a PSA that closes the ticket itself is not stopped. |
| **Rich notes** | The reply box has a working toolbar — bold, italic, lists, quote, code, link, table — writing markdown the thread renders. Pasting or dropping a screenshot attaches it to the reply, where it shows inline. Notes are still rendered as React elements from a small markdown subset, never as HTML. Some PSAs (Autotask) show the marks as typed. |

`/api/boards/sla-plans`, `/api/canned-responses`, `/api/tickets/{id}/canned-responses`, `/api/tickets/{id}/tasks`,
`/api/tickets/tasks/{taskId}[/done|/move]`. Migration `SlaCannedTasks` adds three tables (`sla_plans`,
`canned_responses`, `ticket_tasks`) and five nullable columns (tickets: `SlaPlanId`, `FirstResponseDueAt`,
`FirstRespondedAt`; boards: `DefaultSlaPlanId`; board_topics: `SlaPlanId`). Additive; its `Down` drops exactly those.
Rollback tag for this step: `pre-sla-canned-tasks`.

## Recurring tickets, holidays and the SLA pause

| Feature | What it does |
|---|---|
| **Recurring tickets** | *Boards → Recurring.* A schedule — every day, every weekday, weekly on a day, monthly on a day 1-28 or the last day — raises a board ticket at a local hour in the organization's time zone. It goes through the same path as raising by hand (`IInternalTicketService`), so number, topic defaults and SLA plan apply, and its checklist becomes the ticket's tasks. Raised in the name of whoever last saved the schedule; if that person is no longer active the run says so instead of raising. *Skip if open* (on by default) skips a run while the last ticket is still open. A worker down for a while raises one late ticket, not one per missed run. **Raise now** raises immediately without moving the schedule. The worker (`RecurringTicketBackgroundService`) checks every two minutes; each schedule runs in its own tenant scope. |
| **Holidays** | *Boards → SLA plans → Holidays.* The desk's own closed days (distinct from a client's holidays in the control panel). Working-hours plans with *Skip holidays* step over them; round-the-clock plans work through them. Adding one does not re-date tickets already raised. |
| **Night shifts** | A working-hours plan may close at or before it opens — 22:00 to 06:00 — meaning a shift that runs into the next morning. A shift belongs to the day it starts: Monday's runs into Tuesday morning, and a holiday on Monday cancels the shift that would have started Monday night. Only the same hour at both ends is refused. |
| **SLA pause** | A plan with *Pause while waiting* (on by default) stops the clock when a board ticket goes to Waiting customer or On hold (`Ticket.SlaPausedAt`), and on the way out moves each unmet promise forward by the plan-time the pause took — working time for a business-hours plan, so a weekend pause does not hand out two free days. A paused ticket is never on the Overdue view. Resolving or closing ends the pause without moving anything. PSA tickets are unaffected. |

Migration `RecurringHolidaysPause`: tables `recurring_tickets`, `desk_holidays`; columns `tickets.SlaPausedAt`,
`sla_plans.SkipHolidays`, `sla_plans.PauseWhileWaiting`. Additive; rollback tag `pre-recurring-holidays-pause`.

## Alerts from a monitoring tool

A monitoring board can be fed by the tools that watch the estate. Each tool is registered as an
**alert source** with its own key, under Internal boards → Monitoring tools.

| Step | What to do |
|---|---|
| 1 | Create a monitoring board, then connect a tool to it. The key is shown once and never again — only its hash is kept, so a copy of the database cannot raise tickets here. |
| 2 | In the tool, add a webhook that posts JSON to `https://<host>/api/bff/api/intake/alerts`. |
| 3 | Add the header `X-Desk-Alert-Key` carrying the key. It travels in a header, not the URL, so it stays out of access logs and browser history. |
| 4 | Send the documented body. The field names the vendors' own templates use are understood too. |

```json
{
  "alertId": "the tool's own id for the condition",
  "title": "Disk C: is 95% full",
  "description": "Free space 4 GB of 100 GB",
  "severity": "critical",
  "device": "ACME-SRV01",
  "client": "Acme Dental",
  "status": "raised"
}
```

What the portal does with it:

- **One ticket per condition.** The same alert id arriving again updates the existing ticket rather
  than opening another, and a repeat within the hour adds no note at all — a tool that reports every
  five minutes would otherwise bury its own ticket in its own repetitions.
- **Closes itself.** `"status": "cleared"` closes the ticket with a closed date, so it leaves the
  board and still counts in resolution-time figures. A source can be told to leave it open instead.
- **Comes back if the fault does.** The same alert within a day of closing reopens that ticket
  instead of opening a second one.
- **Names the client when it can.** The tool's client name is matched against the customer list,
  ignoring case, spacing and punctuation. An unrecognised name is reported back and kept in the
  ticket text rather than guessed at.
- **Severity becomes priority.** critical and high become HIGH, emergency becomes URGENT, warning
  becomes NORMAL, info becomes LOW, anything unrecognised becomes NORMAL.

Authentication is the key and nothing else, because the request arrives from a vendor's cloud with
no session behind it. An unknown key, a switched-off source and a closed board all answer "not
found", so probing keys teaches nothing. A key that leaks is replaced with **New key**, which
invalidates the old one immediately.

Sources are listed with what they have sent, when they last sent it, and why the last delivery was
refused if it was — which is what makes a misconfigured webhook diagnosable without server access.

**Tested (NinjaOne, Datto RMM):** both publish outbound webhooks with a payload you write yourself,
and both have an OAuth2 REST API. The webhook route needs no API credentials at all.

## What is not built yet

- **Reading back from the RMM.** Nothing polls the tool; alerts arrive only when it sends them.
- **Per-device client mapping.** Matching is on the client name the tool sends; an explicit map
  from a tool's site id to a customer would be more robust for estates with awkward naming.
- **Promoting an internal ticket into a PSA** once a client grants access.
- Pausing on statuses other than Waiting customer and On hold (the pair is fixed for now).
