using System.Net;
using System.Threading.Channels;
using MailKit.Net.Smtp;
using MailKit.Security;
using MatTicket.Configuration;
using MatTicket.Core.Text;
using MatTicket.Data;
using MatTicket.Data.Entities;
using MatTicket.Logic;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatTicket.Services.Mail;

/// <summary>Something that should become a mail. The mail itself is written later from the database, so a job never carries content.</summary>
public abstract record MailJob
{
    public sealed record Created(long TicketId, bool ToCustomer) : MailJob;

    public sealed record Replied(long TicketId, long MessageId) : MailJob;

    public sealed record Moved(long TicketId) : MailJob;

    public sealed record CustomerWrote(long TicketId) : MailJob;

    public sealed record Test(string To, TaskCompletionSource<string?> Done) : MailJob;
}

/// <summary>
/// The mails of the tickets go through a queue: a page never waits for a mail server, and a mail server that is down never breaks a page.
/// Without mail settings the jobs are dropped quietly.
/// </summary>
public sealed class MailQueue
{
    private readonly Channel<MailJob> _channel = Channel.CreateBounded<MailJob>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<MailJob> Reader => _channel.Reader;

    public void TicketCreated(long ticketId, bool toCustomer) => _channel.Writer.TryWrite(new MailJob.Created(ticketId, toCustomer));

    public void Replied(long ticketId, long messageId) => _channel.Writer.TryWrite(new MailJob.Replied(ticketId, messageId));

    public void TicketMoved(long ticketId) => _channel.Writer.TryWrite(new MailJob.Moved(ticketId));

    public void CustomerWrote(long ticketId) => _channel.Writer.TryWrite(new MailJob.CustomerWrote(ticketId));

    /// <summary>Sends a test mail and says what went wrong (null: sent).</summary>
    public async Task<string?> TestAsync(string to)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(new MailJob.Test(to, done));
        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(45)));
        return finished == done.Task ? done.Task.Result : "Der Mailserver hat nicht rechtzeitig geantwortet.";
    }
}

/// <summary>Writes and sends the mails of <see cref="MailQueue"/>.</summary>
public sealed class MailSender(MailQueue queue, IServiceScopeFactory scopes, SettingStore settings, ConfigStore config, ILogger<MailSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await HandleAsync(job, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "A mail of {Job} could not be sent.", job.GetType().Name);
                if (job is MailJob.Test test)
                {
                    test.Done.TrySetResult(ex.Message);
                }
            }
        }
    }

    private async Task HandleAsync(MailJob job, CancellationToken ct)
    {
        var mail = settings.Get<MailSettings>(SettingStore.Mail);
        if (job is MailJob.Test test)
        {
            if (!mail.IsUsable)
            {
                test.Done.TrySetResult("Die Mail-Einstellungen sind nicht vollständig oder ausgeschaltet.");
                return;
            }

            await SendAsync(mail, [test.To], "MatTicket: Testnachricht", "<p>Diese Nachricht kommt von MatTicket. Der Versand funktioniert.</p>", null, ct);
            test.Done.TrySetResult(null);
            return;
        }

        if (!mail.IsUsable)
        {
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MtDbContext>();
        var site = settings.Get<SiteSettings>(SettingStore.Site);
        var ticketId = job switch
        {
            MailJob.Created c => c.TicketId,
            MailJob.Replied r => r.TicketId,
            MailJob.Moved m => m.TicketId,
            MailJob.CustomerWrote w => w.TicketId,
            _ => 0,
        };
        var ticket = await db.Ticket.AsNoTracking().Include(t => t.Department).FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return;
        }

        var link = Absolute($"/t/{ticket.Token:N}");
        var subject = $"[{ticket.Number}] {ticket.Subject}";
        var replyTo = ticket.Department?.Email;
        switch (job)
        {
            case MailJob.Created c:
                if (c.ToCustomer && FormEngineEmail(ticket.RequesterEmail))
                {
                    await SendAsync(mail, [ticket.RequesterEmail], subject,
                        $"<p>Guten Tag {Enc(ticket.RequesterName)},</p><p>wir haben Ihre Anfrage erhalten und kümmern uns darum. Ihre Ticketnummer ist <strong>{Enc(ticket.Number)}</strong>.</p>" +
                        $"<p>Den Stand sehen und antworten können Sie hier:<br><a href=\"{Enc(link)}\">{Enc(link)}</a></p><p>Ihr {Enc(site.ServiceName)}</p>", replyTo, ct);
                }

                if (mail.NotifyAgents)
                {
                    await NotifyAgentsAsync(db, mail, ticket, $"Neues Ticket {ticket.Number}: {ticket.Subject}", ct);
                }

                break;

            case MailJob.Replied r when FormEngineEmail(ticket.RequesterEmail):
                var message = await db.TicketMessage.AsNoTracking().FirstOrDefaultAsync(m => m.Id == r.MessageId, ct);
                if (message is null || !message.IsPublic)
                {
                    return;
                }

                var signature = message.AuthorUserId is { } author
                    ? await db.User.AsNoTracking().Where(u => u.Id == author).Select(u => u.Signature ?? u.DisplayName).FirstOrDefaultAsync(ct)
                    : null;
                await SendAsync(mail, [ticket.RequesterEmail], "Re: " + subject,
                    message.Body + (signature is null ? "" : HtmlSafe.FromText(signature)) +
                    $"<hr><p>Antworten Sie direkt auf der Seite Ihres Tickets:<br><a href=\"{Enc(link)}\">{Enc(link)}</a></p>", replyTo, ct);
                break;

            case MailJob.Moved:
                if (mail.NotifyAgents)
                {
                    await NotifyAgentsAsync(db, mail, ticket, $"Ticket {ticket.Number} ist jetzt bei euch: {ticket.Subject}", ct);
                }

                break;

            case MailJob.CustomerWrote:
                var to = ticket.AssigneeUserId is { } assignee
                    ? await db.User.AsNoTracking().Where(u => u.Id == assignee && u.IsActive).Select(u => u.Email).ToListAsync(ct)
                    : [];
                if (to.Count > 0)
                {
                    await SendAsync(mail, to, $"Antwort des Kunden zu {ticket.Number}", $"<p>{Enc(ticket.RequesterName)} hat auf das Ticket <strong>{Enc(ticket.Subject)}</strong> geantwortet.</p><p><a href=\"{Enc(Absolute($"/Tickets/Details?id={ticket.Id}"))}\">Ticket öffnen</a></p>", null, ct);
                }

                break;
        }
    }

    private async Task NotifyAgentsAsync(MtDbContext db, MailSettings mail, Ticket ticket, string subject, CancellationToken ct)
    {
        var to = new List<string>();
        if (ticket.TeamId is { } team)
        {
            to.AddRange(await db.TeamMember.AsNoTracking().Where(m => m.TeamId == team && m.User!.IsActive).Select(m => m.User!.Email).ToListAsync(ct));
        }
        else if (ticket.DepartmentId is { } department)
        {
            to.AddRange(await db.TeamMember.AsNoTracking().Where(m => m.Team!.DepartmentId == department && m.User!.IsActive).Select(m => m.User!.Email).ToListAsync(ct));
            if (ticket.Department?.Email is { Length: > 0 } departmentMail)
            {
                to.Add(departmentMail);
            }
        }

        to = [.. to.Distinct(StringComparer.OrdinalIgnoreCase)];
        if (to.Count == 0)
        {
            return;
        }

        await SendAsync(mail, to, subject,
            $"<p><strong>{Enc(ticket.Subject)}</strong></p><p>von {Enc(ticket.RequesterName)} {Enc(ticket.RequesterEmail)}</p>" +
            $"<p><a href=\"{Enc(Absolute($"/Tickets/Details?id={ticket.Id}"))}\">Ticket öffnen</a></p>", null, ct);
    }

    private async Task SendAsync(MailSettings mail, IReadOnlyList<string> to, string subject, string html, string? replyTo, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(mail.FromName, mail.FromAddress));
        foreach (var address in to)
        {
            message.To.Add(MailboxAddress.Parse(address));
        }

        if (!string.IsNullOrWhiteSpace(replyTo))
        {
            message.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        }

        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = html, TextBody = HtmlSafe.ToText(html) }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        var security = mail.Security switch
        {
            "ssl" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(mail.Host, mail.Port, security, ct);
        if (!string.IsNullOrEmpty(mail.UserName))
        {
            using var scope = scopes.CreateScope();
            var password = scope.ServiceProvider.GetRequiredService<SecretProtector>().Unprotect(mail.PasswordProtected);
            await client.AuthenticateAsync(mail.UserName, password, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    private string Absolute(string path) => PublicUrl.Base(config) + path;

    private static bool FormEngineEmail(string? address) => Core.Forms.FormEngine.IsEmail(address);

    private static string Enc(string? text) => WebUtility.HtmlEncode(text ?? "");
}
