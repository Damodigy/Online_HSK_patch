using Model;
using OCUnion;
using OCUnion.Transfer.Model;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Verse;

namespace RimWorldOnlineCity
{
    /// <summary>
    /// Visit-only (safe) flow: builds a local snapshot map from host without PvP battle loop.
    /// </summary>
    public class VisitSession
    {
        private const int CleanupState = 90;
        private const int WaitHostReadySeconds = 60;
        private const int WaitMapSnapshotSeconds = 180;

        public static bool CanStart => SessionClientController.Data != null
            && SessionClientController.Data.VisitModule == null
            && !SessionClientController.Data.VisitHostResponding;

        public static VisitSession Get => SessionClientController.Data?.VisitModule;

        public static bool TryStart(Caravan caravan, CaravanOnline target, string mode)
        {
            if (!CanStart)
            {
                ShowMessage("Visit session is already active");
                return false;
            }

            var session = new VisitSession();
            SessionClientController.Data.VisitModule = session;
            if (!session.StartInternal(caravan, target, mode))
            {
                session.Clear();
                return false;
            }

            return true;
        }

        public static void ClearCurrent()
        {
            Get?.Clear();
        }

        /// <summary>
        /// Called from UpdateWorld when server says this player should provide host snapshot.
        /// </summary>
        public static void TryHandleIncomingHostRequest(SessionClient connect)
        {
            if (connect == null || SessionClientController.Data == null) return;
            if (SessionClientController.Data.VisitHostResponding) return;
            if (SessionClientController.Data.VisitModule != null) return;

            SessionClientController.Data.VisitHostResponding = true;
            try
            {
                HandleIncomingHostRequestInternal(connect);
            }
            catch (Exception ex)
            {
                Loger.Log("VisitSession host responder failed: " + ex, Loger.LogLevel.ERROR);
            }
            finally
            {
                if (SessionClientController.Data != null)
                {
                    SessionClientController.Data.VisitHostResponding = false;
                }
            }
        }

        public string Mode { get; private set; }
        public string TargetLogin { get; private set; }
        public long TargetPlaceServerId { get; private set; }
        public long InitiatorPlaceServerId { get; private set; }
        public DateTime StartUtc { get; private set; }

        private bool cleared;
        private bool backgroundSaveGameOffBefore;
        private object watchTimerObj;
        private Map visitMap;
        private MapParent visitMapParent;
        private List<ThingEntry> visitorPawns;

        private bool StartInternal(Caravan caravan, CaravanOnline target, string mode)
        {
            var validationError = Validate(caravan, target);
            if (!string.IsNullOrEmpty(validationError))
            {
                ShowMessage(validationError);
                return false;
            }

            Mode = string.IsNullOrEmpty(mode) ? "visit" : mode;
            TargetLogin = target.OnlinePlayerLogin;
            TargetPlaceServerId = target.OnlineWObject.PlaceServerId;
            InitiatorPlaceServerId = UpdateWorldController.GetServerInfo(caravan).PlaceServerId;
            StartUtc = DateTime.UtcNow;
            visitorPawns = caravan.PawnsListForReading.Select(p => ThingEntry.CreateEntry(p, 1)).ToList();

            Loger.Log($"VisitSession start {InitiatorPlaceServerId} -> {TargetPlaceServerId} mode={Mode}");

            backgroundSaveGameOffBefore = SessionClientController.Data.BackgroundSaveGameOff;
            SessionClientController.Data.BackgroundSaveGameOff = true;
            SessionClientController.Data.DontCheckTimerFail = true;
            Find.TickManager.Pause();

            if (!TryLoadSnapshotFromHost(caravan, target, out AttackInitiatorFromSrv snapshot, out string err))
            {
                ShowMessage(err ?? "Failed to receive host snapshot");
                return false;
            }

            CreateVisitMap(target.Tile, snapshot);
            return true;
        }

        private bool TryLoadSnapshotFromHost(Caravan caravan, CaravanOnline target, out AttackInitiatorFromSrv snapshot, out string err)
        {
            AttackInitiatorFromSrv snapshotLocal = null;
            string errLocal = null;

            SessionClientController.Command((connect) =>
            {
                try
                {
                    connect.ErrorMessage = null;
                    var startResponse = connect.AttackOnlineInitiator(new AttackInitiatorToSrv()
                    {
                        State = 0,
                        StartHostPlayer = target.OnlinePlayerLogin,
                        HostPlaceServerId = target.OnlineWObject.PlaceServerId,
                        InitiatorPlaceServerId = InitiatorPlaceServerId,
                        TestMode = true,
                    });
                    if (!CheckInitiatorResponse(connect, startResponse, out errLocal)) return;

                    var waitHostUntil = DateTime.UtcNow.AddSeconds(WaitHostReadySeconds);
                    while (true)
                    {
                        var hostReady = connect.AttackOnlineInitiator(new AttackInitiatorToSrv() { State = 1 });
                        if (!CheckInitiatorResponse(connect, hostReady, out errLocal)) return;
                        if (hostReady.State >= 2) break;
                        if (DateTime.UtcNow > waitHostUntil)
                        {
                            errLocal = "Host did not accept visit request in time";
                            return;
                        }
                        Thread.Sleep(250);
                    }

                    var waitMapUntil = DateTime.UtcNow.AddSeconds(WaitMapSnapshotSeconds);
                    while (true)
                    {
                        var mapResponse = connect.AttackOnlineInitiator(new AttackInitiatorToSrv() { State = 3 });
                        if (!CheckInitiatorResponse(connect, mapResponse, out errLocal)) return;
                        if (mapResponse.State >= 4
                            && mapResponse.MapSize != null
                            && mapResponse.TerrainDefNameCell != null
                            && mapResponse.TerrainDefName != null
                            && mapResponse.ThingCell != null
                            && mapResponse.Thing != null)
                        {
                            snapshotLocal = mapResponse;
                            return;
                        }
                        if (DateTime.UtcNow > waitMapUntil)
                        {
                            errLocal = "Host map snapshot timeout";
                            return;
                        }
                        Thread.Sleep(300);
                    }
                }
                catch (Exception ex)
                {
                    errLocal = ex.Message;
                    Loger.Log("VisitSession initiator handshake failed: " + ex, Loger.LogLevel.ERROR);
                }
                finally
                {
                    TrySendCleanup(connect);
                    SessionClientController.Data.DontCheckTimerFail = false;
                }
            });

            snapshot = snapshotLocal;
            err = errLocal;
            return snapshot != null && string.IsNullOrEmpty(err);
        }

        private static bool CheckInitiatorResponse(SessionClient connect, AttackInitiatorFromSrv response, out string err)
        {
            if (!string.IsNullOrEmpty(connect.ErrorMessage))
            {
                err = connect.ErrorMessage.ServerTranslate();
                return false;
            }
            if (response == null)
            {
                err = "No response from server";
                return false;
            }
            if (!string.IsNullOrEmpty(response.ErrorText))
            {
                err = response.ErrorText.ServerTranslate();
                return false;
            }
            err = null;
            return true;
        }

        private static bool CheckHostResponse(SessionClient connect, AttackHostFromSrv response, out string err)
        {
            if (!string.IsNullOrEmpty(connect.ErrorMessage))
            {
                err = connect.ErrorMessage.ServerTranslate();
                return false;
            }
            if (response == null)
            {
                err = "No response from server";
                return false;
            }
            if (!string.IsNullOrEmpty(response.ErrorText))
            {
                err = response.ErrorText.ServerTranslate();
                return false;
            }
            err = null;
            return true;
        }

        private static void TrySendCleanup(SessionClient connect)
        {
            try
            {
                connect.AttackOnlineInitiator(new AttackInitiatorToSrv()
                {
                    State = CleanupState,
                    TestMode = true,
                });
            }
            catch (Exception ex)
            {
                Loger.Log("VisitSession cleanup request failed: " + ex, Loger.LogLevel.WARNING);
            }
        }

        private static void HandleIncomingHostRequestInternal(SessionClient connect)
        {
            connect.ErrorMessage = null;
            var req = connect.AttackOnlineHost(new AttackHostToSrv() { State = 2 });
            if (!CheckHostResponse(connect, req, out string err))
            {
                Loger.Log("VisitSession host request rejected: " + err, Loger.LogLevel.WARNING);
                return;
            }

            AttackHostToSrv mapPacket = null;
            if (!ModBaseData.RunMainThreadSync(() =>
            {
                mapPacket = BuildHostMapSnapshotPacket(req.HostPlaceServerId);
            }, 120))
            {
                Loger.Log("VisitSession host map snapshot timeout", Loger.LogLevel.WARNING);
                return;
            }
            if (mapPacket == null)
            {
                Loger.Log("VisitSession host map snapshot not available", Loger.LogLevel.WARNING);
                return;
            }

            connect.ErrorMessage = null;
            var push = connect.AttackOnlineHost(mapPacket);
            if (!CheckHostResponse(connect, push, out err))
            {
                Loger.Log("VisitSession host map upload failed: " + err, Loger.LogLevel.WARNING);
                return;
            }
        }

        private static AttackHostToSrv BuildHostMapSnapshotPacket(long hostPlaceServerId)
        {
            var hostPlace = UpdateWorldController.GetWOByServerId(hostPlaceServerId) as MapParent;
            var hostMap = hostPlace?.Map;
            if (hostMap == null) return null;

            var packet = new AttackHostToSrv()
            {
                State = 4,
                MapSize = new IntVec3S(hostMap.Size),
                TerrainDefNameCell = new List<IntVec3S>(),
                TerrainDefName = new List<string>(),
                Thing = new List<ThingTrade>(),
                ThingCell = new List<IntVec3S>(),
            };

            CellRect cellRect = CellRect.WholeMap(hostMap);
            cellRect.ClipInsideMap(hostMap);

            foreach (IntVec3 current in cellRect)
            {
                var terr = hostMap.terrainGrid.TerrainAt(current);
                packet.TerrainDefNameCell.Add(new IntVec3S(current));
                packet.TerrainDefName.Add(terr?.defName ?? "Soil");
            }

            foreach (IntVec3 current in cellRect)
            {
                foreach (Thing thc in hostMap.thingGrid.ThingsAt(current).ToList<Thing>())
                {
                    if (thc == null || thc is Pawn) continue;
                    if (thc.Position != current) continue;
                    if (thc.def.category == ThingCategory.Plant && !thc.def.plant.IsTree) continue;

                    try
                    {
                        var tt = ThingTrade.CreateTrade(thc, thc.stackCount, false);
                        packet.ThingCell.Add(new IntVec3S(current));
                        packet.Thing.Add(tt);
                    }
                    catch (Exception ex)
                    {
                        Loger.Log("VisitSession skip thing in snapshot: " + ex.Message, Loger.LogLevel.WARNING);
                    }
                }
            }

            return packet;
        }

        private static string Validate(Caravan caravan, CaravanOnline target)
        {
            if (caravan == null || target == null || target.OnlineWObject == null)
            {
                return "Visit start failed: wrong caravan or target";
            }

            if (!(target is BaseOnline))
            {
                return "Visit is available only for player bases";
            }

            var myWObject = UpdateWorldController.GetServerInfo(caravan);
            if (myWObject == null || myWObject.PlaceServerId <= 0)
            {
                return "Caravan is not synced with server yet";
            }

            if (target.OnlineWObject.PlaceServerId <= 0)
            {
                return "Target is not synced with server yet";
            }

            var player = target.Player;
            if (player == null || !player.Online)
            {
                return "Host not online";
            }

            return null;
        }

        private void CreateVisitMap(int tile, AttackInitiatorFromSrv snapshot)
        {
            LongEventHandler.QueueLongEvent(delegate
            {
                try
                {
                    Rand.PushState();
                    try
                    {
                        TileFinder_IsValidTileForNewSettlement_Patch.Off = true;
                        Rand.Seed = Gen.HashCombineInt(Find.World.info.Seed, tile);

                        var mapParent = (MapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Ambush);
                        mapParent.Tile = tile;
                        Find.WorldObjects.Add(mapParent);
                        mapParent.SetFaction(Find.FactionManager.OfPlayer);

                        var mapDef = new MapGeneratorDef()
                        {
                            genSteps = mapParent.MapGeneratorDef.genSteps
                                .Where(gs => gs.defName == "ElevationFertility"
                                    || gs.defName == "Caves"
                                    || gs.defName == "Terrain"
                                    || gs.defName == "CavesTerrain"
                                    || gs.defName == "FindPlayerStartSpot"
                                    || gs.defName == "ScenParts"
                                    || gs.defName == "Fog")
                                .ToList()
                        };

                        var map = MapGenerator.GenerateMap(snapshot.MapSize.Get(), mapParent, mapDef, null, null);

                        LongEventHandler.QueueLongEvent(delegate
                        {
                            try
                            {
                                CellRect cellRect = CellRect.WholeMap(map);
                                cellRect.ClipInsideMap(map);
                                GenDebug.ClearArea(cellRect, map);

                                var terrCount = Math.Min(snapshot.TerrainDefNameCell.Count, snapshot.TerrainDefName.Count);
                                for (int i = 0; i < terrCount; i++)
                                {
                                    try
                                    {
                                        var current = snapshot.TerrainDefNameCell[i].Get();
                                        var terrName = snapshot.TerrainDefName[i];
                                        var terrDef = DefDatabase<TerrainDef>.GetNamed(terrName, false) ?? TerrainDefOf.Soil;
                                        map.terrainGrid.SetTerrain(current, terrDef);
                                    }
                                    catch (Exception ex)
                                    {
                                        Loger.Log("VisitSession terrain apply failed: " + ex.Message, Loger.LogLevel.WARNING);
                                    }
                                }

                                var thingCount = Math.Min(snapshot.ThingCell.Count, snapshot.Thing.Count);
                                for (int i = 0; i < thingCount; i++)
                                {
                                    try
                                    {
                                        var current = snapshot.ThingCell[i].Get();
                                        var tt = snapshot.Thing[i];
                                        var th = tt.CreateThing();
                                        GenSpawn.Spawn(th, current, map, new Rot4(tt.Rotation), WipeMode.Vanish);
                                    }
                                    catch (Exception ex)
                                    {
                                        Loger.Log("VisitSession spawn thing failed: " + ex.Message, Loger.LogLevel.WARNING);
                                    }
                                }

                                if (visitorPawns != null && visitorPawns.Count > 0)
                                {
                                    var nextCell = GameUtils.GetAttackCells(map);
                                    GameUtils.SpawnList(map, visitorPawns, false, (p) => false, null, (p) => nextCell());
                                }

                                visitMap = map;
                                visitMapParent = mapParent;
                                StartWatchTimer();

                                CameraJumper.TryJump(map.Center, map);
                                Messages.Message("Visit snapshot loaded. Return to world map to finish visit.", MessageTypeDefOf.NeutralEvent);
                            }
                            catch (Exception ex)
                            {
                                Loger.Log("VisitSession create map (stage2) failed: " + ex, Loger.LogLevel.ERROR);
                                ShowMessage("Failed to build visit map");
                                Clear();
                            }
                            finally
                            {
                                TileFinder_IsValidTileForNewSettlement_Patch.Off = false;
                            }
                        }, "GeneratingMapForNewEncounter", false, null);
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                }
                catch (Exception ex)
                {
                    Loger.Log("VisitSession create map failed: " + ex, Loger.LogLevel.ERROR);
                    ShowMessage("Failed to build visit map");
                    Clear();
                }
            }, "GeneratingMapForNewEncounter", false, null);
        }

        private void StartWatchTimer()
        {
            if (SessionClientController.Timers == null) return;
            watchTimerObj = SessionClientController.Timers.Add(1000, WatchVisitMapState);
        }

        private void WatchVisitMapState()
        {
            try
            {
                if (cleared || visitMap == null || visitMapParent == null) return;
                if (Find.CurrentMap == visitMap) return;

                // leaving snapshot map -> cleanup and return to normal world state
                Clear();
            }
            catch (Exception ex)
            {
                Loger.Log("VisitSession watcher failed: " + ex, Loger.LogLevel.ERROR);
                Clear();
            }
        }

        private void CleanupVisitMap()
        {
            if (visitMapParent == null) return;

            try
            {
                ModBaseData.RunMainThreadSync(() =>
                {
                    if (Find.WorldObjects.AllWorldObjects.Contains(visitMapParent))
                    {
                        Find.WorldObjects.Remove(visitMapParent);
                    }
                }, 20, true);
            }
            catch (Exception ex)
            {
                Loger.Log("VisitSession cleanup map failed: " + ex, Loger.LogLevel.WARNING);
            }
            finally
            {
                visitMap = null;
                visitMapParent = null;
            }
        }

        public void Clear()
        {
            if (cleared) return;
            cleared = true;

            if (watchTimerObj != null && SessionClientController.Timers != null)
            {
                SessionClientController.Timers.Remove(watchTimerObj);
                watchTimerObj = null;
            }

            CleanupVisitMap();

            if (SessionClientController.Data != null)
            {
                SessionClientController.Data.BackgroundSaveGameOff = backgroundSaveGameOffBefore;
                SessionClientController.Data.DontCheckTimerFail = false;
                if (SessionClientController.Data.VisitModule == this)
                {
                    SessionClientController.Data.VisitModule = null;
                }
            }
        }

        private static void ShowMessage(string text)
        {
            GameUtils.ShowDialodOKCancel(
                "OCity_Caravan_GoTrade2".Translate().ToString(),
                text,
                () => { },
                null);
        }
    }
}
