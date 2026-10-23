using MatTicket.Core.Forms;
using MatTicket.Core.Text;
using MatTicket.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatTicket.Logic;

/// <summary>The activity log, the facts of the system page and the sample data.</summary>
public static class SystemLogic
{
    public static IQueryable<ActivityLog> Activity(Biz biz)
    {
        biz.RequireAdmin();
        return biz.Db.ActivityLog.AsNoTracking();
    }

    public sealed record Facts(int Users, int Tickets, int OpenTickets, int Forms, int Companies, int Contacts, int SyncTasks, int AiRuns, string Database);

    public static async Task<Facts> FactsAsync(Biz biz)
    {
        biz.RequireAdmin();
        var db = biz.Db;
        var version = "";
        try
        {
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SHOW server_version";
            version = "PostgreSQL " + await command.ExecuteScalarAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return new Facts(
            await db.User.CountAsync(),
            await db.Ticket.CountAsync(),
            await db.Ticket.CountAsync(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed),
            await db.TicketForm.CountAsync(),
            await db.Company.CountAsync(),
            await db.Contact.CountAsync(),
            await db.SyncTask.CountAsync(),
            await db.AiRun.CountAsync(),
            version);
    }

    public static async Task<bool> HasSampleDataAsync(Biz biz)
    {
        biz.RequireAdmin();
        return await biz.Db.Department.AnyAsync(d => d.Name == "IT-Support") || await biz.Db.TicketForm.AnyAsync(f => f.Slug == "it-support");
    }

    /// <summary>
    /// A small company to try everything with: departments and teams, categories, the catalogue of questions, two published forms (one with
    /// an AI step), companies and contacts with fields and buttons, and a few tickets. The administrator becomes a member of the IT team.
    /// </summary>
    public static async Task CreateSampleDataAsync(Biz biz)
    {
        biz.RequireAdmin();
        if (await HasSampleDataAsync(biz))
        {
            throw new BizException("Die Beispieldaten gibt es schon.");
        }

        var db = biz.Db;
        Department Dept(string name, string description, string colour, int order) => new() { Name = name, Description = description, Colour = colour, SortOrder = order };
        var it = Dept("IT-Support", "Rechner, Drucker, E-Mail, Zugänge und Software", "#2563eb", 1);
        var service = Dept("Kundenservice", "Fragen zu Aufträgen, Lieferungen und Verträgen", "#0d9488", 2);
        var field = Dept("Technischer Außendienst", "Wartung und Reparatur beim Kunden vor Ort", "#a15c07", 3);
        var accounting = Dept("Buchhaltung", "Rechnungen und Zahlungen", "#7c3aed", 4);
        db.Department.AddRange(it, service, field, accounting);
        await db.SaveChangesAsync();

        Team Team(Department d, string name, string description) => new() { DepartmentId = d.Id, Name = name, Description = description };
        var firstLevel = Team(it, "1st Level", "Erste Hilfe, Passwörter, Standardfälle");
        var secondLevel = Team(it, "2nd Level", "Server, Netzwerk, ERP");
        var center = Team(service, "Service-Center", "Telefon und E-Mail");
        var north = Team(field, "Techniker Nord", "PLZ 0–4");
        var south = Team(field, "Techniker Süd", "PLZ 5–9");
        var debtors = Team(accounting, "Debitoren", "Rechnungen an Kunden");
        db.Team.AddRange(firstLevel, secondLevel, center, north, south, debtors);
        await db.SaveChangesAsync();

        var me = biz.UserId;
        db.TeamMember.Add(new TeamMember { TeamId = firstLevel.Id, UserId = me, IsLead = true });
        var lena = new User { Email = "lena.weber@example.com", DisplayName = "Lena Weber", Role = UserRole.Agent };
        var tom = new User { Email = "tom.becker@example.com", DisplayName = "Tom Becker", Role = UserRole.Agent };
        if (!await db.User.AnyAsync(u => u.Email == lena.Email || u.Email == tom.Email))
        {
            db.User.AddRange(lena, tom);
            await db.SaveChangesAsync();
            db.TeamMember.AddRange(new TeamMember { TeamId = firstLevel.Id, UserId = lena.Id }, new TeamMember { TeamId = north.Id, UserId = tom.Id, IsLead = true });
        }

        await db.SaveChangesAsync();

        Category Cat(string name, string icon, Department d, Team? t, Category? parent = null, TicketPriority priority = TicketPriority.Normal, string? description = null)
            => new() { Name = name, Icon = icon, DepartmentId = d.Id, TeamId = t?.Id, Parent = parent, DefaultPriority = priority, Description = description };
        var hardware = Cat("Hardware", "monitor", it, firstLevel, description: "Geräte am Arbeitsplatz");
        var software = Cat("Software", "apps", it, firstLevel, description: "Programme, E-Mail, Zugänge");
        var billing = Cat("Rechnung und Vertrag", "file-text", accounting, debtors, description: "Fragen zu Rechnungen, Zahlungen, Verträgen");
        var maintenance = Cat("Wartung und Reparatur", "wrench", field, null, description: "Störungen und Termine vor Ort");
        db.Category.AddRange(hardware, software, billing, maintenance);
        await db.SaveChangesAsync();
        var printer = Cat("Drucker", "file", it, firstLevel, hardware);
        var laptop = Cat("Laptop oder PC", "monitor", it, firstLevel, hardware);
        var mail = Cat("E-Mail und Outlook", "mail", it, firstLevel, software);
        var erp = Cat("ERP und Navision", "database", it, secondLevel, software, TicketPriority.High);
        var access = Cat("Zugang und Passwort", "key", it, firstLevel, software);
        var invoice = Cat("Rechnungsfrage", "file-text", accounting, debtors, billing);
        var outage = Cat("Störung vor Ort", "alert", field, null, maintenance, TicketPriority.High);
        var appointment = Cat("Wartungstermin", "calendar", field, null, maintenance, TicketPriority.Low);
        db.Category.AddRange(printer, laptop, mail, erp, access, invoice, outage, appointment);
        await db.SaveChangesAsync();

        db.Question.AddRange(
            new Question { Label = "Seriennummer des Geräts", Key = "seriennummer", Type = BlockTypes.Text, Help = "Steht meist auf einem Aufkleber auf der Rückseite.", CategoryId = hardware.Id },
            new Question { Label = "Wie dringend ist es?", Key = "dringlichkeit", Type = BlockTypes.Radio, Options = "niedrig|Kann warten\nnormal|Normal\nhoch|Ich bin stark eingeschränkt\ndringend|Nichts geht mehr", Required = true },
            new Question { Label = "Rechnungsnummer", Key = "rechnungsnummer", Type = BlockTypes.Text, Placeholder = "z. B. RE-2026-1042", CategoryId = billing.Id },
            new Question { Label = "Standort des Geräts", Key = "standort", Type = BlockTypes.Text, Help = "Adresse, Gebäude, Raum.", CategoryId = maintenance.Id },
            new Question { Label = "Seit wann besteht das Problem?", Key = "seit_wann", Type = BlockTypes.Date });
        await db.SaveChangesAsync();

        var itForm = ItSupportForm(it.Id, firstLevel.Id, hardware.Id);
        var serviceForm = ServiceForm(field.Id);
        foreach (var (name, slug, description, definition, departmentId, ai) in new[]
        {
            ("IT-Support", "it-support", "Hilfe bei Rechnern, Druckern, E-Mail und Zugängen – mit Assistent.", itForm, (long?)it.Id, true),
            ("Service-Anfrage", "service", "Wartung, Reparatur oder eine Störung beim Kunden vor Ort melden.", serviceForm, (long?)field.Id, false),
        })
        {
            var form = new TicketForm { Name = name, Slug = slug, Description = description, DraftJson = definition.ToJson(), DepartmentId = departmentId, AiTriage = ai };
            db.TicketForm.Add(form);
            await db.SaveChangesAsync();
            db.TicketFormVersion.Add(new TicketFormVersion { TicketFormId = form.Id, Version = 1, DefinitionJson = definition.ToJson(), PublishedDate = biz.Now, Note = "Beispiel" });
            form.PublishedVersion = 1;
            form.PublishedDate = biz.Now;
            form.State = FormState.Published;
            await db.SaveChangesAsync();
        }

        // ---- CRM ----
        db.CustomField.AddRange(
            new CustomField { Entity = CrmEntity.Company, Key = "branche", Label = "Branche", Type = CustomFieldType.Select, Options = "Bau\nLogistik\nMaschinenbau\nHandel", SortOrder = 1 },
            new CustomField { Entity = CrmEntity.Company, Key = "vertrag", Label = "Wartungsvertrag", Type = CustomFieldType.Select, Options = "Keiner\nBasis\nPremium", Section = "Vertrag", SortOrder = 2 },
            new CustomField { Entity = CrmEntity.Company, Key = "vertrag_bis", Label = "Vertrag bis", Type = CustomFieldType.Date, Section = "Vertrag", SortOrder = 3 },
            new CustomField { Entity = CrmEntity.Contact, Key = "abteilung", Label = "Abteilung", Type = CustomFieldType.Text, SortOrder = 1 });
        db.ViewAction.AddRange(
            new ViewAction { Entity = CrmEntity.Company, Kind = ViewActionKind.Link, Label = "In Navision öffnen", Icon = "database", Template = "https://navision.example.com/customers/{CustomerNumber}", SortOrder = 1 },
            new ViewAction { Entity = CrmEntity.Company, Kind = ViewActionKind.Map, Label = "Route planen", Icon = "map-pin", Template = "{Address}", SortOrder = 2 },
            new ViewAction { Entity = CrmEntity.Company, Kind = ViewActionKind.Call, Label = "Anrufen", Icon = "phone", Template = "{Phone}", SortOrder = 3, NewWindow = false },
            new ViewAction { Entity = CrmEntity.Contact, Kind = ViewActionKind.Link, Label = "Im CRM öffnen", Icon = "external", Template = "https://crm.example.com/contacts?email={Email}", SortOrder = 1 },
            new ViewAction { Entity = CrmEntity.Contact, Kind = ViewActionKind.Map, Label = "Zum Kunden", Icon = "map-pin", Template = "{Address}", SortOrder = 2 },
            new ViewAction { Entity = CrmEntity.Contact, Kind = ViewActionKind.Mail, Label = "E-Mail schreiben", Icon = "mail", Template = "{Email}", SortOrder = 3, NewWindow = false });

        var companies = new[]
        {
            new Company { Name = "Musterbau GmbH", CustomerNumber = "10001", Street = "Hafenstraße 12", PostalCode = "20457", City = "Hamburg", Country = "Deutschland", Phone = "+49 40 123456", Email = "info@musterbau.example", Website = "https://musterbau.example", ExtraJson = """{"branche":"Bau","vertrag":"Premium","vertrag_bis":"2027-12-31"}""" },
            new Company { Name = "Nordlicht Logistik AG", CustomerNumber = "10002", Street = "Am Speicher 3", PostalCode = "28195", City = "Bremen", Country = "Deutschland", Phone = "+49 421 987654", Email = "service@nordlicht.example", ExtraJson = """{"branche":"Logistik","vertrag":"Basis"}""" },
            new Company { Name = "Alpen Technik KG", CustomerNumber = "10003", Street = "Leopoldstraße 88", PostalCode = "80802", City = "München", Country = "Deutschland", Phone = "+49 89 445566", Email = "kontakt@alpentechnik.example", ExtraJson = """{"branche":"Maschinenbau","vertrag":"Premium","vertrag_bis":"2026-12-31"}""" },
        };
        db.Company.AddRange(companies);
        await db.SaveChangesAsync();
        var contacts = new[]
        {
            new Contact { CompanyId = companies[0].Id, Salutation = "Frau", FirstName = "Sarah", LastName = "Keller", Email = "sarah.keller@musterbau.example", Phone = "+49 40 123456-20", Position = "Bauleiterin", ExtraJson = """{"abteilung":"Projekte"}""" },
            new Contact { CompanyId = companies[0].Id, Salutation = "Herr", FirstName = "Jonas", LastName = "Brandt", Email = "jonas.brandt@musterbau.example", Phone = "+49 40 123456-31", Position = "IT-Koordinator" },
            new Contact { CompanyId = companies[1].Id, Salutation = "Frau", FirstName = "Mia", LastName = "Hansen", Email = "mia.hansen@nordlicht.example", Mobile = "+49 170 5551234", Position = "Disposition" },
            new Contact { CompanyId = companies[2].Id, Salutation = "Herr", FirstName = "Lukas", LastName = "Huber", Email = "lukas.huber@alpentechnik.example", Phone = "+49 89 445566-10", Position = "Werkstattleiter", Street = "Werkstraße 5", PostalCode = "85748", City = "Garching" },
        };
        db.Contact.AddRange(contacts);
        await db.SaveChangesAsync();

        // ---- tickets ----
        var now = biz.Now;
        async Task<Ticket> TicketAsync(string subject, string text, Category category, Contact? contact, TicketStatus status, TicketPriority priority, long? assignee, int hoursAgo, params (MessageKind Kind, string Author, string Text)[] messages)
        {
            var ticket = new Ticket
            {
                Subject = subject,
                Description = HtmlSafe.FromText(text),
                Status = status,
                Priority = priority,
                Source = TicketSource.Form,
                CategoryId = category.Id,
                DepartmentId = category.DepartmentId,
                TeamId = category.TeamId,
                AssigneeUserId = assignee,
                ContactId = contact?.Id,
                CompanyId = contact?.CompanyId,
                RequesterName = contact?.FullName ?? "Peter Schulz",
                RequesterEmail = contact?.Email ?? "peter.schulz@example.com",
                RequesterPhone = contact?.Phone,
                CreateDate = now.AddHours(-hoursAgo),
                LastActivityDate = now.AddHours(-hoursAgo / 2.0),
                ResolvedDate = status is TicketStatus.Resolved or TicketStatus.Closed ? now.AddHours(-1) : null,
            };
            db.Ticket.Add(ticket);
            await db.SaveChangesAsync();
            await TicketLogic.NumberAsync(db, ticket);
            var minute = 0;
            foreach (var (kind, author, body) in messages)
            {
                db.TicketMessage.Add(new TicketMessage { TicketId = ticket.Id, Kind = kind, AuthorName = author, Body = HtmlSafe.FromText(body), CreateDate = ticket.CreateDate.AddMinutes(30 + minute++ * 40), AuthorUserId = kind is MessageKind.Reply or MessageKind.Note ? me : null });
            }

            await db.SaveChangesAsync();
            return ticket;
        }

        await TicketAsync("Drucker im 2. OG druckt nur leere Seiten", "Seit heute früh kommen aus dem großen Drucker im 2. OG nur leere Blätter. Toner ist laut Anzeige voll.", printer, contacts[1], TicketStatus.New, TicketPriority.Normal, null, 3);
        await TicketAsync("Kein Zugriff auf Navision", "Ich bekomme beim Start von Navision die Meldung 'Anmeldung fehlgeschlagen'. Wir können keine Aufträge erfassen!", erp, contacts[0], TicketStatus.Open, TicketPriority.Urgent, me, 6,
            (MessageKind.Note, "Service", "Vermutlich abgelaufenes Kennwort im AD. Prüfe ich."),
            (MessageKind.Reply, "Service", "Guten Tag Frau Keller, wir schauen uns das gerade an und melden uns in Kürze."));
        await TicketAsync("Rechnung RE-2026-1042 doppelt erhalten", "Wir haben die Rechnung RE-2026-1042 zweimal bekommen, einmal per Post und einmal per Mail mit anderem Betrag.", invoice, contacts[2], TicketStatus.Waiting, TicketPriority.Normal, null, 30,
            (MessageKind.Reply, "Service", "Guten Tag Frau Hansen, könnten Sie uns beide Rechnungen als PDF schicken?"));
        await TicketAsync("Hydraulikpresse verliert Öl", "Unsere Presse in Halle 2 verliert seit gestern Öl. Bitte einen Techniker schicken.", outage, contacts[3], TicketStatus.Open, TicketPriority.High, null, 20,
            (MessageKind.Customer, "Lukas Huber", "Ergänzung: Es ist die Presse mit der Inventarnummer HP-07."));
        await TicketAsync("Jährliche Wartung planen", "Bitte einen Termin für die jährliche Wartung im November vorschlagen.", appointment, contacts[0], TicketStatus.New, TicketPriority.Low, null, 50);
        await TicketAsync("Outlook fragt ständig nach dem Passwort", "Outlook fragt alle paar Minuten nach meinem Kennwort.", mail, null, TicketStatus.Resolved, TicketPriority.Normal, me, 72,
            (MessageKind.Reply, "Service", "Bitte einmal die gespeicherten Anmeldedaten in der Windows-Anmeldeinformationsverwaltung löschen und Outlook neu starten."),
            (MessageKind.Customer, "Peter Schulz", "Danke, das hat geholfen!"));

        var site = SettingLogic.Get<SiteSettings>(biz, SettingStore.Site);
        if (string.IsNullOrWhiteSpace(site.PortalIntro))
        {
            site.PortalIntro = "Wählen Sie, wobei wir helfen können. Unser Assistent hilft oft sofort – sonst entsteht ein Ticket, und wir melden uns.";
            await SettingLogic.SaveAsync(biz, SettingStore.Site, site, "Portal-Text gesetzt");
        }

        await biz.Log.LogAsync("Beispieldaten angelegt", null, null, "Abteilungen, Teams, Kategorien, Formulare, CRM und Tickets");
    }

    private static FormDefinition ItSupportForm(long departmentId, long teamId, long hardwareId) => new()
    {
        Title = "IT-Support",
        Intro = "<p>Beschreiben Sie Ihr Problem. Oft können wir sofort helfen – sonst entsteht ein Ticket für unser IT-Team.</p>",
        Start = "bereich",
        Steps =
        [
            new FormStep
            {
                Id = "bereich", Kind = StepKinds.Page, Title = "Worum geht es?", X = 40, Y = 140,
                Blocks =
                [
                    new FormBlock
                    {
                        Id = "bereich-b1", Type = BlockTypes.Radio, Key = "bereich", Label = "Bereich", Required = true,
                        Options = [new() { Value = "drucker", Label = "Drucker" }, new() { Value = "rechner", Label = "Laptop oder PC" }, new() { Value = "email", Label = "E-Mail und Outlook" }, new() { Value = "sonstiges", Label = "Etwas anderes" }],
                    },
                    new FormBlock { Id = "bereich-b2", Type = BlockTypes.Text, Key = "kurz", Label = "In einem Satz", Required = true, Placeholder = "z. B. Drucker druckt nur leere Seiten" },
                    new FormBlock { Id = "bereich-b3", Type = BlockTypes.Textarea, Key = "beschreibung", Label = "Was genau passiert?", Required = true, MapTo = "ticket.description", Help = "Fehlermeldungen bitte wörtlich abschreiben." },
                ],
                Next = [new FormRoute { When = new FormCondition { Field = "bereich", Op = "eq", Value = "drucker" }, To = "drucker" }, new FormRoute { To = "assistent" }],
            },
            new FormStep
            {
                Id = "drucker", Kind = StepKinds.Page, Title = "Kurz prüfen", X = 340, Y = 40,
                Blocks =
                [
                    new FormBlock { Id = "drucker-b1", Type = BlockTypes.RichText, Html = "<p>Bitte prüfen Sie zuerst:</p><ul><li>Ist Papier eingelegt und keine Klappe offen?</li><li>Zeigt das Display eine Meldung?</li><li>Hilft es, den Drucker aus- und wieder einzuschalten?</li></ul>" },
                    new FormBlock { Id = "drucker-b2", Type = BlockTypes.YesNo, Key = "drucker_geloest", Label = "Ist das Problem damit gelöst?", Required = true },
                ],
                Next = [new FormRoute { When = new FormCondition { Field = "drucker_geloest", Op = "eq", Value = "ja" }, To = "geloest" }, new FormRoute { To = "kontakt" }],
            },
            new FormStep
            {
                Id = "assistent", Kind = StepKinds.Ai, Title = "Vorschlag unseres Assistenten", X = 340, Y = 240,
                Instruction = "Hilf bei typischen Problemen der Büro-IT (Windows, Microsoft 365, Outlook, Drucker, WLAN). Keine Eingriffe, die Administratorrechte brauchen.",
                HelpedText = "Das hat geholfen", NotHelpedText = "Ticket erstellen",
                Next = [new FormRoute { When = new FormCondition { Field = FormEngine.AiOutcomeKey, Op = "eq", Value = "solved" }, To = "geloest" }, new FormRoute { To = "kontakt" }],
            },
            new FormStep
            {
                Id = "kontakt", Kind = StepKinds.Ticket, Title = "Ihre Kontaktdaten", X = 660, Y = 140, ButtonText = "Ticket senden",
                SubjectTemplate = "{bereich}: {kurz}", DepartmentId = departmentId, TeamId = teamId, CategoryId = hardwareId,
                Blocks =
                [
                    new FormBlock { Id = "kontakt-b1", Type = BlockTypes.Text, Key = "name", Label = "Name", Required = true, MapTo = "requester.name", Width = "half" },
                    new FormBlock { Id = "kontakt-b2", Type = BlockTypes.Email, Key = "email", Label = "E-Mail-Adresse", Required = true, MapTo = "requester.email", Width = "half" },
                    new FormBlock { Id = "kontakt-b3", Type = BlockTypes.Phone, Key = "telefon", Label = "Telefon", MapTo = "requester.phone", Width = "half" },
                    new FormBlock { Id = "kontakt-b4", Type = BlockTypes.Text, Key = "firma", Label = "Firma", MapTo = "requester.company", Width = "half" },
                    new FormBlock
                    {
                        Id = "kontakt-b5", Type = BlockTypes.Radio, Key = "dringlichkeit", Label = "Wie dringend ist es?", Required = true, MapTo = "ticket.priority",
                        Options = [new() { Value = "niedrig", Label = "Niedrig" }, new() { Value = "normal", Label = "Normal" }, new() { Value = "hoch", Label = "Hoch" }, new() { Value = "dringend", Label = "Dringend" }],
                    },
                    new FormBlock { Id = "kontakt-b6", Type = BlockTypes.Consent, Key = "datenschutz", Label = "Ich bin einverstanden, dass meine Angaben zur Bearbeitung meiner Anfrage gespeichert werden.", Required = true },
                ],
                DoneHtml = "<p>Danke! Ihr Ticket ist bei unserem IT-Team angekommen. Sie bekommen eine Bestätigung per E-Mail.</p>",
            },
            new FormStep
            {
                Id = "geloest", Kind = StepKinds.End, Title = "Schön, dass es geklappt hat!", X = 660, Y = 0,
                DoneHtml = "<p>Prima, dann ist kein Ticket nötig. Bei neuen Fragen sind wir jederzeit hier.</p>",
            },
        ],
    };

    private static FormDefinition ServiceForm(long departmentId) => new()
    {
        Title = "Service-Anfrage",
        Intro = "<p>Melden Sie eine Störung oder fragen Sie einen Wartungstermin an. Unser Außendienst meldet sich.</p>",
        Start = "anliegen",
        Steps =
        [
            new FormStep
            {
                Id = "anliegen", Kind = StepKinds.Page, Title = "Ihr Anliegen", X = 40, Y = 80,
                Blocks =
                [
                    new FormBlock { Id = "anliegen-b1", Type = BlockTypes.Category, Key = "kategorie", Label = "Art der Anfrage", Required = true },
                    new FormBlock { Id = "anliegen-b0", Type = BlockTypes.Customer, Key = "kunde", Label = "Kunde", Visibility = Visibilities.Staff, Help = "Nur für den Service: für welchen Kunden die Anfrage erfasst wird." },
                    new FormBlock { Id = "anliegen-b2", Type = BlockTypes.Text, Key = "geraet", Label = "Gerät oder Anlage", Required = true, MapTo = "ticket.subject" },
                    new FormBlock { Id = "anliegen-b3", Type = BlockTypes.Text, Key = "standort", Label = "Standort", Help = "Adresse, Gebäude, Halle." },
                    new FormBlock { Id = "anliegen-b4", Type = BlockTypes.Date, Key = "wunschtermin", Label = "Wunschtermin", Width = "half" },
                    new FormBlock { Id = "anliegen-b5", Type = BlockTypes.Textarea, Key = "beschreibung", Label = "Beschreibung", Required = true, MapTo = "ticket.description" },
                ],
                Next = [new FormRoute { To = "kontakt" }],
            },
            new FormStep
            {
                Id = "kontakt", Kind = StepKinds.Ticket, Title = "Ihre Kontaktdaten", X = 360, Y = 80, ButtonText = "Anfrage senden", DepartmentId = departmentId,
                Blocks =
                [
                    new FormBlock { Id = "kontakt-b1", Type = BlockTypes.Text, Key = "name", Label = "Name", Required = true, MapTo = "requester.name", Width = "half" },
                    new FormBlock { Id = "kontakt-b2", Type = BlockTypes.Email, Key = "email", Label = "E-Mail-Adresse", Required = true, MapTo = "requester.email", Width = "half" },
                    new FormBlock { Id = "kontakt-b3", Type = BlockTypes.Phone, Key = "telefon", Label = "Telefon für Rückfragen", Required = true, MapTo = "requester.phone", Width = "half" },
                    new FormBlock { Id = "kontakt-b4", Type = BlockTypes.Text, Key = "firma", Label = "Firma", Required = true, MapTo = "requester.company", Width = "half" },
                ],
                DoneHtml = "<p>Vielen Dank! Wir melden uns für die Terminabstimmung.</p>",
            },
        ],
    };
}
