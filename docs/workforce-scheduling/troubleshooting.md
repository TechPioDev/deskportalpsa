# Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| No "Workforce" menu; routes answer 404 | Module switched off | Set `Features__Workforce=true` for the API and redeploy |
| A technician sees only themselves | `schedule.view` scope is Own (default for technicians) | Expected. Widen the scope in Roles & Permissions if a role should see a team. |
| "...is not a time zone this system knows" | Not an IANA id on a Linux server | Pick from the list (it comes from the browser) |
| Times look shifted on a Windows dev machine | Invariant globalization: IANA zones resolve to UTC on Windows | Development only; the servers are Linux |
| "A schedule can start today ... or later" | Past days keep their schedule | Choose today or a later date |
| "Only a schedule that has not started yet can be removed" | That version is already in force | Save a new version from today instead |
| "...is retired. Reactivate it..." | Skill retired | Reactivate it in the Skills catalogue |
