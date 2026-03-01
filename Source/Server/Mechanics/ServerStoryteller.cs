using Model;
using OCUnion;
using ServerOnlineCity.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerOnlineCity.Mechanics
{
    internal static class ServerStoryteller
    {
        private static readonly Random Rnd = new Random();

        public static void Tick(BaseContainer data)
        {
            if (data == null) return;
            if (!IsEnabled()) return;

            var now = DateTime.UtcNow;
            var tickIntervalSeconds = GetTickIntervalSeconds();
            if (data.StorytellerLastRunUtc > DateTime.MinValue
                && (now - data.StorytellerLastRunUtc).TotalSeconds < tickIntervalSeconds)
            {
                return;
            }
            data.StorytellerLastRunUtc = now;

            if (data.WorldObjectOnlineList == null) data.WorldObjectOnlineList = new List<WorldObjectOnline>();
            if (data.StorytellerKnownTiles == null) data.StorytellerKnownTiles = new List<int>();
            if (data.StoryEvents == null) data.StoryEvents = new List<ServerStoryEvent>();
            EnsureFactionCatalog(data);

            NormalizeStoryObjects(data, now);
            RemoveExpiredObjects(data, now);
            TryUpgradeCamps(data, now);
            TryEvolveSettlements(data, now);
            TrySpreadSettlements(data, now);
            TryResolveFactionConflict(data, now);
            TryEmitDiplomaticEvent(data, now);
            TrySpawnGlobalPoint(data, now);
        }

        public static void RegisterKnownTile(BaseContainer data, int tile)
        {
            if (data == null || tile <= 0) return;
            if (data.StorytellerKnownTiles == null) data.StorytellerKnownTiles = new List<int>();
            if (!data.StorytellerKnownTiles.Contains(tile))
            {
                data.StorytellerKnownTiles.Add(tile);
            }
        }

        public static bool AppendStoryEvent(BaseContainer data
            , string label
            , string text
            , int tile = 0
            , string category = "storyteller"
            , string key = null
            , int dedupMinutes = 0)
        {
            if (data == null) return false;
            if (data.StoryEvents == null) data.StoryEvents = new List<ServerStoryEvent>();

            if (!string.IsNullOrWhiteSpace(key) && dedupMinutes > 0)
            {
                var stopDate = DateTime.UtcNow.AddMinutes(-dedupMinutes);
                for (int i = data.StoryEvents.Count - 1; i >= 0; i--)
                {
                    var existing = data.StoryEvents[i];
                    if (existing == null) continue;
                    if (existing.CreatedUtc < stopDate) break;
                    if (string.Equals(existing.Key, key, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            var evt = new ServerStoryEvent()
            {
                Id = ++data.MaxStoryEventId,
                CreatedUtc = DateTime.UtcNow,
                Category = string.IsNullOrWhiteSpace(category) ? "storyteller" : category.Trim(),
                Key = key,
                Label = string.IsNullOrWhiteSpace(label) ? "Событие" : label.Trim(),
                Text = text?.Trim() ?? string.Empty,
                Tile = tile
            };

            data.StoryEvents.Add(evt);
            var maxStoryEvents = GetEventHistoryLimit();
            if (data.StoryEvents.Count > maxStoryEvents)
            {
                var removeCount = data.StoryEvents.Count - maxStoryEvents;
                data.StoryEvents.RemoveRange(0, removeCount);
            }

            return true;
        }

        private static bool CanUseStoryKey(BaseContainer data, string key, int dedupMinutes)
        {
            if (data == null) return false;
            if (string.IsNullOrWhiteSpace(key) || dedupMinutes <= 0) return true;
            if (data.StoryEvents == null || data.StoryEvents.Count == 0) return true;

            var stopDate = DateTime.UtcNow.AddMinutes(-dedupMinutes);
            for (int i = data.StoryEvents.Count - 1; i >= 0; i--)
            {
                var existing = data.StoryEvents[i];
                if (existing == null) continue;
                if (existing.CreatedUtc < stopDate) break;
                if (string.Equals(existing.Key, key, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public static string TriggerDebugEvent(BaseContainer data, string mode, string initiatorLogin = null)
        {
            if (data == null) return "Storyteller test: нет данных мира.";

            if (data.WorldObjectOnlineList == null) data.WorldObjectOnlineList = new List<WorldObjectOnline>();
            if (data.StorytellerKnownTiles == null) data.StorytellerKnownTiles = new List<int>();
            if (data.StoryEvents == null) data.StoryEvents = new List<ServerStoryEvent>();
            EnsureFactionCatalog(data);

            EnsureKnownTilesForDebug(data);
            var now = DateTime.UtcNow;
            NormalizeStoryObjects(data, now);

            var debugMode = NormalizeDebugMode(mode);
            if (debugMode == "random")
            {
                var variants = new List<string>() { "spawn", "evolve", "spread", "conflict", "diplomacy" };
                debugMode = variants[NextInt(variants.Count)];
            }

            switch (debugMode)
            {
                case "spawn":
                    return TriggerDebugSpawn(data, now);
                case "spawn_city":
                    return TriggerDebugSpawnCity(data, now);
                case "grow_city":
                    return TriggerDebugGrowCity(data, now);
                case "evolve":
                    return TriggerDebugEvolve(data, now);
                case "spread":
                    return TriggerDebugSpread(data, now);
                case "conflict":
                    return TriggerDebugConflict(data, now);
                case "diplomacy":
                    return TriggerDebugDiplomacy(data, now);
                default:
                    return TriggerDebugLog(data, initiatorLogin);
            }
        }

        private static string TriggerDebugSpawn(BaseContainer data, DateTime now)
        {
            var kinds = new List<StorySpawnKind>()
            {
                StorySpawnKind.PlayerFrontierCamp,
                StorySpawnKind.TradeCamp,
                StorySpawnKind.Outpost,
                StorySpawnKind.Settlement,
                StorySpawnKind.MilitaryBase,
                StorySpawnKind.Mine,
                StorySpawnKind.Farm,
                StorySpawnKind.IndustrialSite,
                StorySpawnKind.ResearchHub,
                StorySpawnKind.LogisticsHub
            };
            var kind = kinds[NextInt(kinds.Count)];
            var point = CreateDebugPoint(data, now, kind);
            if (point == null)
            {
                return TriggerDebugLog(data, "storytest:spawn");
            }

            AppendStoryEvent(data
                , "Тест рассказчика"
                , "Сгенерирована тестовая точка: " + point.Name
                , point.Tile
                , "storyteller");
            return $"Storyteller test: spawn {point.Name} (tile {point.Tile}).";
        }

        private static string TriggerDebugEvolve(BaseContainer data, DateTime now)
        {
            var settlement = data.WorldObjectOnlineList
                .Where(p => p != null && p.ServerGenerated && IsSettlementType(p.StoryType))
                .OrderByDescending(GetSettlementLevel)
                .FirstOrDefault();
            if (settlement == null)
            {
                settlement = CreateDebugPoint(data, now, StorySpawnKind.Settlement);
                if (settlement == null)
                {
                    return TriggerDebugLog(data, "storytest:evolve");
                }
            }

            if (!TryDebugEvolveByInfrastructure(data, now, settlement, out var infrastructurePoint))
            {
                return TriggerDebugLog(data, "storytest:evolve");
            }

            var tierText = GetSettlementTierLabel(GetSettlementLevel(settlement)).ToLowerInvariant();
            var kindText = GetPointKindLabel(infrastructurePoint.StoryType).ToLowerInvariant();
            AppendStoryEvent(data
                , "Тест рассказчика"
                , $"Поселение \"{settlement.Name}\" ({tierText}) расширило инфраструктуру. Построен {kindText}: {infrastructurePoint.Name}."
                , infrastructurePoint.Tile
                , "storyteller");
            return $"Storyteller test: evolve infrastructure from {settlement.Name} to {infrastructurePoint.Name} (tile {infrastructurePoint.Tile}).";
        }

        private static bool TryDebugEvolveByInfrastructure(BaseContainer data
            , DateTime now
            , WorldObjectOnline source
            , out WorldObjectOnline point)
        {
            point = null;
            if (data == null || source == null) return false;

            var freeTiles = GetFreeKnownTiles(data);
            if (freeTiles.Count == 0) return false;

            var spawnKind = PickEvolveInfrastructureKind(data, source);
            var tile = PickSpreadTile(freeTiles, source.Tile, 10, true);
            if (tile <= 0)
            {
                tile = PickSpreadTile(freeTiles, source.Tile, 10);
            }
            if (tile <= 0) return false;

            point = new WorldObjectOnline()
            {
                Name = BuildPointName(data, spawnKind, source.FactionGroup, source.FactionDef),
                Tile = tile,
                FactionGroup = source.FactionGroup,
                FactionDef = source.FactionDef,
                loadID = source.loadID,
                ServerGenerated = true,
                StoryType = GetStoryType(spawnKind),
                ExpireAtUtc = IsTemporary(spawnKind) ? now.AddHours(GetLifetimeHours(spawnKind)) : DateTime.MinValue,
                StoryLevel = spawnKind == StorySpawnKind.Settlement ? 1 : 0,
                StoryNextActionUtc = spawnKind == StorySpawnKind.Settlement
                    ? now.AddMinutes(GetSettlementActionCooldownMinutes())
                    : DateTime.MinValue,
                StorySeed = BuildStorySeed(spawnKind, tile, source.FactionDef)
            };
            if (spawnKind == StorySpawnKind.Settlement)
            {
                ApplySettlementName(point);
            }
            point.Name = AttachSeedToName(point.Name, point.StorySeed);

            data.WorldObjectOnlineList.Add(point);
            source.StoryNextActionUtc = now.AddMinutes(GetSettlementActionCooldownMinutes());
            return true;
        }

        private static StorySpawnKind PickEvolveInfrastructureKind(BaseContainer data, WorldObjectOnline source)
        {
            var preferred = PickSpreadSpawnKind(data, source, Math.Max(3, GetSettlementLevel(source)));
            if (IsCityInfrastructureKind(preferred)) return preferred;

            var militaryWeight = 20;
            var mineWeight = 18;
            var farmWeight = 16;
            var industrialWeight = 16;
            var researchWeight = 15;
            var logisticsWeight = 15;

            if (preferred == StorySpawnKind.TradeCamp)
            {
                logisticsWeight += 12;
                industrialWeight += 4;
            }
            else if (preferred == StorySpawnKind.Outpost)
            {
                militaryWeight += 12;
                researchWeight += 4;
            }
            else if (preferred == StorySpawnKind.Settlement)
            {
                mineWeight += 6;
                farmWeight += 6;
            }

            var pool = new List<Tuple<StorySpawnKind, int>>()
            {
                Tuple.Create(StorySpawnKind.MilitaryBase, militaryWeight),
                Tuple.Create(StorySpawnKind.Mine, mineWeight),
                Tuple.Create(StorySpawnKind.Farm, farmWeight),
                Tuple.Create(StorySpawnKind.IndustrialSite, industrialWeight),
                Tuple.Create(StorySpawnKind.ResearchHub, researchWeight),
                Tuple.Create(StorySpawnKind.LogisticsHub, logisticsWeight)
            };

            var total = pool.Sum(item => Math.Max(0, item.Item2));
            if (total <= 0) return StorySpawnKind.MilitaryBase;

            var roll = NextInt(total);
            for (int i = 0; i < pool.Count; i++)
            {
                roll -= Math.Max(0, pool[i].Item2);
                if (roll < 0)
                {
                    return pool[i].Item1;
                }
            }

            return StorySpawnKind.MilitaryBase;
        }

        private static string TriggerDebugSpawnCity(BaseContainer data, DateTime now)
        {
            var point = CreateDebugPoint(data, now, StorySpawnKind.Settlement);
            if (point == null)
            {
                return TriggerDebugLog(data, "storytest:spawn_city");
            }

            var oldLevel = Math.Max(1, GetSettlementLevel(point));
            var maxLevel = GetSettlementMaxLevel();
            var targetLevel = Math.Min(maxLevel, Math.Max(3, oldLevel));
            point.StoryLevel = targetLevel;
            point.StoryNextActionUtc = now.AddMinutes(GetSettlementActionCooldownMinutes());
            ApplySettlementName(point);

            AppendStoryEvent(data
                , "Тест рассказчика"
                , $"На карте создан город: \"{point.Name}\" (уровень {point.StoryLevel})."
                , point.Tile
                , "storyteller");
            return $"Storyteller test: spawn city {point.Name} (tile {point.Tile}, level {point.StoryLevel}).";
        }

        private static string TriggerDebugGrowCity(BaseContainer data, DateTime now)
        {
            var settlement = data.WorldObjectOnlineList
                .Where(p => p != null && p.ServerGenerated && IsSettlementType(p.StoryType))
                .OrderByDescending(GetSettlementLevel)
                .FirstOrDefault();
            if (settlement == null)
            {
                settlement = CreateDebugPoint(data, now, StorySpawnKind.Settlement);
                if (settlement == null)
                {
                    return TriggerDebugLog(data, "storytest:grow_city");
                }
            }

            var oldLevel = Math.Max(1, GetSettlementLevel(settlement));
            var maxLevel = GetSettlementMaxLevel();
            var raised = oldLevel < 3 ? 3 : Math.Min(maxLevel, oldLevel + 1);
            settlement.StoryLevel = Math.Max(oldLevel, raised);
            settlement.StoryNextActionUtc = now.AddMinutes(GetSettlementActionCooldownMinutes());
            ApplySettlementName(settlement);

            var oldLevelText = GetSettlementTierLabel(oldLevel).ToLowerInvariant();
            var newLevelText = GetSettlementTierLabel(settlement.StoryLevel).ToLowerInvariant();
            AppendStoryEvent(data
                , "Тест рассказчика"
                , $"Городское развитие: \"{settlement.Name}\" изменено ({oldLevelText} -> {newLevelText})."
                , settlement.Tile
                , "storyteller");
            return $"Storyteller test: grow city {settlement.Name} to level {settlement.StoryLevel}.";
        }

        private static string TriggerDebugSpread(BaseContainer data, DateTime now)
        {
            var source = data.WorldObjectOnlineList
                .Where(p => p != null && p.ServerGenerated && IsSettlementType(p.StoryType))
                .OrderByDescending(GetSettlementLevel)
                .FirstOrDefault();
            if (source == null)
            {
                source = CreateDebugPoint(data, now, StorySpawnKind.Settlement);
                if (source == null)
                {
                    return TriggerDebugLog(data, "storytest:spread");
                }
            }

            if (source.StoryLevel < 2)
            {
                source.StoryLevel = 2;
                ApplySettlementName(source);
            }

            var kind = PickSpreadSpawnKind(data, source, GetSettlementLevel(source));
            var point = CreateDebugPoint(data, now, kind, source.FactionGroup, source.FactionDef);
            if (point == null)
            {
                return TriggerDebugLog(data, "storytest:spread");
            }

            source.StoryNextActionUtc = now.AddMinutes(GetSettlementActionCooldownMinutes());
            AppendStoryEvent(data
                , "Тест рассказчика"
                , $"{source.Name} расширило влияние. Новая точка: {point.Name}."
                , point.Tile
                , "storyteller");
            return $"Storyteller test: spread from {source.Name} to {point.Name} (tile {point.Tile}).";
        }

        private static string TriggerDebugConflict(BaseContainer data, DateTime now)
        {
            var generated = data.WorldObjectOnlineList
                .Where(p => p != null && p.ServerGenerated && p.Tile > 0)
                .ToList();

            if (generated.Count < 2)
            {
                var factions = GetFactionPool(data, true)
                    .Where(f => f != null)
                    .Take(2)
                    .ToList();
                if (factions.Count == 0)
                {
                    factions.Add(new FactionOnline() { LabelCap = "Пираты", DefName = "Pirate", loadID = 0 });
                }
                if (factions.Count == 1)
                {
                    var altDef = string.Equals(factions[0].DefName, "OutlanderCivil", StringComparison.OrdinalIgnoreCase)
                        ? "Pirate"
                        : "OutlanderCivil";
                    var altLabel = string.Equals(altDef, "OutlanderCivil", StringComparison.OrdinalIgnoreCase)
                        ? "Союз аутлендеров"
                        : "Пираты";
                    factions.Add(new FactionOnline() { LabelCap = altLabel, DefName = altDef, loadID = 0 });
                }

                CreateDebugPoint(data, now, StorySpawnKind.Settlement, factions[0].LabelCap, factions[0].DefName);
                CreateDebugPoint(data, now, StorySpawnKind.Settlement, factions[1].LabelCap, factions[1].DefName);
                generated = data.WorldObjectOnlineList
                    .Where(p => p != null && p.ServerGenerated && p.Tile > 0)
                    .ToList();
            }

            var attacker = generated.FirstOrDefault();
            var defenders = generated
                .Where(p => !ReferenceEquals(p, attacker)
                    && (p.FactionDef != attacker?.FactionDef || p.FactionGroup != attacker?.FactionGroup))
                .ToList();
            if (attacker == null || defenders.Count == 0)
            {
                return TriggerDebugLog(data, "storytest:conflict");
            }

            var target = Pick(defenders);
            var conflictPairKey = BuildFactionPairKey(attacker.FactionGroup, attacker.FactionDef, target.FactionGroup, target.FactionDef);
            if (string.IsNullOrWhiteSpace(conflictPairKey))
            {
                conflictPairKey = BuildFallbackPairKeyByLabels(attacker.FactionGroup, target.FactionGroup);
            }
            var conflictKey = $"conflict:{conflictPairKey}:{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            if (IsSettlementType(target.StoryType))
            {
                target.FactionDef = attacker.FactionDef;
                target.FactionGroup = attacker.FactionGroup;
                target.StoryLevel = Math.Max(1, GetSettlementLevel(target));
                target.StoryNextActionUtc = now.AddMinutes(GetSettlementActionCooldownMinutes());
                ApplySettlementName(target);
                AppendStoryEvent(data
                    , "Тест рассказчика"
                    , $"{attacker.FactionGroup} захватили точку: {target.Name}."
                    , target.Tile
                    , "storyteller"
                    , conflictKey);
                return $"Storyteller test: conflict capture {target.Name} by {attacker.FactionGroup}.";
            }

            data.WorldObjectOnlineList.Remove(target);
            AppendStoryEvent(data
                , "Тест рассказчика"
                , $"{attacker.FactionGroup} уничтожили точку: {target.Name}."
                , target.Tile
                , "storyteller"
                , conflictKey);
            return $"Storyteller test: conflict removed {target.Name}.";
        }

        private static string TriggerDebugDiplomacy(BaseContainer data, DateTime now)
        {
            var factions = GetFactionPool(data, true);
            if (factions.Count < 2) return TriggerDebugLog(data, "storytest:diplomacy");

            var first = Pick(factions);
            var secondPool = factions.Where(f => !ReferenceEquals(f, first)).ToList();
            if (secondPool.Count == 0) return TriggerDebugLog(data, "storytest:diplomacy");
            var second = Pick(secondPool);
            var isTrade = Roll(50);
            var ok = ApplyDiplomaticWorldEffect(data
                , now
                , first.LabelCap?.Trim()
                , first.DefName
                , first.loadID
                , second.LabelCap?.Trim()
                , second.DefName
                , second.loadID
                , isTrade
                , true
                , out var effectText
                , out var effectTile);
            if (!ok)
            {
                return TriggerDebugLog(data, "storytest:diplomacy");
            }

            var keyPrefix = isTrade ? "diplomacy_trade" : "diplomacy_tension";
            var pairKey = BuildFactionPairKey(first.LabelCap, first.DefName, second.LabelCap, second.DefName);
            if (string.IsNullOrWhiteSpace(pairKey))
            {
                pairKey = BuildFallbackPairKeyByLabels(first.LabelCap, second.LabelCap);
            }
            var debugDiplomacyKey = $"{keyPrefix}:{pairKey}:{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            AppendStoryEvent(data
                , "Тест рассказчика"
                , effectText
                , effectTile
                , "diplomacy"
                , debugDiplomacyKey);
            return $"Storyteller test: diplomacy {(isTrade ? "trade" : "tension")} {first.LabelCap} <-> {second.LabelCap}.";
        }

        private static string TriggerDebugLog(BaseContainer data, string initiatorLogin)
        {
            var tile = PickDebugTile(data);
            var by = string.IsNullOrWhiteSpace(initiatorLogin) ? "администратора" : initiatorLogin;
            AppendStoryEvent(data
                , "Тест рассказчика"
                , $"Тестовый импульс рассказчика от {by}."
                , tile
                , "storyteller");
            return tile > 0
                ? $"Storyteller test: log event on tile {tile}."
                : "Storyteller test: log event.";
        }

        private static void EnsureKnownTilesForDebug(BaseContainer data)
        {
            if (data == null) return;
            if (data.StorytellerKnownTiles == null) data.StorytellerKnownTiles = new List<int>();

            var known = new HashSet<int>(data.StorytellerKnownTiles.Where(t => t > 0));
            foreach (var tile in (data.WorldObjects ?? new List<WorldObjectEntry>()).Where(o => o != null && o.Tile > 0).Select(o => o.Tile))
            {
                known.Add(tile);
            }
            foreach (var tile in (data.WorldObjectOnlineList ?? new List<WorldObjectOnline>())
                .Where(o => o != null && o.Tile > 0)
                .Select(o => o.Tile))
            {
                known.Add(tile);
            }

            data.StorytellerKnownTiles = known.ToList();
        }

        private static void EnsureFactionCatalog(BaseContainer data)
        {
            if (data == null) return;
            var pool = GetFactionPool(data, false);
            if (pool.Count == 0) return;
            data.FactionOnlineList = pool;
        }

        private static List<FactionOnline> GetFactionPool(BaseContainer data, bool includeSyntheticFallback)
        {
            var pool = new List<FactionOnline>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (data?.WorldObjectOnlineList != null)
            {
                for (int i = 0; i < data.WorldObjectOnlineList.Count; i++)
                {
                    var point = data.WorldObjectOnlineList[i];
                    AddFactionToPool(pool, keys, point?.FactionGroup, point?.FactionDef, point?.loadID ?? 0);
                }
            }

            if (data?.FactionOnlineList != null)
            {
                if (pool.Count == 0)
                {
                    for (int i = 0; i < data.FactionOnlineList.Count; i++)
                    {
                        var faction = data.FactionOnlineList[i];
                        AddFactionToPool(pool, keys, faction?.LabelCap, faction?.DefName, faction?.loadID ?? 0);
                    }
                }
                else
                {
                    for (int i = 0; i < data.FactionOnlineList.Count; i++)
                    {
                        var faction = data.FactionOnlineList[i];
                        TryUpdateFactionLoadId(pool, faction?.LabelCap, faction?.DefName, faction?.loadID ?? 0);
                    }
                }
            }

            if (includeSyntheticFallback && pool.Count < 2)
            {
                AddFactionToPool(pool, keys, "Пираты", "Pirate", 0);
                AddFactionToPool(pool, keys, "Союз аутлендеров", "OutlanderCivil", 0);
            }

            return pool;
        }

        private static void AddFactionToPool(List<FactionOnline> pool
            , HashSet<string> keys
            , string labelRaw
            , string defRaw
            , int loadId)
        {
            if (pool == null || keys == null) return;

            var label = NormalizeFactionLabel(labelRaw);
            if (string.IsNullOrWhiteSpace(label)) return;

            var def = NormalizeFactionDef(defRaw, label);
            var key = (def + "|" + label).ToLowerInvariant();
            if (!keys.Add(key))
            {
                TryUpdateFactionLoadId(pool, label, def, loadId);
                return;
            }

            pool.Add(new FactionOnline()
            {
                LabelCap = label,
                DefName = def,
                loadID = loadId
            });
        }

        private static void TryUpdateFactionLoadId(List<FactionOnline> pool, string labelRaw, string defRaw, int loadId)
        {
            if (pool == null || pool.Count == 0 || loadId <= 0) return;

            var label = NormalizeFactionLabel(labelRaw);
            if (string.IsNullOrWhiteSpace(label)) return;
            var def = NormalizeFactionDef(defRaw, label);
            if (string.IsNullOrWhiteSpace(def)) return;

            var existing = pool.FirstOrDefault(f =>
                string.Equals(f?.LabelCap, label, StringComparison.OrdinalIgnoreCase)
                && string.Equals(f?.DefName, def, StringComparison.OrdinalIgnoreCase));
            if (existing == null) return;
            if (existing.loadID > 0) return;
            existing.loadID = loadId;
        }

        private static string NormalizeFactionLabel(string label)
        {
            return string.IsNullOrWhiteSpace(label)
                ? string.Empty
                : label.Trim();
        }

        private static string NormalizeFactionDef(string def, string label)
        {
            if (!string.IsNullOrWhiteSpace(def)) return def.Trim();

            var safe = ToSafeId(label);
            return string.IsNullOrWhiteSpace(safe)
                ? "unknown_faction"
                : safe;
        }

        private static string ToSafeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var chars = value.Trim().ToLowerInvariant()
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
                .ToArray();
            var safe = new string(chars).Trim('_');
            while (safe.Contains("__")) safe = safe.Replace("__", "_");
            return safe;
        }

        private static int PickDebugTile(BaseContainer data)
        {
            var free = GetFreeKnownTiles(data);
            if (free.Count > 0) return Pick(free);

            var known = data.StorytellerKnownTiles
                .Where(t => t > 0)
                .Distinct()
                .ToList();
            if (known.Count > 0) return Pick(known);
            return 0;
        }

        private static WorldObjectOnline CreateDebugPoint(BaseContainer data
            , DateTime now
            , StorySpawnKind kind
            , string forcedFactionLabel = null
            , string forcedFactionDef = null)
        {
            var factionPool = GetFactionPool(data, true);
            FactionOnline faction = null;
            if (factionPool.Count > 0)
            {
                var forcedLabel = NormalizeFactionLabel(forcedFactionLabel);
                var forcedDef = string.IsNullOrWhiteSpace(forcedFactionDef)
                    ? string.Empty
                    : forcedFactionDef.Trim();

                if (!string.IsNullOrWhiteSpace(forcedDef))
                {
                    faction = factionPool.FirstOrDefault(f =>
                        string.Equals(f?.DefName, forcedDef, StringComparison.OrdinalIgnoreCase));
                }
                if (faction == null && !string.IsNullOrWhiteSpace(forcedLabel))
                {
                    faction = factionPool.FirstOrDefault(f =>
                        string.Equals(f?.LabelCap, forcedLabel, StringComparison.OrdinalIgnoreCase));
                }
                if (faction == null)
                {
                    faction = Pick(factionPool);
                }
            }

            var factionLabel = string.IsNullOrWhiteSpace(forcedFactionLabel)
                ? faction?.LabelCap?.Trim()
                : forcedFactionLabel.Trim();
            if (string.IsNullOrWhiteSpace(factionLabel)) factionLabel = "Пираты";

            var factionDef = string.IsNullOrWhiteSpace(forcedFactionDef)
                ? faction?.DefName
                : forcedFactionDef.Trim();
            if (string.IsNullOrWhiteSpace(factionDef)) factionDef = "Pirate";

            var loadId = faction?.loadID ?? 0;
            var tile = PickDebugTile(data);
            if (tile <= 0)
            {
                if (TryReuseExistingPoint(data, now, kind, factionLabel, factionDef, loadId, out var reusedPoint))
                {
                    return reusedPoint;
                }
                return null;
            }

            var point = new WorldObjectOnline()
            {
                Name = BuildPointName(data, kind, factionLabel, factionDef),
                Tile = tile,
                FactionGroup = factionLabel,
                FactionDef = factionDef,
                loadID = loadId,
                ServerGenerated = true,
                StoryType = GetStoryType(kind),
                ExpireAtUtc = IsTemporary(kind) ? now.AddHours(GetLifetimeHours(kind)) : DateTime.MinValue,
                StoryLevel = kind == StorySpawnKind.Settlement ? 1 : 0,
                StoryNextActionUtc = kind == StorySpawnKind.Settlement
                    ? now.AddMinutes(GetSettlementActionCooldownMinutes())
                    : DateTime.MinValue,
                StorySeed = BuildStorySeed(kind, tile, factionDef)
            };
            if (kind == StorySpawnKind.Settlement)
            {
                ApplySettlementName(point);
            }
            point.Name = AttachSeedToName(point.Name, point.StorySeed);

            data.WorldObjectOnlineList.Add(point);
            return point;
        }

        private static bool TryReuseExistingPoint(BaseContainer data
            , DateTime now
            , StorySpawnKind kind
            , string factionLabel
            , string factionDef
            , int loadId
            , out WorldObjectOnline point)
        {
            point = null;
            var candidates = (data.WorldObjectOnlineList ?? new List<WorldObjectOnline>())
                .Where(p => p != null && p.Tile > 0)
                .ToList();
            if (candidates.Count == 0) return false;

            var target = Pick(candidates);
            target.FactionGroup = string.IsNullOrWhiteSpace(factionLabel) ? target.FactionGroup : factionLabel;
            target.FactionDef = string.IsNullOrWhiteSpace(factionDef) ? target.FactionDef : factionDef;
            if (loadId > 0) target.loadID = loadId;

            target.ServerGenerated = true;
            target.StoryType = GetStoryType(kind);
            target.ExpireAtUtc = IsTemporary(kind) ? now.AddHours(GetLifetimeHours(kind)) : DateTime.MinValue;
            target.StoryLevel = kind == StorySpawnKind.Settlement ? 1 : 0;
            target.StoryNextActionUtc = kind == StorySpawnKind.Settlement
                ? now.AddMinutes(GetSettlementActionCooldownMinutes())
                : DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(target.StorySeed))
            {
                target.StorySeed = BuildStorySeed(kind, target.Tile, target.FactionDef);
            }
            target.Name = BuildPointName(data, kind, target.FactionGroup, target.FactionDef);
            if (kind == StorySpawnKind.Settlement)
            {
                ApplySettlementName(target);
            }
            target.Name = AttachSeedToName(target.Name, target.StorySeed);

            point = target;
            return true;
        }

        private static string NormalizeDebugMode(string mode)
        {
            var value = (mode ?? string.Empty).Trim().ToLowerInvariant();
            if (value == "spawn" || value == "точка" || value == "лагерь") return "spawn";
            if (value == "spawn_city" || value == "city_spawn" || value == "city" || value == "город" || value == "городспавн") return "spawn_city";
            if (value == "grow_city" || value == "city_grow" || value == "citygrow" || value == "ростгорода" || value == "growcity") return "grow_city";
            if (value == "evolve" || value == "эволюция" || value == "рост") return "evolve";
            if (value == "spread" || value == "экспансия" || value == "expand") return "spread";
            if (value == "conflict" || value == "конфликт" || value == "war") return "conflict";
            if (value == "diplomacy" || value == "дипломатия") return "diplomacy";
            if (value == "log" || value == "event" || value == "событие") return "log";
            return "random";
        }

        private static void NormalizeStoryObjects(BaseContainer data, DateTime now)
        {
            if (data?.WorldObjectOnlineList == null || data.WorldObjectOnlineList.Count == 0) return;

            var cooldownMinutes = GetSettlementActionCooldownMinutes();
            for (int i = 0; i < data.WorldObjectOnlineList.Count; i++)
            {
                var point = data.WorldObjectOnlineList[i];
                if (point == null) continue;

                 if (point.ServerGenerated)
                 {
                     if (string.IsNullOrWhiteSpace(point.StorySeed))
                     {
                         point.StorySeed = BuildStorySeed(ResolveSpawnKind(point.StoryType), point.Tile, point.FactionDef);
                     }
                     point.Name = AttachSeedToName(point.Name, point.StorySeed);
                 }

                if (IsSettlementType(point.StoryType))
                {
                    if (point.StoryLevel < 1) point.StoryLevel = 1;
                    if (point.StoryNextActionUtc == DateTime.MinValue)
                    {
                        var randomDelay = Math.Max(1, NextInt(Math.Max(2, cooldownMinutes)));
                        point.StoryNextActionUtc = now.AddMinutes(randomDelay);
                    }
                }
                else
                {
                    point.StoryLevel = 0;
                    point.StoryNextActionUtc = DateTime.MinValue;
                }
            }
        }

        private static void RemoveExpiredObjects(BaseContainer data, DateTime now)
        {
            if (data.WorldObjectOnlineList == null || data.WorldObjectOnlineList.Count == 0) return;

            for (int i = 0; i < data.WorldObjectOnlineList.Count; i++)
            {
                var item = data.WorldObjectOnlineList[i];
                if (item == null
                    || !item.ServerGenerated
                    || item.ExpireAtUtc == DateTime.MinValue
                    || item.ExpireAtUtc > now)
                {
                    continue;
                }

                data.WorldObjectOnlineList.RemoveAt(i--);
                var kindName = GetTemporaryPointKindLabel(item.StoryType);
                AppendStoryEvent(data
                    , "Сюжет мира"
                    , Flavor(
                        $"{kindName} \"{item.Name}\" свернулся и был оставлен.",
                        $"{kindName} \"{item.Name}\" снялся с позиции и ушел с карты.",
                        $"Следы активности угасли: {kindName.ToLowerInvariant()} \"{item.Name}\" больше не действует.")
                    , item.Tile
                    , "storyteller");
            }
        }

        private static void TryUpgradeCamps(BaseContainer data, DateTime now)
        {
            if (data.WorldObjectOnlineList == null || data.WorldObjectOnlineList.Count == 0) return;

            var upgradeChance = GetCampUpgradeChancePercent();
            if (upgradeChance <= 0) return;

            for (int i = 0; i < data.WorldObjectOnlineList.Count; i++)
            {
                var point = data.WorldObjectOnlineList[i];
                if (point == null
                    || !point.ServerGenerated
                    || point.ExpireAtUtc == DateTime.MinValue
                    || point.ExpireAtUtc <= now
                    || IsSettlementType(point.StoryType))
                {
                    continue;
                }

                var oldStoryType = point.StoryType;
                var halfLifeMinutes = GetHalfLifeMinutes(oldStoryType);
                if ((point.ExpireAtUtc - now).TotalMinutes > halfLifeMinutes) continue;
                if (!Roll(upgradeChance)) continue;

                point.StoryType = "settlement";
                point.ExpireAtUtc = DateTime.MinValue;
                point.StoryLevel = 1;
                point.StoryNextActionUtc = now.AddMinutes(GetSettlementActionCooldownMinutes());
                ApplySettlementName(point);

                var kindName = GetTemporaryPointKindLabel(oldStoryType);
                AppendStoryEvent(data
                    , "Сюжет мира"
                    , Flavor(
                        $"{kindName} окреп и превратился в постоянное поселение: {point.Name}.",
                        $"{kindName} разросся и закрепился на карте как поселение {point.Name}.",
                        $"Из временной стоянки выросла новая опорная точка: {point.Name}.")
                    , point.Tile
                    , "storyteller");
            }
        }

        private static void TryEvolveSettlements(BaseContainer data, DateTime now)
        {
            if (data.WorldObjectOnlineList == null || data.WorldObjectOnlineList.Count == 0) return;

            var evolveChance = GetSettlementEvolutionChancePercent();
            var maxLevel = GetSettlementMaxLevel();
            if (evolveChance <= 0 || maxLevel <= 1) return;

            for (int i = 0; i < data.WorldObjectOnlineList.Count; i++)
            {
                var point = data.WorldObjectOnlineList[i];
                if (point == null
                    || !point.ServerGenerated
                    || !IsSettlementType(point.StoryType)
                    || point.ExpireAtUtc != DateTime.MinValue)
                {
                    continue;
                }

                var level = GetSettlementLevel(point);
                if (level >= maxLevel) continue;
                if (point.StoryNextActionUtc > now) continue;

                var chance = evolveChance + Math.Max(0, (maxLevel - level) * 2);
                if (!Roll(chance)) continue;

                var oldName = point.Name;
                var evolveKey = $"settlement_evolve:{point.Tile}:{Math.Min(maxLevel, level + 1)}";
                if (!CanUseStoryKey(data, evolveKey, 180)) continue;
                point.StoryLevel = Math.Min(maxLevel, level + 1);
                point.StoryNextActionUtc = now.AddMinutes(GetSettlementActionCooldownMinutes());
                ApplySettlementName(point);

                var levelLabel = GetSettlementTierLabel(point.StoryLevel).ToLowerInvariant();
                AppendStoryEvent(data
                    , "Сюжет мира"
                    , Flavor(
                        $"Поселение \"{oldName}\" усилилось. Новый статус: {levelLabel} ({point.Name}).",
                        $"В \"{oldName}\" завершен этап роста: теперь это {levelLabel} ({point.Name}).",
                        $"Рост инфраструктуры дал результат: {point.Name} выходит на уровень «{levelLabel}».")
                    , point.Tile
                    , "storyteller"
                    , evolveKey
                    , 180);
            }
        }

        private static void TrySpreadSettlements(BaseContainer data, DateTime now)
        {
            if (data.WorldObjectOnlineList == null || data.WorldObjectOnlineList.Count == 0) return;

            var spreadChance = GetSettlementSpreadChancePercent();
            if (spreadChance <= 0) return;

            var currentServerGenerated = data.WorldObjectOnlineList.Count(o => o?.ServerGenerated == true);
            if (currentServerGenerated >= GetMaxWorldObjects()) return;

            var freeTiles = GetFreeKnownTiles(data);
            if (freeTiles.Count == 0) return;

            var sources = data.WorldObjectOnlineList
                .Where(p => p != null
                    && p.ServerGenerated
                    && IsSettlementType(p.StoryType)
                    && p.ExpireAtUtc == DateTime.MinValue
                    && GetSettlementLevel(p) >= 2
                    && p.StoryNextActionUtc <= now)
                .ToList();
            if (sources.Count == 0) return;

            var maxPerFaction = Math.Max(2, GetMaxWorldObjects() / 3);
            var spawnedInTick = 0;
            while (sources.Count > 0
                && freeTiles.Count > 0
                && currentServerGenerated < GetMaxWorldObjects()
                && spawnedInTick < 1)
            {
                var source = Pick(sources);
                sources.Remove(source);
                if (source == null) continue;

                var sourceLevel = GetSettlementLevel(source);
                var sourceFactionObjects = data.WorldObjectOnlineList.Count(p =>
                    p != null
                    && p.ServerGenerated
                    && p.FactionDef == source.FactionDef
                    && p.FactionGroup == source.FactionGroup);
                if (sourceFactionObjects >= maxPerFaction) continue;

                var chance = spreadChance + Math.Max(0, (sourceLevel - 2) * 6);
                if (!Roll(chance)) continue;

                var spawnKind = PickSpreadSpawnKind(data, source, sourceLevel);
                var spreadRadius = GetSpreadRadiusTiles(spawnKind, sourceLevel);
                var tile = PickSpreadTile(freeTiles, source.Tile, spreadRadius, true);
                if (tile <= 0) continue;
                freeTiles.Remove(tile);
                var spreadKey = $"settlement_spread:{source.Tile}:{tile}:{GetStoryType(spawnKind)}";
                if (!CanUseStoryKey(data, spreadKey, 180)) continue;

                var point = new WorldObjectOnline()
                {
                    Name = BuildPointName(data, spawnKind, source.FactionGroup, source.FactionDef),
                    Tile = tile,
                    FactionGroup = source.FactionGroup,
                    FactionDef = source.FactionDef,
                    loadID = source.loadID,
                    ServerGenerated = true,
                    StoryType = GetStoryType(spawnKind),
                    ExpireAtUtc = IsTemporary(spawnKind) ? now.AddHours(GetLifetimeHours(spawnKind)) : DateTime.MinValue,
                    StoryLevel = spawnKind == StorySpawnKind.Settlement ? 1 : 0,
                    StoryNextActionUtc = spawnKind == StorySpawnKind.Settlement
                        ? now.AddMinutes(GetSettlementActionCooldownMinutes())
                        : DateTime.MinValue,
                    StorySeed = BuildStorySeed(spawnKind, tile, source.FactionDef)
                };
                if (spawnKind == StorySpawnKind.Settlement)
                {
                    ApplySettlementName(point);
                }
                point.Name = AttachSeedToName(point.Name, point.StorySeed);

                data.WorldObjectOnlineList.Add(point);
                currentServerGenerated++;
                spawnedInTick++;
                source.StoryNextActionUtc = now.AddMinutes(GetSettlementActionCooldownMinutes());

                var spreadText = spawnKind == StorySpawnKind.Settlement
                    ? Flavor(
                        $"{source.Name} направило переселенцев и основало новую точку: {point.Name}.",
                        $"Влияние {source.Name} растет: возникло новое поселение {point.Name}.",
                        $"{source.Name} закрепило экспансию, открыв поселение {point.Name}.")
                    : Flavor(
                        $"{source.Name} расширило влияние. На карте появился {GetPointKindLabel(point.StoryType).ToLowerInvariant()}: {point.Name}.",
                        $"От {source.Name} отделился новый контур контроля: {GetPointKindLabel(point.StoryType).ToLowerInvariant()} {point.Name}.",
                        $"{source.Name} усиливает присутствие в регионе: добавлена точка {point.Name}.");
                AppendStoryEvent(data
                    , "Сюжет мира"
                    , spreadText
                    , point.Tile
                    , "storyteller"
                    , spreadKey
                    , 180);
            }
        }

        private static void TryEmitDiplomaticEvent(BaseContainer data, DateTime now)
        {
            if (!Roll(GetDiplomacyChancePercent())) return;

            var factions = GetFactionPool(data, false);
            if (factions.Count < 2) return;

            var first = Pick(factions);
            var secondCandidates = factions
                .Where(f => !ReferenceEquals(f, first)
                    && (f.DefName != first.DefName || f.LabelCap != first.LabelCap))
                .ToList();
            if (secondCandidates.Count == 0) return;

            var second = Pick(secondCandidates);
            var a = first.LabelCap.Trim();
            var b = second.LabelCap.Trim();

            var keyPrefix = Roll(50) ? "diplomacy_trade" : "diplomacy_tension";
            var isTrade = keyPrefix == "diplomacy_trade";
            var pairKey = BuildFactionPairKey(a, first.DefName, b, second.DefName);
            if (string.IsNullOrWhiteSpace(pairKey))
            {
                pairKey = BuildFallbackPairKeyByLabels(a, b);
            }
            var diplomacyKey = $"{keyPrefix}:{pairKey}:{DateTime.UtcNow:yyyyMMdd}";
            if (!CanUseStoryKey(data, diplomacyKey, 120))
            {
                return;
            }
            if (!ApplyDiplomaticWorldEffect(data
                , now
                , a
                , first.DefName
                , first.loadID
                , b
                , second.DefName
                , second.loadID
                , isTrade
                , false
                , out var effectText
                , out var effectTile))
            {
                return;
            }

            AppendStoryEvent(data
                , "Сюжет мира"
                , effectText
                , effectTile
                , "diplomacy"
                , diplomacyKey
                , 120);
        }

        private static void TryResolveFactionConflict(BaseContainer data, DateTime now)
        {
            if (data.WorldObjectOnlineList == null || data.WorldObjectOnlineList.Count < 2) return;
            if (!Roll(GetConflictChancePercent())) return;

            var generated = data.WorldObjectOnlineList
                .Where(p => p != null && p.ServerGenerated && p.Tile > 0)
                .ToList();
            if (generated.Count < 2) return;

            var attacker = Pick(generated);
            var defenders = generated
                .Where(p => !ReferenceEquals(p, attacker)
                    && (!string.IsNullOrEmpty(p.FactionDef) || !string.IsNullOrEmpty(p.FactionGroup))
                    && (p.FactionDef != attacker.FactionDef || p.FactionGroup != attacker.FactionGroup))
                .ToList();
            if (defenders.Count == 0) return;

            var target = Pick(defenders);
            var attackerName = string.IsNullOrEmpty(attacker.FactionGroup) ? "Неизвестная фракция" : attacker.FactionGroup;
            var targetName = string.IsNullOrEmpty(target.FactionGroup) ? "неизвестной фракции" : target.FactionGroup;
            var conflictPairKey = BuildFactionPairKey(attackerName, attacker.FactionDef, targetName, target.FactionDef);
            if (string.IsNullOrWhiteSpace(conflictPairKey))
            {
                conflictPairKey = BuildFallbackPairKeyByLabels(attackerName, targetName);
            }
            var conflictKey = $"conflict:{conflictPairKey}:{DateTime.UtcNow:yyyyMMdd}";

            if (IsSettlementType(target.StoryType) && Roll(35))
            {
                target.FactionDef = attacker.FactionDef;
                target.FactionGroup = attacker.FactionGroup;
                target.StoryLevel = Math.Max(1, GetSettlementLevel(target));
                target.StoryNextActionUtc = now.AddMinutes(GetSettlementActionCooldownMinutes());
                ApplySettlementName(target);
                AppendStoryEvent(data
                    , "Сюжет мира"
                    , Flavor(
                        $"{attackerName} перехватили контроль над точкой фракции {targetName}: {target.Name}.",
                        $"После штурма силы {attackerName} заняли поселение {target.Name}, ранее принадлежавшее {targetName}.",
                        $"{target.Name} сменило хозяина: теперь точка под влиянием {attackerName}.")
                    , target.Tile
                    , "storyteller"
                    , conflictKey);
            }
            else
            {
                data.WorldObjectOnlineList.Remove(target);
                AppendStoryEvent(data
                    , "Сюжет мира"
                    , Flavor(
                        $"{attackerName} вытеснили силы фракции {targetName}. Точка \"{target.Name}\" исчезла с карты.",
                        $"Столкновение {attackerName} и {targetName} завершилось падением точки \"{target.Name}\".",
                        $"Линия фронта сместилась: \"{target.Name}\" больше не удерживается силами {targetName}.")
                    , target.Tile
                    , "storyteller"
                    , conflictKey);
            }
        }

        private static void TrySpawnGlobalPoint(BaseContainer data, DateTime now)
        {
            if (data.StorytellerKnownTiles == null || data.StorytellerKnownTiles.Count == 0) return;

            var currentServerGenerated = data.WorldObjectOnlineList?.Count(o => o?.ServerGenerated == true) ?? 0;
            if (currentServerGenerated >= GetMaxWorldObjects()) return;

            if (!Roll(GetSpawnChancePercent())) return;

            var freeTiles = GetFreeKnownTiles(data);
            if (freeTiles.Count == 0) return;

            var factions = GetFactionPool(data, false);
            if (factions.Count == 0) return;

            var faction = Pick(factions);
            var playerTiles = data.WorldObjects
                .Where(o => o != null && o.Tile > 0)
                .Select(o => o.Tile)
                .Distinct()
                .ToList();

            var spawnKind = PickSpawnKind(playerTiles.Count > 0);
            var freePlayerTiles = freeTiles.Where(t => playerTiles.Contains(t)).ToList();
            var tile = spawnKind == StorySpawnKind.PlayerFrontierCamp && freePlayerTiles.Count > 0
                ? Pick(freePlayerTiles)
                : Pick(freeTiles);

            var pointName = BuildPointName(data, spawnKind, faction.LabelCap, faction.DefName);

            var point = new WorldObjectOnline()
            {
                Name = pointName,
                Tile = tile,
                FactionGroup = faction.LabelCap,
                FactionDef = faction.DefName,
                loadID = faction.loadID,
                ServerGenerated = true,
                StoryType = GetStoryType(spawnKind),
                ExpireAtUtc = IsTemporary(spawnKind) ? now.AddHours(GetLifetimeHours(spawnKind)) : DateTime.MinValue,
                StoryLevel = spawnKind == StorySpawnKind.Settlement ? 1 : 0,
                StoryNextActionUtc = spawnKind == StorySpawnKind.Settlement
                    ? now.AddMinutes(GetSettlementActionCooldownMinutes())
                    : DateTime.MinValue,
                StorySeed = BuildStorySeed(spawnKind, tile, faction.DefName)
            };
            if (spawnKind == StorySpawnKind.Settlement)
            {
                ApplySettlementName(point);
            }
            point.Name = AttachSeedToName(point.Name, point.StorySeed);

            if (data.WorldObjectOnlineList == null) data.WorldObjectOnlineList = new List<WorldObjectOnline>();
            data.WorldObjectOnlineList.Add(point);

            var pointNameForEvent = point.Name;
            switch (spawnKind)
            {
                case StorySpawnKind.PlayerFrontierCamp:
                    AppendStoryEvent(data
                        , "Сюжет мира"
                        , Flavor(
                            "У границ игроков замечен новый лагерь: " + pointNameForEvent,
                            "Разведка фиксирует движение у рубежей игроков: появился лагерь " + pointNameForEvent,
                            "На пограничных маршрутах возникла новая стоянка: " + pointNameForEvent)
                        , tile
                        , "storyteller");
                    break;

                case StorySpawnKind.TradeCamp:
                    AppendStoryEvent(data
                        , "Сюжет мира"
                        , Flavor(
                            "На карте появился торговый лагерь: " + pointNameForEvent,
                            "Караванные пути оживились: открыт торговый лагерь " + pointNameForEvent,
                            "В регионе разворачивается новый центр обмена: " + pointNameForEvent)
                        , tile
                        , "storyteller");
                    break;

                case StorySpawnKind.Outpost:
                    AppendStoryEvent(data
                        , "Сюжет мира"
                        , Flavor(
                            "На карте появился новый форпост: " + pointNameForEvent,
                            "Военное присутствие усилилось: развернут форпост " + pointNameForEvent,
                            "Фракции укрепляют контроль территории: возник форпост " + pointNameForEvent)
                        , tile
                        , "storyteller");
                    break;

                default:
                    AppendStoryEvent(data
                        , "Сюжет мира"
                        , Flavor(
                            "Основано новое поселение: " + point.Name,
                            "На карте закрепилась новая постоянная точка: " + point.Name,
                            "Фракция завершила колонизацию участка: " + point.Name)
                        , tile
                        , "storyteller");
                    break;
            }
        }

        private static bool ApplyDiplomaticWorldEffect(BaseContainer data
            , DateTime now
            , string firstLabel
            , string firstDef
            , int firstLoadId
            , string secondLabel
            , string secondDef
            , int secondLoadId
            , bool tradeAgreement
            , bool force
            , out string effectText
            , out int effectTile)
        {
            effectText = string.Empty;
            effectTile = 0;

            var a = string.IsNullOrWhiteSpace(firstLabel) ? "Неизвестная фракция" : firstLabel.Trim();
            var b = string.IsNullOrWhiteSpace(secondLabel) ? "Неизвестная фракция" : secondLabel.Trim();

            if (tradeAgreement)
            {
                var owner = Roll(50)
                    ? Tuple.Create(a, firstDef, firstLoadId)
                    : Tuple.Create(b, secondDef, secondLoadId);

                if (TryCreateServerPointOnFreeTile(data
                    , now
                    , StorySpawnKind.TradeCamp
                    , owner.Item1
                    , owner.Item2
                    , owner.Item3
                    , out var tradePoint
                    , force))
                {
                    effectTile = tradePoint.Tile;
                    effectText = Flavor(
                        $"После переговоров {a} и {b} на карте открылся торговый лагерь: {tradePoint.Name}.",
                        $"Союз между {a} и {b} оживил торговлю. Появилась новая точка обмена: {tradePoint.Name}.",
                        $"Фракции {a} и {b} закрепили договор делом: на карте развернут торговый лагерь {tradePoint.Name}.");
                    return true;
                }

                var settlements = (data.WorldObjectOnlineList ?? new List<WorldObjectOnline>())
                    .Where(p => p != null
                        && p.ServerGenerated
                        && IsSettlementType(p.StoryType)
                        && (IsFactionMatch(p, a, firstDef) || IsFactionMatch(p, b, secondDef)))
                    .OrderBy(GetSettlementLevel)
                    .ToList();
                if (settlements.Count > 0)
                {
                    var settlement = Pick(settlements);
                    var oldLevel = GetSettlementLevel(settlement);
                    settlement.StoryLevel = Math.Min(GetSettlementMaxLevel(), oldLevel + 1);
                    settlement.StoryNextActionUtc = now.AddMinutes(GetSettlementActionCooldownMinutes());
                    ApplySettlementName(settlement);
                    effectTile = settlement.Tile;
                    effectText = Flavor(
                        $"Торговое соглашение {a} и {b} подстегнуло рост. Поселение {settlement.Name} укрепилось.",
                        $"Караваны пошли плотнее: {settlement.Name} получает экономический подъем после сделки между {a} и {b}.",
                        $"Сделка между {a} и {b} дала эффект на месте: {settlement.Name} быстро расширяется.");
                    return true;
                }

                return false;
            }

            if (TryCreateServerPointOnFreeTile(data
                , now
                , StorySpawnKind.Outpost
                , a
                , firstDef
                , firstLoadId
                , out var outpost
                , force))
            {
                effectTile = outpost.Tile;
                effectText = Flavor(
                    $"Отношения {a} и {b} испортились: фракция {a} выдвинула форпост {outpost.Name}.",
                    $"Дипломатический кризис между {a} и {b} перешел в силовую фазу. Появился форпост {outpost.Name}.",
                    $"{a} и {b} перешли от слов к давлению: на карте развернут форпост {outpost.Name}.");
                return true;
            }

            var opponentPoints = (data.WorldObjectOnlineList ?? new List<WorldObjectOnline>())
                .Where(p => p != null
                    && p.ServerGenerated
                    && IsFactionMatch(p, b, secondDef))
                .ToList();
            if (opponentPoints.Count > 0)
            {
                var target = Pick(opponentPoints);
                if (IsSettlementType(target.StoryType) && GetSettlementLevel(target) > 1)
                {
                    target.StoryLevel = GetSettlementLevel(target) - 1;
                    target.StoryNextActionUtc = now.AddMinutes(GetSettlementActionCooldownMinutes());
                    ApplySettlementName(target);
                    effectTile = target.Tile;
                    effectText = Flavor(
                        $"Эскалация между {a} и {b} ударила по инфраструктуре. Поселение {target.Name} откатилось в развитии.",
                        $"Из-за конфликта {a} и {b} поселение {target.Name} потеряло темп и часть влияния.",
                        $"Напряжение {a} и {b} привело к откату: {target.Name} снижает уровень развития.");
                    return true;
                }

                data.WorldObjectOnlineList.Remove(target);
                effectTile = target.Tile;
                effectText = Flavor(
                    $"Кризис между {a} и {b} привел к потере позиции: точка \"{target.Name}\" оставлена.",
                    $"После серии столкновений {a} и {b} точка \"{target.Name}\" исчезла с карты.",
                    $"Горячая фаза противостояния {a} и {b}: \"{target.Name}\" больше не удерживается.");
                return true;
            }

            return false;
        }

        private static bool TryCreateServerPointOnFreeTile(BaseContainer data
            , DateTime now
            , StorySpawnKind kind
            , string factionLabel
            , string factionDef
            , int loadId
            , out WorldObjectOnline point
            , bool ignoreMaxObjectsLimit = false)
        {
            point = null;
            if (data == null) return false;

            if (!ignoreMaxObjectsLimit)
            {
                var currentServerGenerated = data.WorldObjectOnlineList?.Count(o => o?.ServerGenerated == true) ?? 0;
                if (currentServerGenerated >= GetMaxWorldObjects()) return false;
            }

            var freeTiles = GetFreeKnownTiles(data);
            if (freeTiles.Count == 0)
            {
                return TryReuseExistingPoint(data
                    , now
                    , kind
                    , factionLabel
                    , factionDef
                    , loadId
                    , out point);
            }

            var tile = Pick(freeTiles);
            var label = string.IsNullOrWhiteSpace(factionLabel) ? "Неизвестной фракции" : factionLabel.Trim();
            var def = string.IsNullOrWhiteSpace(factionDef) ? "unknown_faction" : factionDef;

            point = new WorldObjectOnline()
            {
                Name = BuildPointName(data, kind, label, def),
                Tile = tile,
                FactionGroup = label,
                FactionDef = def,
                loadID = loadId,
                ServerGenerated = true,
                StoryType = GetStoryType(kind),
                ExpireAtUtc = IsTemporary(kind) ? now.AddHours(GetLifetimeHours(kind)) : DateTime.MinValue,
                StoryLevel = kind == StorySpawnKind.Settlement ? 1 : 0,
                StoryNextActionUtc = kind == StorySpawnKind.Settlement
                    ? now.AddMinutes(GetSettlementActionCooldownMinutes())
                    : DateTime.MinValue,
                StorySeed = BuildStorySeed(kind, tile, def)
            };
            if (kind == StorySpawnKind.Settlement)
            {
                ApplySettlementName(point);
            }
            point.Name = AttachSeedToName(point.Name, point.StorySeed);

            if (data.WorldObjectOnlineList == null) data.WorldObjectOnlineList = new List<WorldObjectOnline>();
            data.WorldObjectOnlineList.Add(point);
            return true;
        }

        private static bool IsFactionMatch(WorldObjectOnline point, string label, string def)
        {
            if (point == null) return false;
            if (!string.IsNullOrWhiteSpace(def)
                && string.Equals(point.FactionDef, def, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(label)
                && string.Equals(point.FactionGroup, label, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private static string Flavor(params string[] variants)
        {
            if (variants == null || variants.Length == 0) return string.Empty;
            if (variants.Length == 1) return variants[0] ?? string.Empty;
            return variants[NextInt(variants.Length)] ?? string.Empty;
        }

        private static int NextCode()
        {
            lock (Rnd)
            {
                return Rnd.Next(1000, 9999);
            }
        }

        private static T Pick<T>(List<T> list)
        {
            lock (Rnd)
            {
                return list[Rnd.Next(list.Count)];
            }
        }

        private static bool Roll(int chancePercent)
        {
            if (chancePercent <= 0) return false;
            if (chancePercent >= 100) return true;
            lock (Rnd)
            {
                return Rnd.Next(100) < chancePercent;
            }
        }

        private static bool IsEnabled()
        {
            var settings = ServerManager.ServerSettings.GeneralSettings;
            return settings.StorytellerEnable;
        }

        private static int GetTickIntervalSeconds()
        {
            return ReadSetting(s => s.StorytellerTickIntervalSeconds, 90, 5, 3600);
        }

        private static int GetSpawnChancePercent()
        {
            return ReadSetting(s => s.StorytellerSpawnChancePercent, 30, 0, 100);
        }

        private static int GetMaxWorldObjects()
        {
            return ReadSetting(s => s.StorytellerMaxWorldObjects, 24, 1, 500);
        }

        private static int GetCampLifetimeHours()
        {
            return ReadSetting(s => s.StorytellerCampLifetimeHours, 72, 1, 2160);
        }

        private static int GetOutpostLifetimeHours()
        {
            return ReadSetting(s => s.StorytellerOutpostLifetimeHours, 120, 1, 2160);
        }

        private static int GetCampUpgradeChancePercent()
        {
            return ReadSetting(s => s.StorytellerCampUpgradeChancePercent, 18, 0, 100);
        }

        private static int GetSettlementEvolutionChancePercent()
        {
            return ReadSetting(s => s.StorytellerSettlementEvolutionChancePercent, 11, 1, 100);
        }

        private static int GetSettlementSpreadChancePercent()
        {
            return ReadSetting(s => s.StorytellerSettlementSpreadChancePercent, 15, 1, 100);
        }

        private static int GetSpreadCityEnemyMilitaryBaseChancePercent()
        {
            return ReadSetting(s => s.StorytellerSpreadCityEnemyMilitaryBaseChancePercent, 85, 1, 100);
        }

        private static int GetSpreadCityAllyTradeCampChancePercent()
        {
            return ReadSetting(s => s.StorytellerSpreadCityAllyTradeCampChancePercent, 75, 1, 100);
        }

        private static int GetSpreadLowEnemyOutpostChancePercent()
        {
            return ReadSetting(s => s.StorytellerSpreadLowEnemyOutpostChancePercent, 80, 1, 100);
        }

        private static int GetSpreadLowAllyTradeCampChancePercent()
        {
            return ReadSetting(s => s.StorytellerSpreadLowAllyTradeCampChancePercent, 70, 1, 100);
        }

        private static int GetSettlementActionCooldownMinutes()
        {
            return ReadSetting(s => s.StorytellerSettlementActionCooldownMinutes, 540, 1, 60 * 24 * 30);
        }

        private static int GetSpreadRadiusTiles(StorySpawnKind kind, int settlementLevel)
        {
            if (IsCityInfrastructureKind(kind))
            {
                return 10;
            }

            if (settlementLevel >= 3)
            {
                return 14;
            }

            return 12;
        }

        private static int GetSettlementMaxLevel()
        {
            return 3;
        }

        private static int GetEventHistoryLimit()
        {
            return ReadSetting(s => s.StorytellerEventHistoryLimit, 3000, 100, 100000);
        }

        private static int GetConflictChancePercent()
        {
            return ReadSetting(s => s.StorytellerConflictChancePercent, 8, 0, 100);
        }

        private static int GetDiplomacyChancePercent()
        {
            return ReadSetting(s => s.StorytellerDiplomacyChancePercent, 10, 0, 100);
        }

        private static StorySpawnKind PickSpawnKind(bool hasPlayerTiles)
        {
            var playerWeight = hasPlayerTiles
                ? ReadSetting(s => s.StorytellerPlayerCampWeightPercent, 45, 0, 1000)
                : 0;
            var tradeWeight = ReadSetting(s => s.StorytellerTradeCampWeightPercent, 30, 0, 1000);
            var settlementWeight = ReadSetting(s => s.StorytellerSettlementWeightPercent, 25, 0, 1000);
            var outpostWeight = ReadSetting(s => s.StorytellerOutpostWeightPercent, 18, 0, 1000);
            var total = playerWeight + tradeWeight + settlementWeight + outpostWeight;
            if (total <= 0) return StorySpawnKind.Settlement;

            var roll = NextInt(total);
            if (roll < playerWeight) return StorySpawnKind.PlayerFrontierCamp;
            roll -= playerWeight;
            if (roll < tradeWeight) return StorySpawnKind.TradeCamp;
            roll -= tradeWeight;
            if (roll < settlementWeight) return StorySpawnKind.Settlement;
            return StorySpawnKind.Outpost;
        }

        private static string BuildPointName(BaseContainer data, StorySpawnKind kind, string factionLabel, string factionDef = null)
        {
            var core = BuildStyledCoreName(data, factionLabel, factionDef);
            if (string.IsNullOrWhiteSpace(core))
            {
                var factionText = string.IsNullOrWhiteSpace(factionLabel) ? "Неизвестной фракции" : factionLabel.Trim();
                core = $"{factionText} #{NextCode()}";
            }

            switch (kind)
            {
                case StorySpawnKind.PlayerFrontierCamp:
                    return EnsureNamePrefix("Лагерь", core);
                case StorySpawnKind.TradeCamp:
                    return EnsureNamePrefix("Торговый лагерь", core);
                case StorySpawnKind.Outpost:
                    return EnsureNamePrefix("Форпост", core);
                case StorySpawnKind.MilitaryBase:
                    return EnsureNamePrefix("Военная база", core);
                case StorySpawnKind.Mine:
                    return EnsureNamePrefix("Шахта", core);
                case StorySpawnKind.Farm:
                    return EnsureNamePrefix("Ферма", core);
                case StorySpawnKind.IndustrialSite:
                    return EnsureNamePrefix("Промзона", core);
                case StorySpawnKind.ResearchHub:
                    return EnsureNamePrefix("Исследовательский узел", core);
                case StorySpawnKind.LogisticsHub:
                    return EnsureNamePrefix("Логистический узел", core);
                default:
                    return EnsureNamePrefix("Поселение", core);
            }
        }

        private static bool IsTemporary(StorySpawnKind kind)
        {
            return kind == StorySpawnKind.PlayerFrontierCamp
                || kind == StorySpawnKind.TradeCamp
                || kind == StorySpawnKind.Outpost;
        }

        private static int GetLifetimeHours(StorySpawnKind kind)
        {
            if (kind == StorySpawnKind.Outpost) return GetOutpostLifetimeHours();
            return GetCampLifetimeHours();
        }

        private static int GetHalfLifeMinutes(string storyType)
        {
            var totalHours = string.Equals(storyType, "outpost", StringComparison.OrdinalIgnoreCase)
                ? GetOutpostLifetimeHours()
                : GetCampLifetimeHours();
            return Math.Max(30, totalHours * 60 / 2);
        }

        private static string GetPointKindLabel(string storyType)
        {
            if (string.Equals(storyType, "trade_camp", StringComparison.OrdinalIgnoreCase)) return "Торговый лагерь";
            if (string.Equals(storyType, "outpost", StringComparison.OrdinalIgnoreCase)) return "Форпост";
            if (string.Equals(storyType, "military_base", StringComparison.OrdinalIgnoreCase)) return "Военная база";
            if (string.Equals(storyType, "mine", StringComparison.OrdinalIgnoreCase)) return "Шахта";
            if (string.Equals(storyType, "farm", StringComparison.OrdinalIgnoreCase)) return "Ферма";
            if (string.Equals(storyType, "industrial_site", StringComparison.OrdinalIgnoreCase)) return "Промзона";
            if (string.Equals(storyType, "research_hub", StringComparison.OrdinalIgnoreCase)) return "Исследовательский узел";
            if (string.Equals(storyType, "logistics_hub", StringComparison.OrdinalIgnoreCase)) return "Логистический узел";
            if (string.Equals(storyType, "settlement", StringComparison.OrdinalIgnoreCase)) return "Поселение";
            return "Лагерь";
        }

        private static string GetTemporaryPointKindLabel(string storyType)
        {
            return GetPointKindLabel(storyType);
        }

        private static bool IsSettlementType(string storyType)
        {
            return string.Equals(storyType, "settlement", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetSettlementLevel(WorldObjectOnline point)
        {
            if (point == null || !IsSettlementType(point.StoryType)) return 0;
            var maxLevel = GetSettlementMaxLevel();
            if (point.StoryLevel < 1) return 1;
            if (point.StoryLevel > maxLevel) return maxLevel;
            return point.StoryLevel;
        }

        private static string GetSettlementTierLabel(int level)
        {
            if (level <= 1) return "Поселение";
            if (level == 2) return "Укрепленное поселение";
            return "Город";
        }

        private static void ApplySettlementName(WorldObjectOnline point, BaseContainer data = null)
        {
            if (point == null || !IsSettlementType(point.StoryType)) return;

            var core = ExtractPointCoreName(point.Name);
            if (string.IsNullOrWhiteSpace(core))
            {
                core = BuildStyledCoreName(data, point.FactionGroup, point.FactionDef);
            }
            if (string.IsNullOrWhiteSpace(core))
            {
                var factionText = string.IsNullOrWhiteSpace(point.FactionGroup) ? "Неизвестной фракции" : point.FactionGroup.Trim();
                core = $"{factionText} #{NextCode()}";
            }

            point.Name = EnsureNamePrefix(GetSettlementTierLabel(GetSettlementLevel(point)), core);
            point.Name = AttachSeedToName(point.Name, point.StorySeed);
        }

        private static string BuildStyledCoreName(BaseContainer data, string factionLabel, string factionDef)
        {
            var seeds = GetFactionNameSeeds(data, factionLabel, factionDef);
            if (seeds.Count == 0) return string.Empty;

            var core = Pick(seeds);
            if (seeds.Count > 1 && Roll(35))
            {
                core = BuildCompositeCoreName(core, Pick(seeds));
            }
            if (Roll(18))
            {
                core = AddDirectionalPrefix(core);
            }

            return NormalizeCoreName(core);
        }

        private static List<string> GetFactionNameSeeds(BaseContainer data, string factionLabel, string factionDef)
        {
            var result = new List<string>();
            if (data?.WorldObjectOnlineList == null || data.WorldObjectOnlineList.Count == 0) return result;

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matched = data.WorldObjectOnlineList
                .Where(p => p != null
                    && p.Tile > 0
                    && IsFactionMatch(p, factionLabel, factionDef))
                .OrderBy(p => p.ServerGenerated ? 1 : 0)
                .ToList();

            foreach (var point in matched)
            {
                var core = NormalizeCoreName(point.Name);
                if (string.IsNullOrWhiteSpace(core)) continue;
                if (!keys.Add(core)) continue;
                result.Add(core);
                if (result.Count >= 80) break;
            }

            return result;
        }

        private static string BuildCompositeCoreName(string first, string second)
        {
            var firstWords = SplitWords(NormalizeCoreName(first));
            var secondWords = SplitWords(NormalizeCoreName(second));
            if (firstWords.Count == 0) return NormalizeCoreName(second);
            if (secondWords.Count == 0) return NormalizeCoreName(first);

            var left = firstWords[0];
            var right = secondWords[secondWords.Count - 1];
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeCoreName(string.Join(" ", firstWords.Take(Math.Min(2, firstWords.Count))));
            }

            return NormalizeCoreName(left + " " + right);
        }

        private static List<string> SplitWords(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new List<string>();
            var parts = value.Split(new[] { ' ', '\t', '-', '.', ',', ';', ':', '/', '\\', '|', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries);
            return parts
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
        }

        private static string AddDirectionalPrefix(string name)
        {
            var core = NormalizeCoreName(name);
            if (string.IsNullOrWhiteSpace(core)) return string.Empty;

            var prefixes = new[] { "Новый", "Старый", "Верхний", "Нижний", "Дальний", "Ближний" };
            var prefix = prefixes[NextInt(prefixes.Length)];
            if (core.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)) return core;
            return prefix + " " + core;
        }

        private static string NormalizeCoreName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var core = value.Trim().Trim('"', '\'');
            core = StripSeedTag(core);
            core = StripTrailingNumericCode(core);

            var prefixes = new[]
            {
                "Исследовательский узел",
                "Логистический узел",
                "Укрепленное поселение",
                "Торговый лагерь",
                "Торговый пост",
                "Военная база",
                "Поселение",
                "Форпост",
                "Промзона",
                "Шахта",
                "Ферма",
                "Лагерь",
                "Город"
            };
            for (int i = 0; i < prefixes.Length; i++)
            {
                var prefix = prefixes[i];
                if (!core.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)) continue;
                core = core.Substring(prefix.Length).Trim();
                break;
            }

            core = core.Trim().Trim('"', '\'');
            if (core.Length > 48) core = core.Substring(0, 48).Trim();
            if (core.Length < 3) return string.Empty;
            if (!core.Any(char.IsLetterOrDigit)) return string.Empty;
            return core;
        }

        private static string StripSeedTag(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var value = text.Trim();
            if (!value.EndsWith("]")) return value;

            var open = value.LastIndexOf('[');
            if (open < 0 || open >= value.Length - 1) return value;
            var tag = value.Substring(open + 1, value.Length - open - 2).Trim();
            if (tag.Length < 4 || tag.Length > 20) return value;
            if (!tag.Contains("-")) return value;
            if (!tag.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')) return value;
            return value.Substring(0, open).Trim();
        }

        private static string ExtractPointCoreName(string pointName)
        {
            return NormalizeCoreName(pointName);
        }

        private static string StripTrailingNumericCode(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var index = text.LastIndexOf('#');
            if (index < 0 || index >= text.Length - 1) return text;

            var codeText = text.Substring(index + 1).Trim();
            if (!int.TryParse(codeText, out var code) || code <= 0) return text;
            return text.Substring(0, index).Trim();
        }

        private static string EnsureNamePrefix(string prefix, string core)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return core ?? string.Empty;

            var normalizedCore = NormalizeCoreName(core);
            if (string.IsNullOrWhiteSpace(normalizedCore))
            {
                return $"{prefix} #{NextCode()}";
            }
            if (normalizedCore.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedCore;
            }

            return prefix + " " + normalizedCore;
        }

        private static string BuildStorySeed(StorySpawnKind kind, int tile, string factionDef)
        {
            var prefix = GetSeedPrefix(kind);
            var seedSource = $"{prefix}:{tile}:{factionDef ?? string.Empty}:{DateTime.UtcNow.Ticks}:{NextCode()}";
            var hash = StableSeedHash(seedSource).ToString("X6");
            return $"{prefix}-{hash}";
        }

        private static int StableSeedHash(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            unchecked
            {
                uint hash = 2166136261;
                for (var i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 16777619;
                }
                return (int)(hash & 0x7FFFFFFF);
            }
        }

        private static string GetSeedPrefix(StorySpawnKind kind)
        {
            switch (kind)
            {
                case StorySpawnKind.PlayerFrontierCamp: return "CP";
                case StorySpawnKind.TradeCamp: return "TC";
                case StorySpawnKind.Outpost: return "OP";
                case StorySpawnKind.MilitaryBase: return "MB";
                case StorySpawnKind.Mine: return "MN";
                case StorySpawnKind.Farm: return "FM";
                case StorySpawnKind.IndustrialSite: return "IN";
                case StorySpawnKind.ResearchHub: return "RS";
                case StorySpawnKind.LogisticsHub: return "LG";
                default: return "ST";
            }
        }

        private static string AttachSeedToName(string name, string seed)
        {
            if (string.IsNullOrWhiteSpace(name)) return name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(seed)) return name.Trim();

            var trimmed = name.Trim();
            var marker = "[" + seed.Trim() + "]";
            if (trimmed.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) return trimmed;
            return $"{trimmed} {marker}";
        }

        private static StorySpawnKind PickSpreadSpawnKind(BaseContainer data, WorldObjectOnline source, int settlementLevel)
        {
            var nearbyContext = AnalyzeNearbyFactionContext(data, source, 10);
            var effectiveEnemyCount = nearbyContext.EnemyCount + nearbyContext.NeutralCount;
            var enemyNearby = effectiveEnemyCount > 0;
            var allyNearby = nearbyContext.AllyCount > 0;

            if (settlementLevel >= 3)
            {
                if (enemyNearby
                    && effectiveEnemyCount >= nearbyContext.AllyCount
                    && Roll(GetSpreadCityEnemyMilitaryBaseChancePercent()))
                {
                    return StorySpawnKind.MilitaryBase;
                }
                if (allyNearby
                    && nearbyContext.AllyCount > nearbyContext.EnemyCount
                    && Roll(GetSpreadCityAllyTradeCampChancePercent()))
                {
                    return StorySpawnKind.TradeCamp;
                }
                if (enemyNearby && allyNearby)
                {
                    if (Roll(GetSpreadCityEnemyMilitaryBaseChancePercent())) return StorySpawnKind.MilitaryBase;
                    if (Roll(GetSpreadCityAllyTradeCampChancePercent())) return StorySpawnKind.TradeCamp;
                }

                var cityPool = new List<Tuple<StorySpawnKind, int>>
                {
                    Tuple.Create(StorySpawnKind.MilitaryBase, 22),
                    Tuple.Create(StorySpawnKind.Mine, 16),
                    Tuple.Create(StorySpawnKind.Farm, 15),
                    Tuple.Create(StorySpawnKind.IndustrialSite, 14),
                    Tuple.Create(StorySpawnKind.ResearchHub, 14),
                    Tuple.Create(StorySpawnKind.LogisticsHub, 13),
                    Tuple.Create(StorySpawnKind.Settlement, 3),
                    Tuple.Create(StorySpawnKind.Outpost, 2),
                    Tuple.Create(StorySpawnKind.TradeCamp, 1)
                };

                var sum = cityPool.Sum(t => t.Item2);
                var pick = NextInt(Math.Max(1, sum));
                for (int i = 0; i < cityPool.Count; i++)
                {
                    pick -= cityPool[i].Item2;
                    if (pick < 0) return cityPool[i].Item1;
                }

                return StorySpawnKind.MilitaryBase;
            }

            if (enemyNearby
                && effectiveEnemyCount >= nearbyContext.AllyCount
                && Roll(GetSpreadLowEnemyOutpostChancePercent()))
            {
                return StorySpawnKind.Outpost;
            }
            if (allyNearby
                && nearbyContext.AllyCount > nearbyContext.EnemyCount
                && Roll(GetSpreadLowAllyTradeCampChancePercent()))
            {
                return StorySpawnKind.TradeCamp;
            }
            if (enemyNearby && allyNearby)
            {
                if (Roll(GetSpreadLowEnemyOutpostChancePercent())) return StorySpawnKind.Outpost;
                if (Roll(GetSpreadLowAllyTradeCampChancePercent())) return StorySpawnKind.TradeCamp;
            }

            var settlementWeight = 15;
            var outpostWeight = 55;
            var tradeWeight = 30;
            var total = settlementWeight + outpostWeight + tradeWeight;
            if (total <= 0) return StorySpawnKind.Outpost;

            var roll = NextInt(total);
            if (roll < settlementWeight) return StorySpawnKind.Settlement;
            roll -= settlementWeight;
            if (roll < outpostWeight) return StorySpawnKind.Outpost;
            return StorySpawnKind.TradeCamp;
        }

        private static NearbyFactionContext AnalyzeNearbyFactionContext(BaseContainer data, WorldObjectOnline source, int radius)
        {
            var context = new NearbyFactionContext();
            if (data?.WorldObjectOnlineList == null || source == null || source.Tile <= 0 || radius <= 0)
            {
                return context;
            }

            var sourceFaction = BuildFactionSignature(source.FactionGroup, source.FactionDef, source.loadID);
            var processedFactions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nearby = data.WorldObjectOnlineList
                .Where(p => p != null
                    && p.ServerGenerated
                    && p.Tile > 0
                    && !ReferenceEquals(p, source)
                    && Math.Abs(p.Tile - source.Tile) <= radius)
                .OrderBy(p => Math.Abs(p.Tile - source.Tile))
                .ToList();

            for (int i = 0; i < nearby.Count; i++)
            {
                var point = nearby[i];
                var factionSignature = BuildFactionSignature(point.FactionGroup, point.FactionDef, point.loadID);
                if (string.IsNullOrWhiteSpace(factionSignature)) continue;
                if (!processedFactions.Add(factionSignature)) continue;
                if (string.Equals(sourceFaction, factionSignature, StringComparison.OrdinalIgnoreCase)) continue;

                var relation = GetFactionRelationHint(data
                    , source.FactionGroup
                    , source.FactionDef
                    , point.FactionGroup
                    , point.FactionDef);

                if (relation == FactionRelationHint.Ally)
                {
                    context.AllyCount++;
                }
                else if (relation == FactionRelationHint.Enemy)
                {
                    context.EnemyCount++;
                }
                else
                {
                    context.NeutralCount++;
                }
            }

            return context;
        }

        private static FactionRelationHint GetFactionRelationHint(BaseContainer data
            , string firstLabel
            , string firstDef
            , string secondLabel
            , string secondDef)
        {
            if (data?.StoryEvents == null || data.StoryEvents.Count == 0)
            {
                return FactionRelationHint.Neutral;
            }

            var pairKey = BuildFactionPairKey(firstLabel, firstDef, secondLabel, secondDef);
            if (string.IsNullOrWhiteSpace(pairKey))
            {
                return FactionRelationHint.Neutral;
            }

            var firstLabelNorm = NormalizeFactionLabel(firstLabel);
            var secondLabelNorm = NormalizeFactionLabel(secondLabel);
            var firstDefNorm = NormalizeFactionDef(firstDef, firstLabelNorm);
            var secondDefNorm = NormalizeFactionDef(secondDef, secondLabelNorm);
            var cutoff = DateTime.UtcNow.AddDays(-14);

            var relation = FactionRelationHint.Neutral;
            var relationTime = DateTime.MinValue;
            for (int i = data.StoryEvents.Count - 1; i >= 0; i--)
            {
                var evt = data.StoryEvents[i];
                if (evt == null) continue;
                if (evt.CreatedUtc < cutoff) break;

                var key = evt.Key;
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (!StoryKeyMatchesPair(key, pairKey, firstLabelNorm, secondLabelNorm, firstDefNorm, secondDefNorm)) continue;

                if (key.StartsWith("diplomacy_trade:", StringComparison.OrdinalIgnoreCase))
                {
                    if (evt.CreatedUtc >= relationTime)
                    {
                        relation = FactionRelationHint.Ally;
                        relationTime = evt.CreatedUtc;
                    }
                    continue;
                }

                if (key.StartsWith("diplomacy_tension:", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("conflict:", StringComparison.OrdinalIgnoreCase))
                {
                    if (evt.CreatedUtc >= relationTime)
                    {
                        relation = FactionRelationHint.Enemy;
                        relationTime = evt.CreatedUtc;
                    }
                }
            }

            return relation;
        }

        private static bool StoryKeyMatchesPair(string key
            , string pairKey
            , string firstLabel
            , string secondLabel
            , string firstDef
            , string secondDef)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (!string.IsNullOrWhiteSpace(pairKey)
                && key.IndexOf(":" + pairKey + ":", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var byLabel = ContainsIgnoreCase(key, firstLabel) && ContainsIgnoreCase(key, secondLabel);
            if (byLabel) return true;

            var byDef = ContainsIgnoreCase(key, firstDef) && ContainsIgnoreCase(key, secondDef);
            return byDef;
        }

        private static bool ContainsIgnoreCase(string source, string value)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(value)) return false;
            return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildFactionPairKey(string firstLabel, string firstDef, string secondLabel, string secondDef)
        {
            var first = NormalizeFactionDef(firstDef, firstLabel);
            var second = NormalizeFactionDef(secondDef, secondLabel);
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return string.Empty;

            first = first.Trim().ToLowerInvariant();
            second = second.Trim().ToLowerInvariant();
            if (string.CompareOrdinal(first, second) <= 0)
            {
                return first + "|" + second;
            }
            return second + "|" + first;
        }

        private static string BuildFallbackPairKeyByLabels(string firstLabel, string secondLabel)
        {
            var first = ToSafeId(firstLabel);
            var second = ToSafeId(secondLabel);
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return "unknown_pair";
            if (string.CompareOrdinal(first, second) <= 0)
            {
                return first + "|" + second;
            }
            return second + "|" + first;
        }

        private static string BuildFactionSignature(string label, string def, int loadId)
        {
            if (loadId > 0) return "id:" + loadId;

            var normalizedLabel = NormalizeFactionLabel(label);
            var normalizedDef = NormalizeFactionDef(def, normalizedLabel);
            if (string.IsNullOrWhiteSpace(normalizedDef) && string.IsNullOrWhiteSpace(normalizedLabel)) return string.Empty;
            return (normalizedDef + "|" + normalizedLabel).ToLowerInvariant();
        }

        private static int PickSpreadTile(List<int> freeTiles, int sourceTile, int maxDistance = 0, bool requireWithinDistance = false)
        {
            if (freeTiles == null || freeTiles.Count == 0) return 0;
            if (sourceTile <= 0 || freeTiles.Count <= 4)
            {
                if (maxDistance > 0)
                {
                    var local = freeTiles.Where(t => Math.Abs(t - sourceTile) <= maxDistance).ToList();
                    if (local.Count > 0) return Pick(local);
                    if (requireWithinDistance) return 0;
                }
                return Pick(freeTiles);
            }

            if (maxDistance > 0)
            {
                var local = freeTiles
                    .Where(t => Math.Abs(t - sourceTile) <= maxDistance)
                    .OrderBy(t => Math.Abs(t - sourceTile))
                    .Take(Math.Min(12, freeTiles.Count))
                    .ToList();
                if (local.Count > 0) return Pick(local);
                if (requireWithinDistance) return 0;
            }

            var nearest = freeTiles
                .OrderBy(t => Math.Abs(t - sourceTile))
                .Take(Math.Min(12, freeTiles.Count))
                .ToList();
            if (nearest.Count == 0) return Pick(freeTiles);
            return Pick(nearest);
        }

        private static bool IsCityInfrastructureKind(StorySpawnKind kind)
        {
            return kind == StorySpawnKind.MilitaryBase
                || kind == StorySpawnKind.Mine
                || kind == StorySpawnKind.Farm
                || kind == StorySpawnKind.IndustrialSite
                || kind == StorySpawnKind.ResearchHub
                || kind == StorySpawnKind.LogisticsHub;
        }

        private static List<int> GetFreeKnownTiles(BaseContainer data)
        {
            if (data?.StorytellerKnownTiles == null || data.StorytellerKnownTiles.Count == 0)
            {
                return new List<int>();
            }

            var occupiedTiles = BuildOccupiedTiles(data);
            var freeKnownTiles = data.StorytellerKnownTiles
                .Where(t => t > 0 && !occupiedTiles.Contains(t))
                .Distinct()
                .ToList();
            if (freeKnownTiles.Count > 0) return freeKnownTiles;

            return BuildSyntheticFreeTiles(data.StorytellerKnownTiles, occupiedTiles);
        }

        private static HashSet<int> BuildOccupiedTiles(BaseContainer data)
        {
            var occupiedTiles = new HashSet<int>(
                (data.WorldObjectOnlineList ?? new List<WorldObjectOnline>())
                .Where(o => o != null && o.Tile > 0)
                .Select(o => o.Tile));
            foreach (var wo in (data.WorldObjects ?? new List<WorldObjectEntry>()).Where(o => o != null && o.Tile > 0))
            {
                occupiedTiles.Add(wo.Tile);
            }

            return occupiedTiles;
        }

        private static List<int> BuildSyntheticFreeTiles(List<int> knownTiles, HashSet<int> occupiedTiles)
        {
            if (knownTiles == null || knownTiles.Count == 0) return new List<int>();

            var anchors = knownTiles
                .Where(t => t > 0)
                .Distinct()
                .Take(64)
                .ToList();
            if (anchors.Count == 0) return new List<int>();
            var maxKnownTile = anchors.Max();
            if (maxKnownTile <= 0) return new List<int>();

            var result = new HashSet<int>();
            var offsets = new int[] { 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144, 233, 377, 610, 987, 1597 };
            for (int ai = 0; ai < anchors.Count && result.Count < 256; ai++)
            {
                var anchor = anchors[ai];
                for (int oi = 0; oi < offsets.Length && result.Count < 256; oi++)
                {
                    var minus = anchor - offsets[oi];
                    if (minus > 0 && !occupiedTiles.Contains(minus))
                    {
                        result.Add(minus);
                    }

                    var plus = anchor + offsets[oi];
                    if (plus > 0 && plus <= maxKnownTile && !occupiedTiles.Contains(plus))
                    {
                        result.Add(plus);
                    }
                }
            }
            if (result.Count > 0) return result.ToList();

            var fallbackAnchor = anchors[0];
            for (int d = 1; d <= 5000; d++)
            {
                var minus = fallbackAnchor - d;
                if (minus > 0 && !occupiedTiles.Contains(minus))
                {
                    result.Add(minus);
                    break;
                }

                var plus = fallbackAnchor + d;
                if (plus > 0 && plus <= maxKnownTile && !occupiedTiles.Contains(plus))
                {
                    result.Add(plus);
                    break;
                }
            }

            return result.ToList();
        }

        private static string GetStoryType(StorySpawnKind kind)
        {
            switch (kind)
            {
                case StorySpawnKind.PlayerFrontierCamp:
                    return "camp";
                case StorySpawnKind.TradeCamp:
                    return "trade_camp";
                case StorySpawnKind.Outpost:
                    return "outpost";
                case StorySpawnKind.MilitaryBase:
                    return "military_base";
                case StorySpawnKind.Mine:
                    return "mine";
                case StorySpawnKind.Farm:
                    return "farm";
                case StorySpawnKind.IndustrialSite:
                    return "industrial_site";
                case StorySpawnKind.ResearchHub:
                    return "research_hub";
                case StorySpawnKind.LogisticsHub:
                    return "logistics_hub";
                default:
                    return "settlement";
            }
        }

        private static StorySpawnKind ResolveSpawnKind(string storyType)
        {
            if (string.Equals(storyType, "camp", StringComparison.OrdinalIgnoreCase)) return StorySpawnKind.PlayerFrontierCamp;
            if (string.Equals(storyType, "trade_camp", StringComparison.OrdinalIgnoreCase)) return StorySpawnKind.TradeCamp;
            if (string.Equals(storyType, "outpost", StringComparison.OrdinalIgnoreCase)) return StorySpawnKind.Outpost;
            if (string.Equals(storyType, "military_base", StringComparison.OrdinalIgnoreCase)) return StorySpawnKind.MilitaryBase;
            if (string.Equals(storyType, "mine", StringComparison.OrdinalIgnoreCase)) return StorySpawnKind.Mine;
            if (string.Equals(storyType, "farm", StringComparison.OrdinalIgnoreCase)) return StorySpawnKind.Farm;
            if (string.Equals(storyType, "industrial_site", StringComparison.OrdinalIgnoreCase)) return StorySpawnKind.IndustrialSite;
            if (string.Equals(storyType, "research_hub", StringComparison.OrdinalIgnoreCase)) return StorySpawnKind.ResearchHub;
            if (string.Equals(storyType, "logistics_hub", StringComparison.OrdinalIgnoreCase)) return StorySpawnKind.LogisticsHub;
            return StorySpawnKind.Settlement;
        }

        private static int ReadSetting(Func<ServerGeneralSettings, int> getter, int fallback, int min, int max)
        {
            var value = getter(ServerManager.ServerSettings.GeneralSettings);
            if (value < min || value > max) return fallback;
            return value;
        }

        private static int NextInt(int maxExclusive)
        {
            lock (Rnd)
            {
                return Rnd.Next(maxExclusive);
            }
        }

        private sealed class NearbyFactionContext
        {
            public int AllyCount { get; set; }
            public int EnemyCount { get; set; }
            public int NeutralCount { get; set; }
        }

        private enum FactionRelationHint
        {
            Neutral = 0,
            Ally = 1,
            Enemy = 2
        }

        private enum StorySpawnKind
        {
            PlayerFrontierCamp = 0,
            TradeCamp = 1,
            Settlement = 2,
            Outpost = 3,
            MilitaryBase = 4,
            Mine = 5,
            Farm = 6,
            IndustrialSite = 7,
            ResearchHub = 8,
            LogisticsHub = 9
        }
    }
}
