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
- **Due dates, recurring internal tasks and checklists.**
