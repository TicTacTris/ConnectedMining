using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ConnectedMining
{
    internal sealed class Deposit
    {
        internal static readonly FieldInfo Areas5 = AccessTools.Field(typeof(MineRock5), "m_hitAreas");
        internal static readonly Type AreaType = AccessTools.Inner(typeof(MineRock5), "HitArea");
        internal static readonly FieldInfo AreaCollider = AccessTools.Field(AreaType, "m_collider");
        internal static readonly FieldInfo AreaHealth = AccessTools.Field(AreaType, "m_health");
        internal static readonly FieldInfo Areas = AccessTools.Field(typeof(MineRock), "m_hitAreas");
        internal static readonly MethodInfo LoadHealth = AccessTools.Method(typeof(MineRock5), "LoadHealth");
        internal static readonly MethodInfo DamageArea = AccessTools.Method(typeof(MineRock5), "DamageArea");
        internal static readonly MethodInfo HitRock = AccessTools.Method(typeof(MineRock), "RPC_Hit");
        internal static readonly MethodInfo HitDestructible = AccessTools.Method(typeof(Destructible), "RPC_Damage");
        internal static readonly FieldInfo Destroyed = AccessTools.Field(typeof(Destructible), "m_destroyed");

        internal readonly Component Owner;
        internal readonly Collider Collider;
        internal readonly int Index;
        internal readonly ZNetView View;
        private readonly object area;
        internal readonly string Key;
        internal readonly ZDOID Id;
        internal readonly int Kind;
        internal Deposit(Component owner, Collider collider, int index, object hitArea = null)
        {
            Owner = owner; Collider = collider; Index = index; area = hitArea;
            View = owner.GetComponent<ZNetView>();
            Id = View && View.IsValid() ? View.GetZDO().m_uid : ZDOID.None;
            Key = Id + ":" + index;
            Kind = owner is MineRock5 ? 0 : owner is MineRock ? 1 : 2;
        }

        internal int Tier => Owner is MineRock5 a ? a.m_minToolTier : Owner is MineRock b ? b.m_minToolTier : ((Destructible)Owner).m_minToolTier;
        internal HitData.DamageModifiers Modifiers => Owner is MineRock5 a ? a.m_damageModifiers : Owner is MineRock b ? b.m_damageModifiers : ((Destructible)Owner).m_damages;
        internal float Health
        {
            get
            {
                if (!Owner || !View || !View.IsValid()) return 0;
                if (Owner is MineRock5) return (float)AreaHealth.GetValue(area);
                if (Owner is MineRock b) return View.GetZDO().GetFloat("Health" + Index, b.GetHealth());
                var d = (Destructible)Owner;
                if ((bool)Destroyed.GetValue(d)) return 0;
                return View.GetZDO().GetFloat(ZDOVars.s_health, d.m_health * (1 + Game.m_worldLevel * Game.instance.m_worldLevelMineHPMultiplier));
            }
        }
        internal bool Alive => Owner && Collider && Collider.enabled && Collider.gameObject.activeInHierarchy && View && View.IsValid() && Health > 0;
        internal bool Allowed(HitData hit) => Alive && hit.CheckToolTier(Tier) && CanPickaxe(Modifiers.m_pickaxe);
        internal void Refresh()
        {
            if (Owner is MineRock5 && View && View.IsValid()) LoadHealth.Invoke(Owner, null);
        }
        internal static bool CanPickaxe(HitData.DamageModifier modifier) => modifier != HitData.DamageModifier.Immune && modifier != HitData.DamageModifier.Ignore;

        internal static Deposit FromArea(Component owner, int index)
        {
            if (!owner) return null;
            var view = owner.GetComponent<ZNetView>();
            if (!view || !view.IsValid()) return null;
            if (owner is MineRock5)
            {
                var list = Areas5.GetValue(owner) as IList;
                if (list == null || index < 0 || index >= list.Count) return null;
                return new Deposit(owner, (Collider)AreaCollider.GetValue(list[index]), index, list[index]);
            }
            if (owner is MineRock)
            {
                var list = Areas.GetValue(owner) as Collider[];
                if (list == null || index < 0 || index >= list.Length) return null;
                return new Deposit(owner, list[index], index);
            }
            return null;
        }

        internal static Deposit FromCollider(Collider collider)
        {
            if (!collider || collider.isTrigger || collider is TerrainCollider ||
                collider.GetComponentInParent<Piece>() || collider.GetComponentInParent<WearNTear>() ||
                collider.GetComponentInParent<TreeBase>() || collider.GetComponentInParent<TreeLog>() ||
                collider.GetComponentInParent<Character>() || collider.GetComponentInParent<Heightmap>()) return null;
            var rock5 = collider.GetComponentInParent<MineRock5>();
            if (rock5)
            {
                var list = Areas5.GetValue(rock5) as IList;
                if (list == null) return null;
                for (int i = 0; i < list.Count; i++)
                    if ((Collider)AreaCollider.GetValue(list[i]) == collider) return new Deposit(rock5, collider, i, list[i]);
                return null;
            }
            var rock = collider.GetComponentInParent<MineRock>();
            if (rock)
            {
                var list = Areas.GetValue(rock) as Collider[];
                int i = list == null ? -1 : Array.IndexOf(list, collider);
                return i >= 0 ? new Deposit(rock, collider, i) : null;
            }
            var destructible = collider.GetComponentInParent<Destructible>();
            return IsNaturalDeposit(destructible) ? new Deposit(destructible, collider, -1) : null;
        }

        private static bool IsNaturalDeposit(Destructible d)
        {
            if (!d || !CanPickaxe(d.m_damages.m_pickaxe)) return false;
            // Large natural boulders can first change into a segmented MineRock5 prefab.
            if (d.m_spawnWhenDestroyed && (d.m_spawnWhenDestroyed.GetComponent<MineRock5>() || d.m_spawnWhenDestroyed.GetComponent<MineRock>())) return true;
            // A restrictive natural-resource name filter prevents generic destructibles/ruins from joining.
            if (!d.GetComponent<DropOnDestroyed>()) return false;
            string name = Utils.GetPrefabName(d.gameObject);
            foreach (string prefix in Plugin.Instance.EffectivePrefixes.Split(','))
                if (prefix.Trim().Length > 0 && name.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal void Break(HitData original)
        {
            Refresh();
            if (!Allowed(original) || !View.IsOwner()) return;
            HitData hit = original.Clone();
            hit.m_damage = new HitData.DamageTypes { m_pickaxe = Math.Max(1, Health) * 8 + 100 };
            hit.m_point = Collider.bounds.center;
            hit.m_hitCollider = Collider;
            hit.m_radius = 0;
            hit.m_pushForce = 0;
            hit.m_skillRaiseAmount = 0;
            // Only the deposit's own damage receiver runs. No Attack, TerrainModifier or TerrainComp calls.
            if (Owner is MineRock5) DamageArea.Invoke(Owner, new object[] { Index, hit });
            else if (Owner is MineRock) HitRock.Invoke(Owner, new object[] { 0L, hit, Index });
            else HitDestructible.Invoke(Owner, new object[] { 0L, hit });
        }
    }
}
