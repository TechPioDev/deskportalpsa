# What a PSA's classification means in the portal

Phase 9, slice 4e. A PSA files a ticket under up to three levels. This is how a desk says what
those mean in the portal's own words (a category, a work type, a subcategory) and what happens to
a classification nobody has said anything about: nothing.

It follows the owner's decision of 6 October 2026 (decision 4 in section 20a of the
[audit](PHASE9_PSA_INTEGRATION_ARCHITECTURE_AUDIT.md)): translate only through a mapping somebody
configured, for one connection; keep the PSA's original; never guess; unknown is unmapped; show a
preview and a health figure; audit every change; derive nothing about technicians' skills.

The code is `ClassificationRules` (what a rule means, in one place), `ClassificationMappingService`
(the rules, the figures, the preview, applying), three lines of `TicketSyncService` (a ticket as
it arrives) and `ClassificationMapping.tsx` (the Classification tab of Field Mapping). The tests
are `ClassificationMappingTests`, on a SQL translator; the last browser test in
`e2e/connections.spec.ts`; and `psaLevels.unit.ts`.

## What is kept and what is added

| On the ticket | What it is | Who writes it |
|---|---|---|
| `PsaTicketType`, `PsaIssueType`, `PsaSubIssueType` | The PSA's three levels, as it sent them. Autotask: ticket type, issue type, sub-issue type. ConnectWise: type, subtype, item | The sync, and nothing else (slice 4d) |
| `PsaCategory` | The PSA's own category field, as sent | The sync |
| `PortalCategory` | The portal's category | A rule's category where a rule gives one; otherwise the category field's own mapping, as before |
| `PortalWorkType` (new) | What kind of work it is | A rule, or nothing |
| `PortalSubcategory` | The portal's subcategory | A rule, or nothing |

The three PSA levels are never changed by a rule and never hidden by one. A technician sees them
on the ticket under the PSA's own names, with what the rules made of them beside them.

The work type here is the ticket's. It is not the work type of a time entry, which is the PSA's
billing code for an hour of work and has a tab of its own on the same page.

## A rule

A rule belongs to one connection. It names some of the three levels and gives some of the three
portal words:

| Type (PSA) | Subtype (PSA) | Item (PSA) | → | Category | Work type | Subcategory |
|---|---|---|---|---|---|---|
| Hardware | *any* | *any* | | Support | Break/fix | |
| Hardware | Printer | *any* | | | | Printing |

- A level left empty means "whatever it is". The first rule above speaks for every ticket under
  Hardware.
- A level that is named has to be the ticket's. A rule naming a subtype does not speak for a
  ticket that has none.
- The PSA's words are compared without regard to case or to space at either end, and otherwise
  exactly. "Hardware" is not "Hardware issues". Nothing is matched by resemblance.
- A rule names at least one level. **There is no rule for "everything else"**: it would give a
  meaning to a classification nobody has looked at. One is refused when saved, and would not be
  obeyed if it reached the table some other way.
- A rule gives at least one of the three portal words. One that gives none says nothing and is
  refused.
- One rule for one set of levels, on one connection (a unique index holds it). Up to 500 rules a
  connection, each word up to 200 characters.
- Two connections to the same kind of PSA are two accounts. A rule on one says nothing about the
  other, even where the words are the same.

The portal's words are whatever the desk writes: there is no fixed list of categories or work
types to choose from. The page offers the ones already in use on the connection's rules and
tickets, so that the same thing is spelled the same way twice.

### When several rules name a ticket

Each of the category, the work type and the subcategory is taken from **the most exact rule that
gives it**. A rule naming more levels is more exact; of two naming as many, the one naming the
deeper level is. With the two rules above, a ticket filed under Hardware / Printer / Toner is
Support, Break/fix, Printing: the broad rule says what kind of work it is and the narrow one adds
what only it knows. No two rules that name the same ticket can be equally exact, so the answer
does not depend on the order they were written in.

## Unmapped

A ticket the PSA filed under something that no rule names is **unmapped**. Its work type and its
subcategory are empty. Nothing is carried across from the PSA's wording to stand in for a rule,
and nothing is inferred from a similar classification.

One thing has to be said plainly about the category. Before this slice a ticket's category was
already what the category field's own mapping made of the PSA's category field, or that field's
word where nothing mapped it. That is unchanged: an unmapped ticket keeps exactly the category it
would have had last week. A classification rule that gives a category speaks over it; where no
rule does, nothing about the category is new.

Where "unmapped" is said:

- **The Classification tab** lists what tickets are filed under, the unmapped first, each with
  how many tickets, an UNMAPPED mark and a way to write a rule for it with the PSA's levels
  filled in and the portal's left empty.
- **The connection's mapping health** counts classified tickets, how many a rule names and how
  many are unmapped.
- **A ticket**, for staff: "Portal classification: Unmapped" under the PSA's levels.

A ticket the PSA filed under nothing at all has nothing to map. It is not counted as unmapped.

A connection with no rules has not taken this up. Its tickets keep the PSA's classification, the
ticket page says nothing about mapping, and the connection's health is not marked down for it.
Once a connection has a rule, what its rules do not reach is counted as left to do (the
"Optional" level, never a warning or a block: a ticket is whole without it).

## When a rule takes effect

- **As a ticket arrives.** The sync reads the connection's rules once for a run and gives each
  ticket what they say.
- **The next time a ticket is read after the rules change.** What a rule gives is part of what
  the sync compares to decide whether a ticket has changed, so a ticket the PSA sends exactly as
  before is still rewritten when its rule is new, changed or gone. A connection with no rules has
  the comparisons it always had, and nothing is rewritten for having deployed this.
- **When asked: "Apply to tickets already here".** A PSA only sends what changed, so a ticket
  nobody touches in the PSA would wait indefinitely. Applying gives every classified ticket of the
  connection what the saved rules say now.

Saving rules does not touch a ticket. It is one step and applying is another, and the page says
how many tickets are out of step between the two.

### What applying does and does not touch

It does what the sync would do on reading each ticket again, and no more:

- It rewrites the category, the work type and the subcategory of tickets the PSA has filed under
  something, where they are not what the rules say. A rule taken away is taken off the tickets it
  was on: the work type and the subcategory are emptied and the category goes back to what the
  category field's mapping says.
- It never touches the PSA's own levels or category, a ticket's status, or anything else.
- It never touches a ticket filed under nothing. That includes one raised in the portal and not
  yet read back from the PSA, whose category is what the person raising it chose.
- It writes nothing to the PSA. A rule's category is the portal's word and is not sent anywhere;
  the portal sends a category to a PSA only when a ticket is first raised from the portal, from
  what the person chose.
- It is done by id, a thousand tickets a statement, after one read of what each classified ticket
  is filed under and holds. No ticket is loaded. Each rewritten ticket's version moves on, so
  anything holding it from before knows it changed; and a ticket a running sync has refiled
  between the read and the write is left to the sync.
- Done twice, the second does nothing.

## Preview

Before saving, **Preview** says what the rules as written would do: how many tickets already here
would change, how many would be unmapped, and up to ten of the tickets with what they hold now
and what they would hold. It saves no rule and changes no ticket. It refuses what saving would
refuse, in the same words, and says everything that is wrong at once.

## Who may, and what is recorded

| | Permission |
|---|---|
| Read the rules, what tickets are filed under, and a preview | `mappings.view` |
| Save rules, apply them | `mappings.manage` |

- A connection of another organization is not found, for reading and for every change. The
  scheduled sync, which runs with the tenant filter off, names the connection's own organization
  when it reads rules.
- `classification.mapping.changed` is written when a save changed anything, with each rule added,
  changed (before and after) and removed. A save that changes nothing writes nothing.
- `classification.mapping.applied` is written each time rules are applied, with how many tickets
  changed and what to.
- The work type, the subcategory and "unmapped" reach staff only. A client is shown none of them,
  and none of the PSA's levels (slice 4d). A client does see the ticket's category, as they did
  before; where a rule gives the category, that is the word they see.

## What is not derived

Nothing about a technician. No skill, team or routing is worked out from a classification or from
a rule, and no rule can say one.

## The interface

`/api/admin/connections/{connectionId}/classification`

| | | |
|---|---|---|
| `GET` | `mappings.view` | The rules, what tickets are filed under (up to 300: the unmapped first, then by how many tickets), the figures, and the portal words in use |
| `PUT` | `mappings.manage` | Replaces the rules with the list sent. All or nothing |
| `POST …/preview` | `mappings.view` | What the list sent would do. Nothing saved, nothing changed |
| `POST …/apply` | `mappings.manage` | Gives the tickets already here what the saved rules say |

One table, `classification_mappings`, and one nullable column, `tickets.PortalWorkType`
(migration `PsaClassificationMapping`, additive: no existing row is changed).

## Not in this slice

- **Reports by work type.** The work type is on the ticket and on the ticket page. No report or
  dashboard groups by it yet; the workforce figures called "work types" are still the time
  entries' billing codes.
- **A fixed list of the portal's categories and work types.** They are free words, kept
  consistent by what the page offers and not by a list an administrator maintains.
- **Versions and rollback of rules,** as the field mappings have. Every change is in the audit
  log in full, and a set of rules can be put back by hand from it.
- **Proved against a real PSA.** The three levels are read by the Autotask and ConnectWise
  connectors and held by the contract tests against stand-ins. That a real Autotask or
  ConnectWise sends them as expected is part of the certification that waits on the owner's test
  environments: BLOCKED — TEST ENVIRONMENT REQUIRED.
- **In the browser, against imported tickets.** The browser test saves, previews and removes
  rules through the real API. What tickets are filed under, and applying, are shown there over
  stood-in answers, because the browser suite cannot import tickets (the reason is in
  [mapping-health.md](mapping-health.md)); the answers themselves are held by
  `ClassificationMappingTests` and, at volume on PostgreSQL, by the benchmark.
