# The report center (Phase 8)

Workforce reports previewed on screen and exported as CSV or XLSX. A report is a definition in code
and one builder; the preview and both exports call the same builder with the same filters, so a
file's totals are the ones previewed. Facts from records, by the definitions the dashboards use:
[PHASE7_ANALYTICS_METRIC_SPEC.md](PHASE7_ANALYTICS_METRIC_SPEC.md) and
[PHASE8_MANAGEMENT_INSIGHTS_DESIGN.md](PHASE8_MANAGEMENT_INSIGHTS_DESIGN.md). Not a score.

**Internal only.** Every route is under `api/workforce`, behind the Workforce switch, for staff.

## Where it is

Workforce → **Reports** (`/dashboard/workforce/reports`): the catalogue, grouped by category. A
report opens at `/dashboard/workforce/reports/{key}` with its filters in the address.

## The reports

| Category | Report (`key`) | Period | Rows |
|---|---|---|---|
| Workforce | Workforce utilization (`workforce-utilization`) | A period | Per person: capacity, planned, tentative, actual, scheduled and capacity utilization, over capacity |
| Workforce | Technician work summary (`technician-work-summary`) | A period | Per person: actual, planned actual, reactive and its share, client / internal / monitoring, billable, completed, work items |
| Workforce | Team work summary (`team-work-summary`) | A period | Per team: people, capacity, planned, actual, planned actual, reactive, completed, work items |
| Capacity | Capacity and demand by day (`capacity-demand`) | A window ahead | Per day: capacity, confirmed, tentative, unscheduled due that day, what is left |
| Capacity | Future capacity by technician (`future-capacity`) | A window ahead | Per person: capacity, confirmed, tentative, estimated unscheduled, unestimated items, projected, gap, projected load; one more row for work not yet assigned |
| Clients | Client workload (`client-workload`) | A period against the one before | Per client: actual now and before, change, reactive, completed |
| Delivery | Planned against actual by day (`planned-vs-actual`) | A period | Per day: capacity, planned, tentative, actual, planned actual, reactive, variance, completed |
| Delivery | Reactive work by week (`reactive-work`) | The last eight weeks | Per week: actual, planned actual, reactive and its share, completed |
| Delivery | Estimate variance (`estimate-variance`) | A period against the one before | Per category, client or source (`by`): planned, recorded on planned work, variance, estimate variance, ticket-days |
| Delivery | Work sources (`work-sources`) | A period against the one before | Per source: actual now and before, change, reactive, completed |
| Quality | Operational quality (`operational-quality`) | A period against the one before | Per signal: data quality, met, out of, share, the same before, what is counted, what it rests on |
| Integrations | Mapping and integration health (`integration-health`) | As it stands | Per connection: state, last sync, tickets, mapping coverage, failed records. Needs `integration.health.view` |

Rows about people are in name order. Nothing is ranked.

## The preview

- What was applied: the period and its time zone, each filter by name ("None: everyone and all the
  work you may see" when there is none), when it was generated, and how fresh each PSA sync is.
- A summary of the report's totals.
- The table: the first 500 rows with the full count; any column can be sorted.
- The notes: what the numbers cover and what they do not.

A filter is named only by what the caller is offered: a team from their own lists, a client only if
they may open some of its work. An id they could not otherwise see is not given a name.

## Export

- **CSV**: UTF-8 with a byte-order mark. Every text cell goes through the existing formula-safe
  writer: a value beginning `=`, `+`, `-`, `@`, a tab or a carriage return is prefixed with an
  apostrophe.
- **XLSX**: one sheet, written by `XlsxWriter` on `System.IO.Compression` (part of .NET). **No new
  dependency.** Text is written as an inline string, which a spreadsheet shows and never evaluates,
  so a value beginning `=` is text by construction. Numbers are numeric cells. The file holds six
  parts (content types, two relationship parts, the workbook, the styles, the sheet) and no formula,
  link, macro or external reference.
- Both hold the same rows in the same order: the title, the period, the generation time, the
  filters, the summary, the table, the notes. A figure that does not apply is written **N/A**, never
  0. A signed difference in the summary is said in words ("64 h left", "2 h over plan"), so nothing
  in the file begins with a sign.
- At most 50,000 rows; past that the export is refused with the count, asking for a narrower period.
- The file name is `pio-manage-{key}-{from}-{to}.{csv|xlsx}`.

## API

All GET, under `api/workforce`.

| Route | Permission | Returns |
|---|---|---|
| `reports` | `schedule.view` | The reports the caller may run (integration health only with its permission) |
| `reports/{key}?period\|window\|compare&from&to&by&teamId&departmentId&appUserId&clientId&source&priority` | `schedule.view` | The preview: definition, period, applied filters, summary, columns, the first 500 rows, the full count, notes, sync freshness, whether the caller may export |
| `reports/{key}/export?format=csv\|xlsx&…` | `workforce.analytics.export` | The file |

A cell is text, a number or null. A column's kind (`text`, `hours`, `count`, `percent`, `date`) says
how it is shown on screen and typed in a spreadsheet; hours are decimal hours to two places.

An unknown report key is "not found"; an unknown format or grouping is a 400.

## Permissions and isolation

| Capability | Permission |
|---|---|
| The catalogue and a preview | `schedule.view`; its scope decides whose rows |
| An export | `workforce.analytics.export` (Manager, Administrator), required by the controller and checked again in the service |
| The integration health report | Also `integration.health.view` |

- **Nothing is stored.** There is no saved report, generated file or export job, so there is no id
  to guess: a report is addressed by a key from a fixed list, and the rows are whatever the caller's
  scope reaches when they ask. Recorded because the brief asks for tests against forged report and
  export ids: the test is that every unknown key is "not found" and that no such id exists.
- A person outside the caller's scope is "Person was not found." in every report. A team outside it
  is nobody. A client or connection that is not the organization's is "not found".
- Another organization's manager, holding every permission, gets that organization's rows and none
  of this one's, in every report and both formats (tested).

## Audit

Every successful export writes `workforce.report.exported` with the report key, the format, the
period and its zone, the filters and the row count. A refused export writes nothing. Previews are
not audited.

## Scheduled reports

Not offered. The existing staff-report scheduler accepts any syntactically valid address as a
recipient and does not re-check the creator's permission when a run fires; a workforce report sent
on those terms could leave the organization, or outlive the right that created it. What a safe
version needs (recipients limited to active staff who hold the permission at send time, the
permission re-checked on every run, a retention rule for stored files, an audit entry per delivery)
is recorded in the design report, §17.

## Troubleshooting

| Seen | Why | Do |
|---|---|---|
| No Export buttons | The viewer lacks `workforce.analytics.export` | An administrator grants it on the role |
| "There is no such report, or it is not one you may open." | The key is not in the catalogue, or it is the integration health report without its permission | Open it from the catalogue |
| "That is N rows; an export holds at most 50,000." | The report is too large | Narrow the period or the filters |
| A cell reads N/A | The figure does not apply (no capacity, nothing to divide by, no data) | Expected: it is not a zero |
| A name in the CSV starts with an apostrophe | It began with a formula character | Expected: the writer neutralised it |
| Totals differ from yesterday's file | The report is built from the records when it is opened | Expected: nothing is cached |
