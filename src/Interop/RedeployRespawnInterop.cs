// RedeployRespawnInterop.cs - interop v1 for maschine-Redeploy.Core "RespawnApi" (needs ApiVersion >= 1, degrades below).
// Master copy (provider-maintained): Redeploy repo, interop/RedeployRespawnInterop.cs.
// Copy verbatim and change ONLY the namespace. No reference to Redeploy or ArenaMode is needed.
//
// Emits ONE chained, de-duplicated BodyChanged(previous, current, reason) stream from two sources:
//   1) RespawnApi v1 events, bound by reflection when maschine-Redeploy.Core is installed;
//   2) GameWorld.MainPlayer reference polling, always on (no Core, older Core, Fika, non-Core respawn mods).
// C# 7.3; net471 or netstandard2.1. Unity main thread only.
using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KillcamReplay.Interop
{
    /// <summary>Add once to your plugin's own GameObject: gameObject.AddComponent&lt;LocalBodyTracker&gt;().</summary>
    internal sealed class LocalBodyTracker : MonoBehaviour
    {
        public const string ReasonRaidStart = "raid-start";
        public const string ReasonDeath = "death";
        public const string ReasonSwap = "swap";
        public const string ReasonRaidEnd = "raid-end";
        public const string ReasonUnknown = "unknown";

        private const string ApiTypeName = "Redeploy.Core.Api.RespawnApi, maschine-Redeploy.Core";
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const float MaxReplacingHoldSeconds = 35f;

        /// <summary>Test seam. Production resolves through BepInEx's assembly resolver.</summary>
        internal static Func<string, Type> ResolveType = name => Type.GetType(name, false);

        /// <summary>First tracker of this plugin (for Harmony patches that need the queries).</summary>
        public static LocalBodyTracker Instance { get; private set; }

        /// <summary>(previous or null, current or null, reason). Chained: each current becomes the next previous;
        /// at most one event per real change; previous == null always comes with "raid-start".
        /// Treat any reason other than the constants above like "unknown".
        /// Delivery differs by source: with Core it runs inside Core's Replaced dispatch (mid-pipeline, before spawn
        /// protection); without Core it runs from Update. So in EVERY case: never start a respawn, stop the raid or
        /// destroy a body directly from a handler; defer such work by one frame.
        /// After a failed replacement the poll may report old->orphan (and orphan->old if the requester recovers).
        /// "raid-end" is exact only with Core (GameEnd); without Core it arrives late, when the GameWorld goes away.
        /// The hideout and trader narrate scenes also show up as "raid-start": filter them yourself if needed.</summary>
        public event Action<Player, Player, string> BodyChanged;

        /// <summary>Core only: (dead body, driver name). The raid continues for now; a Killed Stop may still follow.</summary>
        public event Action<Player, string> DeathClaimed;

        /// <summary>Core only: (old body, stage). No Replaced follows for that request. BodyChanged follows only
        /// if MainPlayer really changed.</summary>
        public event Action<Player, string> ReplacementFailed;

        /// <summary>Core only: the LocalGame raid ended (GameEnd postfix). Never under Fika, in the hideout or in
        /// narrate scenes. Without Core, use your own LocalGame-filtered BaseLocalGame.GameEnd postfix for per-raid latches.</summary>
        public event Action RaidEnded;

        public Action<string> Log = message => UnityEngine.Debug.Log("[RespawnInterop] " + message);
        public Player Current { get; private set; }
        public bool IsBound { get; private set; }

        private readonly List<KeyValuePair<EventInfo, Delegate>> _subscriptions = new List<KeyValuePair<EventInfo, Delegate>>();
        private Func<Player, bool> _isRespawnPending;
        private Func<bool> _isReplacing;
        private Func<bool> _isAvailable;
        private GameWorld _world;
        private GameWorld _endedWorld;
        private float _holdSince = -1f;

        /// <summary>true: a Core driver claimed (or, before CG_Spawn, is predicted to claim) this death and still holds
        /// the raid open; a replacement is possible, NOT promised, and a Killed Stop may still follow.
        /// false: Core will not replace this body (another respawn mod still might); it does not mean "final death".
        /// null: unknown (Core absent, older than RespawnApi v1, or unavailable, e.g. under Fika): keep today's logic.</summary>
        public bool? IsRespawnPending(Player body)
        {
            if (_isRespawnPending == null || !IsRespawnAvailable) return null;
            try { return _isRespawnPending(body); }
            catch (Exception) { return null; }
        }

        /// <summary>Core can respawn in this session (false when unbound, under Fika, or if Core's patches failed).</summary>
        public bool IsRespawnAvailable { get { return ReadFlag(_isAvailable); } }

        /// <summary>The vanilla current-body test for LIVING bodies. Never IsYourPlayer (true on corpses) or ProfileId
        /// (shared by all bodies). In death handlers use ReferenceEquals(MainPlayer, body) instead: a dead body fails IsAlive.</summary>
        public static bool IsCurrentLocalBody(Player body)
        {
            if (body == null || !Singleton<GameWorld>.Instantiated) return false;  // Unity-null included
            return ReferenceEquals(Singleton<GameWorld>.Instance.MainPlayer, body)
                   && body.HealthController != null && body.HealthController.IsAlive;
        }

        /// <summary>Adds a fresh T. Pooled player GameObjects (corpses returned to the pool) may carry a stale one.</summary>
        public static T AttachFresh<T>(Player body) where T : Component
        {
            T stale = body.GetComponent<T>();
            if (stale != null) DestroyImmediate(stale);
            return body.gameObject.AddComponent<T>();
        }

        /// <summary>Removes T from a body that stopped being the local body; a corpse keeps every component.</summary>
        public static void Detach<T>(Player body) where T : Component
        {
            if (body == null) return;                                   // Unity-null: destroyed, nothing to clean
            T component = body.GetComponent<T>();
            if (component != null) Destroy(component);
        }

        private void Awake()
        {
            if (Instance == null) Instance = this;
            TryBind();
        }

        private void OnDestroy()
        {
            Unbind();
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private void Update()
        {
            GameWorld world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (!ReferenceEquals(world, _world))
            {
                _world = world;
                _endedWorld = null;
                _holdSince = -1f;
                if (!ReferenceEquals(Current, null)) Change(null, ReasonRaidEnd);   // late fallback; Core reports GameEnd earlier
                if (world != null && !IsBound) TryBind();                          // never cache a negative probe
            }

            if (world == null || ReferenceEquals(world, _endedWorld)) return;        // after RaidEnded MainPlayer may be pooled
            Player main = world.MainPlayer;
            if (main == null || ReferenceEquals(main, Current)) return;            // ReferenceEquals: destroyed != same

            if (ReadFlag(_isReplacing))                                            // Core reports the body once it is wired
            {
                if (_holdSince < 0f) _holdSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - _holdSince < MaxReplacingHoldSeconds) return;
            }

            _holdSince = -1f;
            Change(main, ReferenceEquals(Current, null) ? ReasonRaidStart : Infer(Current));
        }

        private static string Infer(Player previous)
        {
            if (previous == null) return ReasonUnknown;                             // destroyed (Unity-null)
            var health = previous.HealthController;
            return health != null && health.IsAlive ? ReasonSwap : ReasonDeath;
        }

        private void Change(Player next, string reason)
        {
            Player previous = Current;
            Current = next;
            Action<Player, Player, string> handlers = BodyChanged;
            if (handlers == null) return;
            foreach (Action<Player, Player, string> handler in handlers.GetInvocationList())
            {
                try { handler(previous, next, reason); }
                catch (Exception ex) { Log("BodyChanged subscriber failed: " + ex); }
            }
        }

        private void Forward(Action<Player, string> handlers, Player body, string text, string name)
        {
            if (handlers == null) return;
            foreach (Action<Player, string> handler in handlers.GetInvocationList())
            {
                try { handler(body, text); }
                catch (Exception ex) { Log(name + " subscriber failed: " + ex); }
            }
        }

        // ---- bound to Core by reflection: exact v1 parameter types, so no relaxed delegate binding is needed ----
        private void OnApiReplaced(Player oldBody, Player newBody, string reason)
        {
            _holdSince = -1f;
            if (ReferenceEquals(newBody, Current)) return;
            Change(newBody, ReferenceEquals(Current, null) ? ReasonRaidStart : (reason ?? ReasonUnknown)); // late attach keeps the chain rule
        }

        private void OnApiReplacementFailed(Player oldBody, string stage)
        {
            _holdSince = -1f;              // the poll reconciles with MainPlayer as soon as IsReplacing is false again
            Forward(ReplacementFailed, oldBody, stage, "ReplacementFailed");
        }

        private void OnApiDeathClaimed(Player deadBody, string driver)
        {
            Forward(DeathClaimed, deadBody, driver, "DeathClaimed");
        }

        private void OnApiRaidEnded()
        {
            _endedWorld = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : _world;
            if (!ReferenceEquals(Current, null)) Change(null, ReasonRaidEnd);
            Action handlers = RaidEnded;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception ex) { Log("RaidEnded subscriber failed: " + ex); }
            }
        }

        private void TryBind()
        {
            try
            {
                Type api = ResolveType(ApiTypeName);
                if (api == null) return;                                           // Core absent: poll only
                PropertyInfo version = api.GetProperty("ApiVersion", PublicStatic);
                if (version == null || version.PropertyType != typeof(int) || (int)version.GetValue(null, null) < 1) return;
                if (!Subscribe(api, "Replaced", nameof(OnApiReplaced))) return;    // the only required member
                Subscribe(api, "DeathClaimed", nameof(OnApiDeathClaimed));
                Subscribe(api, "ReplacementFailed", nameof(OnApiReplacementFailed));
                Subscribe(api, "RaidEnded", nameof(OnApiRaidEnded));
                _isReplacing = BoolGetter(api, "IsReplacing");
                _isAvailable = BoolGetter(api, "IsAvailable");
                MethodInfo pending = api.GetMethod("IsRespawnPending", PublicStatic, null, new[] { typeof(Player) }, null);
                if (pending != null && pending.ReturnType == typeof(bool))
                    _isRespawnPending = (Func<Player, bool>)Delegate.CreateDelegate(typeof(Func<Player, bool>), pending, false);
                IsBound = true;
                Log("bound to RespawnApi v" + version.GetValue(null, null));
            }
            catch (Exception ex)
            {
                Unbind();
                Log("binding failed, polling only: " + ex.Message);
            }
        }

        private void Unbind()
        {
            foreach (KeyValuePair<EventInfo, Delegate> subscription in _subscriptions)
            {
                try { subscription.Key.RemoveEventHandler(null, subscription.Value); }   // the SAME delegate instance
                catch (Exception) { }
            }
            _subscriptions.Clear();
            _isRespawnPending = null;
            _isReplacing = null;
            _isAvailable = null;
            IsBound = false;
        }

        private bool Subscribe(Type api, string eventName, string handlerName)
        {
            EventInfo evt = api.GetEvent(eventName, PublicStatic);
            MethodInfo method = typeof(LocalBodyTracker).GetMethod(handlerName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (evt == null || method == null) return false;
            Delegate handler = Delegate.CreateDelegate(evt.EventHandlerType, this, method, false);
            if (handler == null) return false;                                     // signature drift: skip this member only
            evt.AddEventHandler(null, handler);
            _subscriptions.Add(new KeyValuePair<EventInfo, Delegate>(evt, handler));
            return true;
        }

        private static Func<bool> BoolGetter(Type api, string name)
        {
            PropertyInfo property = api.GetProperty(name, PublicStatic);
            MethodInfo getter = property != null && property.PropertyType == typeof(bool) ? property.GetGetMethod() : null;
            return getter == null ? null : (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter, false);
        }

        private static bool ReadFlag(Func<bool> flag)
        {
            if (flag == null) return false;
            try { return flag(); }
            catch (Exception) { return false; }
        }
    }
}

// ---- Usage: per-body component (DoorDash style) ----
// Plugin.Awake:
//   var bodies = gameObject.AddComponent<LocalBodyTracker>();
//   bodies.Log = m => Logger.LogInfo(m);
//   bodies.BodyChanged += (previous, current, reason) =>
//   {
//       LocalBodyTracker.Detach<RaycastBreacher>(previous);          // the corpse would keep ramming doors
//       if (current != null) LocalBodyTracker.AttachFresh<RaycastBreacher>(current);
//   };
// RaycastBreacher.Update:
//   if (!LocalBodyTracker.IsCurrentLocalBody(player)) return;
//
// ---- Usage: death-time decision (e.g. a Player.OnDead postfix) ----
//   if (!Singleton<GameWorld>.Instantiated
//       || !ReferenceEquals(Singleton<GameWorld>.Instance.MainPlayer, __instance)) return; // bot or retired swap body
//   bool? pending = LocalBodyTracker.Instance?.IsRespawnPending(__instance);
//   if (pending == true)       { /* Core holds the raid open: do not treat the raid as over yet; a Killed Stop may still follow, keep your Stop handling */ }
//   else if (pending == false) { /* Core will not replace this body; another respawn mod still might */ }
//   else                       { /* unknown (no Core, older Core, Fika): keep today's logic */ }
