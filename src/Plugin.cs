using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ConnectedMining
{
    [BepInPlugin(Id, "Connected Mining", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "tristan.valheim.connectedmining";
        public const string Version = "1.1.0";
        internal static Plugin Instance;
        internal ConfigEntry<string> DepositPrefixes;
        private ConfigEntry<bool> enabledSetting;
        private ConfigEntry<float> tolerance;
        private ConfigEntry<int> perFrame;
        private Harmony harmony;
        private bool applying;
        private Multiplayer network;
        private readonly HashSet<string> unreadable = new HashSet<string>();

        internal sealed class Snapshot
        {
            internal Deposit Source;
            internal HitData Hit;
            internal List<Work> Connected;
            internal List<Shape> SourceShapes;
        }
        internal sealed class Work
        {
            internal Deposit Node;
            internal HitData Hit;
            internal List<Shape> Shapes;
        }
        internal bool LocalEnabled => enabledSetting.Value;
        internal float LocalTolerance => tolerance.Value;
        internal int LocalPerFrame => perFrame.Value;
        internal string LocalPrefixes => DepositPrefixes.Value;
        internal string EffectivePrefixes => network?.Prefixes ?? LocalPrefixes;
        internal void Warn(string text) => Logger.LogWarning(text);

        private void Awake()
        {
            Instance = this;
            enabledSetting = Config.Bind("General", "Enabled", true, "Enable connected mining. Automatic mining never deforms terrain.");
            tolerance = Config.Bind("Mining", "ConnectionTolerance", 0.10f, new ConfigDescription("Maximum surface gap in metres treated as touching.", new AcceptableValueRange<float>(0, 0.5f)));
            perFrame = Config.Bind("Mining", "SectionsPerFrame", 8, new ConfigDescription("Maximum additional sections broken per rendered frame.", new AcceptableValueRange<int>(1, 64)));
            DepositPrefixes = Config.Bind("Mining", "NaturalDepositPrefixes", "rock,stone,obsidian,mudpile,MineRock,copper,tin,silver,flametal,blackmarble,sulfur", "Additional classification for single-piece natural deposits with resource drops. MineRock/MineRock5 deposits are detected automatically. Buildings and trees are excluded.");
            try
            {
                ValidateHooks();
                network = new Multiplayer(this);
                harmony = new Harmony(Id);
                harmony.Patch(Deposit.DamageArea, prefix: Method(nameof(BeforeArea)), postfix: Method(nameof(AfterArea)));
                harmony.Patch(Deposit.HitRock, prefix: Method(nameof(BeforeArea)), postfix: Method(nameof(AfterVoid)));
                harmony.Patch(Deposit.HitDestructible, prefix: Method(nameof(BeforeDestructible)), postfix: Method(nameof(AfterVoid)));
                Logger.LogInfo("Connected Mining " + Version + " loaded. Dedicated-server coordination enabled; automatic mining does not alter terrain.");
            }
            catch (Exception e)
            {
                harmony?.UnpatchSelf();
                enabled = false;
                Logger.LogError("Mining hooks could not be initialized: " + e);
            }
        }

        private static HarmonyMethod Method(string name) => new HarmonyMethod(typeof(Plugin), name);
        private static void ValidateHooks()
        {
            foreach (MemberInfo member in new MemberInfo[] { Deposit.Areas5, Deposit.AreaCollider, Deposit.AreaHealth, Deposit.Areas, Deposit.LoadHealth, Deposit.DamageArea, Deposit.HitRock, Deposit.HitDestructible, Deposit.Destroyed })
                if (member == null) throw new MissingMemberException("The installed Valheim mining API differs from the supported version.");
        }
        private bool CanRun => network != null && network.Ready && ZNet.instance && ZNetScene.instance;
        internal void WarnUnreadable(string name)
        {
            if (unreadable.Add(name)) Logger.LogWarning("Cannot inspect collider mesh '" + name + "'; connections through it are skipped rather than guessed from bounding boxes.");
        }

        private static void BeforeArea(Component __instance, HitData hit, int hitAreaIndex, out Snapshot __state)
        {
            __state = null;
            if (!Instance || Instance.applying || !Instance.CanRun || hit == null || hit.m_damage.m_pickaxe <= 0 || !(hit.GetAttacker() is Player)) return;
            try
            {
                if (__instance is MineRock5) Deposit.LoadHealth.Invoke(__instance, null);
                __state = Instance.Capture(Deposit.FromArea(__instance, hitAreaIndex), hit);
            }
            catch (Exception e) { Instance.Logger.LogError("Could not inspect mining hit: " + e); }
        }

        private static void BeforeDestructible(Destructible __instance, HitData hit, out Snapshot __state)
        {
            __state = null;
            if (!Instance || Instance.applying || !Instance.CanRun || hit == null || hit.m_damage.m_pickaxe <= 0 || !(hit.GetAttacker() is Player)) return;
            try
            {
                foreach (Collider collider in __instance.GetComponentsInChildren<Collider>())
                {
                    Deposit node = Deposit.FromCollider(collider);
                    if (node == null || node.Owner != __instance || !node.Alive) continue;
                    __state = Instance.Capture(node, hit);
                    break;
                }
            }
            catch (Exception e) { Instance.Logger.LogError("Could not inspect deposit hit: " + e); }
        }

        private Snapshot Capture(Deposit source, HitData hit)
        {
            if (source == null || !source.Allowed(hit) || !source.View.IsOwner()) return null;
            if (Deposit.FromCollider(source.Collider) == null) return null;
            HitData prediction = hit.Clone();
            prediction.ApplyResistance(source.Modifiers, out _);
            if (prediction.GetTotalDamage() < source.Health) return null;
            var shapes = Shape.Capture(source);
            return new Snapshot { Source = source, Hit = hit.Clone(), SourceShapes = shapes, Connected = Discover(shapes, source.Key, hit) };
        }

        // Snapshot the graph before any collider is removed, including bridges that vanilla
        // support collapse may remove during the original hit. Tool-ineligible nodes cannot bridge.
        internal List<Work> Discover(List<Shape> seeds, string excludedKey, HitData hit)
        {
            var result = new List<Work>();
            var seen = new HashSet<string>();
            if (excludedKey != null) seen.Add(excludedKey);
            var frontier = new Queue<Shape>(seeds);
            var shapesByKey = new Dictionary<string, List<Shape>>();
            while (frontier.Count > 0)
            {
                Shape source = frontier.Dequeue();
                foreach (Collider collider in Physics.OverlapBox(source.Bounds.center, source.Bounds.extents + Vector3.one * network.Tolerance, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                {
                    Deposit node = Deposit.FromCollider(collider);
                    if (node == null || seen.Contains(node.Key) || !node.Allowed(hit)) continue;
                    if (!shapesByKey.TryGetValue(node.Key, out var shapes))
                    {
                        shapes = Shape.Capture(node);
                        shapesByKey.Add(node.Key, shapes);
                    }
                    bool connected = false;
                    foreach (Shape shape in shapes)
                        if (source.Touches(shape, network.Tolerance)) { connected = true; break; }
                    if (!connected) continue;
                    seen.Add(node.Key);
                    result.Add(new Work { Node = node, Hit = hit.Clone(), Shapes = shapes });
                    if (result.Count >= Multiplayer.MaxSections) throw new InvalidOperationException("Connected deposit exceeds the 4096-section network limit; chain not started.");
                    foreach (Shape shape in shapes) frontier.Enqueue(shape);
                }
            }
            return result;
        }

        private static void AfterArea(bool __result, Snapshot __state)
        {
            if (__result) Complete(__state);
        }
        private static void AfterVoid(Snapshot __state)
        {
            if (__state != null && __state.Source.Health <= 0) Complete(__state);
        }
        private static void Complete(Snapshot snapshot)
        {
            if (snapshot == null || !Instance) return;
            try
            {
                Instance.network.Submit(snapshot);
            }
            catch (Exception e) { Instance.Logger.LogError("Could not schedule connected mining: " + e); }
        }

        private void Update()
        {
            try { network?.Tick(); }
            catch (Exception e) { Logger.LogError("Connected Mining network update failed: " + e); }
        }

        internal void Execute(Deposit node, HitData hit)
        {
            try { applying = true; node.Break(hit); }
            finally { applying = false; }
        }
        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
            network?.Reset();
            if (Instance == this) Instance = null;
        }
    }
}
