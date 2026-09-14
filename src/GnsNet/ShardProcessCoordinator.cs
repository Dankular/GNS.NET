namespace GnsNet;

using System.Diagnostics;
using System.Text.Json;

public interface IShardProcessController<TShardId>
{
    Task StartAsync(TShardId shard, CancellationToken cancellationToken = default);
    Task StopAsync(TShardId shard, CancellationToken cancellationToken = default);
    Task MigrateAsync<TPlayerId>(TPlayerId player, TShardId source, TShardId target, CancellationToken cancellationToken = default);
}

public interface IShardHealthController<TShardId> where TShardId : notnull
{
    bool IsRunning(TShardId shard);
}

/// <summary>Monitors shard processes and asks the coordinator to restart missing shards.</summary>
public sealed class ShardSupervisor<TShardId, TPlayerId> where TShardId : notnull where TPlayerId : notnull
{
    private readonly ShardProcessCoordinator<TShardId, TPlayerId> coordinator;
    private readonly ShardLifecycle<TShardId, TPlayerId> lifecycle;
    private readonly IShardHealthController<TShardId> health;
    public ShardSupervisor(ShardProcessCoordinator<TShardId, TPlayerId> coordinator, ShardLifecycle<TShardId, TPlayerId> lifecycle, IShardHealthController<TShardId> health) { this.coordinator = coordinator; this.lifecycle = lifecycle; this.health = health; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public async Task<int> RecoverDeadShardsAsync(CancellationToken cancellationToken = default)
    {
        int recovered = 0;
        foreach (TShardId shard in this.lifecycle.Shards)
            if (!this.health.IsRunning(shard) && await this.coordinator.RecoverAsync(shard, cancellationToken).ConfigureAwait(false)) recovered++;
        return recovered;
    }
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await this.RecoverDeadShardsAsync(cancellationToken).ConfigureAwait(false);
            try { await Task.Delay(this.PollInterval, cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
}

public interface IShardStateTransfer<TShardId, TPlayerId>
{
    Task<byte[]> ExportAsync(TPlayerId player, TShardId source, CancellationToken cancellationToken = default);
    Task ImportAsync(TPlayerId player, TShardId target, ReadOnlyMemory<byte> state, CancellationToken cancellationToken = default);
}

/// <summary>Coordinates shard process startup, draining, migration, and shutdown.</summary>
public sealed class ShardProcessCoordinator<TShardId, TPlayerId> where TShardId : notnull where TPlayerId : notnull
{
    private readonly IShardProcessController<TShardId> controller;
    private readonly ShardLifecycle<TShardId, TPlayerId> lifecycle;
    private readonly IShardStateTransfer<TShardId, TPlayerId>? transfer;
    public ShardProcessCoordinator(IShardProcessController<TShardId> controller, ShardLifecycle<TShardId, TPlayerId> lifecycle, IShardStateTransfer<TShardId, TPlayerId>? transfer = null) { this.controller = controller; this.lifecycle = lifecycle; this.transfer = transfer; }
    public async Task<bool> StartAsync(TShardId shard, CancellationToken cancellationToken = default) { if (!this.lifecycle.Register(shard)) return false; await this.controller.StartAsync(shard, cancellationToken); return true; }
    public async Task<bool> MigrateAsync(TPlayerId player, TShardId target, CancellationToken cancellationToken = default)
    { if (!this.lifecycle.TryGetPlayerShard(player, out TShardId source) || !this.lifecycle.IsAvailable(target)) return false; byte[] state = this.transfer is null ? [] : await this.transfer.ExportAsync(player, source, cancellationToken); await this.controller.MigrateAsync(player, source, target, cancellationToken); if (!this.lifecycle.Migrate(player, target)) return false; if (this.transfer is not null) await this.transfer.ImportAsync(player, target, state, cancellationToken); return true; }
    public async Task StopAsync(TShardId shard, CancellationToken cancellationToken = default) { this.lifecycle.SetDraining(shard, true); await this.controller.StopAsync(shard, cancellationToken); }
    public async Task<bool> RecoverAsync(TShardId shard, CancellationToken cancellationToken = default)
    {
        if (this.controller is IShardHealthController<TShardId> health && health.IsRunning(shard)) return false;
        if (!this.lifecycle.Register(shard)) return false;
        await this.controller.StartAsync(shard, cancellationToken).ConfigureAwait(false); return true;
    }
}

/// <summary>Default controller for one local shard process per match/room/zone.</summary>
public sealed class LocalShardProcessController<TShardId> : IShardProcessController<TShardId>, IShardHealthController<TShardId> where TShardId : notnull
{
    private readonly string executable;
    private readonly Func<TShardId, string> arguments;
    private readonly Dictionary<TShardId, Process> processes = new();
    public LocalShardProcessController(string executable, Func<TShardId, string>? arguments = null) { this.executable = executable; this.arguments = arguments ?? (_ => string.Empty); }
    public Task StartAsync(TShardId shard, CancellationToken cancellationToken = default) { if (this.processes.TryGetValue(shard, out Process? existing)) { if (!existing.HasExited) return Task.CompletedTask; this.processes.Remove(shard); existing.Dispose(); } Process process = Process.Start(new ProcessStartInfo(this.executable, this.arguments(shard)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true }) ?? throw new InvalidOperationException("Unable to start shard process."); this.processes[shard] = process; return Task.CompletedTask; }
    /// <summary>Sends an explicit migration command to both live shard processes over their control stdin.</summary>
    public async Task MigrateAsync<TPlayerId>(TPlayerId player, TShardId source, TShardId target, CancellationToken cancellationToken = default)
    {
        if (!this.processes.TryGetValue(source, out Process? sourceProcess) || sourceProcess.HasExited) throw new InvalidOperationException("Source shard process is not running.");
        if (!this.processes.TryGetValue(target, out Process? targetProcess) || targetProcess.HasExited) throw new InvalidOperationException("Target shard process is not running.");
        string command = JsonSerializer.Serialize(new { type = "player.migrate", player, source, target }) + Environment.NewLine;
        await sourceProcess.StandardInput.WriteAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false); await sourceProcess.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        await targetProcess.StandardInput.WriteAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false); await targetProcess.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(5);
    public bool IsRunning(TShardId shard) => this.processes.TryGetValue(shard, out Process? process) && !process.HasExited;
    public async Task StopAsync(TShardId shard, CancellationToken cancellationToken = default)
    {
        if (!this.processes.Remove(shard, out Process? process)) return;
        try
        {
            if (process.HasExited) return;
            process.CloseMainWindow();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(this.ShutdownGracePeriod);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !process.HasExited)
            { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        }
        finally { process.Dispose(); }
    }
}
