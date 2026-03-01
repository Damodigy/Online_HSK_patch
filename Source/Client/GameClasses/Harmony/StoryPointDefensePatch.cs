
using HarmonyLib;
using Model;
using OCUnion;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI.Group;

namespace RimWorldOnlineCity.GameClasses.Harmony
{
    [HarmonyPatch(typeof(Map))]
    [HarmonyPatch("FinalizeInit")]
    internal static class StoryPointDefensePatch
    {
        private static readonly HashSet<int> ProcessedMapIds = new HashSet<int>();
        private static readonly ServerGeneralSettings DefaultGeneralSettings = new ServerGeneralSettings().SetDefault();
        private static readonly string[] PreferredCityGenStepDefNames =
        {
            "ScatterRuinsSimple",
            "ScatterRuins",
            "StandartLostCityLGE",
            "InfestedLostCityLGE",
            "ToxicLostCityLGE",
            "AncientUrbanRuins"
        };

        [HarmonyPostfix]
        public static void Postfix(Map __instance)
        {
            if (__instance == null) return;
            if (__instance.uniqueID <= 0) return;
            if (!ProcessedMapIds.Add(__instance.uniqueID)) return;

            var settlement = __instance.Parent as Settlement;
            if (settlement == null) return;
            if (settlement.Tile <= 0) return;
            if (settlement.Faction == null || settlement.Faction.IsPlayer) return;

            var descriptor = UpdateWorldController.GetOnlineWorldDescriptorByTile(settlement.Tile);
            if (descriptor == null || !descriptor.ServerGenerated) return;

            var playerFaction = Faction.OfPlayer;
            if (playerFaction == null) return;
            var isHostile = settlement.Faction.HostileTo(playerFaction);
            var factionTechLevel = ResolveFactionTechLevel(settlement.Faction);

            var defenseProfile = BuildDefenseProfile(descriptor);
            if (isHostile && defenseProfile.RaidPoints > 0f)
            {
                var totalWaves = 1 + Math.Max(0, defenseProfile.ExtraRaidWaves);
                var siegeWaveLimit = GetPreferredSiegeWaveCount(descriptor);
                for (int waveIndex = 0; waveIndex < totalWaves; waveIndex++)
                {
                    var points = CalculateRaidWavePoints(defenseProfile.RaidPoints, waveIndex);
                    var preferSiege = waveIndex < siegeWaveLimit;
                    SpawnRaidWave(__instance, settlement.Faction, points, preferSiege);
                }
            }

            if (isHostile && defenseProfile.Traps > 0)
            {
                SpawnDefenses(__instance, settlement.Faction, GetTrapDefs(factionTechLevel), defenseProfile.Traps, nearCenter: false);
            }
            if (isHostile)
            {
                SpawnStoryTypeStructures(__instance, settlement.Faction, descriptor, defenseProfile, factionTechLevel);
            }
            if (isHostile)
            {
                EnsureHostilePresence(__instance, settlement.Faction, defenseProfile);
            }

            var lootProfile = BuildLootProfile(descriptor);
            SpawnLootCaches(__instance, descriptor, lootProfile, factionTechLevel);
        }

        private static DefenseProfile BuildDefenseProfile(WorldObjectOnline descriptor)
        {
            var storyType = (descriptor?.StoryType ?? string.Empty).Trim().ToLowerInvariant();
            var level = Math.Max(1, descriptor?.StoryLevel ?? 1);

            switch (storyType)
            {
                case "camp":
                    return new DefenseProfile(160f, 0, 1, 1, 0);

                case "trade_camp":
                    return new DefenseProfile(220f, 0, 1, 2, 0);

                case "outpost":
                    return level >= 2
                        ? new DefenseProfile(420f, 1, 2, 4, 0)
                        : new DefenseProfile(320f, 0, 2, 3, 0);

                case "military_base":
                    return new DefenseProfile(1450f, 3, 8, 14, 10);

                case "mine":
                    return new DefenseProfile(430f, 1, 2, 5, 1);

                case "farm":
                    return new DefenseProfile(300f, 0, 1, 3, 0);

                case "industrial_site":
                    return new DefenseProfile(650f, 1, 3, 6, 1);

                case "research_hub":
                    return new DefenseProfile(780f, 1, 3, 7, 1);

                case "logistics_hub":
                    return new DefenseProfile(560f, 1, 2, 5, 1);

                default:
                    if (level >= 3) return new DefenseProfile(1200f, 3, 5, 12, 2);
                    if (level == 2) return new DefenseProfile(760f, 1, 4, 8, 1);
                    return new DefenseProfile(520f, 0, 3, 5, 0);
            }
        }

        private static LootProfile BuildLootProfile(WorldObjectOnline descriptor)
        {
            var storyType = (descriptor?.StoryType ?? string.Empty).Trim().ToLowerInvariant();
            var level = Math.Max(1, descriptor?.StoryLevel ?? 1);
            var levelBonusPercent = ReadLootSetting(s => s.StoryPointLootLevelMarketBonusPercent, 20, 1, 300);

            if (storyType == "trade_camp")
            {
                return new LootProfile(
                    cacheCount: ReadLootSetting(s => s.StoryPointLootTradeCampCacheCount, 5, 1, 100),
                    itemsPerCacheMin: ReadLootSetting(s => s.StoryPointLootTradeCampItemsMin, 4, 1, 100),
                    itemsPerCacheMax: ReadLootSetting(s => s.StoryPointLootTradeCampItemsMax, 8, 1, 150),
                    theme: LootTheme.Trade,
                    marketValueMin: ReadLootSetting(s => s.StoryPointLootTradeCampMarketMin, 450, 50, 100000),
                    marketValueMax: ReadLootSetting(s => s.StoryPointLootTradeCampMarketMax, 1200, 50, 200000),
                    levelMarketBonusPercent: levelBonusPercent,
                    techHint: TechLevel.Industrial,
                    traderHints: new[] { "bulk", "goods", "exotic", "settlement" });
            }

            if (storyType == "outpost")
            {
                return new LootProfile(
                    cacheCount: ReadLootSetting(s => s.StoryPointLootOutpostCacheCount, 6, 1, 100),
                    itemsPerCacheMin: ReadLootSetting(s => s.StoryPointLootOutpostItemsMin, 4, 1, 100),
                    itemsPerCacheMax: ReadLootSetting(s => s.StoryPointLootOutpostItemsMax, 8, 1, 150),
                    theme: LootTheme.Outpost,
                    marketValueMin: ReadLootSetting(s => s.StoryPointLootOutpostMarketMin, 700, 50, 100000),
                    marketValueMax: ReadLootSetting(s => s.StoryPointLootOutpostMarketMax, 1700, 50, 200000),
                    levelMarketBonusPercent: levelBonusPercent,
                    techHint: TechLevel.Industrial,
                    traderHints: new[] { "combat", "weapon", "arms", "military", "pirate" });
            }
            if (storyType == "military_base")
            {
                return new LootProfile(
                    cacheCount: Math.Max(4, ReadLootSetting(s => s.StoryPointLootOutpostCacheCount, 6, 1, 100)),
                    itemsPerCacheMin: 4,
                    itemsPerCacheMax: 8,
                    theme: LootTheme.Military,
                    marketValueMin: 1000,
                    marketValueMax: 2800,
                    levelMarketBonusPercent: levelBonusPercent,
                    techHint: TechLevel.Industrial,
                    traderHints: new[] { "combat", "weapon", "arms", "military", "pirate" });
            }
            if (storyType == "mine")
            {
                return new LootProfile(
                    cacheCount: 5,
                    itemsPerCacheMin: 4,
                    itemsPerCacheMax: 8,
                    theme: LootTheme.Mine,
                    marketValueMin: 650,
                    marketValueMax: 1900,
                    levelMarketBonusPercent: levelBonusPercent,
                    techHint: TechLevel.Industrial,
                    traderHints: new[] { "bulk", "industrial", "minerals", "resource" });
            }
            if (storyType == "farm")
            {
                return new LootProfile(
                    cacheCount: 5,
                    itemsPerCacheMin: 4,
                    itemsPerCacheMax: 8,
                    theme: LootTheme.Farm,
                    marketValueMin: 500,
                    marketValueMax: 1500,
                    levelMarketBonusPercent: levelBonusPercent,
                    techHint: null,
                    traderHints: new[] { "food", "plant", "bulk", "settlement" });
            }
            if (storyType == "industrial_site")
            {
                return new LootProfile(
                    cacheCount: 5,
                    itemsPerCacheMin: 4,
                    itemsPerCacheMax: 8,
                    theme: LootTheme.Industry,
                    marketValueMin: 900,
                    marketValueMax: 2400,
                    levelMarketBonusPercent: levelBonusPercent,
                    techHint: TechLevel.Industrial,
                    traderHints: new[] { "industrial", "bulk", "trade", "components" });
            }
            if (storyType == "research_hub")
            {
                return new LootProfile(
                    cacheCount: 5,
                    itemsPerCacheMin: 3,
                    itemsPerCacheMax: 7,
                    theme: LootTheme.Research,
                    marketValueMin: 1000,
                    marketValueMax: 2800,
                    levelMarketBonusPercent: levelBonusPercent,
                    techHint: TechLevel.Spacer,
                    traderHints: new[] { "exotic", "tech", "industrial", "orbital" });
            }
            if (storyType == "logistics_hub")
            {
                return new LootProfile(
                    cacheCount: 5,
                    itemsPerCacheMin: 4,
                    itemsPerCacheMax: 8,
                    theme: LootTheme.Logistics,
                    marketValueMin: 800,
                    marketValueMax: 2200,
                    levelMarketBonusPercent: levelBonusPercent,
                    techHint: TechLevel.Industrial,
                    traderHints: new[] { "bulk", "goods", "trade", "caravan" });
            }

            if (storyType == "city" || (storyType == "settlement" && level >= 3))
            {
                return new LootProfile(
                    cacheCount: ReadLootSetting(s => s.StoryPointLootCityCacheCount, 6, 1, 100),
                    itemsPerCacheMin: ReadLootSetting(s => s.StoryPointLootCityItemsMin, 4, 1, 100),
                    itemsPerCacheMax: ReadLootSetting(s => s.StoryPointLootCityItemsMax, 9, 1, 150),
                    theme: LootTheme.City,
                    marketValueMin: ReadLootSetting(s => s.StoryPointLootCityMarketMin, 900, 50, 200000),
                    marketValueMax: ReadLootSetting(s => s.StoryPointLootCityMarketMax, 2600, 50, 300000),
                    levelMarketBonusPercent: levelBonusPercent,
                    techHint: TechLevel.Spacer,
                    traderHints: new[] { "exotic", "tech", "industrial", "orbital" });
            }

            if (storyType == "settlement")
            {
                return new LootProfile(
                    cacheCount: ReadLootSetting(s => s.StoryPointLootSettlementCacheCount, 5, 1, 100),
                    itemsPerCacheMin: ReadLootSetting(s => s.StoryPointLootSettlementItemsMin, 3, 1, 100),
                    itemsPerCacheMax: ReadLootSetting(s => s.StoryPointLootSettlementItemsMax, 7, 1, 150),
                    theme: LootTheme.Settlement,
                    marketValueMin: ReadLootSetting(s => s.StoryPointLootSettlementMarketMin, 500, 50, 100000),
                    marketValueMax: ReadLootSetting(s => s.StoryPointLootSettlementMarketMax, 1400, 50, 200000),
                    levelMarketBonusPercent: levelBonusPercent,
                    techHint: level >= 2 ? (TechLevel?)TechLevel.Industrial : null,
                    traderHints: new[] { "bulk", "food", "plant", "settlement" });
            }

            return new LootProfile(
                cacheCount: ReadLootSetting(s => s.StoryPointLootGenericCacheCount, 3, 1, 100),
                itemsPerCacheMin: ReadLootSetting(s => s.StoryPointLootGenericItemsMin, 3, 1, 100),
                itemsPerCacheMax: ReadLootSetting(s => s.StoryPointLootGenericItemsMax, 5, 1, 150),
                theme: LootTheme.Generic,
                marketValueMin: ReadLootSetting(s => s.StoryPointLootGenericMarketMin, 350, 50, 100000),
                marketValueMax: ReadLootSetting(s => s.StoryPointLootGenericMarketMax, 1000, 50, 200000),
                levelMarketBonusPercent: levelBonusPercent,
                techHint: null,
                traderHints: null);
        }

        private static float CalculateRaidWavePoints(float basePoints, int waveIndex)
        {
            if (waveIndex <= 0) return basePoints;
            var extraIndex = waveIndex - 1;
            return Math.Max(80f, basePoints * (0.6f + 0.2f * extraIndex));
        }

        private static void SpawnRaidWave(Map map, Faction faction, float points, bool preferSiege)
        {
            if (map == null || faction == null || points <= 0f) return;
            try
            {
                var parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                parms.forced = true;
                parms.target = map;
                parms.faction = faction;
                parms.points = points;
                var immediate = DefDatabase<RaidStrategyDef>.GetNamedSilentFail("ImmediateAttack");
                var siege = DefDatabase<RaidStrategyDef>.GetNamedSilentFail("Siege");
                parms.raidStrategy = preferSiege ? (siege ?? immediate) : immediate;

                var incident = IncidentDefOf.RaidEnemy;
                var executed = incident?.Worker?.TryExecute(parms) ?? false;
                if (!executed && preferSiege && immediate != null)
                {
                    parms.raidStrategy = immediate;
                    incident?.Worker?.TryExecute(parms);
                }
            }
            catch (Exception ex)
            {
                Loger.Log("Ошибка StoryPointDefensePatch при спавне волны: " + ex, Loger.LogLevel.ERROR);
            }
        }

        private static int GetPreferredSiegeWaveCount(WorldObjectOnline descriptor)
        {
            if (descriptor == null) return 0;

            var storyType = (descriptor.StoryType ?? string.Empty).Trim().ToLowerInvariant();
            var level = Math.Max(1, descriptor.StoryLevel);
            var name = (descriptor.Name ?? string.Empty).Trim().ToLowerInvariant();

            var isCity = storyType == "city"
                || (storyType == "settlement" && level >= 3)
                || name.Contains("город")
                || name.Contains("city");
            if (isCity)
            {
                return 2;
            }

            var isSettlement = storyType == "settlement"
                || name.Contains("поселени")
                || name.Contains("settlement");
            if (storyType == "outpost" || isSettlement)
            {
                return 1;
            }

            return 0;
        }

        private static void SpawnDefenses(Map map, Faction faction, List<ThingDef> defs, int count, bool nearCenter)
        {
            if (map == null || faction == null || count <= 0) return;
            if (defs == null || defs.Count == 0) return;

            var spawned = 0;
            for (int i = 0; i < count; i++)
            {
                if (!TryFindCell(map, nearCenter, out var cell)) continue;

                var idx = (spawned + Rand.Range(0, defs.Count)) % defs.Count;
                var def = defs[idx];
                if (def == null) continue;

                try
                {
                    var stuff = def.MadeFromStuff ? PickStuff(def) : null;
                    var thing = ThingMaker.MakeThing(def, stuff);
                    if (thing == null) continue;
                    thing.SetFaction(faction);
                    GenSpawn.Spawn(thing, cell, map, WipeMode.Vanish);
                    spawned++;
                }
                catch (Exception ex)
                {
                    Loger.Log("Ошибка StoryPointDefensePatch при спавне обороны: " + ex, Loger.LogLevel.ERROR);
                }
            }
        }

        private static void SpawnStoryTypeStructures(Map map
            , Faction faction
            , WorldObjectOnline descriptor
            , DefenseProfile profile
            , TechLevel maxTechLevel)
        {
            if (map == null || faction == null || descriptor == null) return;
            var storyType = (descriptor.StoryType ?? string.Empty).Trim().ToLowerInvariant();
            if (storyType.Length == 0) return;

            if (IsCityDescriptor(descriptor))
            {
                TryApplyCityAssetGeneration(map);
                return;
            }

            if (!TryFindLayoutCenter(map, out var center))
            {
                SpawnLegacyStoryTypeStructures(map, faction, storyType, profile, maxTechLevel);
                return;
            }

            switch (storyType)
            {
                case "military_base":
                    SpawnMilitaryBaseLayout(map, faction, center, profile, maxTechLevel);
                    break;
                case "mine":
                    SpawnMineLayout(map, faction, center, maxTechLevel);
                    break;
                case "farm":
                    SpawnFarmLayout(map, faction, center, maxTechLevel);
                    break;
                case "industrial_site":
                    SpawnIndustrialLayout(map, faction, center, maxTechLevel);
                    break;
                case "research_hub":
                    SpawnResearchLayout(map, faction, center, maxTechLevel);
                    break;
                case "logistics_hub":
                    SpawnLogisticsLayout(map, faction, center, maxTechLevel);
                    break;
            }
        }

        private static void SpawnLegacyStoryTypeStructures(Map map
            , Faction faction
            , string storyType
            , DefenseProfile profile
            , TechLevel maxTechLevel)
        {
            switch (storyType)
            {
                case "military_base":
                    SpawnDefenses(map, faction, GetStructureDefs(new[] { "barricade", "sandbag", "mortar", "autocannon", "turret" }, includeTurrets: true, maxTechLevel: maxTechLevel), Math.Max(6, profile.LightTurrets + profile.HeavyTurrets), nearCenter: true);
                    break;
                case "mine":
                    SpawnDefenses(map, faction, GetStructureDefs(new[] { "deepdrill", "scanner", "smelter", "generator", "battery" }, includeTurrets: false, maxTechLevel: maxTechLevel), 5, nearCenter: true);
                    break;
                case "farm":
                    SpawnDefenses(map, faction, GetStructureDefs(new[] { "sunlamp", "hydroponic", "stove", "shelf", "cooler" }, includeTurrets: false, maxTechLevel: maxTechLevel), 5, nearCenter: true);
                    break;
                case "industrial_site":
                    SpawnDefenses(map, faction, GetStructureDefs(new[] { "fabrication", "machining", "smelter", "generator", "battery", "toolcabinet" }, includeTurrets: false, maxTechLevel: maxTechLevel), 6, nearCenter: true);
                    break;
                case "research_hub":
                    SpawnDefenses(map, faction, GetStructureDefs(new[] { "research", "multianalyzer", "comms", "hi-tech", "server", "console" }, includeTurrets: false, maxTechLevel: maxTechLevel), 5, nearCenter: true);
                    break;
                case "logistics_hub":
                    SpawnDefenses(map, faction, GetStructureDefs(new[] { "orbital", "beacon", "transportpod", "launcher", "shelf", "drop" }, includeTurrets: false, maxTechLevel: maxTechLevel), 5, nearCenter: true);
                    break;
            }
        }

        private static void SpawnMilitaryBaseLayout(Map map, Faction faction, IntVec3 center, DefenseProfile profile, TechLevel maxTechLevel)
        {
            var barricades = GetStructureDefs(new[] { "barricade", "sandbag", "wall", "fence" }, includeTurrets: false, maxTechLevel: maxTechLevel);
            var support = GetStructureDefs(new[] { "mortar", "comms", "generator", "battery", "beacon", "bunker" }, includeTurrets: false, maxTechLevel: maxTechLevel);
            var lightTurrets = GetTurretDefs(heavy: false, maxTechLevel: maxTechLevel);
            var heavyTurrets = GetTurretDefs(heavy: true, maxTechLevel: maxTechLevel);
            var traps = GetTrapDefs(maxTechLevel);

            SpawnPerimeterStructures(map, faction, barricades, center, radius: 9, count: 34);
            SpawnPerimeterStructures(map, faction, lightTurrets, center, radius: 6, count: Math.Max(4, profile.LightTurrets / 2));
            SpawnPerimeterStructures(map, faction, heavyTurrets, center, radius: 8, count: Math.Max(5, profile.HeavyTurrets / 2));
            SpawnPerimeterStructures(map, faction, traps, center, radius: 10, count: Math.Max(8, profile.Traps));
            SpawnAroundAnchor(map, faction, support, center, count: 8, minRadius: 2, maxRadius: 6, preferOuterRing: false);
            SpawnFallbackHostileGroup(map, faction, Math.Max(280f, profile.RaidPoints * 0.45f), 7);
        }

        private static void SpawnFarmLayout(Map map, Faction faction, IntVec3 center, TechLevel maxTechLevel)
        {
            var farmBuildings = GetStructureDefs(new[] { "sunlamp", "hydroponic", "grow", "greenhouse", "fertil", "planter" }, includeTurrets: false, maxTechLevel: maxTechLevel);
            var serviceBuildings = GetStructureDefs(new[] { "stove", "cooler", "shelf", "wind", "solar", "battery", "generator", "barn", "pen" }, includeTurrets: false, maxTechLevel: maxTechLevel);
            var perimeter = GetStructureDefs(new[] { "fence", "wall", "sandbag" }, includeTurrets: false, maxTechLevel: maxTechLevel);

            SpawnPerimeterStructures(map, faction, perimeter, center, radius: 10, count: 24);
            SpawnAroundAnchor(map, faction, farmBuildings, center, count: 10, minRadius: 2, maxRadius: 10, preferOuterRing: false);
            SpawnAroundAnchor(map, faction, serviceBuildings, center, count: 8, minRadius: 1, maxRadius: 8, preferOuterRing: false);

            SpawnCropPatches(map, center, patchCount: 4, minHalfSize: 3, maxHalfSize: 5);
            SpawnThemedResourceStacks(map, center
                , new[] { "hay", "rice", "corn", "potato", "meal", "milk", "egg" }
                , minStacks: 6
                , maxStacks: 10
                , minStackCount: 25
                , maxStackCount: 90
                , maxTechLevel: maxTechLevel);
        }

        private static void SpawnMineLayout(Map map, Faction faction, IntVec3 center, TechLevel maxTechLevel)
        {
            var mineBuildings = GetStructureDefs(new[] { "deepdrill", "scanner", "smelter", "generator", "battery", "drill", "extractor" }, includeTurrets: false, maxTechLevel: maxTechLevel);
            var perimeter = GetStructureDefs(new[] { "wall", "fence", "barricade", "sandbag" }, includeTurrets: false, maxTechLevel: maxTechLevel);

            SpawnPerimeterStructures(map, faction, perimeter, center, radius: 9, count: 18);
            SpawnAroundAnchor(map, faction, mineBuildings, center, count: 10, minRadius: 1, maxRadius: 8, preferOuterRing: false);
            SpawnThemedResourceStacks(map, center
                , new[] { "steel", "plasteel", "uranium", "component", "gold", "silver", "jade", "chunk", "ore" }
                , minStacks: 8
                , maxStacks: 14
                , minStackCount: 18
                , maxStackCount: 75
                , maxTechLevel: maxTechLevel);
        }

        private static void SpawnIndustrialLayout(Map map, Faction faction, IntVec3 center, TechLevel maxTechLevel)
        {
            var buildings = GetStructureDefs(new[] { "fabrication", "machining", "smelter", "tool", "generator", "battery", "hopper", "bench" }, includeTurrets: false, maxTechLevel: maxTechLevel);
            SpawnAroundAnchor(map, faction, buildings, center, count: 12, minRadius: 1, maxRadius: 9, preferOuterRing: false);
            SpawnThemedResourceStacks(map, center
                , new[] { "component", "steel", "plasteel", "chemfuel", "cloth", "advancedcomponent" }
                , minStacks: 6
                , maxStacks: 12
                , minStackCount: 12
                , maxStackCount: 60
                , maxTechLevel: maxTechLevel);
        }

        private static void SpawnResearchLayout(Map map, Faction faction, IntVec3 center, TechLevel maxTechLevel)
        {
            var buildings = GetStructureDefs(new[] { "research", "multianalyzer", "hi-tech", "comms", "console", "server", "battery" }, includeTurrets: false, maxTechLevel: maxTechLevel);
            SpawnAroundAnchor(map, faction, buildings, center, count: 11, minRadius: 1, maxRadius: 8, preferOuterRing: false);
            SpawnThemedResourceStacks(map, center
                , new[] { "component", "advancedcomponent", "neutroamine", "medicine", "glitter", "techprof" }
                , minStacks: 5
                , maxStacks: 10
                , minStackCount: 6
                , maxStackCount: 40
                , maxTechLevel: maxTechLevel);
        }

        private static void SpawnLogisticsLayout(Map map, Faction faction, IntVec3 center, TechLevel maxTechLevel)
        {
            var buildings = GetStructureDefs(new[] { "transportpod", "launcher", "beacon", "shelf", "drop", "loading", "platform" }, includeTurrets: false, maxTechLevel: maxTechLevel);
            SpawnAroundAnchor(map, faction, buildings, center, count: 10, minRadius: 1, maxRadius: 9, preferOuterRing: false);
            SpawnThemedResourceStacks(map, center
                , new[] { "packaged", "survivalmeal", "medicine", "cloth", "leather", "steel", "wood", "smokeleaf" }
                , minStacks: 7
                , maxStacks: 14
                , minStackCount: 10
                , maxStackCount: 70
                , maxTechLevel: maxTechLevel);
        }

        private static bool IsCityDescriptor(WorldObjectOnline descriptor)
        {
            if (descriptor == null) return false;
            var storyType = (descriptor.StoryType ?? string.Empty).Trim().ToLowerInvariant();
            var level = Math.Max(1, descriptor.StoryLevel);
            var name = (descriptor.Name ?? string.Empty).Trim().ToLowerInvariant();

            if (storyType == "city") return true;
            if (storyType == "settlement" && level >= 3) return true;
            if (name.Contains("город") || name.Contains("city")) return true;
            return false;
        }

        private static void TryApplyCityAssetGeneration(Map map)
        {
            if (map == null) return;

            if (TryRunPreferredCityGenStep(map)) return;
            if (TryRunFallbackCityGenStep(map)) return;

            Loger.Log("StoryPointDefensePatch: не найден подходящий vanilla/map-asset GenStep для генерации руин города.", Loger.LogLevel.WARNING);
        }

        private static bool TryRunPreferredCityGenStep(Map map)
        {
            for (int i = 0; i < PreferredCityGenStepDefNames.Length; i++)
            {
                var def = DefDatabase<GenStepDef>.GetNamedSilentFail(PreferredCityGenStepDefNames[i]);
                if (TryRunCityGenStep(map, def)) return true;
            }

            return false;
        }

        private static bool TryRunFallbackCityGenStep(Map map)
        {
            var candidates = DefDatabase<GenStepDef>.AllDefsListForReading
                .Where(IsCityAssetGenStep)
                .OrderByDescending(GetCityGenStepWeight)
                .ThenBy(def => def?.defName ?? string.Empty)
                .ToList();

            for (int i = 0; i < candidates.Count; i++)
            {
                if (TryRunCityGenStep(map, candidates[i])) return true;
            }

            return false;
        }

        private static bool IsCityAssetGenStep(GenStepDef def)
        {
            if (def?.genStep == null) return false;

            var defName = (def.defName ?? string.Empty).ToLowerInvariant();
            var className = def.genStep.GetType().Name.ToLowerInvariant();

            var hasCityToken =
                defName.Contains("ruin")
                || defName.Contains("city")
                || defName.Contains("lost")
                || defName.Contains("ancient");
            if (!hasCityToken) return false;

            if (defName.Contains("hive")
                || defName.Contains("insect")
                || defName.Contains("shipcore")
                || defName.Contains("prison")
                || defName.Contains("ambrosia"))
            {
                return false;
            }

            if (className.Contains("ruin")
                || className.Contains("city")
                || className.Contains("ancient")
                || className.Contains("urban"))
            {
                return true;
            }

            return defName.Contains("scatterruinsimple")
                || defName.Contains("scatterruins")
                || defName.Contains("lostcity");
        }

        private static int GetCityGenStepWeight(GenStepDef def)
        {
            if (def?.genStep == null) return 0;

            var defName = (def.defName ?? string.Empty).ToLowerInvariant();
            var className = def.genStep.GetType().Name.ToLowerInvariant();
            var score = 0;

            if (defName.Contains("scatterruinsimple")) score += 1200;
            if (defName.Contains("scatterruins")) score += 900;
            if (defName.Contains("standartlostcitylge")) score += 850;
            if (defName.Contains("lostcity")) score += 700;
            if (defName.Contains("city")) score += 320;
            if (defName.Contains("ruin")) score += 300;
            if (defName.Contains("ancient")) score += 180;

            if (className.Contains("ruin")) score += 170;
            if (className.Contains("city")) score += 140;
            if (className.Contains("ancient")) score += 80;

            return score;
        }

        private static bool TryRunCityGenStep(Map map, GenStepDef def)
        {
            if (map == null || def?.genStep == null) return false;

            try
            {
                var parms = new GenStepParams();
                def.genStep.Generate(map, parms);
                Loger.Log($"StoryPointDefensePatch: city карта сгенерирована через GenStep '{def.defName}'.", Loger.LogLevel.DEBUG);
                return true;
            }
            catch (Exception ex)
            {
                Loger.Log($"StoryPointDefensePatch: ошибка GenStep '{def.defName}' при генерации city: {ex}", Loger.LogLevel.WARNING);
                return false;
            }
        }

        private static bool TryFindLayoutCenter(Map map, out IntVec3 center)
        {
            center = IntVec3.Invalid;
            if (map == null) return false;
            if (TryFindCell(map, nearCenter: true, out center)) return true;
            if (TryFindCell(map, nearCenter: false, out center)) return true;
            return false;
        }

        private static int SpawnAroundAnchor(Map map
            , Faction faction
            , List<ThingDef> defs
            , IntVec3 anchor
            , int count
            , int minRadius
            , int maxRadius
            , bool preferOuterRing)
        {
            if (map == null || faction == null || defs == null || defs.Count == 0) return 0;
            if (count <= 0) return 0;

            var spawned = 0;
            for (int i = 0; i < count; i++)
            {
                if (!TryFindCellAround(map, anchor, minRadius, maxRadius, preferOuterRing, out var cell)) continue;
                var def = defs[Rand.Range(0, defs.Count)];
                if (TrySpawnStructure(map, faction, def, cell))
                {
                    spawned++;
                }
            }

            return spawned;
        }

        private static int SpawnPerimeterStructures(Map map
            , Faction faction
            , List<ThingDef> defs
            , IntVec3 center
            , int radius
            , int count)
        {
            if (map == null || faction == null || defs == null || defs.Count == 0) return 0;
            if (radius <= 0 || count <= 0) return 0;

            var cells = BuildPerimeterCells(map, center, radius);
            if (cells.Count == 0) return 0;

            var spawned = 0;
            var attempts = Math.Min(cells.Count * 2, count * 5);
            while (attempts-- > 0 && spawned < count && cells.Count > 0)
            {
                var index = Rand.Range(0, cells.Count);
                var cell = cells[index];
                cells.RemoveAt(index);

                var def = defs[Rand.Range(0, defs.Count)];
                if (!TrySpawnStructure(map, faction, def, cell)) continue;
                spawned++;
            }

            return spawned;
        }

        private static List<IntVec3> BuildPerimeterCells(Map map, IntVec3 center, int radius)
        {
            var result = new List<IntVec3>();
            if (map == null || radius <= 0) return result;

            var rect = CellRect.CenteredOn(center, radius).ClipInsideMap(map);
            foreach (var cell in rect)
            {
                if (!cell.InBounds(map)) continue;

                var dx = Math.Abs(cell.x - center.x);
                var dz = Math.Abs(cell.z - center.z);
                if (Math.Max(dx, dz) != radius) continue;
                if (!CanSpawnStructureAt(map, cell)) continue;

                result.Add(cell);
            }

            return result;
        }

        private static bool TryFindCellAround(Map map
            , IntVec3 anchor
            , int minRadius
            , int maxRadius
            , bool preferOuterRing
            , out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            if (map == null) return false;
            if (maxRadius < minRadius) maxRadius = minRadius;

            for (int i = 0; i < 160; i++)
            {
                var dx = Rand.RangeInclusive(-maxRadius, maxRadius);
                var dz = Rand.RangeInclusive(-maxRadius, maxRadius);
                var dist = Math.Abs(dx) + Math.Abs(dz);
                if (dist < minRadius || dist > maxRadius * 2) continue;
                if (preferOuterRing && dist < Math.Max(minRadius + 1, maxRadius)) continue;

                var candidate = new IntVec3(anchor.x + dx, 0, anchor.z + dz);
                if (!CanSpawnStructureAt(map, candidate)) continue;

                cell = candidate;
                return true;
            }

            return false;
        }

        private static bool CanSpawnStructureAt(Map map, IntVec3 cell)
        {
            if (map == null) return false;
            if (!cell.InBounds(map)) return false;
            if (!cell.Standable(map)) return false;
            if (cell.GetEdifice(map) != null) return false;
            if (cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Building)) return false;
            return true;
        }

        private static bool TrySpawnStructure(Map map, Faction faction, ThingDef def, IntVec3 cell)
        {
            if (map == null || def == null || !cell.InBounds(map)) return false;
            if (!CanSpawnStructureAt(map, cell)) return false;

            try
            {
                var stuff = def.MadeFromStuff ? PickStuff(def) : null;
                var thing = ThingMaker.MakeThing(def, stuff);
                if (thing == null) return false;

                if (thing.def.category == ThingCategory.Building && faction != null)
                {
                    thing.SetFaction(faction);
                }

                GenSpawn.Spawn(thing, cell, map, WipeMode.Vanish);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SpawnCropPatches(Map map, IntVec3 center, int patchCount, int minHalfSize, int maxHalfSize)
        {
            if (map == null || patchCount <= 0) return;
            var cropDefs = GetCropDefs();
            if (cropDefs.Count == 0) return;

            for (int patch = 0; patch < patchCount; patch++)
            {
                if (!TryFindCellAround(map, center, minRadius: 2, maxRadius: 14, preferOuterRing: false, out var patchCenter))
                {
                    continue;
                }

                var halfSize = Rand.RangeInclusive(Math.Max(1, minHalfSize), Math.Max(minHalfSize, maxHalfSize));
                var patchRect = CellRect.CenteredOn(patchCenter, halfSize).ClipInsideMap(map);
                var primaryCrop = cropDefs[Rand.Range(0, cropDefs.Count)];
                var secondaryCrop = cropDefs.Count > 1 ? cropDefs[Rand.Range(0, cropDefs.Count)] : primaryCrop;

                foreach (var cell in patchRect)
                {
                    if (Rand.Value > 0.82f) continue;
                    if (!CanSpawnPlantAt(map, cell)) continue;

                    var cropDef = Rand.Value < 0.75f ? primaryCrop : secondaryCrop;
                    TrySpawnPlant(map, cropDef, cell, Rand.Range(0.42f, 0.99f));
                }
            }
        }

        private static List<ThingDef> GetCropDefs()
        {
            return DefDatabase<ThingDef>.AllDefsListForReading
                .Where(def => def != null
                    && def.category == ThingCategory.Plant
                    && def.plant != null
                    && !def.plant.IsTree
                    && def.plant.Sowable
                    && def.plant.harvestedThingDef != null)
                .OrderBy(def => def.BaseMarketValue)
                .ToList();
        }

        private static bool CanSpawnPlantAt(Map map, IntVec3 cell)
        {
            if (map == null) return false;
            if (!cell.InBounds(map)) return false;
            if (cell.GetEdifice(map) != null) return false;
            if (!cell.Walkable(map)) return false;
            if (map.fertilityGrid != null && map.fertilityGrid.FertilityAt(cell) < 0.35f) return false;
            if (cell.GetThingList(map).Any(t => t is Plant)) return false;
            return true;
        }

        private static bool TrySpawnPlant(Map map, ThingDef plantDef, IntVec3 cell, float growth)
        {
            if (map == null || plantDef == null) return false;
            if (!CanSpawnPlantAt(map, cell)) return false;

            try
            {
                var plant = ThingMaker.MakeThing(plantDef) as Plant;
                if (plant == null) return false;

                plant.Growth = Math.Max(0.05f, Math.Min(1f, growth));
                GenSpawn.Spawn(plant, cell, map, WipeMode.Vanish);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SpawnThemedResourceStacks(Map map
            , IntVec3 center
            , IEnumerable<string> tokens
            , int minStacks
            , int maxStacks
            , int minStackCount
            , int maxStackCount
            , TechLevel maxTechLevel)
        {
            if (map == null || tokens == null) return;

            var tokenList = tokens
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (tokenList.Count == 0) return;

            var defs = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(def => def != null
                    && def.category == ThingCategory.Item
                    && def.stackLimit > 1
                    && IsTechAllowed(def, maxTechLevel)
                    && def.BaseMarketValue > 0f
                    && tokenList.Any(token => HasToken(def.defName, token) || HasToken(def.label, token)))
                .OrderBy(def => def.BaseMarketValue)
                .ToList();
            if (defs.Count == 0) return;

            var stackCount = Rand.RangeInclusive(Math.Max(0, minStacks), Math.Max(minStacks, maxStacks));
            for (int i = 0; i < stackCount; i++)
            {
                if (!TryFindCellAround(map, center, minRadius: 1, maxRadius: 12, preferOuterRing: false, out var cell)) continue;
                var def = defs[Rand.Range(0, defs.Count)];

                try
                {
                    var thing = ThingMaker.MakeThing(def);
                    if (thing == null) continue;
                    var desired = Rand.RangeInclusive(Math.Max(1, minStackCount), Math.Max(minStackCount, maxStackCount));
                    thing.stackCount = Math.Min(thing.def.stackLimit, Math.Max(1, desired));
                    GenDrop.TryDropSpawn(thing, cell, map, ThingPlaceMode.Near, out _, null);
                }
                catch
                {
                    // ignore spawn errors for invalid defs/terrain
                }
            }
        }

        private static List<ThingDef> GetStructureDefs(IEnumerable<string> tokens, bool includeTurrets, TechLevel maxTechLevel)
        {
            if (tokens == null) return new List<ThingDef>();
            var tokenList = tokens
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (tokenList.Count == 0) return new List<ThingDef>();

            var defs = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(def => def != null
                    && def.category == ThingCategory.Building
                    && def.building != null
                    && IsTechAllowed(def, maxTechLevel)
                    && (includeTurrets || !IsTurretDef(def))
                    && !IsTrapBuilding(def)
                    && tokenList.Any(token => HasToken(def.defName, token) || HasToken(def.label, token)))
                .OrderBy(def => def.BaseMarketValue)
                .ToList();
            return defs;
        }

        private static bool IsTrapBuilding(ThingDef def)
        {
            if (def == null) return false;
            if (def.thingClass != null && typeof(Building_Trap).IsAssignableFrom(def.thingClass)) return true;
            return HasToken(def.defName, "trap") || HasToken(def.label, "trap");
        }

        private static void EnsureHostilePresence(Map map, Faction faction, DefenseProfile profile)
        {
            if (map == null || faction == null) return;

            var playerFaction = Faction.OfPlayer;
            if (playerFaction == null) return;
            if (!faction.HostileTo(playerFaction)) return;

            var hostileCount = map.mapPawns?.AllPawnsSpawned?.Count(p =>
                p != null
                && !p.Dead
                && p.Faction != null
                && p.Faction.HostileTo(playerFaction)) ?? 0;
            var minExpected = Math.Max(4, (int)Math.Round(Math.Max(120f, profile.RaidPoints) / 180f));
            if (hostileCount >= minExpected) return;

            var need = Math.Max(1, minExpected - hostileCount);
            var points = Math.Max(180f, profile.RaidPoints * 0.7f);
            var spawned = SpawnFallbackHostileGroup(map, faction, points, need);
            if (spawned <= 0 && profile.RaidPoints > 0f)
            {
                SpawnFallbackHostileGroup(map, faction, Math.Max(220f, profile.RaidPoints), minExpected);
            }
        }

        private static int SpawnFallbackHostileGroup(Map map, Faction faction, float points, int minCount)
        {
            if (map == null || faction == null || points <= 0f) return 0;

            try
            {
                var parms = new PawnGroupMakerParms
                {
                    faction = faction,
                    groupKind = PawnGroupKindDefOf.Combat,
                    points = Math.Max(120f, points),
                    tile = map.Tile
                };

                var generated = PawnGroupMakerUtility.GeneratePawns(parms)
                    .Where(p => p != null)
                    .ToList();
                if (generated.Count == 0) return 0;

                if (minCount > 0 && generated.Count < minCount)
                {
                    var extraPoints = Math.Max(120f, points * 0.8f);
                    var extra = PawnGroupMakerUtility.GeneratePawns(new PawnGroupMakerParms
                    {
                        faction = faction,
                        groupKind = PawnGroupKindDefOf.Combat,
                        points = extraPoints,
                        tile = map.Tile
                    })
                    .Where(p => p != null)
                    .ToList();

                    generated.AddRange(extra);
                }

                var spawnedPawns = new List<Pawn>();
                for (int i = 0; i < generated.Count; i++)
                {
                    var pawn = generated[i];
                    if (pawn == null || pawn.Dead) continue;
                    if (!TryFindCell(map, nearCenter: false, out var cell)) continue;

                    GenSpawn.Spawn(pawn, cell, map, WipeMode.Vanish);
                    spawnedPawns.Add(pawn);
                }

                if (spawnedPawns.Count > 0)
                {
                    LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction), map, spawnedPawns);
                }

                return spawnedPawns.Count;
            }
            catch (Exception ex)
            {
                Loger.Log("Ошибка StoryPointDefensePatch при аварийном спавне защитников: " + ex, Loger.LogLevel.ERROR);
                return 0;
            }
        }

        private static void SpawnLootCaches(Map map, WorldObjectOnline descriptor, LootProfile profile, TechLevel maxTechLevel)
        {
            if (map == null) return;
            if (profile.CacheCount <= 0) return;

            var level = Math.Max(1, descriptor?.StoryLevel ?? 1);
            for (int cache = 0; cache < profile.CacheCount; cache++)
            {
                if (!TryFindCell(map, nearCenter: true, out var cell)) continue;

                var wantedItems = Rand.RangeInclusive(profile.ItemsPerCacheMin, profile.ItemsPerCacheMax);
                var levelBonusFactor = 1f + Math.Max(0, level - 1) * (profile.LevelMarketBonusPercent / 100f);
                var targetMarketValue = Rand.Range(profile.MarketValueMin, profile.MarketValueMax) * levelBonusFactor;

                var generated = GenerateLootWithRimworld(profile, map, level, targetMarketValue, wantedItems * 2, maxTechLevel);
                generated = generated
                    .Where(thing => thing != null && IsAllowedLootThing(thing, maxTechLevel) && MatchesTheme(thing, profile.Theme))
                    .ToList();

                if (generated.Count < wantedItems)
                {
                    var fallback = GenerateFallbackLoot(profile.Theme, wantedItems - generated.Count, level, targetMarketValue, maxTechLevel);
                    generated.AddRange(fallback);
                }

                if (generated.Count == 0) continue;

                var selected = TakeRandomThings(generated, wantedItems);
                for (int i = 0; i < selected.Count; i++)
                {
                    var thing = selected[i];
                    if (thing == null) continue;

                    try
                    {
                        GenDrop.TryDropSpawn(thing, cell, map, ThingPlaceMode.Near, out _, null);
                    }
                    catch (Exception ex)
                    {
                        Loger.Log("Ошибка StoryPointDefensePatch при спавне лута: " + ex, Loger.LogLevel.ERROR);
                    }
                }
            }
        }

        private static List<Thing> GenerateLootWithRimworld(LootProfile profile
            , Map map
            , int level
            , float targetMarketValue
            , int countHint
            , TechLevel maxTechLevel)
        {
            var result = new List<Thing>();
            var trader = PickTraderKind(profile.Theme, profile.TraderHints);

            AppendGeneratedFromMaker(result, "TraderStock", profile, map, level, targetMarketValue, countHint, trader, maxTechLevel);
            if (result.Count < countHint)
            {
                AppendGeneratedFromMaker(result, "Reward_ItemsStandard", profile, map, level, targetMarketValue, countHint, null, maxTechLevel);
            }

            return result;
        }
        private static void AppendGeneratedFromMaker(List<Thing> buffer
            , string makerDefName
            , LootProfile profile
            , Map map
            , int level
            , float targetMarketValue
            , int countHint
            , TraderKindDef traderKind
            , TechLevel maxTechLevel)
        {
            if (buffer == null) return;
            if (string.IsNullOrWhiteSpace(makerDefName)) return;

            var makerDef = DefDatabase<ThingSetMakerDef>.GetNamedSilentFail(makerDefName.Trim());
            if (makerDef?.root == null) return;

            var minMarket = Math.Max(120f, targetMarketValue * 0.75f);
            var maxMarket = Math.Max(minMarket + 40f, targetMarketValue * 1.25f);
            var minCount = Math.Max(2, countHint / 2);
            var maxCount = Math.Max(minCount, countHint);

            var parms = new ThingSetMakerParams()
            {
                totalMarketValueRange = new FloatRange(minMarket, maxMarket),
                countRange = new IntRange(minCount, maxCount),
                tile = map?.Tile,
                techLevel = ResolveTechLevel(profile.TechHint, level, maxTechLevel),
                traderDef = string.Equals(makerDefName, "TraderStock", StringComparison.OrdinalIgnoreCase) ? traderKind : null,
                validator = def => IsAllowedLootDef(def, maxTechLevel),
                allowNonStackableDuplicates = true
            };

            try
            {
                var generated = makerDef.root.Generate(parms);
                if (generated == null || generated.Count == 0) return;

                for (int i = 0; i < generated.Count; i++)
                {
                    var thing = generated[i];
                    if (thing == null) continue;
                    if (!IsAllowedLootThing(thing, maxTechLevel)) continue;
                    buffer.Add(thing);
                }
            }
            catch (Exception ex)
            {
                Loger.Log("Ошибка StoryPointDefensePatch при генерации через " + makerDefName + ": " + ex, Loger.LogLevel.WARNING);
            }
        }

        private static List<Thing> GenerateFallbackLoot(LootTheme theme
            , int count
            , int level
            , float targetMarketValue
            , TechLevel maxTechLevel)
        {
            var result = new List<Thing>();
            if (count <= 0) return result;

            var candidates = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(def => IsFallbackLootDef(def, theme, maxTechLevel))
                .ToList();
            if (candidates.Count == 0) return result;

            var perItemMarket = Math.Max(35f, targetMarketValue / Math.Max(1, count));
            var safeGuard = Math.Max(8, count * 10);
            while (result.Count < count && safeGuard-- > 0)
            {
                var pickedDef = PickWeightedDef(candidates, theme);
                if (pickedDef == null) break;

                var thing = TryCreateFallbackThing(pickedDef, level, perItemMarket);
                if (thing == null) continue;
                if (!IsAllowedLootThing(thing, maxTechLevel)) continue;
                if (!MatchesTheme(thing, theme)) continue;
                result.Add(thing);
            }

            return result;
        }

        private static Thing TryCreateFallbackThing(ThingDef def, int level, float perItemMarket)
        {
            if (def == null) return null;

            try
            {
                var stuff = def.MadeFromStuff ? PickStuff(def) : null;
                var thing = ThingMaker.MakeThing(def, stuff);
                if (thing == null) return null;

                if (def.category == ThingCategory.Building)
                {
                    if (!def.Minifiable) return null;
                    thing = MinifyUtility.MakeMinified(thing);
                    if (thing == null) return null;
                    return thing;
                }

                if (thing.def.stackLimit > 1)
                {
                    var oneValue = Math.Max(0.1f, thing.MarketValue);
                    var targetCount = (int)Math.Round(perItemMarket / oneValue);
                    targetCount = Math.Max(1, targetCount);
                    targetCount = Math.Min(targetCount, thing.def.stackLimit);
                    thing.stackCount = targetCount;
                }
                else
                {
                    thing.stackCount = 1;
                }

                ApplyQualityByLevel(thing, level);
                return thing;
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyQualityByLevel(Thing thing, int level)
        {
            if (thing == null) return;
            var qualityComp = thing.TryGetComp<CompQuality>();
            if (qualityComp == null) return;

            var roll = Rand.RangeInclusive(0, 100);
            QualityCategory quality;
            if (level >= 3)
            {
                quality = roll >= 92 ? QualityCategory.Masterwork
                    : roll >= 66 ? QualityCategory.Excellent
                    : roll >= 38 ? QualityCategory.Good
                    : QualityCategory.Normal;
            }
            else if (level == 2)
            {
                quality = roll >= 87 ? QualityCategory.Excellent
                    : roll >= 58 ? QualityCategory.Good
                    : QualityCategory.Normal;
            }
            else
            {
                quality = roll >= 78 ? QualityCategory.Good : QualityCategory.Normal;
            }

            qualityComp.SetQuality(quality, ArtGenerationContext.Outsider);
        }

        private static List<Thing> TakeRandomThings(List<Thing> source, int count)
        {
            var result = new List<Thing>();
            if (source == null || source.Count == 0 || count <= 0) return result;

            var pool = new List<Thing>(source);
            while (result.Count < count && pool.Count > 0)
            {
                var idx = Rand.Range(0, pool.Count);
                result.Add(pool[idx]);
                pool.RemoveAt(idx);
            }

            return result;
        }

        private static TechLevel? ResolveTechLevel(TechLevel? hint, int level, TechLevel maxTechLevel)
        {
            TechLevel? resolved = hint;
            if (!resolved.HasValue && level >= 3)
            {
                resolved = TechLevel.Industrial;
            }

            if (!resolved.HasValue) return null;
            if (resolved.Value > maxTechLevel) return maxTechLevel;
            return resolved.Value;
        }

        private static TraderKindDef PickTraderKind(LootTheme theme, string[] hints)
        {
            var all = DefDatabase<TraderKindDef>.AllDefsListForReading;
            if (all == null || all.Count == 0) return null;

            var candidates = all
                .Where(def => def != null)
                .ToList();
            if (candidates.Count == 0) return null;

            var tokens = new List<string>();
            if (hints != null && hints.Length > 0) tokens.AddRange(hints.Where(x => !string.IsNullOrWhiteSpace(x)));
            tokens.AddRange(GetThemeTraderTokens(theme));

            var bestScore = int.MinValue;
            var best = new List<TraderKindDef>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var def = candidates[i];
                var score = ScoreTraderKind(def, tokens);
                if (score > bestScore)
                {
                    bestScore = score;
                    best.Clear();
                    best.Add(def);
                }
                else if (score == bestScore)
                {
                    best.Add(def);
                }
            }

            if (best.Count > 0) return best[Rand.Range(0, best.Count)];
            return candidates[Rand.Range(0, candidates.Count)];
        }

        private static IEnumerable<string> GetThemeTraderTokens(LootTheme theme)
        {
            switch (theme)
            {
                case LootTheme.Trade:
                    return new[] { "bulk", "goods", "exotic", "settlement", "trader" };
                case LootTheme.Settlement:
                    return new[] { "bulk", "food", "plant", "settlement", "caravan" };
                case LootTheme.City:
                    return new[] { "exotic", "tech", "industrial", "orbital" };
                case LootTheme.Outpost:
                    return new[] { "combat", "weapon", "arms", "military", "pirate" };
                case LootTheme.Military:
                    return new[] { "combat", "weapon", "arms", "military", "pirate", "security" };
                case LootTheme.Mine:
                    return new[] { "bulk", "industrial", "minerals", "resource", "ore" };
                case LootTheme.Farm:
                    return new[] { "food", "plant", "bulk", "settlement", "caravan" };
                case LootTheme.Industry:
                    return new[] { "industrial", "bulk", "goods", "components", "manufacture" };
                case LootTheme.Research:
                    return new[] { "exotic", "tech", "industrial", "orbital", "science" };
                case LootTheme.Logistics:
                    return new[] { "bulk", "trade", "goods", "caravan", "transport" };
                default:
                    return new[] { "bulk", "trader", "settlement" };
            }
        }

        private static int ScoreTraderKind(TraderKindDef def, List<string> tokens)
        {
            if (def == null) return int.MinValue;
            if (tokens == null || tokens.Count == 0) return 0;

            var defName = (def.defName ?? string.Empty).ToLowerInvariant();
            var label = (def.label ?? string.Empty).ToLowerInvariant();
            var category = (def.category ?? string.Empty).ToLowerInvariant();

            var score = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = (tokens[i] ?? string.Empty).Trim().ToLowerInvariant();
                if (token.Length == 0) continue;
                if (defName.Contains(token)) score += 5;
                if (label.Contains(token)) score += 3;
                if (category.Contains(token)) score += 2;
            }

            if (!def.orbital) score += 1;
            return score;
        }
        private static List<ThingDef> GetTurretDefs(bool heavy, TechLevel maxTechLevel)
        {
            var allTurrets = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(def => IsTurretDef(def) && IsTechAllowed(def, maxTechLevel))
                .OrderBy(def => def.BaseMarketValue)
                .ToList();
            if (allTurrets.Count == 0) return new List<ThingDef>();

            var split = Math.Max(1, allTurrets.Count / 2);
            var selected = heavy
                ? allTurrets.Skip(split).ToList()
                : allTurrets.Take(split).ToList();

            if (selected.Count == 0) selected = allTurrets;
            return selected;
        }

        private static List<ThingDef> GetTrapDefs(TechLevel maxTechLevel)
        {
            return DefDatabase<ThingDef>.AllDefsListForReading
                .Where(def => def != null
                    && def.category == ThingCategory.Building
                    && IsTechAllowed(def, maxTechLevel)
                    && IsTrapBuilding(def))
                .OrderBy(def => def.BaseMarketValue)
                .ToList();
        }

        private static bool IsTurretDef(ThingDef def)
        {
            return def != null
                && def.category == ThingCategory.Building
                && def.building != null
                && def.building.turretGunDef != null;
        }

        private static bool IsFallbackLootDef(ThingDef def, LootTheme theme, TechLevel maxTechLevel)
        {
            if (!IsAllowedLootDef(def, maxTechLevel)) return false;
            if (!MatchesTheme(def, theme)) return false;

            if (def.category == ThingCategory.Item) return true;
            if (def.category == ThingCategory.Plant) return true;
            if (def.category == ThingCategory.Building && def.Minifiable) return true;
            return false;
        }

        private static ThingDef PickWeightedDef(List<ThingDef> defs, LootTheme theme)
        {
            if (defs == null || defs.Count == 0) return null;

            float total = 0f;
            for (int i = 0; i < defs.Count; i++)
            {
                total += Math.Max(0.01f, GetDefWeight(defs[i], theme));
            }
            if (total <= 0f) return defs[Rand.Range(0, defs.Count)];

            var roll = Rand.Value * total;
            for (int i = 0; i < defs.Count; i++)
            {
                roll -= Math.Max(0.01f, GetDefWeight(defs[i], theme));
                if (roll <= 0f) return defs[i];
            }

            return defs[defs.Count - 1];
        }

        private static float GetDefWeight(ThingDef def, LootTheme theme)
        {
            if (def == null) return 0.01f;
            var value = Math.Max(0.1f, def.BaseMarketValue);
            var weight = 1f / (float)Math.Sqrt(value);

            switch (theme)
            {
                case LootTheme.Trade:
                    if (IsMedicineLike(def) || IsFoodLike(def))
                    {
                        weight *= ReadLootWeightMultiplier(s => s.StoryPointLootWeightTradeFoodMedicinePercent, 160);
                    }
                    if (IsTechLike(def))
                    {
                        weight *= ReadLootWeightMultiplier(s => s.StoryPointLootWeightTradeTechPercent, 125);
                    }
                    break;

                case LootTheme.Settlement:
                    if (IsFoodLike(def))
                    {
                        weight *= ReadLootWeightMultiplier(s => s.StoryPointLootWeightSettlementFoodPercent, 210);
                    }
                    if (IsFurnitureLike(def))
                    {
                        weight *= ReadLootWeightMultiplier(s => s.StoryPointLootWeightSettlementFurniturePercent, 180);
                    }
                    break;

                case LootTheme.City:
                    if (IsWeaponLike(def))
                    {
                        weight *= ReadLootWeightMultiplier(s => s.StoryPointLootWeightCityWeaponPercent, 220);
                    }
                    if (IsTechLike(def))
                    {
                        weight *= ReadLootWeightMultiplier(s => s.StoryPointLootWeightCityTechPercent, 200);
                    }
                    break;

                case LootTheme.Outpost:
                    if (IsWeaponLike(def))
                    {
                        weight *= ReadLootWeightMultiplier(s => s.StoryPointLootWeightOutpostWeaponPercent, 220);
                    }
                    if (IsProstheticLike(def))
                    {
                        weight *= ReadLootWeightMultiplier(s => s.StoryPointLootWeightOutpostProstheticPercent, 200);
                    }
                    if (IsTurretResourceLike(def))
                    {
                        weight *= ReadLootWeightMultiplier(s => s.StoryPointLootWeightOutpostTurretResourcePercent, 180);
                    }
                    break;

                case LootTheme.Military:
                    if (IsWeaponLike(def) || IsTurretDef(def))
                    {
                        weight *= 2.6f;
                    }
                    if (IsProstheticLike(def) || IsTurretResourceLike(def))
                    {
                        weight *= 1.9f;
                    }
                    break;

                case LootTheme.Mine:
                    if (IsMiningResourceLike(def))
                    {
                        weight *= 2.8f;
                    }
                    if (IsIndustrialLike(def))
                    {
                        weight *= 1.7f;
                    }
                    break;

                case LootTheme.Farm:
                    if (IsAgricultureLike(def))
                    {
                        weight *= 2.7f;
                    }
                    if (IsMedicineLike(def))
                    {
                        weight *= 1.4f;
                    }
                    break;

                case LootTheme.Industry:
                    if (IsIndustrialLike(def))
                    {
                        weight *= 2.5f;
                    }
                    if (IsTurretResourceLike(def))
                    {
                        weight *= 1.5f;
                    }
                    break;

                case LootTheme.Research:
                    if (IsResearchLike(def))
                    {
                        weight *= 2.8f;
                    }
                    if (IsTechLike(def))
                    {
                        weight *= 1.9f;
                    }
                    break;

                case LootTheme.Logistics:
                    if (IsLogisticsLike(def))
                    {
                        weight *= 2.6f;
                    }
                    if (IsTradeLike(def))
                    {
                        weight *= 1.4f;
                    }
                    break;
            }

            return Math.Max(0.01f, weight);
        }

        private static bool IsAllowedLootThing(Thing thing, TechLevel maxTechLevel)
        {
            if (thing == null) return false;
            if (thing is Pawn) return false;
            if (thing is Corpse) return false;
            return IsAllowedLootDef(thing.def, maxTechLevel);
        }

        private static bool IsAllowedLootDef(ThingDef def, TechLevel maxTechLevel)
        {
            if (def == null) return false;
            if (def.category == ThingCategory.Pawn) return false;
            if (def.category == ThingCategory.Projectile) return false;
            if (def.category == ThingCategory.Filth) return false;
            if (def.category == ThingCategory.Mote) return false;
            if (def.category == ThingCategory.Gas) return false;
            if (def.category == ThingCategory.Ethereal) return false;
            if (def.category == ThingCategory.Attachment) return false;
            if (def.category == ThingCategory.PsychicEmitter) return false;
            if (def.BaseMarketValue <= 0f && def.category != ThingCategory.Building) return false;
            if (!IsTechAllowed(def, maxTechLevel)) return false;
            return true;
        }

        private static bool MatchesTheme(Thing thing, LootTheme theme)
        {
            if (thing == null) return false;
            var def = thing.def;
            if (thing is MinifiedThing minified && minified.InnerThing != null)
            {
                def = minified.InnerThing.def;
            }

            return MatchesTheme(def, theme);
        }

        private static bool MatchesTheme(ThingDef def, LootTheme theme)
        {
            if (def == null) return false;
            if (theme == LootTheme.Generic) return true;

            switch (theme)
            {
                case LootTheme.Trade:
                    return IsTradeLike(def);

                case LootTheme.Settlement:
                    return IsFoodLike(def) || IsFurnitureLike(def) || IsMedicineLike(def);

                case LootTheme.City:
                    return IsWeaponLike(def) || IsTechLike(def) || IsMedicineLike(def);

                case LootTheme.Outpost:
                    return IsWeaponLike(def) || IsProstheticLike(def) || IsTurretResourceLike(def) || IsTechLike(def);

                case LootTheme.Military:
                    return IsWeaponLike(def) || IsProstheticLike(def) || IsTurretResourceLike(def) || IsTurretDef(def);

                case LootTheme.Mine:
                    return IsMiningResourceLike(def) || IsIndustrialLike(def);

                case LootTheme.Farm:
                    return IsAgricultureLike(def) || IsMedicineLike(def);

                case LootTheme.Industry:
                    return IsIndustrialLike(def) || IsTurretResourceLike(def);

                case LootTheme.Research:
                    return IsResearchLike(def) || IsTechLike(def) || IsMedicineLike(def);

                case LootTheme.Logistics:
                    return IsLogisticsLike(def) || IsTradeLike(def) || IsTechLike(def);

                default:
                    return true;
            }
        }

        private static bool IsTradeLike(ThingDef def)
        {
            if (def == null) return false;
            if (def.tradeability == Tradeability.None && def.category != ThingCategory.Building) return false;
            return def.category == ThingCategory.Item
                || def.category == ThingCategory.Plant
                || (def.category == ThingCategory.Building && def.Minifiable);
        }

        private static bool IsFoodLike(ThingDef def)
        {
            if (def == null) return false;
            if (def.IsNutritionGivingIngestible) return true;
            if (def.IsDrug) return true;
            if (def.category == ThingCategory.Plant) return true;
            return HasToken(def.defName, "meal")
                || HasToken(def.defName, "pemmican")
                || HasToken(def.defName, "survivalmeal")
                || HasToken(def.defName, "rice")
                || HasToken(def.defName, "corn")
                || HasToken(def.defName, "potato")
                || HasToken(def.defName, "chocolate");
        }

        private static bool IsFurnitureLike(ThingDef def)
        {
            if (def == null) return false;
            if (def.category == ThingCategory.Building && def.Minifiable && !IsTurretDef(def))
            {
                return HasToken(def.defName, "bed")
                    || HasToken(def.defName, "table")
                    || HasToken(def.defName, "chair")
                    || HasToken(def.defName, "dresser")
                    || HasToken(def.defName, "shelf")
                    || HasToken(def.defName, "sofa")
                    || HasToken(def.defName, "lamp");
            }

            if (def.IsStuff) return true;
            return HasToken(def.defName, "wood")
                || HasToken(def.defName, "steel")
                || HasToken(def.defName, "cloth")
                || HasToken(def.defName, "leather")
                || HasToken(def.defName, "blocks")
                || HasToken(def.defName, "stone")
                || HasToken(def.defName, "chemfuel");
        }

        private static bool IsWeaponLike(ThingDef def)
        {
            return def != null && def.IsWeapon;
        }

        private static bool IsTechLike(ThingDef def)
        {
            if (def == null) return false;
            if (def.techLevel >= TechLevel.Industrial) return true;
            return HasToken(def.defName, "component")
                || HasToken(def.defName, "plasteel")
                || HasToken(def.defName, "uranium")
                || HasToken(def.defName, "persona")
                || HasToken(def.defName, "techprof")
                || HasToken(def.defName, "nanite");
        }

        private static bool IsMedicineLike(ThingDef def)
        {
            if (def == null) return false;
            return HasToken(def.defName, "medicine")
                || HasToken(def.defName, "neutroamine")
                || HasToken(def.defName, "glitterworld");
        }

        private static bool IsProstheticLike(ThingDef def)
        {
            if (def == null) return false;
            return HasToken(def.defName, "prosthetic")
                || HasToken(def.defName, "bionic")
                || HasToken(def.defName, "archotech")
                || HasToken(def.defName, "powerclaw")
                || HasToken(def.defName, "drillarm");
        }

        private static bool IsTurretResourceLike(ThingDef def)
        {
            if (def == null) return false;
            return HasToken(def.defName, "component")
                || HasToken(def.defName, "plasteel")
                || HasToken(def.defName, "uranium")
                || HasToken(def.defName, "steel")
                || HasToken(def.defName, "chemfuel");
        }

        private static bool IsMiningResourceLike(ThingDef def)
        {
            if (def == null) return false;
            return HasToken(def.defName, "steel")
                || HasToken(def.defName, "plasteel")
                || HasToken(def.defName, "uranium")
                || HasToken(def.defName, "gold")
                || HasToken(def.defName, "silver")
                || HasToken(def.defName, "jade")
                || HasToken(def.defName, "component")
                || HasToken(def.defName, "drill")
                || HasToken(def.defName, "chunk")
                || HasToken(def.defName, "ore");
        }

        private static bool IsAgricultureLike(ThingDef def)
        {
            if (def == null) return false;
            return IsFoodLike(def)
                || HasToken(def.defName, "seed")
                || HasToken(def.defName, "hay")
                || HasToken(def.defName, "fertil")
                || HasToken(def.defName, "hydroponic")
                || HasToken(def.defName, "sunlamp")
                || HasToken(def.defName, "grow");
        }

        private static bool IsIndustrialLike(ThingDef def)
        {
            if (def == null) return false;
            return HasToken(def.defName, "component")
                || HasToken(def.defName, "fabrication")
                || HasToken(def.defName, "machining")
                || HasToken(def.defName, "smelter")
                || HasToken(def.defName, "tool")
                || HasToken(def.defName, "generator")
                || HasToken(def.defName, "battery");
        }

        private static bool IsResearchLike(ThingDef def)
        {
            if (def == null) return false;
            return HasToken(def.defName, "research")
                || HasToken(def.defName, "multianalyzer")
                || HasToken(def.defName, "hi-tech")
                || HasToken(def.defName, "persona")
                || HasToken(def.defName, "techprof")
                || HasToken(def.defName, "neuro");
        }

        private static bool IsLogisticsLike(ThingDef def)
        {
            if (def == null) return false;
            return HasToken(def.defName, "packaged")
                || HasToken(def.defName, "survivalmeal")
                || HasToken(def.defName, "transportpod")
                || HasToken(def.defName, "beacon")
                || HasToken(def.defName, "shelf")
                || HasToken(def.defName, "drop")
                || HasToken(def.defName, "podlauncher");
        }

        private static TechLevel ResolveFactionTechLevel(Faction faction)
        {
            var level = faction?.def?.techLevel ?? TechLevel.Industrial;
            if (level == TechLevel.Undefined) return TechLevel.Industrial;
            if (level <= TechLevel.Animal) return TechLevel.Neolithic;
            return level;
        }

        private static bool IsTechAllowed(ThingDef def, TechLevel maxTechLevel)
        {
            if (def == null) return false;
            if (maxTechLevel == TechLevel.Undefined) return true;

            if (maxTechLevel < TechLevel.Industrial)
            {
                if (IsTurretDef(def)) return false;
                if (IsProbableFirearm(def)) return false;
            }

            var tech = def.techLevel;
            if (tech == TechLevel.Undefined || tech == TechLevel.Animal) return true;
            return tech <= maxTechLevel;
        }

        private static bool IsProbableFirearm(ThingDef def)
        {
            if (def == null) return false;
            if (!def.IsWeapon) return false;

            var name = (def.defName ?? string.Empty).ToLowerInvariant();
            var label = (def.label ?? string.Empty).ToLowerInvariant();
            if (name.Length == 0 && label.Length == 0) return false;

            return name.Contains("gun")
                || name.Contains("rifle")
                || name.Contains("pistol")
                || name.Contains("smg")
                || name.Contains("shotgun")
                || name.Contains("minigun")
                || name.Contains("revolver")
                || name.Contains("autopistol")
                || name.Contains("assault")
                || name.Contains("sniper")
                || label.Contains("gun")
                || label.Contains("rifle")
                || label.Contains("pistol")
                || label.Contains("shotgun")
                || label.Contains("revolver")
                || label.Contains("assault")
                || label.Contains("sniper");
        }

        private static bool HasToken(string source, string token)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(token)) return false;
            return source.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ThingDef PickStuff(ThingDef product)
        {
            if (product == null) return null;

            var defaultStuff = GenStuff.DefaultStuffFor(product);
            if (product.stuffCategories == null || product.stuffCategories.Count == 0)
            {
                return defaultStuff;
            }

            var options = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(def => def != null
                    && def.IsStuff
                    && def.stuffProps != null
                    && def.stuffProps.categories != null
                    && def.stuffProps.categories.Any(cat => product.stuffCategories.Contains(cat)))
                .ToList();
            if (options.Count == 0) return defaultStuff;

            return options[Rand.Range(0, options.Count)];
        }

        private static float ReadLootWeightMultiplier(Func<ServerGeneralSettings, int> getter, int fallbackPercent)
        {
            var percent = ReadLootSetting(getter, fallbackPercent, 1, 10000);
            return Math.Max(0.01f, percent / 100f);
        }

        private static int ReadLootSetting(Func<ServerGeneralSettings, int> getter, int fallback, int min, int max)
        {
            if (getter == null) return fallback;
            int value;
            try
            {
                value = getter(GetGeneralSettings());
            }
            catch
            {
                return fallback;
            }

            if (value < min || value > max) return fallback;
            return value;
        }

        private static ServerGeneralSettings GetGeneralSettings()
        {
            try
            {
                var data = SessionClientController.Data;
                if (data != null) return data.GeneralSettings;
            }
            catch
            {
                // ignore
            }

            return DefaultGeneralSettings;
        }

        private static bool TryFindCell(Map map, bool nearCenter, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            if (map == null) return false;

            var rect = nearCenter
                ? CellRect.CenteredOn(map.Center, 20).ClipInsideMap(map)
                : CellRect.WholeMap(map);

            for (int i = 0; i < 220; i++)
            {
                var candidate = rect.RandomCell;
                if (!candidate.InBounds(map)) continue;
                if (!candidate.Standable(map)) continue;
                if (nearCenter && candidate.DistanceTo(map.Center) < 8f) continue;
                if (!nearCenter && candidate.DistanceTo(map.Center) < 18f) continue;
                if (candidate.GetEdifice(map) != null) continue;
                if (candidate.GetThingList(map).Any(t => t.def.category == ThingCategory.Building)) continue;
                cell = candidate;
                return true;
            }

            return false;
        }

        private enum LootTheme
        {
            Generic = 0,
            Trade = 1,
            Settlement = 2,
            City = 3,
            Outpost = 4,
            Military = 5,
            Mine = 6,
            Farm = 7,
            Industry = 8,
            Research = 9,
            Logistics = 10
        }

        private readonly struct LootProfile
        {
            public readonly int CacheCount;
            public readonly int ItemsPerCacheMin;
            public readonly int ItemsPerCacheMax;
            public readonly LootTheme Theme;
            public readonly float MarketValueMin;
            public readonly float MarketValueMax;
            public readonly int LevelMarketBonusPercent;
            public readonly TechLevel? TechHint;
            public readonly string[] TraderHints;

            public LootProfile(int cacheCount
                , int itemsPerCacheMin
                , int itemsPerCacheMax
                , LootTheme theme
                , float marketValueMin
                , float marketValueMax
                , int levelMarketBonusPercent
                , TechLevel? techHint
                , string[] traderHints)
            {
                CacheCount = Math.Max(0, cacheCount);
                ItemsPerCacheMin = Math.Max(1, itemsPerCacheMin);
                ItemsPerCacheMax = Math.Max(ItemsPerCacheMin, itemsPerCacheMax);
                Theme = theme;
                MarketValueMin = Math.Max(50f, marketValueMin);
                MarketValueMax = Math.Max(MarketValueMin, marketValueMax);
                LevelMarketBonusPercent = Math.Max(0, levelMarketBonusPercent);
                TechHint = techHint;
                TraderHints = traderHints ?? new string[0];
            }
        }

        private readonly struct DefenseProfile
        {
            public readonly float RaidPoints;
            public readonly int ExtraRaidWaves;
            public readonly int LightTurrets;
            public readonly int HeavyTurrets;
            public readonly int Traps;

            public DefenseProfile(float raidPoints, int extraRaidWaves, int lightTurrets, int heavyTurrets, int traps)
            {
                RaidPoints = raidPoints;
                ExtraRaidWaves = Math.Max(0, extraRaidWaves);
                LightTurrets = Math.Max(0, lightTurrets);
                HeavyTurrets = Math.Max(0, heavyTurrets);
                Traps = Math.Max(0, traps);
            }
        }
    }
}
