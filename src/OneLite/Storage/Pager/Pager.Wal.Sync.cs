using RecursiveAI.OneLite.Storage.Wal;

namespace RecursiveAI.OneLite.Storage.Pager;

public sealed partial class Pager
{
    /// <summary>Runs a <see cref="Checkpoint"/> inline and returns what it did. Throws the pager's error.</summary>
    /// <param name="mode">The kind of checkpoint.</param>
    /// <returns>The three numbers of <c>PRAGMA wal_checkpoint</c>.</returns>
    public CheckpointResult CheckpointSync(CheckpointMode mode)
    {
        RunSync(() => Checkpoint(mode));
        return LastCheckpoint;
    }

    /// <summary>Awaiting twin of <see cref="CheckpointSync"/>.</summary>
    /// <param name="mode">The kind of checkpoint.</param>
    /// <param name="ct">Cancellation, observed at requests that are safe to abandon.</param>
    /// <returns>The three numbers of <c>PRAGMA wal_checkpoint</c>.</returns>
    public async ValueTask<CheckpointResult> CheckpointAsync(CheckpointMode mode, CancellationToken ct = default)
    {
        await RunAsync(() => Checkpoint(mode), ct).ConfigureAwait(false);
        return LastCheckpoint;
    }

    /// <summary>Runs <see cref="ChangeJournalMode"/> inline. Throws the pager's error.</summary>
    /// <param name="target">The new journal mode.</param>
    public void ChangeJournalModeSync(JournalMode target) => RunSync(() => ChangeJournalMode(target));

    /// <summary>Awaiting twin of <see cref="ChangeJournalModeSync"/>.</summary>
    /// <param name="target">The new journal mode.</param>
    /// <param name="ct">Cancellation, observed at requests that are safe to abandon.</param>
    /// <returns>A task that finishes when the mode has changed.</returns>
    public ValueTask ChangeJournalModeAsync(JournalMode target, CancellationToken ct = default) => RunAsync(() => ChangeJournalMode(target), ct);
}
