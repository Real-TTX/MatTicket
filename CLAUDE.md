# MatTicket: notes for working in this repository

Tickets with a form designer (WYSIWYG steps + flow editor), published forms at `/{slug}`, a small CRM with sync tasks, an AI layer (profiles,
layered instructions, runs, journal), Microsoft Entra ID sign-in and a full PWA. ASP.NET Core 10 Razor Pages, EF Core 10 on PostgreSQL 17, one
container (port **2740**) plus a database. User-facing text is **German** (customers are addressed with "Sie", staff with "du"); code, comments and docs
(except the README) are English. Infrastructure (setup, sign-in, TOTP, sessions, list controls, shell) came from `../InvoiceMesh`; the look is MatDesign
(`../MatDesign`, MatUi copied into `src/MatTicket/wwwroot/ui`, version in `scripts/matui-synced.txt`). Before changing UI read MatDesign's `CLAUDE.md`
and `docs/ADOPTING.md`.

## Commands

```bash
dotnet build MatTicket.slnx                  # build before a redeploy: a compile error otherwise shows up as "docker compose failed"
dotnet test MatTicket.slnx                   # + MATTICKET_TEST_DB="Host=localhost;Port=2742;Username=matticket;Password=matticket" for the integration tests
./scripts/redeploy.sh  (or .ps1)             # rebuild the image and start the local stack: web 2740, pgAdmin 2741, Postgres 2742; --clean / -Clean empties the volumes
dotnet ef migrations add <Name> -p src/MatTicket -s src/MatTicket -o Data/Migrations
cd tests/e2e && npm install && node pwa-check.js http://localhost:2740      # PWA in Edge
node tests/e2e/screens.js http://localhost:2740 docs/images                 # README screenshots (uses tests/e2e/test-account.json, local stack only)
NODE_PATH=tests/e2e/node_modules node tools/icons.js                       # PNG icons from the SVGs
./scripts/sync-matui.ps1 ; ./scripts/check-matui.ps1                       # MatUi from MatDesign
```

## Layout

- `src/MatTicket.Core` (no web types, unit tested): `Forms/` (`FormDefinition` – steps, blocks, routes; `FormEngine` – read/validate answers, next step,
  `Check`, `Clean`), `Text/` (`HtmlSafe` – the one HTML gate, `Placeholders`, `Slug`, `Pii`), `Ai/` (`ChatCompletionsClient`, `ChatGptAccount` (device code),
  `LenientJson`, `AiPresets`).
- `src/MatTicket/Logic`: **the business layer, static classes** taking `Biz` (db, current user, clock, services) and checking permissions themselves
  (`RequireAdmin`/`RequireAgent`, ticket visibility in `TicketLogic.VisibleAsync`). `ForbiddenException` → 403 (middleware), `BizException` → message at the field.
  Pages never query the database; list pages pass the `IQueryable` the logic returns to `UiKit/ListBuilder`.
- `src/MatTicket/Services`: infrastructure (auth, sessions, mail queue + sender, AI secret store + rate limiter + worker, config).
- `src/MatTicket/Pages`: Tickets, Crm, Admin (Forms + Designer, Questions, Categories, Departments, Teams, Users, Crm fields/actions/layout, Sync, Ai, Entra,
  Mail, Portal, System, Activity), Account, Portal (`/hilfe`, `/{slug}`, `/t/{token}` – layout `_Public`), About (linked from the version in the menu).
- `wwwroot/js`: `app.js` (offline, rich-text editor `data-rte`, AI suggestion, map), `designer.js`, `portal.js`, `admin.js`. No inline scripts or styles (CSP).

## Rules of the domain

- Roles: Admin, Agent (service staff), Requester (portal only: forms with their account, "Meine Anfragen" on /hilfe; agent pages redirect them to /hilfe).
- Portal access (`PortalLogic.AccessOf`): form `Access` Public / SignedIn / Staff / Groups (Entra group object ids stored on the user at sign-in, `User.EntraGroupsJson`);
  staff may use every form. Categories can be limited to groups (a subcategory also needs its parent). Blocks have a `Visibility`; what a viewer does not see is not
  read (`FormEngine.Read/Validate` with `PublicForm.Visible`). `AccountData`: requester name/e-mail/phone from the account, the form does not ask. The customer block
  (CRM companies) is forced to staff-only in `FormEngine.Clean` and only staff get the list.
- Ticket visibility: admin all; agent = assigned to them, no department, departments of their teams, their teams. Moving clears an assignee who is not in the new place.
- Public forms: the server runs every step; the state travels protected (`PortalLogic.Protect`, 12 h). Only answers of the current step are read from a post.
  Reserved slugs in `FormLogic.ReservedSlugs`. Publishing copies the cleaned draft into an immutable `TicketFormVersion`.
- Every HTML that is shown goes through `HtmlSafe.Clean`; AI output with a script is rejected (`ContainsScript`), the assistant's links are stripped.
- AI: one JSON object per answer (`LenientJson`), required field per action, at most two nudges then `Stuck`; limits before the call; tokens estimated (chars/4)
  when missing; changes in `AiJournalEntry`, undo newest first. Secrets only in `{data}/secrets/ai` (`AiSecretStore`), never in DB or page; ChatGPT sign-ins are moved, never copied.
- Entra: options from `SettingStore` (`EntraOptionsSetup`), cache invalidated on save; incomplete config gives harmless placeholder options; challenge errors
  and remote failures end on the login page with a reason. Groups → roles via `EntraGroupRole`; no group → no access.
- Database: tables PascalCase, `Id` bigint, audit columns filled by `MtDbContext`, `UpdateState` soft delete, enums as text, `*Json` properties are jsonb,
  `Token` (uuid) for secrets (ticket status link).

## Gotchas

- Razor: a variable named `section` clashes with the directive; collection initialisers inside tag-helper attributes do not compile (use a variable).
- `@page` route templates: braces are doubled (`{{0,59}}`) but brackets are not.
- Forms with an explicit `action` need `asp-antiforgery="true"`; edit forms that post to other handlers need `asp-page-handler=""` on the form.
- MatUi caps `svg` at `max-width: 100%` (the flow SVG overrides it).
- The embedded browser of the Claude app cannot register service workers; test the PWA with `tests/e2e/pwa-check.js`.

## Git

Branches: `main` (release), `dev` (nightly). Commit and push **under the user's name** only: no `Co-Authored-By` trailer, no "Generated with" line, no other
names in branches. Versions: release `<major>.<minor>.<build>-<date>`, dev `nightly-<build>-<date>`, local `local-<date>` (`Directory.Build.props`, set by the action).
