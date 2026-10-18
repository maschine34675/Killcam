namespace Killcam.Replay;

// Keeps a respawn of the replay's victim waiting until the death replay has handed
// the screen over, the same way DeferStopPatch keeps a Killed raid end waiting.
//
// Pure state and decisions: no Unity, no EFT. Bodies are compared by reference only,
// so the offline checks in tools/HandoffChecks drive it with plain objects. The Harmony
// hold on Redeploy.Core's RespawnEngine.RunRespawn asks ShouldHold every frame;
// ReplayController reports the replay's progress.
//
// Life of one death replay:
//   BeginDeathReplay(victim)            RunDeathReplay starts (before the lead-in)
//   ShouldHold(victim) -> true          a replacement of the victim is asked for: hold it
//   ReleaseUnderBlack()                 the ending faded to black: let it run now
//   ShouldHold(victim) -> false         the hold ends; BeginRunning .. EndRunning around the pipeline
//   EndDeathReplay()                    the replay is over for any reason: nothing held
// AbandonWaiting() instead of the release when the hold gives up by itself (raid ending,
// hold timeout): the replay then ends like one nobody waits for.
public sealed class RespawnHandoff
{
    private object _victim;
    private bool _waiting;
    private bool _released;
    private bool _running;

    /// <summary>A death replay of <paramref name="victim"/> owns the screen from now on.</summary>
    public void BeginDeathReplay(object victim)
    {
        _victim = victim;
        _waiting = false;
        _released = false;
        _running = false;
    }

    /// <summary>The replay is over (ended, skipped, failed to start): hold nothing.</summary>
    public void EndDeathReplay()
    {
        _victim = null;
        _waiting = false;
        _released = false;
        _running = false;
    }

    /// <summary>
    /// Asked by the hold every frame while a replacement of <paramref name="body"/> waits.
    /// True keeps it waiting. The first true also records that a replacement is waiting,
    /// which turns the replay's ending into a fade to black instead of handing the corpse
    /// camera back.
    /// </summary>
    public bool ShouldHold(object body)
    {
        if (_victim == null || body == null || !ReferenceEquals(body, _victim) || _released)
            return false;

        _waiting = true;
        return true;
    }

    /// <summary>A replacement of the victim is waiting and has not been released.</summary>
    public bool ReplacementWaiting => _waiting && !_released;

    /// <summary>The waiting replacement was released under black and should arrive this frame or the next.</summary>
    public bool ReplacementReleased => _released;

    /// <summary>The released pipeline is between its first and last step.</summary>
    public bool ReplacementRunning => _running;

    /// <summary>The ending reached full black: a waiting replacement may run now. No-op otherwise.</summary>
    public void ReleaseUnderBlack()
    {
        if (_waiting)
            _released = true;
    }

    /// <summary>
    /// The hold stopped waiting by itself (the raid is ending, or its cap ran out) and lets the
    /// pipeline decide. Nothing waits for the black any more: the replay ends like one nobody
    /// waits for, instead of fading to black for a respawn that will not come.
    /// </summary>
    public void AbandonWaiting()
    {
        if (!_released)
            _waiting = false;
    }

    public void BeginRunning() => _running = true;

    public void EndRunning() => _running = false;

    /// <summary>
    /// After the live tail: fade to black and hold it when something will take over the
    /// screen under that black (a deferred Stop or a waiting replacement); otherwise hand
    /// the camera straight back.
    /// </summary>
    public static bool FadeToBlackAfterLiveTail(bool stopDeferred, bool replacementWaiting)
        => stopDeferred || replacementWaiting;

    public enum SkipAction
    {
        EndNow,
        FadeToBlack,
        AlreadyLeaving,
    }

    /// <summary>
    /// The skip key. Without anything taking over after the replay, just end it. With a deferred
    /// Stop or a waiting respawn, go to the final fade so that hand-off still happens under black,
    /// unless the ending is already fading out or black.
    /// </summary>
    public static SkipAction DecideSkip(bool stopDeferred, bool replacementWaiting, bool alreadyFadingOutOrBlack)
    {
        if (!FadeToBlackAfterLiveTail(stopDeferred, replacementWaiting))
            return SkipAction.EndNow;
        return alreadyFadingOutOrBlack ? SkipAction.AlreadyLeaving : SkipAction.FadeToBlack;
    }

    public enum BlackHoldExit
    {
        Wait,
        HandedOffUnderGameBlack,
        ReplacementDidNotArrive,
        Timeout,
    }

    /// <summary>
    /// When the black hold ends. A re-invoked Stop owns the end: wait until its own black
    /// screen is fully up. A released replacement normally ends playback itself in the
    /// release frame (BodyChanged); while its pipeline is still running, keep waiting; once
    /// it has finished without arriving (refused), give the screen back after a short grace.
    /// The long timeout covers everything else.
    /// </summary>
    public static BlackHoldExit DecideBlackHoldExit(
        float now, float phaseClock, float stopReinvokedAt, bool replacementReleased, bool replacementRunning,
        float gameBlackDelay = 1.5f, float replacementGrace = 1.5f, float timeout = 5f)
    {
        if (stopReinvokedAt > 0f)
        {
            if (now - stopReinvokedAt >= gameBlackDelay) return BlackHoldExit.HandedOffUnderGameBlack;
            return phaseClock >= timeout ? BlackHoldExit.Timeout : BlackHoldExit.Wait;
        }

        if (replacementReleased && !replacementRunning && phaseClock >= replacementGrace)
            return BlackHoldExit.ReplacementDidNotArrive;

        return phaseClock >= timeout ? BlackHoldExit.Timeout : BlackHoldExit.Wait;
    }

    /// <summary>
    /// Opacity of the fade-in after the replay ended under its own black. Full black in the frame
    /// the replay ended and the next one (the replacement's frame is usually long, and the next
    /// frame's clock already contains it), then linear to zero over <paramref name="duration"/>.
    /// <paramref name="fadeStart"/> is set on the first fading frame (negative = not started).
    /// Returns a value &lt;= 0 once the fade is over.
    /// </summary>
    public static float RevealAlpha(int frame, int armedFrame, float now, ref float fadeStart, float duration)
    {
        if (armedFrame < 0) return 0f;
        if (frame <= armedFrame + 1) return 1f;
        if (fadeStart < 0f) fadeStart = now;
        return duration <= 0f ? 0f : 1f - (now - fadeStart) / duration;
    }
}
