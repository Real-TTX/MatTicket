using System.Threading.Channels;
using MatTicket.Data;
using MatTicket.Logic;

namespace MatTicket.Services.Ai;

/// <summary>What the AI does with a new ticket of a form, after the visitor already has their answer: sort it, maybe answer first.</summary>
public sealed record AiTicketJob(long TicketId, bool Triage, bool AutoReply);

/// <summary>The queue of <see cref="AiTicketJob"/>: a visitor never waits for the AI to sort their ticket.</summary>
public sealed class AiQueue
{
    private readonly Channel<AiTicketJob> _channel = Channel.CreateBounded<AiTicketJob>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<AiTicketJob> Reader => _channel.Reader;

    public void Enqueue(AiTicketJob job)
    {
        if (job.Triage || job.AutoReply)
        {
            _channel.Writer.TryWrite(job);
        }
    }
}

public sealed class AiWorker(AiQueue queue, IServiceScopeFactory scopes, ILogger<AiWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MtDbContext>();
                if (job.Triage)
                {
                    await AiLogic.TriageInternalAsync(db, scope.ServiceProvider, job.TicketId, null, "system");
                }

                if (job.AutoReply)
                {
                    await AiLogic.AutoReplyInternalAsync(db, scope.ServiceProvider, job.TicketId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The AI could not handle ticket {Ticket}.", job.TicketId);
            }
        }
    }
}
