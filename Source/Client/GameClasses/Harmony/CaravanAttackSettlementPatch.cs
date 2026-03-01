using HarmonyLib;
using OCUnion;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using Verse;

namespace RimWorldOnlineCity.GameClasses.Harmony
{
    [HarmonyPatch(typeof(CaravanArrivalAction_AttackSettlement))]
    [HarmonyPatch("Arrived")]
    internal static class CaravanArrivalAction_AttackSettlement_Arrived_Patch
    {
        private static readonly HashSet<int> ConfirmedAttackTiles = new HashSet<int>();

        [HarmonyPrefix]
        public static bool Prefix(CaravanArrivalAction_AttackSettlement __instance, Caravan caravan)
        {
            if (__instance == null || caravan == null) return true;

            var settlement = ResolveSettlement(__instance);
            if (settlement == null) return true;
            if (settlement.Faction == null || settlement.Faction.IsPlayer) return true;

            var playerFaction = Faction.OfPlayer;
            if (playerFaction == null) return true;

            if (settlement.Faction.HostileTo(playerFaction)) return true;

            if (settlement.Tile > 0 && ConfirmedAttackTiles.Remove(settlement.Tile))
            {
                EnsureHostileToPlayer(settlement.Faction, playerFaction);
                return true;
            }

            Find.TickManager?.Pause();
            var factionName = settlement.Faction.Name ?? settlement.Faction.def?.label ?? "неизвестная фракция";
            GameUtils.ShowDialodOKCancel(
                "Атака союзной фракции",
                $"Вы собираетесь атаковать \"{settlement.LabelCap}\" фракции {factionName}.{Environment.NewLine}" +
                "Это испортит отношения и переведет фракцию во враждебные. Продолжить?",
                () =>
                {
                    EnsureHostileToPlayer(settlement.Faction, playerFaction);
                    if (settlement.Tile > 0) ConfirmedAttackTiles.Add(settlement.Tile);
                    __instance.Arrived(caravan);
                },
                () => { });

            return false;
        }

        private static Settlement ResolveSettlement(CaravanArrivalAction_AttackSettlement action)
        {
            try
            {
                var type = action.GetType();
                var prop = AccessTools.Property(type, "Settlement");
                if (prop != null)
                {
                    var value = prop.GetValue(action, null) as Settlement;
                    if (value != null) return value;
                }

                var fieldNames = new[] { "settlement", "target", "targetSettlement" };
                for (int i = 0; i < fieldNames.Length; i++)
                {
                    var field = AccessTools.Field(type, fieldNames[i]);
                    if (field == null) continue;
                    var value = field.GetValue(action) as Settlement;
                    if (value != null) return value;
                }
            }
            catch (Exception ex)
            {
                Loger.Log("ResolveSettlement error: " + ex, Loger.LogLevel.ERROR);
            }

            return null;
        }

        private static void EnsureHostileToPlayer(Faction faction, Faction playerFaction)
        {
            if (faction == null || playerFaction == null || faction.IsPlayer) return;

            try
            {
                if (!faction.HostileTo(playerFaction))
                {
                    faction.TryAffectGoodwillWith(playerFaction, -200, true, true);
                }

                if (!faction.HostileTo(playerFaction))
                {
                    faction.SetRelationDirect(playerFaction, FactionRelationKind.Hostile);
                    playerFaction.SetRelationDirect(faction, FactionRelationKind.Hostile);
                }
            }
            catch (Exception ex)
            {
                Loger.Log("EnsureHostileToPlayer error: " + ex, Loger.LogLevel.ERROR);
            }
        }
    }
}
