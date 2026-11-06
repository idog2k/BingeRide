using System.Security.Cryptography;
using System.Text;
using BingeRide.Core.Settings;

namespace BingeRide.App.Services.Remote;

/// <summary>
/// QR pairing for the web remote. The QR carries a one-time code (128-bit, 5-minute life, single use);
/// redeeming it gives the phone its own random key (256-bit) kept in a cookie. Only a SHA-256 hash of each
/// key is stored, and every request is checked against those hashes in constant time.
/// Thread-safe: requests arrive on the web server's threads.
/// </summary>
public sealed class RemotePairing(AppSettings settings, Action saveSettings, TimeProvider? clock = null)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private string? _code;
    private DateTimeOffset _codeExpires;

    /// <summary>Raised (on a server thread) when a phone pairs or a paired phone is forgotten.</summary>
    public event EventHandler? PhonesChanged;

    public IReadOnlyList<PairedPhone> Phones
    {
        get
        {
            lock (_lock) return settings.PairedPhones.ToList();
        }
    }

    /// <summary>Starts (or restarts) a pairing window and returns the one-time code for the QR.</summary>
    public string NewCode()
    {
        lock (_lock)
        {
            _code = RandomToken(16);
            _codeExpires = _clock.GetUtcNow() + CodeLifetime;
            return _code;
        }
    }

    public DateTimeOffset? CodeExpires
    {
        get
        {
            lock (_lock) return _code is null ? null : _codeExpires;
        }
    }

    public void CancelCode()
    {
        lock (_lock) _code = null;
    }

    /// <summary>Redeems a code from a scanned QR: returns the phone's new key, or null if the code is wrong, used or expired.</summary>
    public string? Redeem(string? code, string userAgent)
    {
        PairedPhone phone;
        string key;
        lock (_lock)
        {
            if (_code is null || code is null || _clock.GetUtcNow() > _codeExpires || !FixedEquals(code, _code)) return null;
            _code = null; // single use
            key = RandomToken(32);
            phone = new PairedPhone { Name = DeviceName(userAgent), KeyHash = Hash(key), PairedAt = _clock.GetUtcNow(), LastSeen = _clock.GetUtcNow() };
            settings.PairedPhones.Add(phone);
        }
        saveSettings();
        PhonesChanged?.Invoke(this, EventArgs.Empty);
        return key;
    }

    /// <summary>The paired phone owning <paramref name="key"/>, or null.</summary>
    public PairedPhone? Authenticate(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var hash = Hash(key);
        lock (_lock)
        {
            PairedPhone? match = null;
            foreach (var phone in settings.PairedPhones)
            {
                if (FixedEquals(phone.KeyHash, hash)) match = phone; // no early exit: constant time
            }
            if (match is not null && (match.LastSeen is null || _clock.GetUtcNow() - match.LastSeen > TimeSpan.FromMinutes(1)))
                match.LastSeen = _clock.GetUtcNow(); // saved with the next settings write
            return match;
        }
    }

    public void Forget(string phoneId)
    {
        bool removed;
        lock (_lock) removed = settings.PairedPhones.RemoveAll(p => p.Id == phoneId) > 0;
        if (!removed) return;
        saveSettings();
        PhonesChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string RandomToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>"iPhone", "iPad", "Android phone"… from the browser's user agent, for the paired-phones list.</summary>
    internal static string DeviceName(string userAgent) => userAgent switch
    {
        _ when userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) => "iPhone",
        _ when userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) => "iPad",
        _ when userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase) => "Android phone",
        _ when userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) => "Mac",
        _ when userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase) => "Windows PC",
        _ => "Phone",
    };
}
