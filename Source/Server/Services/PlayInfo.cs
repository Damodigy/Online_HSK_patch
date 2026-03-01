using System;
using System.Collections.Generic;
using System.Linq;
using Model;
using OCUnion;
using OCUnion.Transfer.Model;
using ServerOnlineCity.Common;
using ServerOnlineCity.Mechanics;
using ServerOnlineCity.Model;
using Transfer;
using Transfer.ModelMails;

namespace ServerOnlineCity.Services
{
    internal sealed class PlayInfo : IGenerateResponseContainer
    {
        public int RequestTypePackage => (int)PackageType.Request11;

        public int ResponseTypePackage => (int)PackageType.Response12;

        public ModelContainer GenerateModelContainer(ModelContainer request, ServiceContext context)
        {
            if (context.Player == null) return null;
            var result = new ModelContainer() { TypePacket = ResponseTypePackage };
            result.Packet = playInfo((ModelPlayToServer)request.Packet, context);
            return result;
        }

        public ModelPlayToClient playInfo(ModelPlayToServer packet, ServiceContext context)
        {
            if (Repository.CheckIsBanIP(context.AddrIP))
            {
                context.Disconnect("New BanIP " + context.AddrIP);
                return null;
            }
            if (context.PossiblyIntruder)
            {
                context.Disconnect("Possibly intruder");
                return null;
            }
            lock (context.Player)
            {
                var data = Repository.GetData;

                var timeNow = DateTime.UtcNow;
                var generalSettings = ServerManager.ServerSettings.GeneralSettings;
                var wasLongOffline = context.Player.LastUpdateTime != DateTime.MinValue
                    && (timeNow - context.Player.LastUpdateTime).TotalMinutes >= GetStoryDigestOfflineMinutes(generalSettings);
                var toClient = new ModelPlayToClient();
                toClient.UpdateTime = timeNow;
                if (packet.GetPlayersInfo != null && packet.GetPlayersInfo.Count > 0)
                {
                    var pSee = StaticHelper.PartyLoginSee(context.Player);
                    var pGet = new HashSet<string>(packet.GetPlayersInfo);
                    pGet.IntersectWith(pSee);

                    toClient.PlayersInfo = pGet
                        .Select(l => Repository.GetPlayerByLogin(l))
                        .Where(p => p != null)
                        .Select(p => p.Public)
                        //.Where(l => Repository.GetData.PlayersAllDic.ContainsKey(l))
                        //.Select(l => Repository.GetData.PlayersAllDic[l].Public)
                        .ToList();
                }
                if (packet.SaveFileData != null && packet.SaveFileData.Length > 0)
                {
                    Repository.GetSaveData.SavePlayerData(
                        context.Player.Public.Login,
                        packet.SaveFileData,
                        packet.SingleSave,
                        packet.SaveNumber,
                        packet.SaveIsAuto);
                    context.Player.Public.LastSaveTime = timeNow;

                    //Действия при сохранении, оно происодит только здесь!
                    context.Player.MailsConfirmationSave = new List<ModelMail>();

                    Repository.Get.ChangeData = true;
                }
                if (context.Player.GetKeyReconnect())
                {
                    toClient.KeyReconnect = context.Player.KeyReconnect1;
                }

                var pLogin = context.Player.Public.Login;
                //packet.WObjects тут все объекты этого игрока, добавляем которых у нас нет
                var pWOs = packet.WObjects ?? new List<WorldObjectEntry>();
                //packet.WObjectsToDelete тут те объекты этого игрока, что нужно удалить
                var pDs = packet.WObjectsToDelete ?? new List<WorldObjectEntry>();
                //передаем назад объекты у которых не было ServerId, т.е. они новые для сервера + все с изменениями
                var outWO = new List<WorldObjectEntry>();
                var outWOD = new List<WorldObjectEntry>();
                //это первое обращение: initial sync идет без payload своих объектов.
                //для delta-режима пустой список объектов не должен считаться first.
                var first = !packet.IsWorldObjectsSync && pWOs.Count == 0;
                lock (data)
                {
                    for (int i = 0; i < pDs.Count; i++)
                    {
                        if (pDs[i].LoginOwner != context.Player.Public.Login) continue;
                        ServerStoryteller.RegisterKnownTile(data, pDs[i].Tile);
                        var sid = pDs[i].PlaceServerId;
                        var pD = data.WorldObjects.FirstOrDefault(p => p.PlaceServerId == sid);
                        if (pD != null)
                        {
                            if (pD.Type == WorldObjectEntryType.Base)
                            {
                                ServerStoryteller.AppendStoryEvent(data
                                    , "События игроков"
                                    , $"Игрок {context.Player.Public.Login} покинул поселение \"{pD.Name}\"."
                                    , pD.Tile
                                    , "players"
                                    , $"player_base_remove:{context.Player.Public.Login}:{pD.PlaceServerId}");
                            }

                            //удаление из базы
                            pD.UpdateTime = timeNow;
                            data.WorldObjects.Remove(pD);
                            data.WorldObjectsDeleted.Add(pD);
                        }
                    }

                    //расчитываем стоимость безналичных активов
                    float totalMarketValue = 0; //цена всех игровых вещей
                    for (int i = 0; i < pWOs.Count; i++)
                    {
                        if (pWOs[i].LoginOwner != context.Player.Public.Login) continue; // <-на всякий случай
                        totalMarketValue += pWOs[i].MarketValue + pWOs[i].MarketValuePawn;
                    }
                    var cashlessBalance = context.Player.CashlessBalance;
                    context.Player.UpdateStorageBalance();
                    var storageBalance = context.Player.StorageBalance;
                    //обрабатываем инфу о пришедших от игрока данных
                    for (int i = 0; i < pWOs.Count; i++)
                    {
                        if (pWOs[i].LoginOwner != context.Player.Public.Login) continue; // <-на всякий случай
                        ServerStoryteller.RegisterKnownTile(data, pWOs[i].Tile);
                        if (totalMarketValue > 0)
                        {
                            pWOs[i].MarketValueBalance = cashlessBalance * (pWOs[i].MarketValue + pWOs[i].MarketValuePawn) / totalMarketValue;
                            pWOs[i].MarketValueStorage = storageBalance * (pWOs[i].MarketValue + pWOs[i].MarketValuePawn) / totalMarketValue;
                        }
                        var sid = pWOs[i].PlaceServerId;
                        if (sid == 0)
                        {
                            //добавление в базу
                            pWOs[i].UpdateTime = timeNow;
                            pWOs[i].PlaceServerId = data.GetWorldObjectEntryId();
                            data.WorldObjects.Add(pWOs[i]);
                            outWO.Add(pWOs[i]);

                            if (pWOs[i].Type == WorldObjectEntryType.Base)
                            {
                                ServerStoryteller.AppendStoryEvent(data
                                    , "События игроков"
                                    , $"Игрок {context.Player.Public.Login} основал поселение \"{pWOs[i].Name}\"."
                                    , pWOs[i].Tile
                                    , "players"
                                    , $"player_base_create:{context.Player.Public.Login}:{pWOs[i].PlaceServerId}");
                            }

                            TryAppendWorldInteractionEvents(data, pWOs[i], 0);
                            continue;
                        }
                        var WO = data.WorldObjects.FirstOrDefault(p => p.PlaceServerId == sid);
                        if (WO != null)
                        {
                            var oldTile = WO.Tile;
                            var tileChanged = false;
                            if (WO.Type != pWOs[i].Type)
                            {
                                WO.UpdateTime = timeNow;
                                WO.Type = pWOs[i].Type;
                            }
                            //данный объект уже есть в базу обновляем по нему информкацию
                            if (WO.Name != pWOs[i].Name)
                            {
                                WO.UpdateTime = timeNow;
                                WO.Name = pWOs[i].Name;
                            }
                            if (WO.FreeWeight != pWOs[i].FreeWeight)
                            {
                                WO.UpdateTime = timeNow;
                                WO.FreeWeight = pWOs[i].FreeWeight;
                            }
                            if (WO.MarketValue != pWOs[i].MarketValue)
                            {
                                WO.UpdateTime = timeNow;
                                WO.MarketValue = pWOs[i].MarketValue;
                            }
                            if (WO.MarketValuePawn != pWOs[i].MarketValuePawn)
                            {
                                WO.UpdateTime = timeNow;
                                WO.MarketValuePawn = pWOs[i].MarketValuePawn;
                            }
                            if (WO.MarketValueBalance != pWOs[i].MarketValueBalance)
                            {
                                WO.UpdateTime = timeNow;
                                WO.MarketValueBalance = pWOs[i].MarketValueBalance;
                            }
                            if (WO.MarketValueStorage != pWOs[i].MarketValueStorage)
                            {
                                WO.UpdateTime = timeNow;
                                WO.MarketValueStorage = pWOs[i].MarketValueStorage;
                            }
                            if (WO.Tile != pWOs[i].Tile)
                            {
                                WO.UpdateTime = timeNow;
                                WO.Tile = pWOs[i].Tile;
                                tileChanged = true;
                            }

                            if (tileChanged)
                            {
                                TryAppendWorldInteractionEvents(data, WO, oldTile);
                            }
                        }
                        else
                        {
                            Loger.Log("PlayInfo find error add WO: " + pWOs[i].Name + " sid=" + sid);
                        }
                    }

                    //передаем все объекты, которые были изменены, но в первый запуск (first) исключаем свои
                    for (int i = 0; i < data.WorldObjects.Count; i++)
                    {
                        if (data.WorldObjects[i].UpdateTime < packet.UpdateTime) continue;
                        if (!first && data.WorldObjects[i].LoginOwner == pLogin) continue;
                        outWO.Add(data.WorldObjects[i]);
                    }

                    //передаем удаленные объекты других игроков (не для первого запроса)
                    if (packet.UpdateTime > DateTime.MinValue && data.WorldObjectsDeleted != null)
                    {
                        for (int i = 0; i < data.WorldObjectsDeleted.Count; i++)
                        {
                            if (data.WorldObjectsDeleted[i].UpdateTime < packet.UpdateTime)
                            {
                                //Обслуживание общего списка: Удаляем все записи сроком старше 2х минут (их нужно хранить время между тем как игрок у которого удалился караван зальёт это на сервер, и все другие онлайн игроки получат эту инфу, а обновление идет раз в 5 сек)
                                if ((timeNow - data.WorldObjectsDeleted[i].UpdateTime).TotalSeconds > 120000)
                                {
                                    data.WorldObjectsDeleted.RemoveAt(i--);
                                }
                                continue;
                            }
                            if (data.WorldObjectsDeleted[i].LoginOwner == pLogin) continue;
                            outWOD.Add(data.WorldObjectsDeleted[i]);
                        }
                    }

                    //передаем государства
                    if (data.StateUpdateTime > packet.UpdateTime)
                    {
                        toClient.States = data.States
                            .Select(state =>
                            {
                                var res = new StateInfo(state);
                                foreach (var p in data.GetStatePlayers(state.Name))
                                {
                                    if (res.Head == null && Repository.GetStatePosition(p.Public)?.RightHead == true)
                                    {
                                        res.Head = p.Public.Login;
                                    }
                                    res.Players.Add(p.Public.Login);
                                }
                                return res;
                            })
                            .ToList();
                    }

                    #region Non-Player World Objects
                    //World Object Online
                    var forceFirstRunSync = packet.UpdateTime <= DateTime.MinValue;
                    var allowClientWorldObjectsSync = ServerManager.ServerSettings.GeneralSettings.EquableWorldObjects;
                    var needWorldObjectsSync = allowClientWorldObjectsSync
                        || ServerManager.ServerSettings.GeneralSettings.StorytellerEnable
                        || forceFirstRunSync;
                    if (needWorldObjectsSync)
                    {
                        //World Object Online
                        try
                        {
                            if (data.WorldObjectOnlineList == null) data.WorldObjectOnlineList = new List<WorldObjectOnline>();

                            if (allowClientWorldObjectsSync && packet.WObjectOnlineToDelete != null && packet.WObjectOnlineToDelete.Count > 0)
                            {
                                data.WorldObjectOnlineList.RemoveAll(d =>
                                    (d?.ServerGenerated ?? false) == false
                                    && packet.WObjectOnlineToDelete.Any(pkt => ValidateWorldObject(pkt, d)));
                            }
                            if (allowClientWorldObjectsSync && packet.WObjectOnlineToAdd != null && packet.WObjectOnlineToAdd.Count > 0)
                            {
                                data.WorldObjectOnlineList.AddRange(packet.WObjectOnlineToAdd
                                    .Where(add => add != null
                                        && !data.WorldObjectOnlineList.Any(d => ValidateWorldObject(add, d))));
                            }
                            if (packet.WObjectOnlineToDelete != null && packet.WObjectOnlineToDelete.Count > 0)
                            {
                                foreach (var pkt in packet.WObjectOnlineToDelete)
                                {
                                    ServerStoryteller.RegisterKnownTile(data, pkt.Tile);
                                }
                            }
                            if (packet.WObjectOnlineToAdd != null && packet.WObjectOnlineToAdd.Count > 0)
                            {
                                foreach (var pkt in packet.WObjectOnlineToAdd)
                                {
                                    ServerStoryteller.RegisterKnownTile(data, pkt.Tile);
                                }
                            }
                            if (packet.WObjectOnlineList != null && packet.WObjectOnlineList.Count > 0)
                            {
                                var snapshotFromClient = NormalizeWorldObjectSnapshot(packet.WObjectOnlineList);
                                foreach (var pkt in snapshotFromClient)
                                {
                                    ServerStoryteller.RegisterKnownTile(data, pkt.Tile);
                                }

                                var needHardResync = NeedHardResyncWorldSnapshot(data.WorldObjectOnlineList, snapshotFromClient);

                                if (data.WorldObjectOnlineList.Count == 0 || needHardResync)
                                {
                                    var keepServerGenerated = data.WorldObjectOnlineList
                                        .Where(w => w != null && w.ServerGenerated)
                                        .Select(CloneWorldObject)
                                        .ToList();

                                    data.WorldObjectOnlineList = snapshotFromClient;
                                    if (keepServerGenerated.Count > 0)
                                    {
                                        data.WorldObjectOnlineList.AddRange(keepServerGenerated
                                            .Where(extra => extra != null
                                                && extra.Tile > 0
                                                && !data.WorldObjectOnlineList.Any(basePoint => (basePoint?.Tile ?? 0) == extra.Tile)
                                                && !data.WorldObjectOnlineList.Any(basePoint => ValidateWorldObject(extra, basePoint))));
                                    }

                                    RebuildStorytellerKnownTiles(data);

                                    if (needHardResync)
                                    {
                                        ServerStoryteller.AppendStoryEvent(data
                                            , "Сюжет мира"
                                            , "Каталог мировых точек обновлен по актуальному снимку мира с сохранением активных сюжетных точек."
                                            , 0
                                            , "storyteller"
                                            , "world_snapshot_resync"
                                            , 120);
                                    }
                                }
                                else if (allowClientWorldObjectsSync && data.WorldObjectOnlineList != null && data.WorldObjectOnlineList.Count > 0)
                                {
                                    toClient.WObjectOnlineToDelete = snapshotFromClient.Where(pkt => !data.WorldObjectOnlineList.Any(d => ValidateWorldObject(pkt, d))).ToList();
                                    toClient.WObjectOnlineToAdd = data.WorldObjectOnlineList.Where(d => !snapshotFromClient.Any(pkt => ValidateWorldObject(pkt, d))).ToList();
                                }
                            }
                            toClient.WObjectOnlineList = data.WorldObjectOnlineList;
                        }
                        catch
                        {
                            Loger.Log("ERROR PLAYINFO World Object Online", Loger.LogLevel.ERROR);
                        }

                        //Faction Online
                        try
                        {
                            if (data.FactionOnlineList == null) data.FactionOnlineList = new List<FactionOnline>();

                            if (packet.FactionOnlineToDelete != null && packet.FactionOnlineToDelete.Count > 0)
                            {
                                data.FactionOnlineList.RemoveAll(d => packet.FactionOnlineToDelete.Any(pkt => ValidateFaction(pkt, d)));
                            }
                            if (packet.FactionOnlineToAdd != null && packet.FactionOnlineToAdd.Count > 0)
                            {
                                AddMissingFactions(data.FactionOnlineList, packet.FactionOnlineToAdd);
                            }
                            if (packet.FactionOnlineList != null && packet.FactionOnlineList.Count > 0)
                            {
                                AddMissingFactions(data.FactionOnlineList, packet.FactionOnlineList);

                                toClient.FactionOnlineToDelete = packet.FactionOnlineList.Where(pkt => !data.FactionOnlineList.Any(d => ValidateFaction(pkt, d))).ToList();
                                toClient.FactionOnlineToAdd = data.FactionOnlineList.Where(d => !packet.FactionOnlineList.Any(pkt => ValidateFaction(pkt, d))).ToList();
                            }
                            toClient.FactionOnlineList = data.FactionOnlineList;
                        }
                        catch
                        {
                            Loger.Log("ERROR PLAYINFO Faction Online", Loger.LogLevel.ERROR);
                        }
                    }
                    #endregion

                    //получаем торговые точки с общей информацией по ним
                    var outWTO = new List<TradeWorldObjectEntry>();
                    var outWTOD = new List<TradeWorldObjectEntry>();

                    for (int i = 0; i < context.Player.TradeThingStorages.Count; i++)
                    {
                        if (context.Player.TradeThingStorages[i].UpdateTime < packet.UpdateTime) continue;
                        outWTO.Add(context.Player.TradeThingStorages[i]); //это может быть тяжелым объектом и присылаться каждый раз при входе
                    }
                    for (int i = 0; i < data.OrderOperator.TradeWorldObjects.Count; i++)
                    {
                        if (data.OrderOperator.TradeWorldObjects[i].UpdateTime < packet.UpdateTime) continue;
                        outWTO.Add(data.OrderOperator.TradeWorldObjects[i]);
                    }
                    //передаем удаленные объекты других игроков (не для первого запроса)
                    if (packet.UpdateTime > DateTime.MinValue)
                    {
                        for (int i = 0; i < data.OrderOperator.TradeWorldObjectsDeleted.Count; i++)
                        {
                            if (data.OrderOperator.TradeWorldObjectsDeleted[i].UpdateTime < packet.UpdateTime)
                            {
                                //Обслуживание общего списка: Удаляем все записи сроком старше 2х минут (их нужно хранить время между тем как игрок удалил запись, и все другие онлайн игроки получат эту инфу, а обновление идет раз в 5 сек)
                                if ((timeNow - data.OrderOperator.TradeWorldObjectsDeleted[i].UpdateTime).TotalSeconds > 120000)
                                {
                                    data.OrderOperator.TradeWorldObjectsDeleted.RemoveAt(i--);
                                }
                                continue;
                            }
                            if (data.OrderOperator.TradeWorldObjectsDeleted[i].LoginOwner == pLogin) continue;
                            outWTOD.Add(data.OrderOperator.TradeWorldObjectsDeleted[i]);
                        }
                    }

                    toClient.WObjects = outWO;
                    toClient.WObjectsToDelete = outWOD;
                    toClient.WTObjects = outWTO;
                    toClient.WTObjectsToDelete = outWTOD;
                    context.Player.GameProgressLast = context.Player.GameProgress;
                    context.Player.GameProgress = packet.GameProgress;

                    context.Player.WLastUpdateTime = timeNow;
                    context.Player.WLastTick = packet.LastTick;

                    //обновляем статистические поля
                    var costAll = context.Player.CostWorldObjects();
                    if (context.Player.StartMarketValuePawn == 0)
                    {
                        context.Player.StartMarketValue = costAll.MarketValue;
                        context.Player.StartMarketValuePawn = costAll.MarketValuePawn;

                        context.Player.DeltaMarketValue = 0;
                        context.Player.DeltaMarketValuePawn = 0;
                        context.Player.DeltaMarketValueBalance = 0;
                        context.Player.DeltaMarketValueStorage = 0;
                    }
                    else if (context.Player.LastUpdateIsGood && (costAll.MarketValue > 0 || costAll.MarketValuePawn > 0))
                    {
                        //считаем дельту
                        context.Player.DeltaMarketValue = (costAll.MarketValue - context.Player.LastMarketValue);
                        context.Player.DeltaMarketValuePawn = (costAll.MarketValuePawn - context.Player.LastMarketValuePawn);
                        context.Player.DeltaMarketValueBalance = (costAll.MarketValueBalance - context.Player.LastMarketValueBalance);
                        context.Player.DeltaMarketValueStorage = (costAll.MarketValueStorage - context.Player.LastMarketValueStorage);

                        context.Player.SumDeltaGameMarketValue += context.Player.DeltaMarketValue;
                        context.Player.SumDeltaGameMarketValuePawn += context.Player.DeltaMarketValuePawn;
                        context.Player.SumDeltaGameMarketValueBalance += context.Player.DeltaMarketValueBalance;
                        context.Player.SumDeltaGameMarketValueStorage += context.Player.DeltaMarketValueStorage;
                        context.Player.SumDeltaRealMarketValue += context.Player.DeltaMarketValue;
                        context.Player.SumDeltaRealMarketValuePawn += context.Player.DeltaMarketValuePawn;
                        context.Player.SumDeltaRealMarketValueBalance += context.Player.DeltaMarketValueBalance;
                        context.Player.SumDeltaRealMarketValueStorage += context.Player.DeltaMarketValueStorage;

                        if (packet.LastTick - context.Player.StatLastTick > 15 * 60000) // сбор раз в 15 дней
                        {
                            if (context.Player.StatMaxDeltaGameMarketValue < context.Player.SumDeltaGameMarketValue)
                                context.Player.StatMaxDeltaGameMarketValue = context.Player.SumDeltaGameMarketValue;
                            if (context.Player.StatMaxDeltaGameMarketValuePawn < context.Player.SumDeltaGameMarketValuePawn)
                                context.Player.StatMaxDeltaGameMarketValuePawn = context.Player.SumDeltaGameMarketValuePawn;
                            if (context.Player.StatMaxDeltaGameMarketValueBalance < context.Player.SumDeltaGameMarketValueBalance)
                                context.Player.StatMaxDeltaGameMarketValueBalance = context.Player.SumDeltaGameMarketValueBalance;
                            if (context.Player.StatMaxDeltaGameMarketValueStorage < context.Player.SumDeltaGameMarketValueStorage)
                                context.Player.StatMaxDeltaGameMarketValueStorage = context.Player.SumDeltaGameMarketValueStorage;
                            if (context.Player.StatMaxDeltaGameMarketValueTotal < context.Player.SumDeltaGameMarketValue + context.Player.SumDeltaGameMarketValueBalance + context.Player.SumDeltaGameMarketValuePawn + context.Player.SumDeltaGameMarketValueStorage)
                                context.Player.StatMaxDeltaGameMarketValueTotal = context.Player.SumDeltaGameMarketValue + context.Player.SumDeltaGameMarketValueBalance + context.Player.SumDeltaGameMarketValuePawn + context.Player.SumDeltaGameMarketValueStorage;

                            context.Player.SumDeltaGameMarketValue = 0;
                            context.Player.SumDeltaGameMarketValuePawn = 0;
                            context.Player.SumDeltaGameMarketValueBalance = 0;
                            context.Player.SumDeltaGameMarketValueStorage = 0;
                            context.Player.StatLastTick = packet.LastTick;
                        }

                        if (context.Player.SumDeltaRealSecond > 60 * 60) //сбор раз в час
                        {
                            if (context.Player.StatMaxDeltaRealMarketValue < context.Player.SumDeltaRealMarketValue)
                                context.Player.StatMaxDeltaRealMarketValue = context.Player.SumDeltaRealMarketValue;
                            if (context.Player.StatMaxDeltaRealMarketValuePawn < context.Player.SumDeltaRealMarketValuePawn)
                                context.Player.StatMaxDeltaRealMarketValuePawn = context.Player.SumDeltaRealMarketValuePawn;
                            if (context.Player.StatMaxDeltaRealMarketValueBalance < context.Player.SumDeltaRealMarketValueBalance)
                                context.Player.StatMaxDeltaRealMarketValueBalance = context.Player.SumDeltaRealMarketValueBalance;
                            if (context.Player.StatMaxDeltaRealMarketValueStorage < context.Player.SumDeltaRealMarketValueStorage)
                                context.Player.StatMaxDeltaRealMarketValueStorage = context.Player.SumDeltaRealMarketValueStorage;
                            if (context.Player.StatMaxDeltaRealMarketValueTotal < context.Player.SumDeltaRealMarketValue + context.Player.SumDeltaRealMarketValueBalance + context.Player.SumDeltaRealMarketValuePawn + context.Player.SumDeltaRealMarketValueStorage)
                                context.Player.StatMaxDeltaRealMarketValueTotal = context.Player.SumDeltaRealMarketValue + context.Player.SumDeltaRealMarketValueBalance + context.Player.SumDeltaRealMarketValuePawn + context.Player.SumDeltaRealMarketValueStorage;
                            if (context.Player.StatMaxDeltaRealTicks < context.Player.SumDeltaRealTicks)
                                context.Player.StatMaxDeltaRealTicks = context.Player.SumDeltaRealTicks;

                            context.Player.SumDeltaRealMarketValue = 0;
                            context.Player.SumDeltaRealMarketValuePawn = 0;
                            context.Player.SumDeltaRealMarketValueBalance = 0;
                            context.Player.SumDeltaRealMarketValueStorage = 0;
                            context.Player.SumDeltaRealTicks = 0;
                            context.Player.SumDeltaRealSecond = 0;
                        }
                    }
                    context.Player.LastUpdateIsGood = costAll.MarketValue > 0 || costAll.MarketValuePawn > 0;
                    if (context.Player.LastUpdateIsGood)
                    {
                        context.Player.LastMarketValue = costAll.MarketValue;
                        context.Player.LastMarketValuePawn = costAll.MarketValuePawn;
                        context.Player.LastMarketValueBalance = costAll.MarketValueBalance;
                        context.Player.LastMarketValueStorage = costAll.MarketValueStorage;
                    }
                    var dt = packet.LastTick - context.Player.Public.LastTick;
                    context.Player.SumDeltaRealTicks += dt;
                    if (dt > 0)
                    {
                        var ds = (long)(timeNow - context.Player.LastUpdateTime).TotalSeconds;
                        context.Player.SumDeltaRealSecond += ds;
                        context.Player.TotalRealSecond += ds;

                    }

                    context.Player.WLastUpdateTime = context.Player.LastUpdateTime;
                    context.Player.WLastTick = context.Player.Public.LastTick;
                    context.Player.LastUpdateTime = timeNow;
                    context.Player.Public.LastTick = packet.LastTick;
                    context.Player.Public.ExistsEnemyPawns = context.Player.GameProgressLast?.ExistsEnemyPawns == true;


                    //Прошел игровой полдень
                    if (CalcUtils.OnMidday(context.Player.WLastTick, context.Player.Public.LastTick))
                    {
                        //раз в день взымаем налоги на бирже
                        data.OrderOperator.DayPassed(context.Player);
                        //записываем в историю
                        context.Player.MarketValueHistoryAdd(context.Player.LastMarketValue);
                    }
                }

                //обновляем состояние отложенной отправки писем
                if (context.Player.FunctionMails.Count > 0)
                {
                    for (int i = 0; i < context.Player.FunctionMails.Count; i++)
                    {
                        var needRemove = context.Player.FunctionMails[i].Run(context);
                        if (needRemove) context.Player.FunctionMails.RemoveAt(i--);
                    }
                }

                MergeMessageMailsToLog(context.Player, wasLongOffline, generalSettings);
                AddStoryDigestIfNeeded(context.Player, data, wasLongOffline, generalSettings);

                //прикрепляем письма
                //если есть команда на отключение без сохранения, то посылаем только одно это письмо
                var md = context.Player.Mails.FirstOrDefault(m => m is ModelMailAttackCancel);
                if (md == null)
                {
                    toClient.Mails = context.Player.Mails;
                    context.Player.MailsConfirmationSave.AddRange(context.Player.Mails.Where(m => m.NeedSaveGame).ToList());
                    context.Player.Mails = new List<ModelMail>();
                }
                else
                {
                    toClient.Mails = new List<ModelMail>() { md };
                    context.Player.Mails.Remove(md);
                }

                //команда выполнить сохранение и отключиться
                toClient.NeedSaveAndExit = !context.Player.IsAdmin && data.EverybodyLogoff;

                //флаг, что на клиента кто-то напал и он должен запросить подробности
                toClient.AreAttacking = context.Player.AttackData != null && context.Player.AttackData.Host == context.Player && context.Player.AttackData.State == 1;

                if (context.Player.LastUpdateWithMail = (toClient.Mails.Count > 0))
                {
                    foreach (var mail in toClient.Mails)
                    {
                        Loger.Log($"DownloadMail {mail.GetType().Name} {mail.From.Login}->{mail.To.Login} {mail.ContentString()}");
                    }
                }

                toClient.CashlessBalance = context.Player.CashlessBalance;
                toClient.StorageBalance = context.Player.StorageBalance;

                return toClient;
            }
        }

        private static void AddStoryDigestIfNeeded(PlayerServer player, BaseContainer data, bool wasLongOffline, ServerGeneralSettings generalSettings)
        {
            if (player == null || data == null) return;

            List<ServerStoryEvent> pendingEvents;
            lock (data)
            {
                pendingEvents = (data.StoryEvents ?? new List<ServerStoryEvent>())
                    .Where(e => e != null && e.Id > player.LastStoryEventIdDelivered)
                    .OrderBy(e => e.Id)
                    .ToList();

                if (pendingEvents.Count > 0)
                {
                    player.LastStoryEventIdDelivered = pendingEvents[pendingEvents.Count - 1].Id;
                }
            }

            if (pendingEvents.Count == 0) return;

            var login = player.Public?.Login;
            var visibleEvents = pendingEvents
                .Where(e => !IsOwnStoryActionForPlayer(e, login))
                .ToList();

            if (visibleEvents.Count == 0) return;

            var maxLines = GetStoryDigestMaxLines(generalSettings);
            var show = visibleEvents
                .Skip(Math.Max(0, visibleEvents.Count - maxLines))
                .ToList();

            var head = visibleEvents.Count > show.Count
                ? $"Пока вас не было, произошло событий: {visibleEvents.Count}. Показаны последние {show.Count}."
                : $"Пока вас не было, произошло событий: {visibleEvents.Count}.";

            player.Mails.Add(new ModelMailStoryLog()
            {
                From = Repository.GetData.PlayerSystem.Public,
                To = player.Public,
                Kind = StoryLogKind.Narrative,
                PopupOnReceive = true,
                Title = "Лог повествования",
                Summary = head,
                TotalCount = visibleEvents.Count,
                ShownCount = show.Count,
                Entries = show.Select(e => new StoryLogEntry()
                {
                    CreatedUtc = e.CreatedUtc,
                    Category = string.IsNullOrWhiteSpace(e.Category) ? "storyteller" : e.Category,
                    Label = string.IsNullOrWhiteSpace(e.Label) ? "Событие мира" : e.Label,
                    Text = e.Text,
                    Tile = e.Tile
                }).ToList()
            });
        }

        private static void MergeMessageMailsToLog(PlayerServer player, bool wasLongOffline, ServerGeneralSettings generalSettings)
        {
            if (player == null || player.Mails == null || player.Mails.Count == 0) return;

            var messages = player.Mails
                .OfType<ModelMailMessadge>()
                .Where(CanMergeMessageToLog)
                .OrderBy(m => m.Created)
                .ToList();

            if (messages.Count == 0) return;
            if (!wasLongOffline && messages.Count < GetMessageDigestThreshold(generalSettings)) return;

            var maxLines = GetMessageDigestMaxLines(generalSettings);
            var show = messages
                .Skip(Math.Max(0, messages.Count - maxLines))
                .ToList();

            var head = messages.Count > show.Count
                ? $"Уведомлений за период: {messages.Count}. Показаны последние {show.Count}."
                : $"Уведомлений за период: {messages.Count}.";

            var removeSet = new HashSet<ModelMailMessadge>(messages);
            player.Mails = player.Mails
                .Where(m => !(m is ModelMailMessadge mail) || !removeSet.Contains(mail))
                .ToList();

            player.Mails.Add(new ModelMailStoryLog()
            {
                From = Repository.GetData.PlayerSystem.Public,
                To = player.Public,
                Kind = StoryLogKind.Notifications,
                PopupOnReceive = false,
                Title = "Журнал уведомлений",
                Summary = head,
                TotalCount = messages.Count,
                ShownCount = show.Count,
                Entries = show.Select(m => new StoryLogEntry()
                {
                    CreatedUtc = m.Created,
                    Category = "messages",
                    Label = NormalizeLogLine(m.label, 90, "Событие"),
                    Text = NormalizeLogLine(m.text, 300, string.Empty),
                    Tile = m.Tile
                }).ToList()
            });
        }

        private static void TryAppendWorldInteractionEvents(BaseContainer data, WorldObjectEntry worldObject, int oldTile)
        {
            if (data == null || worldObject == null) return;
            if (worldObject.Type != WorldObjectEntryType.Caravan) return;
            if (worldObject.Tile <= 0) return;
            if (oldTile == worldObject.Tile) return;
            if (string.IsNullOrEmpty(worldObject.LoginOwner)) return;
            if (!IsPlayerInteractionStoryEnabled(ServerManager.ServerSettings.GeneralSettings)) return;

            var tile = worldObject.Tile;
            var login = worldObject.LoginOwner;
            var cooldownMinutes = GetInteractionCooldownMinutes(ServerManager.ServerSettings.GeneralSettings);

            var otherCaravans = data.WorldObjects
                .Where(o => o != null
                    && o.PlaceServerId != worldObject.PlaceServerId
                    && o.Type == WorldObjectEntryType.Caravan
                    && o.Tile == tile
                    && !string.IsNullOrEmpty(o.LoginOwner)
                    && o.LoginOwner != login)
                .Select(o => o.LoginOwner)
                .Distinct()
                .ToList();

            foreach (var otherLogin in otherCaravans)
            {
                var pair = CreatePairKey(login, otherLogin);
                ServerStoryteller.AppendStoryEvent(data
                    , "События игроков"
                    , $"Караваны игроков {pair.Item1} и {pair.Item2} встретились на тайле {tile}."
                    , tile
                    , "players"
                    , $"caravan_meet:{pair.Item1}:{pair.Item2}:{tile}"
                    , cooldownMinutes);
            }

            var otherBases = data.WorldObjects
                .Where(o => o != null
                    && o.Type == WorldObjectEntryType.Base
                    && o.Tile == tile
                    && !string.IsNullOrEmpty(o.LoginOwner)
                    && o.LoginOwner != login)
                .Take(3)
                .ToList();

            foreach (var basePoint in otherBases)
            {
                var baseName = string.IsNullOrWhiteSpace(basePoint.Name) ? "безымянное поселение" : $"\"{basePoint.Name}\"";
                ServerStoryteller.AppendStoryEvent(data
                    , "События игроков"
                    , $"Караван игрока {login} прибыл в поселение игрока {basePoint.LoginOwner} {baseName}."
                    , tile
                    , "players"
                    , $"caravan_visit_player_base:{login}:{basePoint.PlaceServerId}"
                    , cooldownMinutes);
            }

            var onlinePoints = (data.WorldObjectOnlineList ?? new List<WorldObjectOnline>())
                .Where(o => o != null && o.Tile == tile)
                .Take(3)
                .ToList();

            foreach (var point in onlinePoints)
            {
                ServerStoryteller.AppendStoryEvent(data
                    , "Сюжет мира"
                    , BuildStoryPointVisitText(login, point)
                    , tile
                    , "storyteller"
                    , $"caravan_visit_story_point:{login}:{tile}:{point.Name}:{point.StoryType}"
                    , cooldownMinutes);
            }
        }

        private static string BuildStoryPointVisitText(string login, WorldObjectOnline point)
        {
            if (point == null) return $"Караван игрока {login} вышел на неизвестную точку карты.";
            var seedTail = string.IsNullOrWhiteSpace(point.StorySeed) ? string.Empty : $" (сид {point.StorySeed})";

            if (point.StoryType == "trade_camp")
            {
                return $"Караван игрока {login} посетил торговый лагерь \"{point.Name}\"{seedTail}.";
            }
            if (point.StoryType == "outpost")
            {
                return $"Караван игрока {login} обнаружил форпост \"{point.Name}\"{seedTail}.";
            }
            if (point.StoryType == "settlement")
            {
                return $"Караван игрока {login} достиг поселения \"{point.Name}\"{seedTail}.";
            }
            if (point.StoryType == "military_base")
            {
                return $"Караван игрока {login} засек военную базу \"{point.Name}\"{seedTail}.";
            }
            if (point.StoryType == "mine")
            {
                return $"Караван игрока {login} вышел к шахте \"{point.Name}\"{seedTail}.";
            }
            if (point.StoryType == "farm")
            {
                return $"Караван игрока {login} достиг фракционной фермы \"{point.Name}\"{seedTail}.";
            }
            if (point.StoryType == "industrial_site")
            {
                return $"Караван игрока {login} обнаружил промзону \"{point.Name}\"{seedTail}.";
            }
            if (point.StoryType == "research_hub")
            {
                return $"Караван игрока {login} посетил исследовательский узел \"{point.Name}\"{seedTail}.";
            }
            if (point.StoryType == "logistics_hub")
            {
                return $"Караван игрока {login} достиг логистического узла \"{point.Name}\"{seedTail}.";
            }
            return $"Караван игрока {login} посетил лагерь \"{point.Name}\"{seedTail}.";
        }

        private static Tuple<string, string> CreatePairKey(string first, string second)
        {
            if (string.CompareOrdinal(first, second) <= 0)
            {
                return Tuple.Create(first, second);
            }

            return Tuple.Create(second, first);
        }

        private static bool IsPlayerInteractionStoryEnabled(ServerGeneralSettings settings)
        {
            return settings.StorytellerPlayerInteractionEventsEnabled;
        }

        private static bool IsOwnStoryActionForPlayer(ServerStoryEvent storyEvent, string login)
        {
            if (storyEvent == null) return false;
            if (string.IsNullOrWhiteSpace(login)) return false;

            var key = storyEvent.Key ?? string.Empty;
            if (key.Length > 0)
            {
                var segments = key.Split(':');
                if (segments.Length >= 2)
                {
                    var prefix = segments[0];
                    if ((prefix == "player_base_create" || prefix == "player_base_remove" || prefix == "caravan_visit_player_base" || prefix == "caravan_visit_story_point")
                        && string.Equals(segments[1], login, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                if (segments.Length >= 3 && segments[0] == "caravan_meet")
                {
                    if (string.Equals(segments[1], login, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(segments[2], login, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            // Резервный фильтр для старых событий без ключа.
            var category = (storyEvent.Category ?? string.Empty).Trim().ToLowerInvariant();
            if (category == "players" || category == "player")
            {
                var combined = ((storyEvent.Label ?? string.Empty) + " " + (storyEvent.Text ?? string.Empty));
                if (ContainsIgnoreCase(combined, "игрок " + login)
                    || ContainsIgnoreCase(combined, "игрока " + login)
                    || ContainsIgnoreCase(combined, "игроку " + login))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsIgnoreCase(string text, string token)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return false;
            return text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool CanMergeMessageToLog(ModelMailMessadge message)
        {
            if (message == null) return false;
            if (message.NeedSaveGame) return false;
            switch (message.type)
            {
                case ModelMailMessadge.MessadgeTypes.ThreatBig:
                case ModelMailMessadge.MessadgeTypes.ThreatSmall:
                case ModelMailMessadge.MessadgeTypes.Death:
                    return false;
                default:
                    return true;
            }
        }

        private static int GetStoryDigestOfflineMinutes(ServerGeneralSettings settings)
        {
            return ReadSetting(settings.StoryDigestOfflineMinutes, 30, 1, 60 * 24 * 30);
        }

        private static int GetStoryDigestImmediateEventsMax(ServerGeneralSettings settings)
        {
            return ReadSetting(settings.StoryDigestImmediateEventsMax, 3, 1, 30);
        }

        private static int GetStoryDigestMaxLines(ServerGeneralSettings settings)
        {
            return ReadSetting(settings.StoryDigestMaxLines, 40, 5, 400);
        }

        private static int GetMessageDigestThreshold(ServerGeneralSettings settings)
        {
            return ReadSetting(settings.MessageDigestThreshold, 12, 2, 500);
        }

        private static int GetMessageDigestMaxLines(ServerGeneralSettings settings)
        {
            return ReadSetting(settings.MessageDigestMaxLines, 30, 5, 400);
        }

        private static int GetInteractionCooldownMinutes(ServerGeneralSettings settings)
        {
            return ReadSetting(settings.StorytellerInteractionCooldownMinutes, 45, 1, 24 * 60);
        }

        private static int ReadSetting(int value, int fallback, int min, int max)
        {
            if (value < min || value > max) return fallback;
            return value;
        }

        private static string NormalizeLogLine(string text, int maxLen, string emptyDefault)
        {
            if (string.IsNullOrWhiteSpace(text)) return emptyDefault;
            var line = text
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            if (line.Length <= maxLen) return line;
            return line.Substring(0, maxLen - 3) + "...";
        }

        private static void AddMissingFactions(List<FactionOnline> target, IEnumerable<FactionOnline> source)
        {
            if (target == null || source == null) return;

            foreach (var item in source)
            {
                if (item == null) continue;

                var normalized = new FactionOnline()
                {
                    LabelCap = item.LabelCap?.Trim(),
                    DefName = item.DefName?.Trim(),
                    loadID = item.loadID
                };

                if (string.IsNullOrWhiteSpace(normalized.LabelCap)
                    && string.IsNullOrWhiteSpace(normalized.DefName))
                {
                    continue;
                }

                if (target.Any(d => ValidateFaction(normalized, d))) continue;

                var sameByName = target.FirstOrDefault(d => ValidateFactionByName(normalized, d));
                if (sameByName != null)
                {
                    if ((sameByName.loadID <= 0) && normalized.loadID > 0)
                    {
                        sameByName.loadID = normalized.loadID;
                    }
                    continue;
                }

                target.Add(normalized);
            }
        }

        private static bool ValidateWorldObject(WorldObjectOnline pkt, WorldObjectOnline data)
        {
            if(pkt.Name == data.Name
                && pkt.Tile == data.Tile)
            {
                return true;
            }
            return false;
        }

        private static List<WorldObjectOnline> NormalizeWorldObjectSnapshot(IEnumerable<WorldObjectOnline> snapshot)
        {
            if (snapshot == null) return new List<WorldObjectOnline>();

            var result = new List<WorldObjectOnline>();
            var byTile = new HashSet<int>();
            foreach (var item in snapshot)
            {
                if (item == null || item.Tile <= 0) continue;
                if (!byTile.Add(item.Tile)) continue;
                result.Add(CloneWorldObject(item));
            }

            return result;
        }

        private static WorldObjectOnline CloneWorldObject(WorldObjectOnline source)
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

        private static bool NeedHardResyncWorldSnapshot(List<WorldObjectOnline> serverSnapshot, List<WorldObjectOnline> clientSnapshot)
        {
            if (clientSnapshot == null || clientSnapshot.Count == 0) return false;
            if (serverSnapshot == null || serverSnapshot.Count == 0) return true;

            var serverBase = serverSnapshot
                .Where(w => w != null && !w.ServerGenerated && w.Tile > 0)
                .ToList();
            if (serverBase.Count == 0) return true;

            var clientBase = clientSnapshot
                .Where(w => w != null && w.Tile > 0)
                .ToList();
            if (clientBase.Count == 0) return false;

            var serverTiles = new HashSet<int>(serverBase.Select(w => w.Tile));
            var overlapCount = clientBase.Count(w => serverTiles.Contains(w.Tile));

            var minCount = Math.Min(serverBase.Count, clientBase.Count);
            var overlapRatio = minCount <= 0
                ? 0d
                : (double)overlapCount / minCount;
            var countDelta = Math.Abs(serverBase.Count - clientBase.Count);
            var significantDelta = countDelta > Math.Max(50, minCount / 5);

            if (overlapCount == 0 && clientBase.Count >= 25) return true;
            if (overlapRatio < 0.25d && significantDelta) return true;

            return false;
        }

        private static void RebuildStorytellerKnownTiles(BaseContainer data)
        {
            if (data == null) return;

            var known = new HashSet<int>();
            foreach (var point in (data.WorldObjectOnlineList ?? new List<WorldObjectOnline>()))
            {
                if ((point?.Tile ?? 0) > 0) known.Add(point.Tile);
            }
            foreach (var point in (data.WorldObjects ?? new List<WorldObjectEntry>()))
            {
                if ((point?.Tile ?? 0) > 0) known.Add(point.Tile);
            }

            data.StorytellerKnownTiles = known.ToList();
        }

         private static bool ValidateFaction(FactionOnline pkt, FactionOnline data)
        {
            if (pkt.DefName == data.DefName && 
                pkt.LabelCap == data.LabelCap &&
                pkt.loadID == data.loadID)
            {
                return true;
            }
            return false;
        }

        private static bool ValidateFactionByName(FactionOnline first, FactionOnline second)
        {
            if (first == null || second == null) return false;
            return string.Equals(first.DefName?.Trim(), second.DefName?.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(first.LabelCap?.Trim(), second.LabelCap?.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}

