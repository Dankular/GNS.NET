namespace GnsNet;

using System.Security.Cryptography;
using System.Text.Json;

/// <summary>A password credential submitted to <see cref="FlatFileIdentityVerifier"/>.</summary>
public readonly record struct FlatFileCredential(string Username, string Password)
{
    public string Encode() => JsonSerializer.Serialize(this);
    public static bool TryDecode(string value, out FlatFileCredential credential)
    {
        try { credential = JsonSerializer.Deserialize<FlatFileCredential>(value); return !string.IsNullOrWhiteSpace(credential.Username) && credential.Password is not null; }
        catch (JsonException) { credential = default; return false; }
    }
}

/// <summary>One flat-file account record. Store only a PBKDF2 hash and salt, never a password.</summary>
public sealed class FlatFileUser
{
    public string Username { get; init; } = "";
    public string Subject { get; init; } = "";
    public string PasswordHash { get; init; } = "";
    public string Salt { get; init; } = "";
    public int Iterations { get; init; } = FlatFilePasswordHasher.DefaultIterations;
    public bool Disabled { get; init; }
    public Dictionary<string, string> Claims { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Creates and verifies PBKDF2 password records for local flat-file accounts.</summary>
public static class FlatFilePasswordHasher
{
    public const int DefaultIterations = 120_000;
    public static FlatFileUser CreateUser(string username, string password, string? subject = null, IReadOnlyDictionary<string, string>? claims = null, int iterations = DefaultIterations)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 128) throw new ArgumentException("Username is required and must be at most 128 characters.", nameof(username));
        if (password is null || password.Length < 8) throw new ArgumentException("Passwords must be at least 8 characters.", nameof(password));
        if (iterations < 100_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        byte[] salt = RandomNumberGenerator.GetBytes(16); byte[] hash = Derive(password, salt, iterations);
        return new FlatFileUser { Username = username, Subject = string.IsNullOrWhiteSpace(subject) ? username : subject, PasswordHash = Convert.ToBase64String(hash), Salt = Convert.ToBase64String(salt), Iterations = iterations, Claims = claims is null ? new(StringComparer.Ordinal) : new Dictionary<string, string>(claims, StringComparer.Ordinal) };
    }
    internal static bool Verify(string password, FlatFileUser user)
    {
        try
        {
            if (user.Iterations < 100_000 || user.Iterations > 10_000_000) return false;
            byte[] salt = Convert.FromBase64String(user.Salt); byte[] expected = Convert.FromBase64String(user.PasswordHash); byte[] actual = Derive(password, salt, user.Iterations);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
    private static byte[] Derive(string password, byte[] salt, int iterations) => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
}

/// <summary>Authenticates JSON flat-file users and returns provider-neutral identities.</summary>
public sealed class FlatFileIdentityVerifier : IExternalIdentityVerifier
{
    private readonly string path;
    public FlatFileIdentityVerifier(string path) => this.path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("A user file path is required.", nameof(path)) : path;
    public async ValueTask<ExternalIdentity?> VerifyAsync(string credential, CancellationToken cancellationToken = default)
    {
        if (!FlatFileCredential.TryDecode(credential, out FlatFileCredential supplied)) return null;
        await using FileStream stream = File.OpenRead(this.path);
        FlatFileUser[] users = await JsonSerializer.DeserializeAsync<FlatFileUser[]>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        FlatFileUser? user = users.FirstOrDefault(candidate => string.Equals(candidate.Username, supplied.Username, StringComparison.Ordinal));
        if (user is null || user.Disabled || !FlatFilePasswordHasher.Verify(supplied.Password, user)) return null;
        var claims = new Dictionary<string, string>(user.Claims, StringComparer.Ordinal) { ["username"] = user.Username };
        return new ExternalIdentity("flat-file", string.IsNullOrWhiteSpace(user.Subject) ? user.Username : user.Subject, claims);
    }
}
