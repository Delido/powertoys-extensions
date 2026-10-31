using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Security.Credentials;

namespace PowerToysExtensions.Core;

/// <summary>
/// Keeps a secret (API token, client secret …) encrypted with a password of the user, optionally also
/// unlockable with Windows Hello. The secret is encrypted with a random data key; that key is wrapped
/// with the password (PBKDF2-SHA256, AES-256-GCM) and, if enabled, with a key derived from a Windows Hello
/// signature, so it cannot be opened without the password or Hello. Unlocked, the secret lives only in
/// the memory of this process and locks again after the auto-lock time without use.
/// </summary>
public sealed class Vault
{
    public const int MinPassword = 6;
    private const int Iterations = 600_000;
    private const int MaxFails = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromSeconds(30);

    private readonly string _file;
    private readonly string _helloName;
    private readonly Func<TimeSpan> _autoLock;
    private readonly Lock _gate = new();
    private string? _secret;
    private DateTime _lastUsed;
    private int _fails;
    private DateTime _lastFail;

    public Vault(string extension, Func<TimeSpan> autoLock)
    {
        _file = Path.Combine(Paths.For(extension), "vault.json");
        _helloName = $"Delido.PowerToysExtensions.{extension}";
        _autoLock = autoLock;
    }

    /// <summary>Raised when the vault locks (manually or by auto-lock), e.g. to delete cached content.</summary>
    public event Action? Locked;

    public bool IsSetUp => File.Exists(_file);

    public bool HelloEnabled => Read()?.Hello is not null;

    public bool IsUnlocked
    {
        get
        {
            lock (_gate)
            {
                if (_secret is not null && DateTime.UtcNow - _lastUsed > _autoLock())
                {
                    LockNow();
                }

                return _secret is not null;
            }
        }
    }

    /// <summary>Seconds until another password attempt is allowed.</summary>
    public int LockoutRemaining => _fails < MaxFails ? 0 : Math.Max(0, (int)Math.Ceiling((_lastFail + Lockout - DateTime.UtcNow).TotalSeconds));

    /// <summary>The secret while unlocked; every use extends the session.</summary>
    public string Secret
    {
        get
        {
            lock (_gate)
            {
                if (!IsUnlocked)
                {
                    throw new VaultLockedException();
                }

                _lastUsed = DateTime.UtcNow;
                return _secret!;
            }
        }
    }

    public void Create(string secret, string password)
    {
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var data = new VaultData
        {
            Salt = Convert.ToBase64String(salt),
            Data = Seal(dataKey, Encoding.UTF8.GetBytes(secret)),
            PasswordKey = Seal(PasswordKey(password, salt), dataKey),
        };
        Write(data);
        Open(secret);
    }

    public bool Unlock(string password)
    {
        if (LockoutRemaining > 0 || Read() is not { } data)
        {
            return false;
        }

        try
        {
            var dataKey = Open(PasswordKey(password, Convert.FromBase64String(data.Salt)), data.PasswordKey);
            Open(Encoding.UTF8.GetString(Open(dataKey, data.Data)));
            _fails = 0;
            return true;
        }
        catch (CryptographicException)
        {
            // wrong password (GCM authentication failed)
            _fails++;
            _lastFail = DateTime.UtcNow;
            return false;
        }
    }

    public bool ChangePassword(string oldPassword, string newPassword)
    {
        if (Read() is not { } data)
        {
            return false;
        }

        try
        {
            var dataKey = Open(PasswordKey(oldPassword, Convert.FromBase64String(data.Salt)), data.PasswordKey);
            var salt = RandomNumberGenerator.GetBytes(16);
            data.Salt = Convert.ToBase64String(salt);
            data.PasswordKey = Seal(PasswordKey(newPassword, salt), dataKey);
            Write(data);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Stores a new secret (e.g. a renewed token); the password and Windows Hello stay as they are.</summary>
    public bool ReplaceSecret(string password, string secret)
    {
        if (Read() is not { } data)
        {
            return false;
        }

        try
        {
            var dataKey = Open(PasswordKey(password, Convert.FromBase64String(data.Salt)), data.PasswordKey);
            data.Data = Seal(dataKey, Encoding.UTF8.GetBytes(secret));
            Write(data);
            Open(secret);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Lets Windows Hello unlock the vault too (needs the password once, Hello confirms).</summary>
    public async Task EnableHello(string password)
    {
        var data = Read() ?? throw new InvalidOperationException("Not set up yet");
        byte[] dataKey;
        try
        {
            dataKey = Open(PasswordKey(password, Convert.FromBase64String(data.Salt)), data.PasswordKey);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Wrong password");
        }

        if (!await KeyCredentialManager.IsSupportedAsync())
        {
            throw new InvalidOperationException("Windows Hello is not set up on this PC");
        }

        var created = await KeyCredentialManager.RequestCreateAsync(_helloName, KeyCredentialCreationOption.ReplaceExisting);
        if (created.Status != KeyCredentialStatus.Success)
        {
            throw new InvalidOperationException($"Windows Hello: {created.Status}");
        }

        var challenge = RandomNumberGenerator.GetBytes(32);
        data.Hello = new HelloData { Challenge = Convert.ToBase64String(challenge), Key = Seal(await HelloKey(created.Credential, challenge), dataKey) };
        Write(data);
    }

    public async Task<bool> UnlockWithHello()
    {
        if (Read() is not { Hello: { } hello } data)
        {
            return false;
        }

        var opened = await KeyCredentialManager.OpenAsync(_helloName);
        if (opened.Status != KeyCredentialStatus.Success)
        {
            throw new InvalidOperationException($"Windows Hello: {opened.Status}");
        }

        try
        {
            var dataKey = Open(await HelloKey(opened.Credential, Convert.FromBase64String(hello.Challenge)), hello.Key);
            Open(Encoding.UTF8.GetString(Open(dataKey, data.Data)));
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void DisableHello()
    {
        if (Read() is { } data)
        {
            data.Hello = null;
            Write(data);
        }

        _ = KeyCredentialManager.DeleteAsync(_helloName).AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public void LockNow()
    {
        lock (_gate)
        {
            if (_secret is null)
            {
                return;
            }

            _secret = null;
        }

        Locked?.Invoke();
    }

    /// <summary>Forgets secret, password and Hello key.</summary>
    public void Reset()
    {
        LockNow();
        if (HelloEnabled)
        {
            DisableHello();
        }

        File.Delete(_file);
        _fails = 0;
    }

    private static byte[] PasswordKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password.Normalize(NormalizationForm.FormC), salt, Iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Windows Hello RSA signatures (PKCS#1 v1.5) are deterministic, so they can serve as key material.</summary>
    private static async Task<byte[]> HelloKey(KeyCredential credential, byte[] challenge)
    {
        var signed = await credential.RequestSignAsync(challenge.AsBuffer());
        if (signed.Status != KeyCredentialStatus.Success)
        {
            throw new InvalidOperationException($"Windows Hello: {signed.Status}");
        }

        return SHA256.HashData(signed.Result.ToArray());
    }

    private static Sealed Seal(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return new Sealed { Iv = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag), Data = Convert.ToBase64String(cipher) };
    }

    private static byte[] Open(byte[] key, Sealed sealedData)
    {
        var cipher = Convert.FromBase64String(sealedData.Data);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(Convert.FromBase64String(sealedData.Iv), cipher, Convert.FromBase64String(sealedData.Tag), plain);
        return plain;
    }

    private void Open(string secret)
    {
        lock (_gate)
        {
            _secret = secret;
            _lastUsed = DateTime.UtcNow;
        }
    }

    private VaultData? Read()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<VaultData>(File.ReadAllText(_file)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private void Write(VaultData data) => File.WriteAllText(_file, JsonSerializer.Serialize(data));

    private sealed class VaultData
    {
        public int V { get; set; } = 1;

        public string Salt { get; set; } = string.Empty;

        public Sealed Data { get; set; } = new();

        public Sealed PasswordKey { get; set; } = new();

        public HelloData? Hello { get; set; }
    }

    private sealed class HelloData
    {
        public string Challenge { get; set; } = string.Empty;

        public Sealed Key { get; set; } = new();
    }

    private sealed class Sealed
    {
        public string Iv { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;

        public string Data { get; set; } = string.Empty;
    }
}

public sealed class VaultLockedException : InvalidOperationException
{
    public VaultLockedException()
        : base("Locked – unlock first")
    {
    }

    public VaultLockedException(string message)
        : base(message)
    {
    }

    public VaultLockedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
