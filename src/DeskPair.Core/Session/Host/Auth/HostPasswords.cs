using System.Security.Cryptography;
using System.Buffers.Binary;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Session.Host.Auth;

/// <summary>
/// Holds the host's password verifiers: a per-install salt, the optional permanent password hash, the
/// current temporary password and the link password that goes in the QR code. Only hashes are stored; the
/// two generated passwords' plaintext lives in memory so the UI can show them.
/// </summary>
public sealed class HostPasswords
{
    private const string SaltKey = "password-salt";
    private const string PermanentKey = "password-permanent-h1";
    private const string PinnedTemporaryKey = "password-temporary-pinned";

    /// <summary>How the stored permanent h1 was derived: kdf (4 bytes LE) then iterations (4 bytes LE). Absent means the protocol-1 hash.</summary>
    private const string PermanentKdfKey = "password-permanent-kdf";

    /// <summary>A user-chosen temporary password is refused below this length.</summary>
    public const int MinTemporaryLength = 6;

    public const int MaxTemporaryLength = 32;
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789"; // no ambiguous glyphs

    /// <summary>
    /// Nobody reads the link password off a screen, so it is not held to a length a person could retype.
    /// Twelve characters of this alphabet is sixty bits, and that — together with being spent on first use —
    /// is what makes it safe to put a secret in a picture at all.
    /// </summary>
    private const int LinkLength = 12;
    private readonly ISecretStore _store;
    private readonly object _lock = new();
    private byte[]? _permanentH1;
    private byte[] _temporaryH1 = [];
    private byte[] _linkH1 = [];
    private readonly HashSet<string> _guessingSources = new(StringComparer.Ordinal);
    private int _temporaryLength = 6;
    private PasswordKdf _kdf;
    private int _iterations;

    private HostPasswords(ISecretStore store, byte[] salt, byte[]? permanentH1, PasswordKdf kdf, int iterations)
    {
        _store = store;
        Salt = salt;
        _permanentH1 = permanentH1;
        _kdf = kdf;
        _iterations = iterations;
        RotateTemporary();
        RotateLink();
    }

    public byte[] Salt { get; }

    /// <summary>
    /// How every password this host holds is derived, announced in the challenge. One derivation for all
    /// three, because a controller computes one proof: the permanent password's, since that is the one
    /// stored and cannot be re-derived, and PBKDF2 once there is none or it has been set under protocol 2.
    /// </summary>
    public PasswordKdf Kdf => _kdf;

    public int Iterations => _iterations;

    public string TemporaryPassword { get; private set; } = string.Empty;

    /// <summary>
    /// The secret the QR code carries, which is deliberately not the temporary password.
    ///
    /// A code on a screen gets photographed, screen-shared and left up, so what it carries has to be worth
    /// nothing shortly afterwards: this one is spent by the first connection that uses it (<see cref="SpendLink"/>)
    /// and immediately replaced, which redraws the code. The temporary password is left where it is, so
    /// somebody who read it off the screen and typed it can still open a second session — a file transfer
    /// beside their remote control — without hunting for a password that moved under them.
    ///
    /// Never written to the secret store: a password that survives a restart is not a one-time password.
    /// </summary>
    public string LinkPassword { get; private set; } = string.Empty;

    /// <summary>Length of generated temporary passwords; changing it regenerates unless the user pinned one.</summary>
    public int TemporaryPasswordLength
    {
        get => _temporaryLength;
        set => _temporaryLength = Math.Clamp(value, MinTemporaryLength, MaxTemporaryLength);
    }

    /// <summary>False disables temporary-password logins entirely (the permanent password still works).</summary>
    public bool TemporaryEnabled { get; private set; } = true;

    /// <summary>True when the user chose the temporary password, so wrong attempts must not replace it.</summary>
    public bool TemporaryPinned { get; private set; }

    /// <summary>
    /// How many distinct sources have to guess wrong before the generated passwords rotate.
    ///
    /// Sources, not attempts. One address guessing is held off by the login backoff and cannot get
    /// anywhere; letting it move the password every ten tries handed anyone on the internet a way to
    /// change the code under the person reading it out. Ten different addresses guessing at once is a
    /// different situation, and that is what still moves it.
    /// </summary>
    public int TemporaryRotationThreshold { get; set; } = 10;

    public bool HasPermanentPassword => _permanentH1 is not null;

    public event Action<string>? TemporaryPasswordChanged;

    /// <summary>Any change the UI shows: permanent set/cleared, temporary rotated, pinned, enabled or disabled.</summary>
    public event Action? StateChanged;

    public static async Task<HostPasswords> LoadAsync(ISecretStore store, CancellationToken ct = default)
    {
        byte[]? salt = await store.GetAsync(SaltKey, ct).ConfigureAwait(false);
        if (salt is null || salt.Length != 16)
        {
            salt = PasswordProof.NewSalt();
            await store.SetAsync(SaltKey, salt, ct).ConfigureAwait(false);
        }

        byte[]? permanent = await store.GetAsync(PermanentKey, ct).ConfigureAwait(false);
        byte[]? permanentH1 = permanent is { Length: 32 } ? permanent : null;

        // A permanent password stored before protocol 2 has no kdf record and was hashed once; it keeps
        // working that way until it is set again, since there is no plaintext to re-derive it from.
        (PasswordKdf kdf, int iterations) = (PasswordProof.DefaultKdf, PasswordProof.DefaultIterations);
        if (permanentH1 is not null)
        {
            byte[]? record = await store.GetAsync(PermanentKdfKey, ct).ConfigureAwait(false);
            (kdf, iterations) = record is { Length: 8 }
                ? ((PasswordKdf)BinaryPrimitives.ReadInt32LittleEndian(record), BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(4)))
                : (PasswordKdf.KdfSha256, 0);
        }

        var passwords = new HostPasswords(store, salt, permanentH1, kdf, iterations);
        byte[]? pinned = await store.GetAsync(PinnedTemporaryKey, ct).ConfigureAwait(false);
        if (pinned is { Length: > 0 })
        {
            // A password the user picked outlives a restart; that is the point of choosing one.
            passwords.ApplyTemporary(System.Text.Encoding.UTF8.GetString(pinned), pinned: true);
        }

        return passwords;
    }

    public async Task SetPermanentAsync(string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            lock (_lock)
            {
                _permanentH1 = null;
            }

            await _store.RemoveAsync(PermanentKey, ct).ConfigureAwait(false);
            await _store.RemoveAsync(PermanentKdfKey, ct).ConfigureAwait(false);
            // Nothing stored the old way any more, so the generated passwords move to the current derivation.
            Rederive(PasswordProof.DefaultKdf, PasswordProof.DefaultIterations);
            StateChanged?.Invoke();
            return;
        }

        // A password set today is derived today's way -- and the temporary and link passwords follow,
        // because the challenge names one derivation for all three.
        byte[] h1 = PasswordProof.ComputeH1(password, Salt, PasswordProof.DefaultKdf, PasswordProof.DefaultIterations);
        lock (_lock)
        {
            _permanentH1 = h1;
        }

        byte[] record = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(record, (int)PasswordProof.DefaultKdf);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), PasswordProof.DefaultIterations);
        await _store.SetAsync(PermanentKey, h1, ct).ConfigureAwait(false);
        await _store.SetAsync(PermanentKdfKey, record, ct).ConfigureAwait(false);
        Rederive(PasswordProof.DefaultKdf, PasswordProof.DefaultIterations);
        StateChanged?.Invoke();
    }

    /// <summary>Switches the derivation and recomputes the two generated passwords' h1 from their plaintext.</summary>
    private void Rederive(PasswordKdf kdf, int iterations)
    {
        lock (_lock)
        {
            if (_kdf == kdf && _iterations == iterations)
            {
                return;
            }

            _kdf = kdf;
            _iterations = iterations;
            _temporaryH1 = PasswordProof.ComputeH1(TemporaryPassword, Salt, kdf, iterations);
            _linkH1 = PasswordProof.ComputeH1(LinkPassword, Salt, kdf, iterations);
        }
    }

    /// <summary>Pins a temporary password the user chose; an empty value restores a random rotating one.</summary>
    public async Task SetTemporaryAsync(string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password))
        {
            await _store.RemoveAsync(PinnedTemporaryKey, ct).ConfigureAwait(false);
            RotateTemporary();
            return;
        }

        if (password.Length < MinTemporaryLength || password.Length > MaxTemporaryLength)
        {
            throw new ArgumentException($"A temporary password is {MinTemporaryLength} to {MaxTemporaryLength} characters.", nameof(password));
        }

        ApplyTemporary(password, pinned: true);
        await _store.SetAsync(PinnedTemporaryKey, System.Text.Encoding.UTF8.GetBytes(password), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the settings the host config owns. A length change regenerates the password, but only when the
    /// user has not pinned one; the constructor generates before the config is known, hence this second pass.
    /// </summary>
    public void Configure(int length, int rotationThreshold, bool enabled)
    {
        int clamped = Math.Clamp(length, MinTemporaryLength, MaxTemporaryLength);
        bool regenerate;
        lock (_lock)
        {
            regenerate = clamped != _temporaryLength && !TemporaryPinned;
            _temporaryLength = clamped;
            TemporaryRotationThreshold = rotationThreshold;
        }

        bool enabledChanged = TemporaryEnabled != enabled;
        TemporaryEnabled = enabled;
        if (regenerate)
        {
            RotateTemporary();
        }
        else if (enabledChanged)
        {
            StateChanged?.Invoke();
        }
    }

    public void RotateTemporary() => ApplyTemporary(Generate(TemporaryPasswordLength), pinned: false);

    /// <summary>Throws the link password away and issues another, which is what makes the QR code change.</summary>
    public void RotateLink()
    {
        string password = Generate(LinkLength);
        lock (_lock)
        {
            LinkPassword = password;
            _linkH1 = PasswordProof.ComputeH1(password, Salt, _kdf, _iterations);
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Spends the link password, once a connection that used it has actually been let in.
    ///
    /// Called from the session rather than from <see cref="Verify"/>, because a login that is checked and
    /// then refused at the "allow this?" prompt has not connected. Burning the code there would let anyone
    /// who could photograph it invalidate it for the person it was held up for.
    /// </summary>
    public void SpendLink() => RotateLink();

    private static string Generate(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    private void ApplyTemporary(string password, bool pinned)
    {
        lock (_lock)
        {
            TemporaryPassword = password;
            _temporaryH1 = PasswordProof.ComputeH1(password, Salt, _kdf, _iterations);
            _guessingSources.Clear();
            TemporaryPinned = pinned;
        }

        TemporaryPasswordChanged?.Invoke(password);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Checks a proof against every verifier in constant time. A failure counts the <paramref name="source"/>
    /// (the address bucket <c>LoginFailureTracker.BucketOf</c> gives) toward the rotation threshold;
    /// rotation happens here so it cannot be bypassed.
    /// </summary>
    public PasswordMatch Verify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof, string source)
    {
        byte[]? permanent;
        byte[] temporary;
        byte[] link;
        lock (_lock)
        {
            permanent = _permanentH1;
            temporary = _temporaryH1;
            link = _linkH1;
        }

        // Switching temporary passwords off means "the permanent password and nothing else". The link
        // password is generated the same way and displayed just as openly, so it goes off with it.
        bool temporaryOk = TemporaryEnabled && PasswordProof.Verify(temporary, challenge, proof);
        bool linkOk = TemporaryEnabled && PasswordProof.Verify(link, challenge, proof);
        bool permanentOk = permanent is not null && PasswordProof.Verify(permanent, challenge, proof);
        if (temporaryOk || linkOk)
        {
            lock (_lock)
            {
                _guessingSources.Clear();
            }

            return temporaryOk ? PasswordMatch.Temporary : PasswordMatch.Link;
        }

        if (permanentOk)
        {
            return PasswordMatch.Permanent;
        }

        bool rotate;
        lock (_lock)
        {
            rotate = TemporaryEnabled && _guessingSources.Add(source) && _guessingSources.Count >= TemporaryRotationThreshold;
        }

        if (rotate)
        {
            // Enough different places are guessing. The link password moves whether or not the temporary
            // one is pinned: pinning is a decision about the password the user reads aloud, and nobody
            // reads this one. A pinned temporary password is still never replaced by failed attempts.
            RotateLink();
            if (TemporaryPinned)
            {
                lock (_lock)
                {
                    _guessingSources.Clear();
                }
            }
            else
            {
                RotateTemporary();
            }
        }

        return PasswordMatch.None;
    }
}

public enum PasswordMatch
{
    None,
    Temporary,
    Permanent,

    /// <summary>The one-time password out of the QR code, which the connection that used it spends.</summary>
    Link,
}
