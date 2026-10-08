# Sign-in invitations and password resets

How a person gets into PioManage, and what an administrator does to make that happen.

## What was wrong

Sign-in is Keycloak. A staff or client user created in PioManage had no login: someone had to
create the Keycloak account by hand, and the portal joined the two the first time that person
signed in with a verified e-mail. The sign-in page said as much ("your account is created by your
provider"). On 8 October 2026 production had 41 active staff of whom 40 had never signed in, and
2 users in the realm.

## What happens now

**PioManage owns the invitation; Keycloak only stores the password.**

1. An administrator invites a person: from **Users** (the row menu, "Send invitation", or
   "Invite everyone who is pending" for everyone who has never signed in), or from the client
   **Control Panel** (adding a user invites them; "Resend invitation" on a row). The desk can also
   invite a client user (`POST api/admin/client-users/{id}/invite`, `clientusers.manage`).
2. A one-time link is made (a random token; only its SHA-256 is stored), mailed from the
   organization's own mail account, and **shown once to the inviter to copy** where mail does not
   reach. It works for seven days.
3. The person opens the link, sees their name and the organization, and chooses a password
   (twelve characters at least, not their e-mail address, some variety). PioManage creates the
   Keycloak user with that password through Keycloak's admin API, links the sign-in to the
   portal user, spends the link, and sends a welcome mail. The page then hands over to the real
   sign-in with the address filled in.
4. Reminders go on the third and the sixth day, each carrying a fresh link (the earlier one stops
   working, and the mail says so). After the seventh day an expiry notice goes once; a new
   invitation is a new link.
5. **Forgot your password?** on the sign-in page: the person enters their address; if a person who
   can sign in has it, a reset link goes (one hour). The page answers the same for any address.
   Keycloak's own reset screen stays off; the realm has no mail of its own.

A second invitation to the same person replaces the first. A used, expired or revoked link cannot
be used; the page says which, in words that give nothing else away, and an unknown link reads
exactly like a revoked one. A link belongs to one organization; from inside another it is not
found.

Every invitation, acceptance, revocation, reset and template change is audited
(`user.invited`, `user.invitation_accepted`, `user.invitation_revoked`, `user.password_reset`,
`email.template_changed`, `email.template_reset`). Every mail sent or attempted is logged with
its result (`email_log`), shown under **E-mail wording → Recent e-mails**.

## The wording

**E-mail wording** (under Integration Health in the menu; `org.manage` to change, the health
view to read) lists the five e-mails: *Invitation, Invitation reminder, Invitation expired,
Welcome, Password reset*. Each can be changed (subject, body, button label where the mail
carries a link), previewed with sample values before saving, sent to yourself, and put back to
the product's default. A template may use only the placeholders its kind offers (`{{user.name}}`,
`{{user.first_name}}`, `{{user.email}}`, `{{organization.name}}`, `{{product}}`,
`{{product_url}}`, and where offered `{{link}}`, `{{expires}}`, `{{invited_by}}`); anything else
is refused when saving, so a typo cannot make a blank where the link should be. The defaults
live in code (`EmailTemplates`); an organization's changes are rows (`email_templates`).

Mail is text and HTML; the HTML is table-based with inline styles and a button for the link, the
same shape as PioAssets' mail, and the text form carries the link on its own line.

## What the host has to set (once)

PioManage needs leave to create users in Keycloak. In the Keycloak console, realm `desk`:

1. **Clients → Create client**: Client ID `desk-admin`, type OpenID Connect, *Client
   authentication* ON, *Service accounts roles* ON, everything else off. Save.
2. On the new client, **Service accounts roles → Assign role → Filter by clients →
   `realm-management` → `manage-users`**. Assign. (Only that role; it is all the portal calls.)
3. **Credentials → Client secret**: copy it.
4. On the server, in the API's environment: `Keycloak__AdminClientId=desk-admin` (the default,
   may be omitted) and `Keycloak__AdminClientSecret=<the secret>`. `Keycloak__Authority` is the
   issuer the API already validates tokens against. Restart the API.
5. `Portal__PublicUrl=https://piomanage.com` is where links point; it falls back to
   `Attachments__PublicBaseUrl`, which production already has.

Until the secret is set, invitations can be made and mailed, but the page at a link says it
cannot be accepted yet, and nothing is sent to Keycloak. Local mode runs no Keycloak and uses an
in-memory provider, so the whole flow can be driven in a browser there except the final sign-in.

## Where the code is

`SignInInvitation`, `EmailTemplate`, `EmailLogEntry` (domain); `IInvitationService`,
`IEmailTemplateService`, `IKeycloakAdmin` (application); `InvitationService`,
`KeycloakAdminClient`, `InvitationReminderRunner`, `EmailTemplates`, `TemplateMailer`,
`EmailTemplateService` (infrastructure); `InvitationsController` (public, rate-limited as the
other public forms), `AdminInvitationsController`, `AdminEmailTemplatesController`, the Control
Panel's `users` and `users/{id}/invite` routes (API); the worker's `InvitationReminderService`
(every thirty minutes); the web's `/invite/[token]`, `/reset-password`, `/reset-password/[token]`,
the Users and Control Panel pages, and `/dashboard/email-templates`. Tests: `InvitationTests`
(unit, on a SQL translator) and `e2e/invitations.spec.ts`.

One migration, `SignInInvitations`: three tables, additive.

## Not done here

- Reminders and resends carry a fresh link; the earlier link stops working. Keeping both alive
  would mean storing the token, which is what the hash is there to avoid.
- Staff cannot yet see client users' invitations in one list from the dashboard; the Control
  Panel shows them to the client's administrators.
- The real Keycloak admin API is exercised only against this document and the client's own
  error handling; the first acceptance in production is the proof, with the secret set by the
  host. The in-memory provider covers everything else.
