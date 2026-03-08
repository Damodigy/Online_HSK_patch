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
        private const int VisitLiveSyncDelayMs = 1200;
        private const int HostLiveStreamDelayMs = 1200;
        private const int MaxSyncErrorsBeforeStop = 8;

        private static object hostLiveStreamTimerObj;
        private static long hostLiveStreamPlaceServerId;
        private static bool hostLiveStreamInTick;
        private static int hostLiveStreamErrors;

        public static bool CanStart => SessionClientController.Data != null
            && SessionClientController.Data.VisitModule == null
            && SessionClientController.Data.AttackModule == null
            && SessionClientController.Data.AttackUsModule == null
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
            if (SessionClientController.Data.VisitModule != null) return;
            if (SessionClientController.Data.AttackModule != null || SessionClientController.Data.AttackUsModule != null) return;

            var releaseRespondingFlag = false;
            if (!SessionClientController.Data.VisitHostResponding)
            {
                SessionClientController.Data.VisitHostResponding = true;
                releaseRespondingFlag = true;
            }
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
                if (releaseRespondingFlag && SessionClientController.Data != null)
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
        private bool handshakeStarted;
        private bool cleanupRequested;
        private bool backgroundSaveGameOffBefore;
        private object watchTimerObj;
        private object liveSyncTimerObj;
        private bool liveSyncInTick;
        private int liveSyncErrors;
        private Map visitMap;
        private MapParent visitMapParent;
        private List<ThingEntry> visitorPawns;
        private readonly Dictionary<int, Pawn> hostSnapshotPawns = new Dictionary<int, Pawn>();

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
                    handshakeStarted = true;
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

        private static void StartHostLiveStream(long hostPlaceServerId)
        {
            if (hostPlaceServerId <= 0 || SessionClientController.Timers == null) return;

            hostLiveStreamPlaceServerId = hostPlaceServerId;
            hostLiveStreamErrors = 0;

            if (hostLiveStreamTimerObj != null) return;

            hostLiveStreamTimerObj = SessionClientController.Timers.Add(HostLiveStreamDelayMs, HostLiveStreamTick);
            Loger.Log("VisitSession host live stream started.");
        }

        private static void StopHostLiveStream()
        {
            if (hostLiveStreamTimerObj != null && SessionClientController.Timers != null)
            {
                SessionClientController.Timers.Remove(hostLiveStreamTimerObj);
            }

            hostLiveStreamTimerObj = null;
            hostLiveStreamPlaceServerId = 0;
            hostLiveStreamErrors = 0;
            hostLiveStreamInTick = false;
        }

        private static void HostLiveStreamTick()
        {
            if (hostLiveStreamInTick) return;
            if (hostLiveStreamPlaceServerId <= 0)
            {
                StopHostLiveStream();
                return;
            }

            hostLiveStreamInTick = true;
            try
            {
                SessionClientController.Command((connect) =>
                {
                    try
                    {
                        AttackHostToSrv mapPacket = null;
                        if (!ModBaseData.RunMainThreadSync(() =>
                        {
                            mapPacket = BuildHostMapSnapshotPacket(
                                hostLiveStreamPlaceServerId,
                                includeTerrain: false,
                                includeNonPawnThings: false,
                                includePawns: true);
                        }, 120))
                        {
                            hostLiveStreamErrors++;
                            return;
                        }

                        if (mapPacket == null)
                        {
                            hostLiveStreamErrors++;
                            return;
                        }

                        connect.ErrorMessage = null;
                        var push = connect.AttackOnlineHost(mapPacket);
                        if (!CheckHostResponse(connect, push, out string err))
                        {
                            hostLiveStreamErrors++;
                            if (!string.IsNullOrEmpty(err)
                                && err.IndexOf("Unexpected request", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                StopHostLiveStream();
                            }
                            return;
                        }

                        if (push.State == CleanupState || push.State < 4 || push.State > 5)
                        {
                            StopHostLiveStream();
                            return;
                        }

                        hostLiveStreamErrors = 0;
                    }
                    catch (Exception ex)
                    {
                        hostLiveStreamErrors++;
                        Loger.Log("VisitSession host live stream tick failed: " + ex.Message, Loger.LogLevel.WARNING);
                    }
                });
            }
            finally
            {
                hostLiveStreamInTick = false;
            }

            if (hostLiveStreamErrors >= MaxSyncErrorsBeforeStop)
            {
                StopHostLiveStream();
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

            StartHostLiveStream(req.HostPlaceServerId);
        }

        private static AttackHostToSrv BuildHostMapSnapshotPacket(
            long hostPlaceServerId,
            bool includeTerrain = true,
            bool includeNonPawnThings = true,
            bool includePawns = true)
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
            var addedPawns = 0;
            var addedThings = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (includeTerrain)
            {
                foreach (IntVec3 current in cellRect)
                {
                    var terr = hostMap.terrainGrid.TerrainAt(current);
                    packet.TerrainDefNameCell.Add(new IntVec3S(current));
                    packet.TerrainDefName.Add(terr?.defName ?? "Soil");
                }
            }

            foreach (IntVec3 current in cellRect)
            {
                foreach (Thing thc in hostMap.thingGrid.ThingsAt(current).ToList<Thing>())
                {
                    if (thc.Position != current) continue;
                    if (thc is Pawn pawn)
                    {
                        if (!includePawns) continue;
                        if (!ShouldIncludeSnapshotPawn(pawn)) continue;

                        try
                        {
                            var tt = ThingTrade.CreateTrade(pawn, 1, true);
                            tt.Affiliation = PawnAffiliation.Neutral;
                            packet.ThingCell.Add(new IntVec3S(current));
                            packet.Thing.Add(tt);
                            addedPawns++;
                        }
                        catch (Exception ex)
                        {
                            Loger.Log("VisitSession skip pawn in snapshot: " + ex.Message, Loger.LogLevel.WARNING);
                        }
                        continue;
                    }
                    if (!includeNonPawnThings) continue;
                    if (!ShouldIncludeSnapshotThing(thc)) continue;

                    try
                    {
                        var tt = ThingTrade.CreateTrade(thc, thc.stackCount, false);
                        packet.ThingCell.Add(new IntVec3S(current));
                        packet.Thing.Add(tt);
                        addedThings++;
                    }
                    catch (Exception ex)
                    {
                        Loger.Log("VisitSession skip thing in snapshot: " + ex.Message, Loger.LogLevel.WARNING);
                    }
                }
                
                // HSK workaround: bail out early if we've spent more than 45 seconds assembling the packet
                // to avoid Unity crash/disconnect (usually MainThreadSync allows up to 120s, but we pad it)
                if (stopwatch.ElapsedMilliseconds > 80000)
                {
                    Loger.Log("VisitSession host snapshot interrupted to prevent timeout. Map is too large.");
                    break;
                }
            }

            if (MainHelper.DebugMode)
            {
                Loger.Log($"VisitSession host snapshot prepared. pawns={addedPawns} things={addedThings}");
            }
            return packet;
        }

        private static bool ShouldIncludeSnapshotPawn(Pawn pawn)
        {
            if (pawn == null || pawn.def == null) return false;
            if (pawn.DestroyedOrNull() || pawn.Dead) return false;
            if (!pawn.Spawned) return false;
            return pawn.Faction == Faction.OfPlayer;
        }

        private static bool ShouldIncludeSnapshotThing(Thing thing)
        {
            if (thing == null || thing.def == null) return false;
            if (thing is Pawn || thing is Corpse || thing is MinifiedThing) return false;
            if (thing is Blueprint || thing is Frame) return false;
            if (thing.def.category == ThingCategory.Pawn) return false;
            if (thing.def.category == ThingCategory.Mote) return false;
            if (thing.def.category == ThingCategory.Gas) return false;
            if (thing.def.category == ThingCategory.Projectile) return false;
            if (thing.def.category == ThingCategory.Attachment) return false;
            if (thing.def.category == ThingCategory.Ethereal) return false;
            if (thing.def.IsFilth) return false;
            if (thing.def.plant != null && !thing.def.plant.IsTree) return false;
            return true;
        }

        private static bool ShouldSpawnSnapshotThing(ThingTrade trade, Map map, IntVec3 cell)
        {
            if (trade == null || map == null) return false;
            if (!cell.InBounds(map)) return false;
            if (string.IsNullOrEmpty(trade.DefName)) return false;

            var def = DefDatabase<ThingDef>.GetNamed(trade.DefName, false);
            if (def == null) return false;
            if (def.category == ThingCategory.Pawn)
            {
                return !string.IsNullOrEmpty(trade.Data);
            }
            if (def.category == ThingCategory.Mote) return false;
            if (def.category == ThingCategory.Gas) return false;
            if (def.category == ThingCategory.Projectile) return false;
            if (def.category == ThingCategory.Attachment) return false;
            if (def.category == ThingCategory.Ethereal) return false;
            if (def.IsFilth) return false;
            if (def.plant != null && !def.plant.IsTree) return false;
            if (trade.DefName == "Corpse" || trade.DefName == "MinifiedThing") return false;
            return true;
        }

        private static bool ShouldKeepSpawnedSnapshotThing(Thing thing)
        {
            if (thing == null || thing.def == null) return false;
            if (thing is Corpse || thing is MinifiedThing) return false;
            if (thing is Blueprint || thing is Frame) return false;
            if (thing.def.category == ThingCategory.Mote) return false;
            if (thing.def.category == ThingCategory.Gas) return false;
            if (thing.def.category == ThingCategory.Projectile) return false;
            if (thing.def.category == ThingCategory.Attachment) return false;
            if (thing.def.category == ThingCategory.Ethereal) return false;
            if (thing.def.IsFilth) return false;
            if (thing.def.plant != null && !thing.def.plant.IsTree) return false;
            if (thing is Pawn pawn && (pawn.DestroyedOrNull() || pawn.Dead)) return false;
            return true;
        }

        private static bool IsSnapshotPawnTrade(ThingTrade trade)
        {
            if (trade == null || string.IsNullOrEmpty(trade.DefName)) return false;
            if (string.IsNullOrEmpty(trade.Data)) return false;
            var def = DefDatabase<ThingDef>.GetNamed(trade.DefName, false);
            return def != null && def.category == ThingCategory.Pawn;
        }

        private static IntVec3 ResolveSnapshotPawnCell(Map map, IntVec3 desiredCell)
        {
            if (map == null) return IntVec3.Invalid;

            if (desiredCell.InBounds(map) && desiredCell.Standable(map)) return desiredCell;

            if (desiredCell.InBounds(map))
            {
                return CellFinder.RandomClosewalkCellNear(desiredCell, map, 2);
            }

            return FindVisitEntryCell(map);
        }

        private static IntVec3 FindVisitEntryCell(Map map)
        {
            if (map == null) return IntVec3.Invalid;

            if (CellFinder.TryFindRandomEdgeCellWith(x => x.Standable(map), map, CellFinder.EdgeRoadChance_Neutral, out var edge))
            {
                return CellFinder.RandomClosewalkCellNear(edge, map, 5);
            }

            return CellFinder.RandomCell(map);
        }

        private static Func<Thing, IntVec3> CreateVisitSpawnCellProvider(Map map)
        {
            var entryCell = FindVisitEntryCell(map);
            return _ => CellFinder.RandomSpawnCellForPawnNear(entryCell, map, 4);
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

                        var mapParent = (MapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Settlement);
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
                                // Defensive cleanup: some modded generators can still spawn pawns.
                                foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList())
                                {
                                    try
                                    {
                                        pawn.Destroy();
                                    }
                                    catch (Exception ex)
                                    {
                                        Loger.Log("VisitSession cleanup generated pawn failed: " + ex.Message, Loger.LogLevel.WARNING);
                                    }
                                }

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
                                        if (!ShouldSpawnSnapshotThing(tt, map, current)) continue;

                                        var th = tt.CreateThing();
                                        if (!ShouldKeepSpawnedSnapshotThing(th)) continue;
                                        GenSpawn.Spawn(th, current, map, new Rot4(tt.Rotation), WipeMode.Vanish);
                                        RegisterHostSnapshotPawn(tt, th);
                                    }
                                    catch (Exception ex)
                                    {
                                        Loger.Log("VisitSession spawn thing failed: " + ex.Message, Loger.LogLevel.WARNING);
                                    }
                                }

                                var jumpCell = map.Center;
                                if (visitorPawns != null && visitorPawns.Count > 0)
                                {
                                    jumpCell = GameUtils.SpawnList(
                                        map,
                                        visitorPawns,
                                        false,
                                        _ => false,
                                        null,
                                        CreateVisitSpawnCellProvider(map));
                                }
                                else
                                {
                                    jumpCell = FindVisitEntryCell(map);
                                }

                                visitMap = map;
                                visitMapParent = mapParent;
                                StartWatchTimer();
                                StartLiveSyncTimer();

                                if (!jumpCell.IsValid || !jumpCell.InBounds(map))
                                {
                                    jumpCell = map.Center;
                                }
                                CameraJumper.TryJump(jumpCell, map);
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

        private void RegisterHostSnapshotPawn(ThingTrade trade, Thing thing)
        {
            if (!(thing is Pawn pawn)) return;
            if (trade == null || trade.OriginalID <= 0) return;
            hostSnapshotPawns[trade.OriginalID] = pawn;
        }

        private void StartLiveSyncTimer()
        {
            if (SessionClientController.Timers == null || liveSyncTimerObj != null) return;
            liveSyncErrors = 0;
            liveSyncTimerObj = SessionClientController.Timers.Add(VisitLiveSyncDelayMs, LiveSyncTick);
        }

        private void LiveSyncTick()
        {
            if (cleared || visitMap == null || visitMapParent == null) return;
            if (Find.CurrentMap != visitMap) return;
            if (liveSyncInTick) return;

            liveSyncInTick = true;
            try
            {
                SessionClientController.Command((connect) =>
                {
                    try
                    {
                        connect.ErrorMessage = null;
                        var mapResponse = connect.AttackOnlineInitiator(new AttackInitiatorToSrv() { State = 3 });

                        if (mapResponse != null && mapResponse.State == CleanupState)
                        {
                            cleanupRequested = true;
                            Clear();
                            return;
                        }

                        if (!CheckInitiatorResponse(connect, mapResponse, out string err))
                        {
                            liveSyncErrors++;
                            if (!string.IsNullOrEmpty(err)
                                && err.IndexOf("Unexpected request", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                cleanupRequested = true;
                                Clear();
                            }
                            return;
                        }

                        if (mapResponse == null || mapResponse.State < 4
                            || mapResponse.Thing == null
                            || mapResponse.ThingCell == null)
                        {
                            liveSyncErrors++;
                            return;
                        }

                        liveSyncErrors = 0;
                        ModBaseData.RunMainThreadSync(() =>
                        {
                            ApplyLiveSnapshot(mapResponse);
                        }, 90, true);
                    }
                    catch (Exception ex)
                    {
                        liveSyncErrors++;
                        Loger.Log("VisitSession live sync tick failed: " + ex.Message, Loger.LogLevel.WARNING);
                    }
                });
            }
            finally
            {
                liveSyncInTick = false;
            }

            if (liveSyncErrors >= MaxSyncErrorsBeforeStop)
            {
                Clear();
            }
        }

        private void ApplyLiveSnapshot(AttackInitiatorFromSrv snapshot)
        {
            if (visitMap == null || snapshot == null || snapshot.Thing == null || snapshot.ThingCell == null) return;

            var snapshotPawns = new Dictionary<int, ThingTrade>();
            var snapshotPawnCells = new Dictionary<int, IntVec3>();
            var snapshotCount = Math.Min(snapshot.Thing.Count, snapshot.ThingCell.Count);
            for (int i = 0; i < snapshotCount; i++)
            {
                var trade = snapshot.Thing[i];
                if (!IsSnapshotPawnTrade(trade)) continue;
                if (trade.OriginalID <= 0) continue;

                var cell = snapshot.ThingCell[i].Get();
                snapshotPawns[trade.OriginalID] = trade;
                snapshotPawnCells[trade.OriginalID] = cell;
            }

            foreach (var knownId in hostSnapshotPawns.Keys.ToList())
            {
                if (!hostSnapshotPawns.TryGetValue(knownId, out var pawn) || pawn.DestroyedOrNull())
                {
                    hostSnapshotPawns.Remove(knownId);
                    continue;
                }

                if (snapshotPawns.ContainsKey(knownId)) continue;

                try
                {
                    pawn.Destroy(DestroyMode.Vanish);
                }
                catch
                {
                    // ignore
                }
                hostSnapshotPawns.Remove(knownId);
            }

            foreach (var pair in snapshotPawns)
            {
                var hostPawnId = pair.Key;
                var trade = pair.Value;
                var targetCell = ResolveSnapshotPawnCell(visitMap, snapshotPawnCells[hostPawnId]);

                if (hostSnapshotPawns.TryGetValue(hostPawnId, out var existingPawn)
                    && !existingPawn.DestroyedOrNull()
                    && existingPawn.Spawned)
                {
                    if (targetCell.IsValid && existingPawn.Position != targetCell)
                    {
                        existingPawn.Position = targetCell;
                        existingPawn.Notify_Teleported(endCurrentJob: true, resetTweenedPos: false);
                    }
                    continue;
                }

                try
                {
                    var thing = trade.CreateThing();
                    if (!(thing is Pawn newPawn))
                    {
                        thing?.Destroy(DestroyMode.Vanish);
                        continue;
                    }

                    GenSpawn.Spawn(newPawn, targetCell, visitMap, new Rot4(trade.Rotation), WipeMode.Vanish);
                    hostSnapshotPawns[hostPawnId] = newPawn;
                }
                catch (Exception ex)
                {
                    Loger.Log("VisitSession live sync spawn pawn failed: " + ex.Message, Loger.LogLevel.WARNING);
                }
            }
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
                hostSnapshotPawns.Clear();
            }
        }

        private void SendCleanupRequest()
        {
            if (!handshakeStarted || cleanupRequested) return;
            cleanupRequested = true;

            try
            {
                SessionClientController.Command((connect) => TrySendCleanup(connect));
            }
            catch (Exception ex)
            {
                Loger.Log("VisitSession send cleanup failed: " + ex.Message, Loger.LogLevel.WARNING);
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
            if (liveSyncTimerObj != null && SessionClientController.Timers != null)
            {
                SessionClientController.Timers.Remove(liveSyncTimerObj);
                liveSyncTimerObj = null;
            }

            SendCleanupRequest();

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
