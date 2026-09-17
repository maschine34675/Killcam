using System.Collections;
using System.Reflection;
using KillcamReplay.Patches;
using KillcamReplay.Replay;

// Offline checks for the respawn hold. Exit code 0 and HANDOFF_CHECKS_PASSED on a full pass;
// exit code 3 and HANDOFF_CHECKS_PARTIAL when the Core member check could not run.
internal static class Program
{
    private static int _checks;

    private static void Check(bool condition, string scenario)
    {
        _checks++;
        if (!condition)
            throw new Exception("FAILED: " + scenario);
    }

    private static int Main()
    {
        CheckHandoffLifecycle();
        CheckAbandonAndRunning();
        CheckEndingDecisions();
        CheckSkip();
        CheckBlackHoldExit();
        CheckReveal();
        CheckStaleSamplers();
        var coreChecked = CheckCoreMembers();

        if (!coreChecked)
        {
            // Distinct token and exit code: a run that could not read Core is not a full pass.
            Console.WriteLine($"PARTIAL: {_checks} hand-off checks passed; the Core member check was skipped (Core build or game not found).");
            Console.WriteLine("HANDOFF_CHECKS_PARTIAL");
            return 3;
        }

        Console.WriteLine($"PASS: {_checks} hand-off checks.");
        Console.WriteLine("HANDOFF_CHECKS_PASSED");
        return 0;
    }

    // ------------------------------------------------------------------ state

    private static void CheckHandoffLifecycle()
    {
        var victim = new object();
        var other = new object();
        var handoff = new RespawnHandoff();

        Check(!handoff.ShouldHold(victim), "no death replay: nothing is held");
        Check(!handoff.ReplacementWaiting, "no death replay: no replacement waiting");

        handoff.BeginDeathReplay(victim);
        Check(!handoff.ReplacementWaiting, "a replay alone does not mean a respawn waits");
        Check(!handoff.ShouldHold(other), "another body (a bot, a swap of someone else) is never held");
        Check(!handoff.ShouldHold(null), "a request without a body is never held");
        Check(!handoff.ReplacementWaiting, "asking for other bodies records nothing");

        Check(handoff.ShouldHold(victim), "the victim's respawn is held while its replay runs");
        Check(handoff.ReplacementWaiting, "the first held ask records a waiting replacement");
        Check(handoff.ShouldHold(victim), "the hold lasts across frames");
        Check(!handoff.ReplacementReleased, "nothing is released before the black");

        handoff.ReleaseUnderBlack();
        Check(handoff.ReplacementReleased, "the black releases a waiting replacement");
        Check(!handoff.ReplacementWaiting, "a released replacement no longer counts as waiting");
        Check(!handoff.ShouldHold(victim), "once released, the pipeline may run");

        handoff.EndDeathReplay();
        Check(!handoff.ShouldHold(victim), "after the replay ends nothing is held");
        Check(!handoff.ReplacementReleased && !handoff.ReplacementWaiting, "ending clears every flag");

        // Release without a waiting replacement (a deferred Stop reached the black) is a no-op:
        // a respawn asked for later in the same replay still waits for the replay's end.
        handoff.BeginDeathReplay(victim);
        handoff.ReleaseUnderBlack();
        Check(!handoff.ReplacementReleased, "the black without a waiting respawn releases nothing");
        Check(handoff.ShouldHold(victim), "a later respawn of the victim still waits");

        // A new death replay starts from a clean state even after a release.
        handoff.ReleaseUnderBlack();
        Check(handoff.ReplacementReleased, "setup: released");
        var nextVictim = new object();
        handoff.BeginDeathReplay(nextVictim);
        Check(!handoff.ReplacementReleased && !handoff.ReplacementWaiting, "a new death replay resets the release");
        Check(!handoff.ShouldHold(victim), "the previous victim is not held by the new replay");
        Check(handoff.ShouldHold(nextVictim), "the new victim is");
    }

    private static void CheckAbandonAndRunning()
    {
        var victim = new object();
        var handoff = new RespawnHandoff();

        // The hold gives up by itself (raid ending, cap): the replay must not fade to black for it.
        handoff.BeginDeathReplay(victim);
        Check(handoff.ShouldHold(victim), "setup: a respawn waits");
        handoff.AbandonWaiting();
        Check(!handoff.ReplacementWaiting, "an abandoned hold no longer counts as waiting");
        Check(!RespawnHandoff.FadeToBlackAfterLiveTail(false, handoff.ReplacementWaiting),
            "after an abandoned hold the live tail hands the camera straight back");
        handoff.ReleaseUnderBlack();
        Check(!handoff.ReplacementReleased, "the black releases nothing after an abandon");

        // Abandon after the release changes nothing: the pipeline already owns the black.
        handoff.BeginDeathReplay(victim);
        handoff.ShouldHold(victim);
        handoff.ReleaseUnderBlack();
        handoff.AbandonWaiting();
        Check(handoff.ReplacementReleased, "an abandon after the release keeps the release");

        // Running flag: set around the released pipeline, cleared by the end of the replay.
        handoff.BeginRunning();
        Check(handoff.ReplacementRunning, "the pipeline is running");
        handoff.EndRunning();
        Check(!handoff.ReplacementRunning, "the pipeline finished");
        handoff.BeginRunning();
        handoff.EndDeathReplay();
        Check(!handoff.ReplacementRunning, "ending the replay clears a running flag a killed coroutine left behind");
    }

    private static void CheckSkip()
    {
        Check(RespawnHandoff.DecideSkip(false, false, false) == RespawnHandoff.SkipAction.EndNow,
            "nothing waits: skip just ends the replay");
        Check(RespawnHandoff.DecideSkip(false, false, true) == RespawnHandoff.SkipAction.EndNow,
            "nothing waits, even in the ending: skip ends it");
        Check(RespawnHandoff.DecideSkip(false, true, false) == RespawnHandoff.SkipAction.FadeToBlack,
            "a waiting respawn: skip fades to black so the respawn runs under it");
        Check(RespawnHandoff.DecideSkip(true, false, false) == RespawnHandoff.SkipAction.FadeToBlack,
            "a deferred Stop: skip fades to black so the Stop hand-off stays covered");
        Check(RespawnHandoff.DecideSkip(true, true, true) == RespawnHandoff.SkipAction.AlreadyLeaving,
            "already fading out or black: skip does not restart the fade");
        Check(RespawnHandoff.DecideSkip(false, true, true) == RespawnHandoff.SkipAction.AlreadyLeaving,
            "a waiting respawn while already black: nothing to do");
    }

    private static void CheckReveal()
    {
        float start = -1f;
        Check(RespawnHandoff.RevealAlpha(10, -1, 1f, ref start, 0.35f) <= 0f, "no reveal armed: nothing drawn");
        Check(start < 0f, "no reveal armed: no fade started");

        start = -1f;
        Check(RespawnHandoff.RevealAlpha(10, 10, 1.0f, ref start, 0.35f) == 1f, "the frame the replay ended stays black");
        Check(RespawnHandoff.RevealAlpha(11, 10, 1.4f, ref start, 0.35f) == 1f, "the next (long) frame stays black too");
        Check(start < 0f, "the fade has not started during the black frames");
        Check(RespawnHandoff.RevealAlpha(12, 10, 2.0f, ref start, 0.35f) == 1f, "the fade starts at full black on the third frame");
        Check(start == 2.0f, "the fade clock starts on the third frame, after the long frame");
        var mid = RespawnHandoff.RevealAlpha(13, 10, 2.175f, ref start, 0.35f);
        Check(mid > 0.45f && mid < 0.55f, "half way through the fade the overlay is half transparent");
        Check(RespawnHandoff.RevealAlpha(14, 10, 2.36f, ref start, 0.35f) <= 0f, "the fade ends after its duration");
        Check(RespawnHandoff.RevealAlpha(900, 10, 99f, ref start, 0.35f) <= 0f, "a late frame never brings the black back");
    }

    private static void CheckEndingDecisions()
    {
        Check(!RespawnHandoff.FadeToBlackAfterLiveTail(false, false),
            "nothing waits (prompt still open): hand the camera straight back");
        Check(RespawnHandoff.FadeToBlackAfterLiveTail(true, false), "a deferred Stop takes over under black");
        Check(RespawnHandoff.FadeToBlackAfterLiveTail(false, true), "a waiting respawn takes over under black");
        Check(RespawnHandoff.FadeToBlackAfterLiveTail(true, true), "both: still black");
    }

    private static void CheckBlackHoldExit()
    {
        const float now = 100f;
        var wait = RespawnHandoff.BlackHoldExit.Wait;

        // The deferred Stop path is unchanged: 1.5s after the re-invoke, or the 5s timeout.
        Check(RespawnHandoff.DecideBlackHoldExit(now, 0.1f, now - 1.0f, false, false) == wait,
            "the game's black screen is not fully up 1.0s after the re-invoke");
        Check(RespawnHandoff.DecideBlackHoldExit(now, 0.1f, now - 1.5f, false, false) == RespawnHandoff.BlackHoldExit.HandedOffUnderGameBlack,
            "1.5s after the re-invoke the game's black screen owns the view");
        Check(RespawnHandoff.DecideBlackHoldExit(now, 5f, now - 0.2f, false, false) == RespawnHandoff.BlackHoldExit.Timeout,
            "a re-invoked Stop still times out at 5s");
        Check(RespawnHandoff.DecideBlackHoldExit(now, 4.9f, 0f, false, false) == wait, "no Stop, nothing released: wait below 5s");
        Check(RespawnHandoff.DecideBlackHoldExit(now, 5f, 0f, false, false) == RespawnHandoff.BlackHoldExit.Timeout, "no Stop, nothing released: 5s timeout");

        // A released respawn normally ends playback itself; a refused one must not keep the screen black for 5s.
        Check(RespawnHandoff.DecideBlackHoldExit(now, 1.4f, 0f, true, false) == wait,
            "a released respawn gets its grace before the black gives up");
        Check(RespawnHandoff.DecideBlackHoldExit(now, 1.5f, 0f, true, false) == RespawnHandoff.BlackHoldExit.ReplacementDidNotArrive,
            "a released respawn whose pipeline finished without arriving gives the screen back after the grace");
        Check(RespawnHandoff.DecideBlackHoldExit(now, 3f, 0f, true, true) == wait,
            "while the released pipeline still runs, the black does not give up on it");
        Check(RespawnHandoff.DecideBlackHoldExit(now, 5f, 0f, true, true) == RespawnHandoff.BlackHoldExit.Timeout,
            "a pipeline that never finishes is still bounded by the timeout");

        // A Killed Stop that follows a refused respawn (give-up) owns the end, not the grace.
        Check(RespawnHandoff.DecideBlackHoldExit(now, 1.6f, now - 0.3f, true, false) == wait,
            "a re-invoked Stop wins over the released-respawn grace");
        Check(RespawnHandoff.DecideBlackHoldExit(now, 1.9f, now - 1.6f, true, false) == RespawnHandoff.BlackHoldExit.HandedOffUnderGameBlack,
            "and ends under the game's black");
    }

    // ------------------------------------------------------------------ stale samplers

    /// <summary>
    /// FixPerfectCullingSamplerPatch finds stale samplers from the ones it saw starting plus the
    /// sampler's static Instance instead of a scene search. The registry is a copy of Redeploy.Core's;
    /// when that repo sits next to this one, the copy must still match it apart from the namespace.
    /// </summary>
    private static void CheckStaleSamplers()
    {
        var alive = new HashSet<object>();
        var registry = new StaleSamplerRegistry(alive.Contains);
        var old = new object();
        var fresh = new object();
        alive.Add(old);
        alive.Add(fresh);

        Check(registry.TakeStale(old, null).Count == 0, "the first sampler of a raid has nothing stale");
        var stale = registry.TakeStale(fresh, old);
        Check(stale.Count == 1 && stale[0] == old, "a respawn camera's sampler finds the one it replaces, once");
        alive.Remove(old);                                  // the prefix destroyed it
        Check(registry.TakeStale(fresh, fresh).Count == 0, "the starting sampler is never stale, even as Instance");

        var unseen = new object();
        alive.Add(unseen);
        var next = new object();
        alive.Add(next);
        stale = registry.TakeStale(next, unseen);
        Check(stale.Count == 2 && stale.Contains(fresh) && stale.Contains(unseen) && !stale.Contains(old),
            "earlier live samplers and an unseen Instance are stale, a destroyed one is forgotten");

        var master = Path.Combine(FindRepoRoot(), "..", "Redeploy", "Redeploy.Core", "Compat", "StaleSamplerRegistry.cs");
        if (!File.Exists(master))
            return;
        var copy = Path.Combine(FindRepoRoot(), "src", "Patches", "StaleSamplerRegistry.cs");
        Check(CodeBody(File.ReadAllText(master)) == CodeBody(File.ReadAllText(copy)),
            "src/Patches/StaleSamplerRegistry.cs still matches Redeploy.Core/Compat/StaleSamplerRegistry.cs (namespace and comments aside)");
    }

    /// <summary>The code after the namespace line, comment lines and blank lines dropped.</summary>
    private static string CodeBody(string source)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.StartsWith("namespace "));
        return string.Join("\n", lines.Skip(start + 1)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("//")));
    }

    // ------------------------------------------------------------------ Core members

    /// <summary>
    /// The members HoldRespawnPatch resolves by name, read from the built Core as metadata:
    /// RespawnEngine.RunRespawn(RespawnRequest, Action&lt;Exception&gt;) returning IEnumerator,
    /// RespawnRequest.Game (LocalGame) and RespawnRequest.DeadPlayer (LocalPlayer, a Player).
    /// </summary>
    private static bool CheckCoreMembers()
    {
        var repo = FindRepoRoot();
        var install = Path.GetFullPath(Path.Combine(repo, "..", ".."));
        var managed = Path.Combine(install, "EscapeFromTarkov_Data", "Managed");
        var core = new[] { "Release", "Debug" }
            .Select(c => Path.Combine(repo, "..", "Redeploy", "Redeploy.Core", "bin", c, "maschine-Redeploy.Core.dll"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? Path.Combine(install, "BepInEx", "plugins", "maschine-Redeploy.Core.dll");

        if (!File.Exists(core) || !Directory.Exists(managed))
            return false;

        var paths = new List<string> { core };
        paths.AddRange(Directory.GetFiles(managed, "*.dll"));
        foreach (var extra in new[] { Path.Combine(install, "BepInEx", "core"), Path.Combine(install, "BepInEx", "plugins", "spt") })
            if (Directory.Exists(extra))
                paths.AddRange(Directory.GetFiles(extra, "*.dll"));

        using var context = new MetadataLoadContext(new PathAssemblyResolver(paths.Distinct()), coreAssemblyName: "mscorlib");
        var assembly = context.LoadFromAssemblyPath(core);

        var engine = assembly.GetType("Redeploy.Core.Engine.RespawnEngine");
        Check(engine != null, "Core has Redeploy.Core.Engine.RespawnEngine");

        var runs = engine.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "RunRespawn").ToList();
        Check(runs.Count == 1, "Core has exactly one public static RespawnEngine.RunRespawn");
        var run = runs[0];
        var parameters = run.GetParameters();
        Check(run.ReturnType.FullName == typeof(IEnumerator).FullName, "RunRespawn returns IEnumerator (consumed with StartCoroutine)");
        Check(parameters.Length == 2, "RunRespawn takes (RespawnRequest, Action<Exception>)");
        Check(parameters[0].ParameterType.FullName == "Redeploy.Core.Engine.RespawnRequest", "the first parameter is the RespawnRequest");

        var request = parameters[0].ParameterType;
        var game = request.GetField("Game", BindingFlags.Public | BindingFlags.Instance);
        var dead = request.GetField("DeadPlayer", BindingFlags.Public | BindingFlags.Instance);
        Check(game != null && game.FieldType.FullName == "EFT.LocalGame", "RespawnRequest.Game is an EFT.LocalGame field");
        Check(dead != null && IsOrDerivesFrom(dead.FieldType, "EFT.Player"), "RespawnRequest.DeadPlayer is a Player field");
        return true;
    }

    private static bool IsOrDerivesFrom(Type type, string fullName)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.FullName == fullName) return true;
        return false;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillcamReplay.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("KillcamReplay.csproj not found above " + AppContext.BaseDirectory);
    }
}
