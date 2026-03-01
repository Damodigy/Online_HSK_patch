using Model;
using OCUnion;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Transfer; 
using Verse;
using GameClasses;
using OCUnion.Transfer.Model;
using UnityEngine;
using Random = System.Random;
using RimWorldOnlineCity.GameClasses.Harmony;

namespace RimWorldOnlineCity
{
    static class UpdateWorldController
    {
        /// <summary>
        /// Для поиска объектов, уже созданных в прошлые разы
        /// </summary>
        private static Dictionary<long, int> ConverterServerId { get; set; }
        public static Dictionary<int, WorldObjectEntry> WorldObjectEntrys { get; private set; }
        private static List<WorldObjectEntry> ToDelete { get; set; }
        /// <summary>
        /// Список объектов TradeOrdersOnline для быстрого доступа (можно получить и из Find.WorldObjects)
        /// </summary>
        public static HashSet<TradeOrdersOnline> WorldObject_TradeOrdersOnline { get; set; }

        private static List<WorldObjectEntry> LastSendMyWorldObjects { get; set; }
        private static List<WorldObjectOnline> LastWorldObjectOnline { get; set; }
        private static List<FactionOnline> LastFactionOnline { get; set; }
        private static Dictionary<int, WorldObjectOnline> OnlineWorldDescriptorsByTile { get; set; }

        private static Dictionary<int, WorldObjectBaseOnline> LastCatchAllWorldObjectsByID;

        public static bool ExistsEnemyPawns => gameProgress?.ExistsEnemyPawns == true;

        #region PrepareInMainThread

        public static List<WorldObject> allWorldObjects;
        public static List<WorldObjectEntry> WObjects;
        public static PlayerGameProgress gameProgress;
        private class CacheMap
        {
            public List<Pawn> Colonists;
            public bool ExistsEnemyPawns;
        }
        public static void PrepareInMainThread()
        {
            try
            {
                //DateTime debugtime = DateTime.UtcNow;
                gameProgress = new PlayerGameProgress() { Pawns = new List<PawnStat>() };

                allWorldObjects = GameUtils.GetAllWorldObjects();
                Dictionary<Map, CacheMap> cacheColonists = new Dictionary<Map, CacheMap>();
                Dictionary<WorldObjectEntry, Map> tmpMap = new Dictionary<WorldObjectEntry, Map>();
                WObjects = allWorldObjects
                        .Where(o => (o.Faction?.IsPlayer ?? false) //o.Faction != null && o.Faction.IsPlayer
                            && (o is Settlement || o is Caravan)) //Чтобы отсеч разные карты событий
                        .Select(o =>
                        {
                            var oo = GetWorldObjectEntry(o, gameProgress, cacheColonists);
                            if (o is MapParent) tmpMap.Add(oo, ((MapParent)o).Map);
                            return oo;
                        })
                        .ToList();

                //обновляем информацию по своим поселениям
                //безналичные средства, которые принадлежит игроку в целом раскидываем порпоционально стоимости его объектов
                var totalMarketValue = WObjects.Sum(wo => wo.MarketValue + wo.MarketValuePawn);
                if (totalMarketValue > 0)
                {
                    var cashlessBalance = Math.Abs(SessionClientController.Data.CashlessBalance);
                    var storageBalance = Math.Abs(SessionClientController.Data.StorageBalance);
                    foreach (var wo in WObjects)
                    {
                        wo.MarketValueBalance = cashlessBalance * (wo.MarketValue + wo.MarketValuePawn) / totalMarketValue;
                        wo.MarketValueStorage = storageBalance * (wo.MarketValue + wo.MarketValuePawn) / totalMarketValue;
                    }
                }
                else
                {
                    foreach (var wo in WObjects)
                    {
                        wo.MarketValueBalance = 0;
                        wo.MarketValueStorage = 0;
                    }
                }

                //устанавливаем доп стоимость в карты
                MainTabWindow_DoStatisticsPage_Patch.PatchColonyWealth = WObjects
                    .Where(o => o.Type == WorldObjectEntryType.Base)
                    .ToDictionary(o => tmpMap[o], o => (o.MarketValueBalance + o.MarketValueStorage) * (float)SessionClientController.Data.GeneralSettings.ExchengePrecentWealthForIncident / 1000f);

                //Loger.Log("PrepareInMainThread " + ModBaseData.GlobalData.ActionNumReady + " debugtime " + (DateTime.UtcNow - debugtime).TotalMilliseconds); 
            }
            catch (Exception ex)
            {
                Loger.Log("Exception PrepareInMainThread " + ex.ToString());
            }
        }
        #endregion PrepareInMainThread

        public static void SendToServer(ModelPlayToServer toServ, bool firstRun, ModelGameServerInfo modelGameServerInfo)
        {
            //Перед запуском должна отработать PrepareInMainThread()

            //Loger.Log("Empire=" + Find.FactionManager.FirstFactionOfDef(FactionDefOf.Empire)?.GetUniqueLoadID());

            toServ.LastTick = (long)Find.TickManager.TicksGame;

            List<Faction> factionList = Find.FactionManager.AllFactionsListForReading
                .Where(f => !f.IsPlayer)
                .ToList();

            // First sync is always used to reconcile non-player factions/settlements with server canonical world.
            if (firstRun)
            {
                try
                {
                    var worldObjectsForSync = allWorldObjects ?? GameUtils.GetAllWorldObjects();
                    var onlineWObjArr = worldObjectsForSync.Where(wo => wo is Settlement)
                        .Where(wo => wo.HasName && !(wo.Faction?.IsPlayer ?? false))
                        .ToList();
                    toServ.WObjectOnlineList = onlineWObjArr
                        .Select(GetWorldObjects)
                        .ToList();
                    toServ.FactionOnlineList = factionList
                        .Select(GetFactions)
                        .ToList();
                    return;
                }
                catch (Exception e)
                {
                    Loger.Log("Exception >> " + e, Loger.LogLevel.ERROR);
                    Log.Error("SendToServer FirstRun error");
                    return;
                }
            }

            toServ.IsWorldObjectsSync = !firstRun;
            if (!firstRun)
            {
                //Loger.Log("Client TestBagSD 035");
                //Отправляем только дельту собственных world-объектов.
                var currentMyWorldObjects = WObjects ?? new List<WorldObjectEntry>();
                toServ.WObjects = BuildMyWorldObjectsDelta(currentMyWorldObjects, LastSendMyWorldObjects);
                //Для UI и клиентского состояния храним полный последний снэпшот, а не дельту.
                LastSendMyWorldObjects = currentMyWorldObjects;

                //Loger.Log("Client TestBagSD 036");
                //свои объекты которые удалил пользователь с последнего обновления
                if (ToDelete != null)
                {
                    var toDeleteNewNow = WorldObjectEntrys
                        .Where(p => !allWorldObjects.Any(wo => wo.ID == p.Key))
                        .Select(p => p.Value)
                        .ToList();
                    ToDelete.AddRange(toDeleteNewNow);
                }

                toServ.WObjectsToDelete = ToDelete;
            }
            gameProgress.TransLog = Loger.GetTransLog();
            toServ.GameProgress = gameProgress;

            if (SessionClientController.Data.GeneralSettings.EquableWorldObjects)
            {
                #region Send to Server: Non-Player World Objects
                //  Non-Player World Objects
                try
                {
                    var OnlineWObjArr = allWorldObjects.Where(wo => wo is Settlement)
                                          .Where(wo => wo.HasName && !wo.Faction.IsPlayer);
                    if (!firstRun)
                    {
                        if (LastWorldObjectOnline != null && LastWorldObjectOnline.Count > 0)
                        {
                            toServ.WObjectOnlineToDelete = LastWorldObjectOnline.Where(WOnline => !OnlineWObjArr.Any(wo => ValidateOnlineWorldObject(WOnline, wo))).ToList();

                            toServ.WObjectOnlineToAdd = OnlineWObjArr.Where(wo => !LastWorldObjectOnline.Any(WOnline => ValidateOnlineWorldObject(WOnline, wo)))
                                                                        .Select(obj => GetWorldObjects(obj)).ToList();
                        }
                    }

                    toServ.WObjectOnlineList = OnlineWObjArr.Select(obj => GetWorldObjects(obj)).ToList();
                    LastWorldObjectOnline = toServ.WObjectOnlineList;
                }
                catch (Exception e)
                {
                    Loger.Log("Exception >> " + e);
                    Log.Error("ERROR SendToServer WorldObject Online");
                }
                #endregion

                #region Send to Server: Non-Player Factions
                // Non-Player Factions
                try
                {
                    if (!firstRun)
                    {
                        if (LastFactionOnline != null && LastFactionOnline.Count > 0)
                        {
                            toServ.FactionOnlineToDelete = LastFactionOnline.Where(FOnline => !factionList.Any(f => ValidateFaction(FOnline, f))).ToList();

                            toServ.FactionOnlineToAdd = factionList.Where(f => !LastFactionOnline.Any(FOnline => ValidateFaction(FOnline, f)))
                                                                        .Select(obj => GetFactions(obj)).ToList();
                        }
                    }

                    toServ.FactionOnlineList = factionList.Select(obj => GetFactions(obj)).ToList();
                    LastFactionOnline = toServ.FactionOnlineList;
                }
                catch (Exception e)
                {
                    Loger.Log("Exception >> " + e);
                    Log.Error("ERROR SendToServer Faction Online");
                }
                #endregion
            }
        }

        private static List<WorldObjectEntry> BuildMyWorldObjectsDelta(List<WorldObjectEntry> current, List<WorldObjectEntry> previous)
        {
            if (current == null || current.Count == 0) return new List<WorldObjectEntry>();
            if (previous == null || previous.Count == 0) return current;

            var prevByServerId = previous
                .Where(wo => wo != null && wo.PlaceServerId > 0)
                .GroupBy(wo => wo.PlaceServerId)
                .ToDictionary(g => g.Key, g => g.First());

            var result = new List<WorldObjectEntry>(current.Count);
            for (int i = 0; i < current.Count; i++)
            {
                var cur = current[i];
                if (cur == null)
                {
                    continue;
                }

                //Новые объекты без serverId отправляем всегда до получения id от сервера.
                if (cur.PlaceServerId <= 0)
                {
                    result.Add(cur);
                    continue;
                }

                if (!prevByServerId.TryGetValue(cur.PlaceServerId, out var prev)
                    || !EqualsWorldObjectEntry(cur, prev))
                {
                    result.Add(cur);
                }
            }

            return result;
        }

        private static bool EqualsWorldObjectEntry(WorldObjectEntry a, WorldObjectEntry b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            return a.Type == b.Type
                && a.Tile == b.Tile
                && string.Equals(a.Name, b.Name)
                && a.PlaceServerId == b.PlaceServerId
                && a.FreeWeight == b.FreeWeight
                && a.MarketValue == b.MarketValue
                && a.MarketValuePawn == b.MarketValuePawn
                && a.MarketValueStorage == b.MarketValueStorage
                && a.MarketValueBalance == b.MarketValueBalance
                && string.Equals(a.LoginOwner, b.LoginOwner);
        }

        public static void LoadFromServer(ModelPlayToClient fromServ, bool removeMissing)
        {
            var hasWorldSyncPayload =
                (fromServ.WObjectOnlineList?.Count ?? 0) > 0
                || (fromServ.WObjectOnlineToAdd?.Count ?? 0) > 0
                || (fromServ.WObjectOnlineToDelete?.Count ?? 0) > 0
                || (fromServ.FactionOnlineList?.Count ?? 0) > 0
                || (fromServ.FactionOnlineToAdd?.Count ?? 0) > 0
                || (fromServ.FactionOnlineToDelete?.Count ?? 0) > 0;

            if (SessionClientController.Data.GeneralSettings.EquableWorldObjects || hasWorldSyncPayload)
            {
                ApplyFactionsToWorld(fromServ);
                // ---------------------------------------------------------------------------------- // 
                ApplyNonPlayerWorldObject(fromServ);
            }

            if (removeMissing)
            {
                //запускается только при первом получении данных от сервера после загрузки или создания карты
                //удаляем все объекты других игроков (на всякий случай, т.к. в сейв они не сохраняются)

                var missingWObjects = Find.WorldObjects.AllWorldObjects
                    .Where(o => o is CaravanOnline || o is WorldObjectBaseOnline)
                    .ToList();
                for (int i = 0; i < missingWObjects.Count; i++)
                {
                    Find.WorldObjects.Remove(missingWObjects[i]);
                }
                Loger.Log("RemoveMissing " + missingWObjects.Count);
            }

            //обновление всех объектов
            ToDelete = new List<WorldObjectEntry>();
            List<WorldObject> catchAllWorldObjects = Find.WorldObjects.AllWorldObjects.ToList();
            Dictionary<int, WorldObjectBaseOnline> catchAllWorldObjectsByID = catchAllWorldObjects
                .Select(wo => wo as WorldObjectBaseOnline)
                .Where(wo => (wo?.ID ?? 0) != 0)
                .ToDictionary(wo => wo.ID);
            if (fromServ.WObjects != null && fromServ.WObjects.Count > 0)
            {
                for (int i = 0; i < fromServ.WObjects.Count; i++)
                    ApplyWorldObject(fromServ.WObjects[i], ref catchAllWorldObjects, ref catchAllWorldObjectsByID);
            }
            if (fromServ.WObjectsToDelete != null && fromServ.WObjectsToDelete.Count > 0)
            {
                for (int i = 0; i < fromServ.WObjectsToDelete.Count; i++)
                    DeleteWorldObject(fromServ.WObjectsToDelete[i], ref catchAllWorldObjects, ref catchAllWorldObjectsByID);
            }
            
            if (fromServ.WTObjects != null && fromServ.WTObjects.Count > 0)
            {
                for (int i = 0; i < fromServ.WTObjects.Count; i++)
                    ApplyTradeWorldObject(fromServ.WTObjects[i], ref catchAllWorldObjects, ref catchAllWorldObjectsByID);
            }
            if (fromServ.WTObjectsToDelete != null && fromServ.WTObjectsToDelete.Count > 0)
            {
                for (int i = 0; i < fromServ.WTObjectsToDelete.Count; i++)
                    DeleteTradeWorldObject(fromServ.WTObjectsToDelete[i], ref catchAllWorldObjects, ref catchAllWorldObjectsByID);
            }
            LastCatchAllWorldObjectsByID = catchAllWorldObjectsByID;

            //свои поселения заполняем отдельно теми, что последний раз отправляли, но на всякий случай не первый раз
            //we fill our settlements separately with those that were last sent, but just in case not for the first time
            if (!removeMissing && SessionClientController.Data.Players.ContainsKey(SessionClientController.My.Login))
            {
                SessionClientController.Data.Players[SessionClientController.My.Login].WObjects = LastSendMyWorldObjects
                    .Select(wo => wo.Type == WorldObjectEntryType.Base
                        ? (CaravanOnline)new BaseOnline() { Tile = wo.Tile, OnlineWObject = wo,  }
                        : new CaravanOnline() { Tile = wo.Tile, OnlineWObject = wo })
                    .ToList();
                    /*
                    UpdateWorldController.WorldObjectEntrys.Values
                    .Where(wo => wo.LoginOwner == SessionClientController.My.Login)
                    .Select(wo => wo.Type == WorldObjectEntryType.Base
                        ? (CaravanOnline)new BaseOnline() { Tile = wo.Tile, OnlineWObject = wo }
                        : new CaravanOnline() { Tile = wo.Tile, OnlineWObject = wo })
                    .ToList();
                    */
            }

            //пришла посылка от каравана другого игрока
            if (fromServ.Mails != null && fromServ.Mails.Count > 0)
            {
                for (int i = 0; i < fromServ.Mails.Count; i++)
                {
                    MailController.MailArrived(fromServ.Mails[i]);
                }
            }
        }

        public static void ClearWorld()
        {
            //Loger.Log("ClearWorld");
            var deleteWObjects = Find.WorldObjects.AllWorldObjects
                .Where(o => o is CaravanOnline)
                .ToList();

            for (int i = 0; i < deleteWObjects.Count; i++)
                Find.WorldObjects.Remove(deleteWObjects[i]);
        }


        #region WorldObject


        public static int GetLocalIdByServerId(long serverId)
        {
            int objId;
            if (ConverterServerId == null
                || !ConverterServerId.TryGetValue(serverId, out objId))
            {
                return 0;
            }
            return objId;
        }

        public static WorldObjectEntry GetMyByServerId(long serverId)
        {
            WorldObjectEntry storeWO;
            int objId;
            if (ConverterServerId == null
                || !ConverterServerId.TryGetValue(serverId, out objId)
                || WorldObjectEntrys == null
                || !WorldObjectEntrys.TryGetValue(objId, out storeWO))
            {
                return null;
            }
            return storeWO;
        }

        public static WorldObjectEntry GetMyByLocalId(int id)
        {
            WorldObjectEntry storeWO;
            if (WorldObjectEntrys == null
                || !WorldObjectEntrys.TryGetValue(id, out storeWO))
            {
                return null;
            }
            return storeWO;
        }
        
        public static WorldObjectBaseOnline GetOtherByServerIdDirtyRead(long serverId) => 
            GetOtherByServerId(serverId, LastCatchAllWorldObjectsByID);
        public static WorldObjectBaseOnline GetOtherByServerId(long serverId
            , Dictionary<int, WorldObjectBaseOnline> allWorldObjectsByID = null
            )
        {
            int objId;
            if (ConverterServerId == null
                || !ConverterServerId.TryGetValue(serverId, out objId))
            {
                return null;
            }

            WorldObjectBaseOnline worldObject = null;

            if (allWorldObjectsByID == null)
            {
                var allWorldObjects = Find.WorldObjects.AllWorldObjects;

                for (int i = 0; i < allWorldObjects.Count; i++)
                {
                    if (allWorldObjects[i].ID == objId && allWorldObjects[i] is WorldObjectBaseOnline)
                    {
                        worldObject = allWorldObjects[i] as WorldObjectBaseOnline;
                        break;
                    }
                }
                return worldObject;
            }
            else
                return allWorldObjectsByID.TryGetValue(objId, out worldObject) ? worldObject : null;
        }

        public static WorldObject GetWOByServerId(long serverId, List<WorldObject> allWorldObjects = null)
        {
            int objId;
            if (ConverterServerId == null
                || !ConverterServerId.TryGetValue(serverId, out objId))
            {
                return null;
            }

            if (allWorldObjects == null) allWorldObjects = Find.WorldObjects.AllWorldObjects;
            
            for (int i = 0; i < allWorldObjects.Count; i++)
            {
                if (allWorldObjects[i].ID == objId)
                {
                    return allWorldObjects[i];
                }
            }
            return null;
        }

        public static string GetTestText()
        {
            var text = "ConverterServerId.";
            foreach (var item in ConverterServerId)
            {
                text += Environment.NewLine + item.Key + ", " + item.Value;
            }

            text += Environment.NewLine + Environment.NewLine + "MyWorldObjectEntry.";
            foreach (var item in WorldObjectEntrys)
            {
                text += Environment.NewLine + item.Key + ", " + item.Value.PlaceServerId + " " + item.Value.Name;
            }

            text += Environment.NewLine + Environment.NewLine + "ToDelete.";
            foreach (var item in ToDelete)
            {
                text += Environment.NewLine + item.PlaceServerId + " " + item.Name;
            }
            return text;
        }

        public static WorldObjectEntry GetServerInfo(WorldObject myWorldObject)
        {
            WorldObjectEntry storeWO;
            if (WorldObjectEntrys == null
                || !WorldObjectEntrys.TryGetValue(myWorldObject.ID, out storeWO))
            {
                return null;
            }
            return storeWO;
        }

        private static void GameProgressAdd(PlayerGameProgress gameProgress, Pawn pawn)
        {
            if (pawn.Dead) return;
            if (pawn.IsFreeColonist && !pawn.IsPrisoner && !pawn.IsPrisonerOfColony && pawn.RaceProps.Humanlike)
            {
                gameProgress.ColonistsCount++;
                gameProgress.Pawns.Add(PawnStat.CreateTrade(pawn));

                if (pawn.Downed) gameProgress.ColonistsDownCount++;
                if (pawn.health.hediffSet.BleedRateTotal > 0) gameProgress.ColonistsBleedCount++;
                //pawn.health.hediffset.pain_total    // уровень боли

                if (pawn.health.HasHediffsNeedingTend()) gameProgress.ColonistsNeedingTend++; //Нуждается в лечении

                int maxSkill = 0;
                for (int i = 0; i < pawn.skills.skills.Count; i++)
                {
                    if (pawn.skills.skills[i].Level == 20) maxSkill++;
                }
                if (maxSkill >= 8) gameProgress.PawnMaxSkill++;

                var kh = pawn.records.GetAsInt(RecordDefOf.KillsHumanlikes);
                var km = pawn.records.GetAsInt(RecordDefOf.KillsMechanoids);

                gameProgress.KillsHumanlikes += kh;
                gameProgress.KillsMechanoids += km;
                if (gameProgress.KillsBestHumanlikesPawnName == null
                    || kh > gameProgress.KillsBestHumanlikes)
                {
                    gameProgress.KillsBestHumanlikesPawnName = pawn.LabelCapNoCount;
                    gameProgress.KillsBestHumanlikes = kh;
                }
                if (gameProgress.KillsBestMechanoidsPawnName == null
                    || km > gameProgress.KillsBestMechanoids)
                {
                    gameProgress.KillsBestMechanoidsPawnName = pawn.LabelCapNoCount;
                    gameProgress.KillsBestMechanoids = km;
                }
            }
            else if (pawn.RaceProps.Animal && pawn.training?.HasLearned(TrainableDefOf.Obedience) == true)
            {
                gameProgress.AnimalObedienceCount++;
                //Loger.Log(pawn.Label + " pawn.playerSettings.followDrafted = " + pawn.playerSettings.followDrafted.ToString());
                //Loger.Log(pawn.Label + " pawn.playerSettings.followFieldwork = " + pawn.playerSettings.followFieldwork.ToString());
                //Loger.Log(pawn.Label + " pawn.playerSettings.animalsReleased = " + pawn.playerSettings.animalsReleased.ToString());
                //Loger.Log(pawn.Label + " pawn.playerSettings.medCare = " + pawn.playerSettings.medCare.ToString());
                //Loger.Log(pawn.Label + " pawn.playerSettings.hostilityResponse = " + pawn.playerSettings.hostilityResponse.ToString());
                //Loger.Log(pawn.Label + " HasLearned Tameness = " + pawn.training.HasLearned(TrainableDefOf.Tameness).ToString());
                //Loger.Log(pawn.Label + " HasLearned Obedience = " + pawn.training.HasLearned(TrainableDefOf.Obedience).ToString());
                //Loger.Log(pawn.Label + " HasLearned Release = " + pawn.training.HasLearned(TrainableDefOf.Release).ToString());
                //Loger.Log(pawn.Label + " CanAssignToTrain Tameness = " + pawn.training.HasLearned(TrainableDefOf.Tameness).ToString());
                //Loger.Log(pawn.Label + " CanAssignToTrain Obedience = " + pawn.training.HasLearned(TrainableDefOf.Obedience).ToString());
                //Loger.Log(pawn.Label + " CanAssignToTrain Release = " + pawn.training.HasLearned(TrainableDefOf.Release).ToString());
            }

        }

        public static Dictionary<int, DateTime> LastForceRecount = new Dictionary<int, DateTime>();
        /// <summary>
        /// Только для своих объетков
        /// </summary>
        private static WorldObjectEntry GetWorldObjectEntry(WorldObject worldObject
            , PlayerGameProgress gameProgress
            , Dictionary<Map, CacheMap> cacheColonists)
        {
            var worldObjectEntry = new WorldObjectEntry();
            worldObjectEntry.Type = worldObject is Caravan ? WorldObjectEntryType.Caravan : WorldObjectEntryType.Base;
            worldObjectEntry.Tile = worldObject.Tile;
            worldObjectEntry.Name = worldObject.LabelCap;
            worldObjectEntry.LoginOwner = SessionClientController.My.Login;
            worldObjectEntry.FreeWeight = 999999;

            //определяем цену и вес 
            var caravan = worldObject as Caravan;
            if (caravan != null)
            {
                //Loger.Log("Client TestBagSD 002");

                var transferables = GameUtils.GetAllThings(caravan, true, false).DistinctToTransferableOneWays();

                //Loger.Log("Client TestBagSD 003");

                List<ThingCount> stackParts = new List<ThingCount>();
                for (int i = 0; i < transferables.Count; i++)
                {
                    var allCount = transferables[i].MaxCount;
                    for (int ti = 0; ti < transferables[i].things.Count; ti++)
                    {
                        int cnt = Mathf.Min(transferables[i].things[ti].stackCount, allCount);
                        allCount -= cnt;

                        stackParts.Add(new ThingCount(transferables[i].things[ti], cnt));

                        if (allCount <= 0) break;
                    }
                }
                //Loger.Log("Client TestBagSD 004");
                worldObjectEntry.FreeWeight = CollectionsMassCalculator.Capacity(stackParts)
                    - CollectionsMassCalculator.MassUsage(stackParts, IgnorePawnsInventoryMode.Ignore, false, false);
                //Loger.Log("Client TestBagSD 005");

                worldObjectEntry.MarketValue = 0f;
                worldObjectEntry.MarketValuePawn = 0f;
                for (int i = 0; i < stackParts.Count; i++)
                {
                    int count = stackParts[i].Count;

                    if (count > 0)
                    {
                        Thing thing = stackParts[i].Thing;
                        if (thing is Pawn)
                        {
                            worldObjectEntry.MarketValuePawn += thing.MarketValue;
                                //убрано из-за того, что эти вещи должны уже получаться здесь: GameUtils.GetAllThings(caravan, *true*, false)
                                //+ WealthWatcher.GetEquipmentApparelAndInventoryWealth(thing as Pawn);
                            GameProgressAdd(gameProgress, thing as Pawn);
                        }
                        else
                            worldObjectEntry.MarketValue += thing.MarketValue * (float)count;
                    }
                }
                //Loger.Log("Client TestBagSD 006");
            }
            else if (worldObject is Settlement)
            {
                //Loger.Log("Client TestBagSD 007");
                var map = (worldObject as Settlement).Map;
                if (map != null)
                {
                    //Loger.Log("Client TestBagSD 008");
                    try
                    {
                        DateTime lastForceRecount;
                        if (!LastForceRecount.TryGetValue(map.uniqueID, out lastForceRecount))
                            LastForceRecount.Add(map.uniqueID, DateTime.UtcNow.AddSeconds(new Random(map.uniqueID * 7).Next(0, 10)));
                        else if ((DateTime.UtcNow - lastForceRecount).TotalSeconds> 30)
                        {
                            LastForceRecount[map.uniqueID] = DateTime.UtcNow;
                            ModBaseData.RunMainThread(() =>
                            {
                                map.wealthWatcher.ForceRecount();
                            });
                        }
                        worldObjectEntry.MarketValue = map.wealthWatcher.WealthTotal;
                    }
                    catch
                    {
                        Thread.Sleep(100);
                        try
                        {
                            worldObjectEntry.MarketValue = map.wealthWatcher.WealthTotal;
                        }
                        catch
                        {
                        }
                    }

                    worldObjectEntry.MarketValuePawn = 0;

                    //Loger.Log("Client TestBagSD 015");
                    CacheMap ps;
                    if (!cacheColonists.TryGetValue(map, out ps))
                    {
                        var mapPawnsA = map.mapPawns.AllPawnsSpawned.ToArray();

                        ps = new CacheMap();
                        ps.Colonists = mapPawnsA.Where(p => p.Faction == Faction.OfPlayer /*&& p.RaceProps.Humanlike*/).ToList();
                        ps.ExistsEnemyPawns = mapPawnsA.Where(p => !p.Dead && !p.Downed && !p.IsPrisoner && p.Faction?.HostileTo(Faction.OfPlayer) == true).Any();

                        //foreach (var p in mapPawnsA) Loger.Log(p.Label + " " + p.IsPrisoner + " " + p.Faction?.HostileTo(Faction.OfPlayer));                        
                        cacheColonists[map] = ps;
                    }

                    //Loger.Log("Client TestBagSD 016");
                    foreach (Pawn current in ps.Colonists)
                    {
                        if (current.RaceProps.Humanlike) worldObjectEntry.MarketValuePawn += current.MarketValue;

                        GameProgressAdd(gameProgress, current);
                    }
                    //Loger.Log(ps.ExistsEnemyPawns.ToString());
                    gameProgress.ExistsEnemyPawns |= ps.ExistsEnemyPawns;

                    //Loger.Log("Client TestBagSD 017");
                    //Loger.Log("Map things "+ worldObjectEntry.MarketValue + " pawns " + worldObjectEntry.MarketValuePawn);
                }
            }
            //Loger.Log("Client TestBagSD 018");

            WorldObjectEntry storeWO;
            if (WorldObjectEntrys.TryGetValue(worldObject.ID, out storeWO))
            {
                //если серверу приходит объект без данного ServerId, значит это наш новый объект (кроме первого запроса, т.к. не было ещё загрузки)
                worldObjectEntry.PlaceServerId = storeWO.PlaceServerId;
            }
            //Loger.Log("Client TestBagSD 019");

            return worldObjectEntry;
        }


        /// <summary>
        /// Для всех объектов с сервера, в т.ч. и для наших.
        /// Для своих объектов заполняем данные в словарь MyWorldObjectEntry
        /// </summary>
        /// <param name="worldObjectEntry"></param>
        /// <returns></returns>
        public static void ApplyWorldObject(WorldObjectEntry worldObjectEntry
            , ref List<WorldObject> allWorldObjects
            , ref Dictionary<int, WorldObjectBaseOnline> allWorldObjectsByID)
        {
            var err = "";
            try
            {
                err += "1 ";
                if (worldObjectEntry.LoginOwner == SessionClientController.My.Login)
                {
                    //для своих нужно только занести в MyWorldObjectEntry (чтобы запомнить ServerId)
                    if (!WorldObjectEntrys.Any(wo => wo.Value.PlaceServerId == worldObjectEntry.PlaceServerId))
                    {
                        err += "2 ";

                        for (int i = 0; i < allWorldObjects.Count; i++)
                        {
                            err += "3 ";
                            if (!WorldObjectEntrys.ContainsKey(allWorldObjects[i].ID)
                                && allWorldObjects[i].Tile == worldObjectEntry.Tile
                                && (allWorldObjects[i] is Caravan && worldObjectEntry.Type == WorldObjectEntryType.Caravan
                                    || allWorldObjects[i] is MapParent && worldObjectEntry.Type == WorldObjectEntryType.Base))
                            {
                                err += "4 ";
                                var id = allWorldObjects[i].ID;
                                Loger.Log("SetMyID " + id + " ServerId " + worldObjectEntry.PlaceServerId + " " + worldObjectEntry.Name);
                                WorldObjectEntrys.Add(id, worldObjectEntry);

                                ConverterServerId[worldObjectEntry.PlaceServerId] = id;
                                err += "5 ";
                                return;
                            }
                        }

                        err += "6 ";
                        Loger.Log("ToDel " + worldObjectEntry.PlaceServerId + " " + worldObjectEntry.Name);

                        //объект нужно удалить на сервере - его нету у самого игрока (не заполняется при самом первом обновлении после загрузки)
                        if (ToDelete != null) ToDelete.Add(worldObjectEntry);
                        err += "7 ";
                    }
                    else
                    {
                        //если такой есть, то обновляем информацию
                        var pair = WorldObjectEntrys.First(wo => wo.Value.PlaceServerId == worldObjectEntry.PlaceServerId);
                        WorldObjectEntrys[pair.Key] = worldObjectEntry;
                    }
                    return;
                }

                //поиск уже существующих
                CaravanOnline worldObject = null;
                /*
                int existId;
                if (ConverterServerId.TryGetValue(worldObjectEntry.ServerId, out existId))
                {
                    for (int i = 0; i < allWorldObjects.Count; i++)
                    {
                        if (allWorldObjects[i].ID == existId && allWorldObjects[i] is CaravanOnline)
                        {
                            worldObject = allWorldObjects[i] as CaravanOnline;
                            break;
                        }
                    }
                }
                */
                err += "8 ";
                worldObject = GetOtherByServerId(worldObjectEntry.PlaceServerId, allWorldObjectsByID) as CaravanOnline;

                //Если тип объекта на сервере сменился (например, караван -> база), пересоздаем world object с нужным классом.
                if (worldObject != null)
                {
                    var mustBeBase = worldObjectEntry.Type == WorldObjectEntryType.Base;
                    var isBaseNow = worldObject is BaseOnline;
                    if (mustBeBase != isBaseNow)
                    {
                        Loger.Log("Replace WO " + worldObjectEntry.PlaceServerId
                            + " type " + worldObject.GetType().Name + " -> "
                            + (mustBeBase ? nameof(BaseOnline) : nameof(CaravanOnline)));
                        allWorldObjectsByID.Remove(worldObject.ID);
                        allWorldObjects.Remove(worldObject);
                        Find.WorldObjects.Remove(worldObject);
                        ConverterServerId.Remove(worldObjectEntry.PlaceServerId);
                        worldObject = null;
                    }
                }

                err += "9 ";
                //если тут база другого игрока, то удаление всех кто занимает этот тайл, кроме караванов (удаление новых НПЦ и событий с занятых тайлов)
                if (worldObjectEntry.Type == WorldObjectEntryType.Base)
                {
                    err += "10 ";
                    for (int i = 0; i < allWorldObjects.Count; i++)
                    {
                        err += "11 ";
                        if (allWorldObjects[i].Tile == worldObjectEntry.Tile && allWorldObjects[i] != worldObject
                            && !(allWorldObjects[i] is Caravan) && !(allWorldObjects[i] is CaravanOnline)
                            && (allWorldObjects[i].Faction == null || !allWorldObjects[i].Faction.IsPlayer))
                        {
                            err += "12 ";
                            Loger.Log("Remove " + worldObjectEntry.PlaceServerId + " " + worldObjectEntry.Name);
                            Find.WorldObjects.Remove(allWorldObjects[i]);
                        }
                    }
                }

                err += "13 ";
                //создание
                if (worldObject == null)
                {
                    err += "14 ";
                    worldObject = worldObjectEntry.Type == WorldObjectEntryType.Base
                        ? (CaravanOnline)WorldObjectMaker.MakeWorldObject(ModDefOf.BaseOnline)
                        : (CaravanOnline)WorldObjectMaker.MakeWorldObject(ModDefOf.CaravanOnline);
                    err += "15 ";
                    worldObject.SetFaction(Faction.OfPlayer);
                    worldObject.Tile = worldObjectEntry.Tile;
                    //DrawTerritory(worldObject.Tile);
                    Find.WorldObjects.Add(worldObject);
                    err += "16 ";
                    ConverterServerId[worldObjectEntry.PlaceServerId] = worldObject.ID;
                    allWorldObjectsByID[worldObject.ID] = worldObject;
                    if (!allWorldObjects.Contains(worldObject)) allWorldObjects.Add(worldObject);
                    Loger.Log("Add " + worldObjectEntry.PlaceServerId + " " + worldObjectEntry.Name + " " + worldObjectEntry.LoginOwner);
                    err += "17 ";
                }
                else
                {
                    err += "18 ";
                    ConverterServerId[worldObjectEntry.PlaceServerId] = worldObject.ID; //на всякий случай
                    err += "19 ";
                    //Loger.Log("SetID " + worldObjectEntry.ServerId + " " + worldObjectEntry.Name);
                }
                err += "20 ";
                //обновление
                worldObject.Tile = worldObjectEntry.Tile;
                err += "21 ";
                worldObject.OnlineWObject = worldObjectEntry;
            }
            catch
            {
                Loger.Log("ApplyWorldObject ErrorLog: " + err);
                throw;
            }
        }

        public static void DrawTerritory(int centralTile)
        {
            List<int> neighbors = new List<int>(0);
            Find.WorldGrid.GetTileNeighbors(centralTile, neighbors);
            foreach (var tile in neighbors)
            {
                Find.WorldDebugDrawer.FlashTile(tile, WorldMaterials.DebugTileRenderQueue, null, 999999);
            }
            Find.WorldDebugDrawer.FlashTile(centralTile, WorldMaterials.DebugTileRenderQueue, null, 999999);
        }
        public static void DeleteWorldObject(WorldObjectEntry worldObjectEntry
            , ref List<WorldObject> allWorldObjects
            , ref Dictionary<int, WorldObjectBaseOnline> allWorldObjectsByID
            )
        {
            
            //поиск уже существующих
            CaravanOnline worldObject = null;
            worldObject = GetOtherByServerId(worldObjectEntry.PlaceServerId, allWorldObjectsByID) as CaravanOnline;

            if (worldObject != null)
            {
                //Loger.Log("DeleteWorldObject " + DevelopTest.TextObj(worldObjectEntry) + " " 
                //    + (worldObject == null ? "null" : worldObject.ID.ToString()));
                allWorldObjectsByID.Remove(worldObject.ID);
                allWorldObjects.Remove(worldObject);
                ConverterServerId.Remove(worldObjectEntry.PlaceServerId);
                Find.WorldObjects.Remove(worldObject);
            }
        }

        public static void ApplyTradeWorldObject(TradeWorldObjectEntry worldObjectEntry
            , ref List<WorldObject> allWorldObjects
            , ref Dictionary<int, WorldObjectBaseOnline> allWorldObjectsByID
            )
        {
            var err = "";
            try
            {
                err += "1 ";

                //поиск уже существующих
                WorldObjectBaseOnline worldObject = null;
                worldObject = GetOtherByServerId(worldObjectEntry.PlaceServerId, allWorldObjectsByID) as WorldObjectBaseOnline;

                err += "2 ";
                //создание
                if (worldObject == null)
                {
                    err += "3 ";
                    if (worldObjectEntry.Type == TradeWorldObjectEntryType.TradeOrder)
                    {
                        worldObject = (WorldObjectBaseOnline)WorldObjectMaker.MakeWorldObject(ModDefOf.TradeOrdersOnline);
                        //передаем только заголовок, TradeOrders нужно обновить до TradeOrder отдельными запросами
                        ((TradeOrdersOnline)worldObject).TradeOrders = new List<TradeOrderShort>() { (TradeOrderShort)worldObjectEntry };
                        WorldObject_TradeOrdersOnline.Add((TradeOrdersOnline)worldObject);
                        if (MainHelper.DebugMode) Loger.Log($"Client WorldObject_TradeOrdersOnline.Add Tile={worldObject.Tile} load={worldObjectEntry.Tile}");
                    }
                    else
                    {
                        worldObject = (WorldObjectBaseOnline)WorldObjectMaker.MakeWorldObject(ModDefOf.TradeThingsOnline);
                        ((TradeThingsOnline)worldObject).TradeThings = (TradeThingStorage)worldObjectEntry;
                    }
                    err += "4 ";
                    worldObject.SetFaction(Faction.OfPlayer);
                    worldObject.Tile = worldObjectEntry.Tile;
                    Find.WorldObjects.Add(worldObject);
                    if (MainHelper.DebugMode) Loger.Log($"Client WorldObject_TradeOrdersOnline Set0 Tile={worldObject.Tile} load={worldObjectEntry.Tile}");
                    err += "5 ";
                    ConverterServerId.Add(worldObjectEntry.PlaceServerId, worldObject.ID);
                    allWorldObjectsByID.Add(worldObject.ID, worldObject);
                    allWorldObjects.Add(worldObject);
                    Loger.Log("Add " + (worldObjectEntry.Type == TradeWorldObjectEntryType.TradeOrder ? "TradeOrderShort greenApp " : "TradeThingStorage redApp")
                        + worldObjectEntry.PlaceServerId + " " + worldObjectEntry.Name + " " + worldObjectEntry.LoginOwner);
                    err += "6 ";
                }
                else
                {
                    err += "7 ";
                    ConverterServerId[worldObjectEntry.PlaceServerId] = worldObject.ID; //на всякий случай
                    if (worldObjectEntry.Type == TradeWorldObjectEntryType.TradeOrder)
                    {
                        var worldObjectTO = worldObject as TradeOrdersOnline;
                        err += "8 ";
                        //передаем только заголовок, TradeOrders нужно обновить до TradeOrder отдельными запросами
                        int i = 0;
                        for (; i < worldObjectTO.TradeOrders.Count; i++)
                        {
                            if (worldObjectTO.TradeOrders[i].Id == worldObjectEntry.Id)
                            {
                                worldObjectTO.TradeOrders[i] = (TradeOrderShort)worldObjectEntry;
                                break;
                            }
                        }
                        if (i == worldObjectTO.TradeOrders.Count)
                            worldObjectTO.TradeOrders.Add((TradeOrderShort)worldObjectEntry);
                    }
                    else
                    {
                        err += "9 ";
                        ((TradeThingsOnline)worldObject).TradeThings = (TradeThingStorage)worldObjectEntry;
                    }
                    //Loger.Log("SetID " + worldObjectEntry.ServerId + " " + worldObjectEntry.Name);
                }
                err += "10 ";
                //обновление
                worldObject.Tile = worldObjectEntry.Tile;
                if (MainHelper.DebugMode) Loger.Log($"Client WorldObject_TradeOrdersOnline Set Tile={worldObject.Tile} load={worldObjectEntry.Tile}");
                err += "11 ";
            }
            catch
            {
                Loger.Log("ApplyTradeWorldObject ErrorLog: " + err, Loger.LogLevel.ERROR);
                throw;
            }
        }

        public static void DeleteTradeWorldObject(TradeWorldObjectEntry worldObjectEntry
            , ref List<WorldObject> allWorldObjects
            , ref Dictionary<int, WorldObjectBaseOnline> allWorldObjectsByID
            )
        {
            //поиск уже существующих
            var worldObject = GetOtherByServerId(worldObjectEntry.PlaceServerId, allWorldObjectsByID) as WorldObjectBaseOnline;

            if (worldObject != null)
            {
                //удаляем ордер из списка этого места
                if (worldObjectEntry.Type == TradeWorldObjectEntryType.TradeOrder)
                {
                    var worldObjectTO = worldObject as TradeOrdersOnline;
                    for (int i = 0; i < worldObjectTO.TradeOrders.Count; i++)
                        if (worldObjectTO.TradeOrders[i].Id == worldObjectEntry.Id)
                        {
                            worldObjectTO.TradeOrders.RemoveAt(i--);
                            break;
                        }
                    if (worldObjectTO.TradeOrders.Count == 0)
                    {
                        allWorldObjectsByID.Remove(worldObject.ID);
                        allWorldObjects.Remove(worldObject);
                        ConverterServerId.Remove(worldObjectEntry.PlaceServerId);
                        Find.WorldObjects.Remove(worldObject);
                        WorldObject_TradeOrdersOnline.Remove(worldObjectTO);
                    };
                }
                else
                {
                    allWorldObjectsByID.Remove(worldObject.ID);
                    allWorldObjects.Remove(worldObject);
                    ConverterServerId.Remove(worldObjectEntry.PlaceServerId);
                    Find.WorldObjects.Remove(worldObject);
                }
                //Loger.Log("DeleteWorldObject " + DevelopTest.TextObj(worldObjectEntry) + " " 
                //    + (worldObject == null ? "null" : worldObject.ID.ToString()));
            }
        }

        #endregion

        public static void InitGame()
        {
            WorldObjectEntrys = new Dictionary<int, WorldObjectEntry>();
            ConverterServerId = new Dictionary<long, int>();
            WorldObject_TradeOrdersOnline = new HashSet<TradeOrdersOnline>();
            OnlineWorldDescriptorsByTile = new Dictionary<int, WorldObjectOnline>();
            ToDelete = null;
            LastSendMyWorldObjects = null;
            LastCatchAllWorldObjectsByID = null;
        }

        #region Non-Player World Objects
        private static void ApplyNonPlayerWorldObject(ModelPlayToClient fromServ)
        {
            try
            {
                // Для серверного рассказчика сервер может присылать полный снимок (WObjectOnlineList),
                // даже без дельты ToAdd/ToDelete.
                if (fromServ.WObjectOnlineList != null && fromServ.WObjectOnlineList.Count > 0)
                {
                    RefreshOnlineDescriptorsSnapshot(fromServ.WObjectOnlineList);
                    ApplyNonPlayerWorldObjectSnapshot(fromServ.WObjectOnlineList);
                    LastWorldObjectOnline = fromServ.WObjectOnlineList
                        .Where(w => w != null)
                        .ToList();
                    return;
                }

                if (fromServ.WObjectOnlineToDelete != null && fromServ.WObjectOnlineToDelete.Count > 0)
                {
                    RemoveOnlineDescriptors(fromServ.WObjectOnlineToDelete);
                    var objectToDelete = Find.WorldObjects.AllWorldObjects.Where(wo => wo is Settlement)
                                                     .Where(wo => wo.HasName && !wo.Faction.IsPlayer)
                                                     .Where(o => fromServ.WObjectOnlineToDelete.Any(fs => ValidateOnlineWorldObject(fs, o))).ToList();
                    var worldChanged = false;
                    objectToDelete.ForEach(o => {
                        var settlementAtTile = Find.WorldObjects.SettlementAt(o.Tile);
                        if (settlementAtTile == null) return;
                        settlementAtTile.Destroy();
                        worldChanged = true;
                    });
                    if (worldChanged)
                    {
                        Find.World.WorldUpdate();
                    }
                    if (LastWorldObjectOnline != null && LastWorldObjectOnline.Count > 0)
                    {
                        LastWorldObjectOnline.RemoveAll(WOnline => objectToDelete.Any(o => ValidateOnlineWorldObject(WOnline, o)));
                    }
                }

                if (fromServ.WObjectOnlineToAdd != null && fromServ.WObjectOnlineToAdd.Count > 0)
                {
                    MergeOnlineDescriptors(fromServ.WObjectOnlineToAdd);
                    for (var i = 0; i < fromServ.WObjectOnlineToAdd.Count; i++)
                    {
                        if (!Find.WorldObjects.AnySettlementAt(fromServ.WObjectOnlineToAdd[i].Tile))
                        {
                            Faction faction = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(fm => 
                            fm.def.LabelCap == fromServ.WObjectOnlineToAdd[i].FactionGroup &&
                            fm.loadID == fromServ.WObjectOnlineToAdd[i].loadID);
                            if (faction != null)
                            {
                                var npcBase = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
                                npcBase.SetFaction(faction);
                                npcBase.Tile = fromServ.WObjectOnlineToAdd[i].Tile;
                                npcBase.Name = fromServ.WObjectOnlineToAdd[i].Name;
                                Find.WorldObjects.Add(npcBase);
                                //LastWorldObjectOnline.Add(fromServ.OnlineWObjectToAdd[i]);
                            }
                            else
                            {
                                Log.Warning("Faction is missing or not found : " + fromServ.WObjectOnlineToAdd[i].FactionGroup);
                                Loger.Log("Skipping ToAdd Settlement : " + fromServ.WObjectOnlineToAdd[i].Name);
                            }

                        }
                        else
                        {
                            Loger.Log("Can't Add Settlement. Tile is already occupied " + Find.WorldObjects.SettlementAt(fromServ.WObjectOnlineToAdd[i].Tile), Loger.LogLevel.WARNING);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("Exception LoadFromServer ApplyNonPlayerWorldObject >> " + e);
            }
        }

        private static void ApplyNonPlayerWorldObjectSnapshot(List<WorldObjectOnline> worldObjectsOnline)
        {
            if (worldObjectsOnline == null || worldObjectsOnline.Count == 0) return;
            if (Find.WorldGrid == null) return;
            var tilesCount = Find.WorldGrid.TilesCount;

            var desiredByTile = worldObjectsOnline
                .Where(w => w != null && w.Tile > 0 && w.Tile < tilesCount)
                .GroupBy(w => w.Tile)
                .ToDictionary(g => g.Key, g => g.First());
            if (desiredByTile.Count == 0) return;

            var worldChanged = false;
            var existingSettlements = Find.WorldObjects.AllWorldObjects
                .OfType<Settlement>()
                .Where(wo => wo.HasName && !(wo.Faction?.IsPlayer ?? false))
                .ToList();

            // Обновляем или удаляем существующие неписевые поселения.
            for (int i = 0; i < existingSettlements.Count; i++)
            {
                var settlement = existingSettlements[i];
                if (settlement == null) continue;

                if (!desiredByTile.TryGetValue(settlement.Tile, out var descriptor))
                {
                    Find.WorldObjects.Remove(settlement);
                    worldChanged = true;
                    continue;
                }

                ApplyOnlineSettlementData(settlement, descriptor);
                desiredByTile.Remove(settlement.Tile);
            }

            // Добавляем недостающие точки.
            foreach (var descriptor in desiredByTile.Values)
            {
                if (descriptor == null || descriptor.Tile <= 0) continue;

                if (Find.WorldObjects.AnySettlementAt(descriptor.Tile))
                {
                    var existingAtTile = Find.WorldObjects.SettlementAt(descriptor.Tile);
                    if (existingAtTile != null && !(existingAtTile.Faction?.IsPlayer ?? false))
                    {
                        ApplyOnlineSettlementData(existingAtTile, descriptor);
                        continue;
                    }

                    Loger.Log("Can't Add Settlement. Tile is already occupied " + existingAtTile, Loger.LogLevel.WARNING);
                    continue;
                }

                if (TryCreateOnlineSettlement(descriptor))
                {
                    worldChanged = true;
                }
            }

            if (worldChanged)
            {
                Find.World.WorldUpdate();
            }
        }

        private static bool TryCreateOnlineSettlement(WorldObjectOnline descriptor)
        {
            if (Find.WorldGrid == null) return false;
            if (descriptor == null) return false;
            if (descriptor.Tile <= 0 || descriptor.Tile >= Find.WorldGrid.TilesCount)
            {
                Loger.Log("Skipping ToAdd Settlement: invalid tile " + descriptor.Tile, Loger.LogLevel.WARNING);
                return false;
            }

            var faction = ResolveOnlineWorldObjectFaction(descriptor);
            if (faction == null)
            {
                Log.Warning("Faction is missing or not found : " + descriptor?.FactionGroup + " / " + descriptor?.FactionDef);
                Loger.Log("Skipping ToAdd Settlement : " + descriptor?.Name);
                return false;
            }

            var npcBase = (Settlement)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
            npcBase.SetFaction(faction);
            npcBase.Tile = descriptor.Tile;
            npcBase.Name = string.IsNullOrWhiteSpace(descriptor.Name)
                ? ("Settlement " + descriptor.Tile)
                : descriptor.Name;
            Find.WorldObjects.Add(npcBase);
            return true;
        }

        private static void ApplyOnlineSettlementData(Settlement settlement, WorldObjectOnline descriptor)
        {
            if (settlement == null || descriptor == null) return;

            var faction = ResolveOnlineWorldObjectFaction(descriptor);
            if (faction != null && settlement.Faction != faction)
            {
                settlement.SetFaction(faction);
            }

            if (!string.IsNullOrWhiteSpace(descriptor.Name) && settlement.Name != descriptor.Name)
            {
                settlement.Name = descriptor.Name;
            }
        }

        private static Faction ResolveOnlineWorldObjectFaction(WorldObjectOnline descriptor)
        {
            if (descriptor == null) return null;

            var factions = Find.FactionManager.AllFactionsListForReading
                .Where(f => f != null && !f.IsPlayer)
                .ToList();
            if (factions.Count == 0) return null;

            var settlementBackedFactions = GetSettlementBackedFactions(factions);
            var preferredPool = settlementBackedFactions.Count > 0
                ? settlementBackedFactions
                : factions;
            var requiresHostile = descriptor.ServerGenerated && RequiresHostileFaction(descriptor);
            var suitablePreferredPool = preferredPool
                .Where(f => IsFactionSuitableForStorySettlement(f, requiresHostile))
                .ToList();
            var suitableAllPool = factions
                .Where(f => IsFactionSuitableForStorySettlement(f, requiresHostile))
                .ToList();

            Faction faction = null;
            if (descriptor.loadID > 0)
            {
                faction = factions.FirstOrDefault(f => f.loadID == descriptor.loadID);
            }
            if (faction == null && !string.IsNullOrWhiteSpace(descriptor.FactionDef))
            {
                var defName = descriptor.FactionDef.Trim();
                faction = factions.FirstOrDefault(f =>
                    string.Equals(f.def?.defName, defName, StringComparison.OrdinalIgnoreCase));
            }
            if (faction == null && !string.IsNullOrWhiteSpace(descriptor.FactionGroup))
            {
                var label = descriptor.FactionGroup.Trim();
                faction = factions.FirstOrDefault(f =>
                    string.Equals(f.def?.LabelCap, label, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Name, label, StringComparison.OrdinalIgnoreCase));
            }

            // Если сервер явно задал фракцию и мы ее нашли — не переопределяем.
            if (faction != null)
            {
                return faction;
            }

            if (descriptor.ServerGenerated)
            {
                if (!IsFactionSuitableForStorySettlement(faction, requiresHostile))
                {
                    var fallbackPool = suitablePreferredPool.Count > 0
                        ? suitablePreferredPool
                        : suitableAllPool;
                    if (fallbackPool.Count > 0)
                    {
                        faction = PickDeterministicFaction(fallbackPool, descriptor);
                        Loger.Log("ResolveOnlineWorldObjectFaction hostile fallback "
                            + $"{descriptor.FactionGroup}/{descriptor.FactionDef} -> {faction?.def?.defName}"
                            , Loger.LogLevel.WARNING);
                    }
                }
                else if (faction != null
                    && settlementBackedFactions.Count > 0
                    && !settlementBackedFactions.Any(f => f.loadID == faction.loadID))
                {
                    var betterPool = suitablePreferredPool.Count > 0
                        ? suitablePreferredPool
                        : suitableAllPool;
                    var better = PickDeterministicFaction(betterPool, descriptor);
                    if (better != null)
                    {
                        faction = better;
                    }
                }
            }

            // Для серверных событий и несовпадений DefName допускаем fallback на подходящую NPC-фракцию.
            if (faction == null)
            {
                var fallbackPool = descriptor.ServerGenerated
                    ? (suitablePreferredPool.Count > 0 ? suitablePreferredPool : suitableAllPool)
                    : preferredPool;
                faction = PickDeterministicFaction(fallbackPool, descriptor);
                if (faction == null && !descriptor.ServerGenerated)
                {
                    faction = PickDeterministicFaction(factions, descriptor);
                }
                if (faction != null)
                {
                    Loger.Log("ResolveOnlineWorldObjectFaction fallback "
                        + $"{descriptor.FactionGroup}/{descriptor.FactionDef} -> {faction?.def?.defName}"
                        , Loger.LogLevel.WARNING);
                }
            }

            return faction;
        }

        private static List<Faction> GetSettlementBackedFactions(List<Faction> factions)
        {
            if (factions == null || factions.Count == 0) return new List<Faction>();

            var settlementLoadIds = new HashSet<int>();
            var settlementDefNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var settlements = Find.WorldObjects.AllWorldObjects
                .OfType<Settlement>()
                .Where(s => s?.Faction != null && !s.Faction.IsPlayer)
                .ToList();

            for (int i = 0; i < settlements.Count; i++)
            {
                var faction = settlements[i].Faction;
                if (faction == null) continue;
                if (faction.loadID > 0) settlementLoadIds.Add(faction.loadID);
                var defName = faction.def?.defName;
                if (!string.IsNullOrWhiteSpace(defName))
                {
                    settlementDefNames.Add(defName.Trim());
                }
            }

            return factions
                .Where(f => (f.loadID > 0 && settlementLoadIds.Contains(f.loadID))
                    || (!string.IsNullOrWhiteSpace(f.def?.defName) && settlementDefNames.Contains(f.def.defName.Trim())))
                .ToList();
        }

        private static bool RequiresHostileFaction(WorldObjectOnline descriptor)
        {
            if (descriptor == null || !descriptor.ServerGenerated) return false;
            var storyType = (descriptor.StoryType ?? string.Empty).Trim().ToLowerInvariant();
            return storyType != "trade_camp";
        }

        private static bool IsHostileToPlayer(Faction faction)
        {
            if (faction == null) return false;
            if (faction.IsPlayer) return false;
            var player = Faction.OfPlayer;
            if (player == null) return false;
            return faction.HostileTo(player);
        }

        private static bool IsFactionSuitableForStorySettlement(Faction faction, bool requiresHostile)
        {
            if (faction == null || faction.IsPlayer) return false;
            if (requiresHostile && !IsHostileToPlayer(faction)) return false;
            if (faction.def == null) return false;
            if (string.IsNullOrWhiteSpace(faction.def.settlementTexturePath)) return false;

            var groupMakers = faction.def.pawnGroupMakers;
            if (groupMakers == null || groupMakers.Count == 0) return false;

            for (int i = 0; i < groupMakers.Count; i++)
            {
                var maker = groupMakers[i];
                if (maker == null) continue;
                if (maker.kindDef == PawnGroupKindDefOf.Settlement) return true;
                if (maker.kindDef == PawnGroupKindDefOf.Combat) return true;
            }

            return false;
        }

        private static Faction PickDeterministicFaction(List<Faction> pool, WorldObjectOnline descriptor)
        {
            if (pool == null || pool.Count == 0) return null;
            var ordered = pool
                .Where(f => f != null && !f.IsPlayer)
                .OrderBy(f => f.def?.defName ?? string.Empty)
                .ThenBy(f => f.Name ?? string.Empty)
                .ThenBy(f => f.loadID)
                .ToList();
            if (ordered.Count == 0) return null;

            var key = (descriptor?.FactionDef ?? descriptor?.FactionGroup ?? descriptor?.Name ?? descriptor?.Tile.ToString() ?? string.Empty).Trim();
            var hash = StableHash(key);
            return ordered[hash % ordered.Count];
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                var hash = 23;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    for (var i = 0; i < value.Length; i++)
                    {
                        hash = hash * 31 + value[i];
                    }
                }
                return hash & int.MaxValue;
            }
        }

        public static WorldObjectOnline GetWorldObjects(WorldObject obj)
        {
            var worldObject = new WorldObjectOnline();
            worldObject.Name = obj.LabelCap;
            worldObject.Tile = obj.Tile;
            worldObject.FactionGroup = obj?.Faction?.def?.LabelCap;
            worldObject.FactionDef = obj?.Faction?.def?.defName;
            worldObject.loadID = obj.Faction.loadID;
            return worldObject;
        }

        public static WorldObjectOnline GetOnlineWorldDescriptorByTile(int tile)
        {
            if (tile <= 0) return null;
            if (OnlineWorldDescriptorsByTile == null || OnlineWorldDescriptorsByTile.Count == 0) return null;
            if (!OnlineWorldDescriptorsByTile.TryGetValue(tile, out var descriptor)) return null;
            return CloneOnlineWorldDescriptor(descriptor);
        }

        public static bool EnsureOnlineSettlementAtTile(int tile)
        {
            if (tile <= 0) return false;

            var descriptor = GetOnlineWorldDescriptorByTile(tile);
            if (descriptor == null) return false;

            var existing = Find.WorldObjects?.SettlementAt(tile);
            if (existing != null)
            {
                ApplyOnlineSettlementData(existing, descriptor);
                return true;
            }

            var created = TryCreateOnlineSettlement(descriptor);
            if (created)
            {
                Find.World?.WorldUpdate();
            }
            return created;
        }

        private static void RefreshOnlineDescriptorsSnapshot(List<WorldObjectOnline> descriptors)
        {
            if (OnlineWorldDescriptorsByTile == null) OnlineWorldDescriptorsByTile = new Dictionary<int, WorldObjectOnline>();
            OnlineWorldDescriptorsByTile.Clear();
            if (descriptors == null || descriptors.Count == 0) return;

            for (int i = 0; i < descriptors.Count; i++)
            {
                var item = descriptors[i];
                if (item == null || item.Tile <= 0) continue;
                OnlineWorldDescriptorsByTile[item.Tile] = CloneOnlineWorldDescriptor(item);
            }
        }

        private static void MergeOnlineDescriptors(List<WorldObjectOnline> descriptors)
        {
            if (OnlineWorldDescriptorsByTile == null) OnlineWorldDescriptorsByTile = new Dictionary<int, WorldObjectOnline>();
            if (descriptors == null || descriptors.Count == 0) return;

            for (int i = 0; i < descriptors.Count; i++)
            {
                var item = descriptors[i];
                if (item == null || item.Tile <= 0) continue;
                OnlineWorldDescriptorsByTile[item.Tile] = CloneOnlineWorldDescriptor(item);
            }
        }

        private static void RemoveOnlineDescriptors(List<WorldObjectOnline> descriptors)
        {
            if (OnlineWorldDescriptorsByTile == null || OnlineWorldDescriptorsByTile.Count == 0) return;
            if (descriptors == null || descriptors.Count == 0) return;

            for (int i = 0; i < descriptors.Count; i++)
            {
                var item = descriptors[i];
                if (item == null || item.Tile <= 0) continue;
                OnlineWorldDescriptorsByTile.Remove(item.Tile);
            }
        }

        private static WorldObjectOnline CloneOnlineWorldDescriptor(WorldObjectOnline source)
        {
            if (source == null) return null;
            return new WorldObjectOnline()
            {
                Name = source.Name,
                Tile = source.Tile,
                FactionGroup = source.FactionGroup,
                FactionDef = source.FactionDef,
                loadID = source.loadID,
                ServerGenerated = source.ServerGenerated,
                ExpireAtUtc = source.ExpireAtUtc,
                StoryType = source.StoryType,
                StoryLevel = source.StoryLevel,
                StoryNextActionUtc = source.StoryNextActionUtc,
                StorySeed = source.StorySeed
            };
        }

        private static bool ValidateOnlineWorldObject(WorldObjectOnline WObjectOnline1, WorldObject WObjectOnline2)
        {
            if (WObjectOnline1.Name == WObjectOnline2.LabelCap
                && WObjectOnline1.Tile == WObjectOnline2.Tile)
            {
                return true;
            }
            return false;
        }
        #endregion

        #region Factions
        private static void ApplyFactionsToWorld(ModelPlayToClient fromServ)
        {
            try
            {
                var worldChanged = false;

                if (fromServ.FactionOnlineList != null && fromServ.FactionOnlineList.Count > 0)
                {
                    OCFactionManager.UpdateFactionIDS(fromServ.FactionOnlineList);
                }

                if (fromServ.FactionOnlineToDelete != null && fromServ.FactionOnlineToDelete.Count > 0)
                {
                    var factionToDelete = Find.FactionManager.AllFactionsListForReading.Where(f => !f.IsPlayer)
                        .Where(obj => fromServ.FactionOnlineToDelete.Any(fs => MatchesFactionDescriptor(fs, obj)))
                        .ToList();

                    for (var i = 0; i < factionToDelete.Count; i++)
                    {
                        OCFactionManager.DeleteFaction(factionToDelete[i]);
                        worldChanged = true;
                    }

                    if (LastFactionOnline != null && LastFactionOnline.Count > 0)
                    {
                        LastFactionOnline.RemoveAll(fOnline => factionToDelete.Any(obj => MatchesFactionDescriptor(fOnline, obj)));
                    }
                }

                var snapshotDescriptors = BuildFactionSnapshot(fromServ);
                for (var i = 0; i < snapshotDescriptors.Count; i++)
                {
                    var descriptor = snapshotDescriptors[i];
                    if (descriptor == null) continue;

                    try
                    {
                        var existingFaction = FindFactionByDescriptor(descriptor);
                        if (existingFaction != null)
                        {
                            if (existingFaction.loadID <= 0 && descriptor.loadID > 0)
                            {
                                existingFaction.loadID = descriptor.loadID;
                            }
                            continue;
                        }

                        if (!CanInstantiateFaction(descriptor))
                        {
                            Loger.Log("Skip add faction. FactionDef is missing: "
                                + descriptor.LabelCap + " / " + descriptor.DefName
                                , Loger.LogLevel.WARNING);
                            continue;
                        }

                        OCFactionManager.AddNewFaction(descriptor);
                        worldChanged = true;
                    }
                    catch
                    {
                        Loger.Log("Error faction to add LabelCap >> " + descriptor.LabelCap, Loger.LogLevel.ERROR);
                        Loger.Log("Error faction to add DefName >> " + descriptor.DefName, Loger.LogLevel.ERROR);
                    }
                }

                if (fromServ.FactionOnlineList != null && fromServ.FactionOnlineList.Count > 0)
                {
                    OCFactionManager.UpdateFactionIDS(fromServ.FactionOnlineList);
                }

                if (worldChanged)
                {
                    Find.World?.WorldUpdate();
                }
            }
            catch (Exception e)
            {
                Log.Error("OnlineCity: Error Apply new faction to world >> " + e);
            }
        }

        private static List<FactionOnline> BuildFactionSnapshot(ModelPlayToClient fromServ)
        {
            var raw = new List<FactionOnline>();
            if (fromServ?.FactionOnlineList != null)
            {
                raw.AddRange(fromServ.FactionOnlineList.Where(d => d != null));
            }
            if (fromServ?.FactionOnlineToAdd != null)
            {
                raw.AddRange(fromServ.FactionOnlineToAdd.Where(d => d != null));
            }

            return raw
                .Where(d => !string.IsNullOrWhiteSpace(d.DefName)
                    || !string.IsNullOrWhiteSpace(d.LabelCap)
                    || d.loadID > 0)
                .GroupBy(GetFactionDescriptorKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(d => d.loadID).FirstOrDefault())
                .Where(d => d != null)
                .ToList();
        }

        private static string GetFactionDescriptorKey(FactionOnline descriptor)
        {
            if (descriptor == null) return string.Empty;

            if (descriptor.loadID > 0)
            {
                return "id:" + descriptor.loadID;
            }

            var defName = descriptor.DefName?.Trim() ?? string.Empty;
            var label = descriptor.LabelCap?.Trim() ?? string.Empty;
            return (defName + "|" + label).ToLowerInvariant();
        }

        private static bool CanInstantiateFaction(FactionOnline descriptor)
        {
            if (descriptor == null) return false;
            if (string.IsNullOrWhiteSpace(descriptor.DefName)) return false;
            return DefDatabase<FactionDef>.GetNamedSilentFail(descriptor.DefName.Trim()) != null;
        }

        private static Faction FindFactionByDescriptor(FactionOnline descriptor)
        {
            if (descriptor == null) return null;

            var factions = Find.FactionManager.AllFactionsListForReading
                .Where(f => f != null && !f.IsPlayer)
                .ToList();
            if (factions.Count == 0) return null;

            if (descriptor.loadID > 0)
            {
                var byId = factions.FirstOrDefault(f => f.loadID == descriptor.loadID);
                if (byId != null) return byId;
            }

            var defName = descriptor.DefName?.Trim();
            if (!string.IsNullOrWhiteSpace(defName))
            {
                var byDefAndLabel = factions.FirstOrDefault(f =>
                    string.Equals(f.def?.defName, defName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.def?.LabelCap, descriptor.LabelCap?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (byDefAndLabel != null) return byDefAndLabel;

                var byDef = factions.FirstOrDefault(f =>
                    string.Equals(f.def?.defName, defName, StringComparison.OrdinalIgnoreCase));
                if (byDef != null) return byDef;
            }

            var label = descriptor.LabelCap?.Trim();
            if (!string.IsNullOrWhiteSpace(label))
            {
                var byLabel = factions.FirstOrDefault(f =>
                    string.Equals(f.def?.LabelCap, label, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Name, label, StringComparison.OrdinalIgnoreCase));
                if (byLabel != null) return byLabel;
            }

            return null;
        }

        private static bool MatchesFactionDescriptor(FactionOnline descriptor, Faction faction)
        {
            if (descriptor == null || faction == null || faction.IsPlayer) return false;
            if (ValidateFaction(descriptor, faction)) return true;

            if (descriptor.loadID > 0 && faction.loadID == descriptor.loadID)
            {
                return true;
            }

            var defName = descriptor.DefName?.Trim();
            var label = descriptor.LabelCap?.Trim();
            if (!string.IsNullOrWhiteSpace(defName)
                && string.Equals(faction.def?.defName, defName, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(label))
                {
                    return true;
                }
                return string.Equals(faction.def?.LabelCap, label, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(faction.Name, label, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                return string.Equals(faction.def?.LabelCap, label, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(faction.Name, label, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        public static FactionOnline GetFactions(Faction obj)
        {
            var faction = new FactionOnline();
            faction.Name = obj.Name;
            faction.LabelCap = obj.def.LabelCap;
            faction.DefName = obj.def.defName;
            faction.loadID = obj.loadID;
            return faction;
        }

        private static bool ValidateFaction(FactionOnline fOnline1, Faction fOnline2)
        {
            if (fOnline1.LabelCap == fOnline2.def.LabelCap &&
                fOnline1.DefName == fOnline2.def.defName && 
                fOnline1.loadID == fOnline2.loadID)
            {
                return true;
            }
            return false;
        }
        #endregion
    }
}
