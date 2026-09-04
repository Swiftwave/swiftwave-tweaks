using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

public enum Safety { Safe, Moderate, Advanced }
public enum OptState { Optimized, NeedsAttention, Manual, Unsupported, Unknown }
public enum ApplyStatus { Applied, AlreadyOptimized, NotApplicable, Manual, Failed }

public sealed record Detection(OptState State, string CurrentState, bool Selectable, string Reason);
public sealed record ApplyResult(ApplyStatus Status, string Message, string? Detail = null, bool RestartRequired = false);

/// <summary>
/// Base contract every optimization implements: detect current real state, apply a real
/// change, independently verify the resulting state, and revert to a saved previous state.
/// (spec 33/36/38)
/// </summary>
public abstract class Optimization
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string Category { get; }
    public abstract Safety Safety { get; }
    public abstract bool RequiresAdmin { get; }
    public virtual bool RequiresRestart => false;

    /// <summary>Reads the live system state for this optimization. Must not modify anything.</summary>
    public abstract Detection Detect();

    /// <summary>Serialised previous state used for rollback.</summary>
    protected abstract string CapturePrevious();

    /// <summary>Performs the actual change. Returns false plus detail on failure.</summary>
    protected abstract bool ApplyChange(out string detail);

    /// <summary>Independent re-read confirming the change took effect.</summary>
    public abstract Detection DetectAfterApply();

    /// <summary>Restores a previously captured state. Returns true when the restored state was verified.</summary>
    public abstract bool Revert(string previousState, out string detail);

    public ApplyResult Apply()
    {
        var before = Detect();
        switch (before.State)
        {
            case OptState.Optimized:
                SafeLog.Write($"Apply skipped (already optimized): {Id}");
                return new ApplyResult(ApplyStatus.AlreadyOptimized, "Already optimized — the recommended state is already active.");
            case OptState.Manual:
                return new ApplyResult(ApplyStatus.Manual, "Manual — " + before.Reason, before.CurrentState);
            case OptState.Unsupported:
                return new ApplyResult(ApplyStatus.NotApplicable, "Not applicable — " + before.Reason, before.CurrentState);
        }

        string previous;
        try { previous = CapturePrevious(); }
        catch (Exception ex)
        {
            SafeLog.Write($"Rollback capture failed: {Id}", ex);
            return new ApplyResult(ApplyStatus.Failed, "Failed — could not capture the previous state for rollback.", ex.Message);
        }

        bool ok;
        string detail = "";
        try { ok = ApplyChange(out detail); }
        catch (Exception ex)
        {
            SafeLog.Write($"Apply threw: {Id}", ex);
            ok = false; detail = ex.Message;
        }

        if (!ok)
        {
            SafeLog.Write($"Apply failed: {Id} :: {detail}");
            return new ApplyResult(ApplyStatus.Failed, "Failed — the change could not be applied" + (detail.Length > 0 ? ": " + detail : "."), detail);
        }

        var after = DetectAfterApply();
        if (after.State != OptState.Optimized)
        {
            string fail = $"Change was not verified. Reported state afterwards: {after.CurrentState}";
            SafeLog.Write($"Verification failed: {Id} :: {fail}");
            bool reverted = false;
            try { reverted = Revert(previous, out _); } catch { }
            SafeLog.Write($"Auto-revert after failed verification: {Id} :: {(reverted ? "reverted" : "revert failed")}");
            return new ApplyResult(ApplyStatus.Failed, "Failed — the resulting state could not be independently verified. The previous state was " + (reverted ? "restored." : "NOT restored; use History to revert."), fail);
        }

        RollbackStore.Add(new RollbackItem(Id, Name, previous, DateTimeOffset.Now));
        SafeLog.Write($"Applied and verified: {Id} (was: {before.CurrentState})");
        return new ApplyResult(ApplyStatus.Applied, "Applied and independently verified.", after.CurrentState, RequiresRestart);
    }

    public ApplyResult Rollback(string previousState)
    {
        try
        {
            if (Revert(previousState, out var detail))
            {
                SafeLog.Write($"Rolled back: {Id}");
                return new ApplyResult(ApplyStatus.Applied, "Restored to the previous state.", detail);
            }
            SafeLog.Write($"Rollback failed: {Id} :: {detail}");
            return new ApplyResult(ApplyStatus.Failed, "Failed — rollback could not be verified.", detail);
        }
        catch (Exception ex)
        {
            SafeLog.Write($"Rollback threw: {Id}", ex);
            return new ApplyResult(ApplyStatus.Failed, "Failed — rollback threw an exception.", ex.Message);
        }
    }
}
