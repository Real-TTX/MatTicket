using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MatTicket.Configuration;
using MatTicket.Core.Ai;
using Microsoft.AspNetCore.DataProtection;

namespace MatTicket.Services.Ai;

/// <summary>
/// The keys and sign-ins of the AI profiles: one encrypted file each in <c>{data}/secrets/ai</c>, protected with the key ring of the app.
/// Not in the database on purpose: a backup or a restore of the database never carries them (a ChatGPT refresh token is renewed at every use
/// and valid once; an old copy coming back would kill the sign-in here), and a new container (rollout) keeps them because the volume stays.
/// Nothing in here is ever sent to a browser.
/// </summary>
public sealed class AiSecretStore
{
    public const string ApiKey = "key";
    public const string ChatGpt = "chatgpt";

    private readonly string _dir;
    private readonly IDataProtector _protector;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public AiSecretStore(ConfigStore config, IDataProtectionProvider provider)
    {
        _dir = Path.Combine(config.DataDir, "secrets", "ai");
        Directory.CreateDirectory(_dir);
        _protector = provider.CreateProtector("MatTicket.AiSecrets.v1");
    }

    private string PathOf(Guid token, string kind) => Path.Combine(_dir, $"{token:N}.{kind}");

    public bool Has(Guid token, string kind) => File.Exists(PathOf(token, kind));

    public async Task<string?> LoadAsync(Guid token, string kind, CancellationToken ct = default)
    {
        var path = PathOf(token, kind);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(await File.ReadAllTextAsync(path, ct));
        }
        catch (CryptographicException)
        {
            // The key ring was lost or replaced: the secret is unreadable and has to be entered again.
            return null;
        }
    }

    public async Task SaveAsync(Guid token, string kind, string? value, CancellationToken ct = default)
    {
        var path = PathOf(token, kind);
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrEmpty(value))
            {
                File.Delete(path);
                return;
            }

            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, _protector.Protect(value), new UTF8Encoding(false), ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Moves a secret to another profile: the old place is empty afterwards. A sign-in is moved, never copied.</summary>
    public void Move(Guid from, Guid to, string kind)
    {
        var source = PathOf(from, kind);
        if (File.Exists(source))
        {
            File.Move(source, PathOf(to, kind), overwrite: true);
        }
    }

    public void DeleteAll(Guid token)
    {
        File.Delete(PathOf(token, ApiKey));
        File.Delete(PathOf(token, ChatGpt));
    }

    /// <summary>The token store of the ChatGPT sign-in of one profile.</summary>
    public IChatGptTokenStore ChatGptStore(Guid token) => new TokenStore(this, token);

    private sealed class TokenStore(AiSecretStore store, Guid token) : IChatGptTokenStore
    {
        public Task<string?> LoadAsync(CancellationToken ct) => store.LoadAsync(token, ChatGpt, ct);

        public Task SaveAsync(string? json, CancellationToken ct) => store.SaveAsync(token, ChatGpt, json, ct);
    }
}

/// <summary>
/// The brake per address: at most N calls of the AI per minute from one address (agents and visitors alike), and the daily hash that stands
/// for a visitor in the runs instead of their address.
/// </summary>
public sealed class AiRateLimiter(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _calls = new();
    private byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private DateOnly _saltDay;
    private readonly Lock _gate = new();

    /// <summary>Takes one call for the address; false when the address has used up its minute.</summary>
    public bool TryTake(string? address, int perMinute)
    {
        if (perMinute <= 0)
        {
            return true;
        }

        var key = Hash(address ?? "unknown");
        var now = clock.GetUtcNow().UtcDateTime;
        var queue = _calls.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() < now.AddMinutes(-1))
            {
                queue.Dequeue();
            }

            if (queue.Count >= perMinute)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    /// <summary>A hash of the address with a secret that changes every day: the same visitor is the same hash for one day, and the address cannot be recovered.</summary>
    public string Hash(string address)
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            if (today != _saltDay)
            {
                _salt = RandomNumberGenerator.GetBytes(32);
                _saltDay = today;
                _calls.Clear();
            }

            return Convert.ToHexString(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(address)))[..24].ToLowerInvariant();
        }
    }
}
