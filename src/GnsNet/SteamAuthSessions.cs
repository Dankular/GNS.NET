namespace GnsNet;

using GnsSharp;

/// <summary>Result delivered by Steam after an auth ticket has been validated.</summary>
public readonly record struct SteamAuthSessionValidation(
    CSteamID SteamId,
    EAuthSessionResponse Response,
    CSteamID OwnerSteamId,
    bool Accepted)
{
    public bool IsOwnerDifferent => this.OwnerSteamId != this.SteamId;
}

/// <summary>Thread-safe state for Steam auth-session validation callbacks.</summary>
public sealed class SteamAuthSessionState
{
    private readonly object sync = new();
    private readonly Dictionary<CSteamID, SteamAuthSessionValidation> validations = new();

    public event Action<SteamAuthSessionValidation>? ValidationReceived;

    public IReadOnlyCollection<SteamAuthSessionValidation> Validations
    {
        get { lock (this.sync) return this.validations.Values.ToArray(); }
    }

    public bool TryGet(CSteamID steamId, out SteamAuthSessionValidation validation)
    {
        lock (this.sync) return this.validations.TryGetValue(steamId, out validation);
    }

    internal SteamAuthSessionValidation Apply(in ValidateAuthTicketResponse_t response)
    {
        var validation = new SteamAuthSessionValidation(response.SteamID, response.AuthSessionResponse, response.OwnerSteamID,
            response.AuthSessionResponse == EAuthSessionResponse.OK);
        lock (this.sync) this.validations[validation.SteamId] = validation;
        this.ValidationReceived?.Invoke(validation);
        return validation;
    }

    internal bool Remove(CSteamID steamId)
    {
        lock (this.sync) return this.validations.Remove(steamId);
    }
}

/// <summary>
/// Owns the Steam <c>BeginAuthSession</c> lifecycle and retains the native validation callback.
/// </summary>
/// <remarks>
/// The callback is asynchronous: a successful <see cref="BeginAuthSession"/> return value is not
/// identity proof until <see cref="ValidationReceived"/> reports an accepted response. The caller
/// must call <see cref="EndAuthSession"/> when the session ends. A Steam client/game-server runtime
/// and platform credentials are still required for an end-to-end validation.
/// </remarks>
public sealed class SteamAuthSessionManager : IDisposable
{
    private readonly ISteamUser user;
    private readonly Callback<ValidateAuthTicketResponse_t> validationCallback;
    private bool disposed;

    public SteamAuthSessionManager(ISteamUser? user = null)
    {
        this.user = user ?? ISteamUser.User
            ?? throw new InvalidOperationException("Steam user authentication is not available in the selected runtime.");
        this.State = new SteamAuthSessionState();
        this.validationCallback = (ref ValidateAuthTicketResponse_t response) => this.State.Apply(in response);
        this.user.ValidateAuthTicketResponse += this.validationCallback;
        this.State.ValidationReceived += validation => this.ValidationReceived?.Invoke(validation);
    }

    public SteamAuthSessionState State { get; }
    public event Action<SteamAuthSessionValidation>? ValidationReceived;

    /// <summary>Starts asynchronous validation of a ticket received from <paramref name="steamId"/>.</summary>
    public EBeginAuthSessionResult BeginAuthSession(ReadOnlySpan<byte> ticket, CSteamID steamId)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (ticket.IsEmpty) throw new ArgumentException("Steam auth ticket cannot be empty.", nameof(ticket));
        return this.user.BeginAuthSession(ticket, steamId);
    }

    /// <summary>Ends the native auth session and removes its last callback result.</summary>
    public void EndAuthSession(CSteamID steamId)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        this.user.EndAuthSession(steamId);
        this.State.Remove(steamId);
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        this.user.ValidateAuthTicketResponse -= this.validationCallback;
        foreach (SteamAuthSessionValidation validation in this.State.Validations) this.user.EndAuthSession(validation.SteamId);
    }
}
