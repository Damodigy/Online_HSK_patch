using Model;
using OCUnion;
using OCUnion.Common;
using OCUnion.Transfer;
using OCUnion.Transfer.Model;
using RimWorld;
using RimWorld.Planet;
using RimWorldOnlineCity.ClientHashCheck;
using RimWorldOnlineCity.GameClasses;
using RimWorldOnlineCity.GameClasses.Harmony;
using RimWorldOnlineCity.Model;
using RimWorldOnlineCity.Services;
using RimWorldOnlineCity.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Transfer;
using UnityEngine;
using Util;
using Verse;
using Verse.Profile;

namespace RimWorldOnlineCity
{
    /// <summary>
    /// Контейнер общих данных и
    /// стандартные повторяющиеся инструкции при работе с классом SessionClient.
    /// </summary>
    public static class SessionClientController
    {
        public static ClientData Data { get; set; }
        //Локальные данные текущей игры
        public static string ConnectAddr { get; set; }
        
        public static WorkTimer Timers { get; set; }
        public static WorkTimer TimerReconnect { get; set; }
        public static Player My { get; set; }
        
        public static TimeSpan ServerTimeDelta { get; set; }
        
        private const string SaveNameBase = "onlineCity";
        private static string SaveName => SaveNameBase + "_" + Data.ServerName.NormalizeFileNameChars();
        private static string SaveFullName => GenFilePaths.FilePathForSavedGame(SaveName);
        private const string ModsConfigFileName = "ModsConfig.xml";

        public static StorytellerDef ChoosedTeller { get; set; }

        public static string ConfigPath { get; private set; }

        public static bool LoginInNewServerIP { get; set; }

        public static ClientFileChecker[] ClientFileCheckers { get; private set; }
        public static bool ClientFileCheckersComplete { get; private set; }

        public static Action UpdateWorldSafelyRun { get; set; }

        /// <summary>
        /// Инициализация при старте игры. Как можно раньше
        /// </summary>
        public static void Init()
        {
            MainHelper.InGame = true;

            ConfigPath = Path.Combine(GenFilePaths.ConfigFolderPath, "OnlineCity");
            if (!Directory.Exists(ConfigPath))
            {
                Directory.CreateDirectory(ConfigPath);
            }

            MainHelper.CultureFromGame = Prefs.LangFolderName ?? "";

            try
            {
                // ..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\OnlineCity
                var path = new DirectoryInfo(GenFilePaths.ConfigFolderPath).Parent.FullName;
                var workPath = Path.Combine(path, "OnlineCity");
                Directory.CreateDirectory(workPath);
                Loger.PathLog = workPath;
                Loger.Enable = true;

                //удаляем логи старше 7 дней
                var now = DateTime.UtcNow;
                foreach (var old in Directory.GetDirectories(workPath, "Log_*", SearchOption.TopDirectoryOnly))
                {
                    var info = new DirectoryInfo(old);
                    //Loger.Log("Client Delete Directory " + old + " " + (now - info.CreationTimeUtc).TotalDays);
                    if (info.Exists && (now - info.CreationTimeUtc).TotalDays > 7d) info.Delete(true);
                }
                foreach (var old in Directory.GetFiles(workPath, "Log_*.*", SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(old);
                    //Loger.Log("Client Delete File " + old + " " + (now - info.CreationTimeUtc).TotalDays);
                    if (info.Exists && (now - info.CreationTimeUtc).TotalDays > 7d) info.Delete();
                }
            }
            catch { }

            CacheResource.Init();

            Loger.Log("Client Init " + MainHelper.VersionInfo);
            Loger.Log("Client Language: " + Prefs.LangFolderName);

            Loger.Log($"Client local time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}");
            Loger.Log($"The difference between time zones: {(DateTime.UtcNow - DateTime.Now).ToString("g")}");

            //Loger.Log("Client MainThreadNum=" + ModBaseData.GlobalData.MainThreadNum.ToString()); всегда строго = 1

            Task.Factory.StartNew(() => SessionClientController.CalculateHash());
        }

        public static void CalculateHash()
        {
            try
            {
                Loger.Log("Client CalculateHash start");
                UpdateModsWindow.Title = "OC_Hash_CalculateLocalFiles".Translate();
                UpdateModsWindow.ResetProgress();
                UpdateModsWindow.SetIndeterminateProgress("Scanning...");
                //Find.WindowStack.Add(new UpdateModsWindow());
                var factory = new ClientFileCheckerFactory();

                var folderTypeValues = Enum.GetValues(typeof(FolderType));
                ClientFileCheckersComplete = false;
                ClientFileCheckers = new ClientFileChecker[folderTypeValues.Length];
                var filesCount = 0;
                foreach (FolderType folderType in folderTypeValues)
                {
                    UpdateModsWindow.Title = "OC_Hash_CalculateFor".Translate() + folderType.ToString();
                    ClientFileCheckers[(int)folderType] = factory.GetFileChecker(folderType);
                    ClientFileCheckers[(int)folderType].CalculateHash();
                    filesCount += ClientFileCheckers[(int)folderType].FilesHash.Count;
                }

                UpdateModsWindow.Title = "OC_Hash_CalculateComplete".Translate();
                UpdateModsWindow.ResetProgress();
                UpdateModsWindow.HashStatus = "OC_Hash_CalculateConfFile".Translate() + ClientFileCheckers[(int)FolderType.ModsConfigPath].FilesHash.Count.ToString() + "\n" +
                "Mods files: " + ClientFileCheckers[(int)FolderType.ModsFolder].FilesHash.Count.ToString();
                //Task.Run(() => ClientHashChecker.StartGenerateHashFiles());
                Loger.Log("Client CalculateHash end");
                ClientFileCheckersComplete = true;
            }
            catch(Exception exp)
            {
                Loger.Log("Client CalculateHash Exception " + exp.ToString());
            }
        }


        private static object UpdatingWorld = new object();
        private static int GetPlayersInfoCountRequest = 0;
        private static readonly int[] UpdateWorldIntervalsMs = { 5000, 10000, 15000 };
        private static DateTime UpdateWorldNextRunAt = DateTime.MinValue;
        private static int UpdateWorldIdleLevel = 0;
        private const int NetworkDebugEventsMax = 12;
        private static readonly object NetworkDebugLock = new object();
        private static readonly Queue<string> NetworkDebugEvents = new Queue<string>();
        private static bool NetworkDebugEnabled = false;
        private static DateTime LastUpdateWorldDebugAt = DateTime.MinValue;
        private static string LastUpdateWorldDebugSummary = "-";
        private static DateTime LastSaveQueuedAt = DateTime.MinValue;
        private static DateTime LastSaveUploadedAt = DateTime.MinValue;
        private static long LastSaveQueuedBytes = 0;
        private static long LastSaveUploadedBytes = 0;
        private static int SaveUploadRetryCount = 0;
        private static string LastSaveUploadError = null;
        private static bool SaveInMemoryEnabled = true;

        public static string NetworkDebugHotkey => "Ctrl+Alt+Shift+D";
        public static bool IsNetworkDebugEnabled => NetworkDebugEnabled;

        public static bool ToggleNetworkDebugMode()
        {
            NetworkDebugEnabled = !NetworkDebugEnabled;
            AddNetworkDebugEvent("Debug mode " + (NetworkDebugEnabled ? "enabled" : "disabled"));
            UpdateGlobalTooltip();
            return NetworkDebugEnabled;
        }

        private static void AddNetworkDebugEvent(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            var line = DateTime.UtcNow.ToString("HH:mm:ss") + " " + message;
            lock (NetworkDebugLock)
            {
                NetworkDebugEvents.Enqueue(line);
                while (NetworkDebugEvents.Count > NetworkDebugEventsMax)
                {
                    NetworkDebugEvents.Dequeue();
                }
            }
        }

        private static List<string> GetRecentNetworkDebugEvents(int count)
        {
            if (count <= 0) return new List<string>();
            lock (NetworkDebugLock)
            {
                return NetworkDebugEvents.Reverse().Take(count).ToList();
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0B";
            if (bytes < 1024L) return bytes + "B";
            if (bytes < 1024L * 1024L) return (bytes / 1024L) + "KB";
            return (bytes / (1024L * 1024L)) + "MB";
        }

        private static void QueueSaveForUpload(byte[] content, bool single, string source)
        {
            if (content == null || content.Length <= 1024)
            {
                LastSaveUploadError = "empty save data";
                Loger.Log("Client QueueSaveForUpload skip (empty save data)");
                return;
            }

            Data.SaveFileData = content;
            Data.SingleSave = single;
            LastSaveQueuedAt = DateTime.UtcNow;
            LastSaveQueuedBytes = content.LongLength;
            LastSaveUploadError = null;

            if (NetworkDebugEnabled)
            {
                AddNetworkDebugEvent("Save queued " + source + " " + FormatBytes(content.LongLength) + (single ? " single" : ""));
            }
        }

        private static string GetSaveOverlayLine()
        {
            var pendingSave = Data?.SaveFileData;
            if (pendingSave != null && pendingSave.Length > 0)
            {
                return "SAVE pending " + FormatBytes(pendingSave.LongLength)
                    + (SaveUploadRetryCount > 0 ? " retry:" + SaveUploadRetryCount : "");
            }

            if (!string.IsNullOrEmpty(LastSaveUploadError) && LastSaveQueuedAt != DateTime.MinValue)
            {
                var sec = (int)Math.Max(0d, (DateTime.UtcNow - LastSaveQueuedAt).TotalSeconds);
                return "SAVE retry " + sec + "s";
            }

            if (LastSaveUploadedAt != DateTime.MinValue)
            {
                var sec = (int)Math.Max(0d, (DateTime.UtcNow - LastSaveUploadedAt).TotalSeconds);
                return "SAVE ok " + sec + "s";
            }

            return null;
        }

        private static List<string> GetNetworkDebugOverlayLines()
        {
            if (!NetworkDebugEnabled) return null;

            var now = DateTime.UtcNow;
            var connect = SessionClient.Get;
            var requestStart = connect?.Client?.CurrentRequestStart ?? DateTime.MinValue;
            var requestInProgress = requestStart != DateTime.MinValue;
            var requestLength = requestInProgress ? connect.Client.CurrentRequestLength : 0;
            var requestSeconds = requestInProgress ? (int)Math.Max(0d, (now - requestStart).TotalSeconds) : 0;
            var nextUpdateSeconds = UpdateWorldNextRunAt == DateTime.MinValue
                ? -1
                : (int)Math.Max(0d, (UpdateWorldNextRunAt - now).TotalSeconds);

            var lines = new List<string>
            {
                "DBG " + NetworkDebugHotkey,
                "UW idle:" + UpdateWorldIdleLevel + " next:" + (nextUpdateSeconds < 0 ? "-" : nextUpdateSeconds + "s"),
                requestInProgress ? "REQ " + requestSeconds + "s " + FormatBytes(requestLength) : "REQ idle",
                "REC " + (SessionClient.IsRelogin ? "relogin" : "ok") + " try:" + (Data?.CountReconnectBeforeUpdate ?? 0)
            };

            if (LastUpdateWorldDebugAt != DateTime.MinValue)
            {
                var lastUpdateSeconds = (int)Math.Max(0d, (now - LastUpdateWorldDebugAt).TotalSeconds);
                lines.Add("LastUW " + lastUpdateSeconds + "s " + LastUpdateWorldDebugSummary);
            }

            foreach (var evt in GetRecentNetworkDebugEvents(2))
            {
                lines.Add("E " + evt);
            }

            return lines;
        }

        private static string GetNetworkDebugTooltipText()
        {
            if (!NetworkDebugEnabled) return null;
            var pingMs = Data == null ? 0 : (int)Data.Ping.TotalMilliseconds;
            var lastServerConnectFail = Data != null && Data.LastServerConnectFail;

            var lines = new List<string>
            {
                "[Network debug]",
                "Hotkey: " + NetworkDebugHotkey,
                "Ping: " + pingMs + "ms",
                "LastServerConnectFail: " + lastServerConnectFail
            };

            var events = GetRecentNetworkDebugEvents(8);
            if (events.Count > 0)
            {
                lines.Add("Events:");
                lines.AddRange(events);
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static void ResetUpdateWorldSchedule()
        {
            UpdateWorldIdleLevel = 0;
            UpdateWorldNextRunAt = DateTime.MinValue;
        }

        private static void ScheduleNextUpdateWorld(bool hasActivity)
        {
            if (hasActivity)
            {
                UpdateWorldIdleLevel = 0;
            }
            else if (UpdateWorldIdleLevel < UpdateWorldIntervalsMs.Length - 1)
            {
                UpdateWorldIdleLevel++;
            }

            UpdateWorldNextRunAt = DateTime.UtcNow.AddMilliseconds(UpdateWorldIntervalsMs[UpdateWorldIdleLevel]);
        }

        private static bool HasNetworkActivity(ModelPlayToServer toServ, ModelPlayToClient fromServ, bool firstRun)
        {
            if (firstRun) return true;

            if ((toServ.WObjects?.Count ?? 0) > 0
                || (toServ.WObjectsToDelete?.Count ?? 0) > 0
                || (toServ.SaveFileData?.Length ?? 0) > 0)
            {
                return true;
            }

            return (fromServ.WObjects?.Count ?? 0) > 0
                || (fromServ.WObjectsToDelete?.Count ?? 0) > 0
                || (fromServ.WObjectOnlineToAdd?.Count ?? 0) > 0
                || (fromServ.WObjectOnlineToDelete?.Count ?? 0) > 0
                || (fromServ.FactionOnlineToAdd?.Count ?? 0) > 0
                || (fromServ.FactionOnlineToDelete?.Count ?? 0) > 0
                || (fromServ.Mails?.Count ?? 0) > 0
                || fromServ.AreAttacking
                || fromServ.NeedSaveAndExit;
        }

        private static void UpdateWorldAdaptiveTimer()
        {
            if (Current.Game == null) return;
            if (!SessionClient.Get.IsLogined) return;
            if (UpdateWorldNextRunAt == DateTime.MinValue)
            {
                UpdateWorldNextRunAt = DateTime.UtcNow.AddMilliseconds(UpdateWorldIntervalsMs[0]);
                return;
            }
            if (DateTime.UtcNow < UpdateWorldNextRunAt) return;
            UpdateWorld(false);
        }

        private static void UpdateWorld(bool firstRun = false)
        {
            lock (UpdatingWorld)
            {
                Command((connect) =>
                {
                    var errorNum = "0 ";
                    byte[] saveFileDataToSend = null;
                    try
                    {
                        //собираем пакет на сервер // collecting the package on the server
                        var toServ = new ModelPlayToServer()
                        {
                            UpdateTime = Data.UpdateTime, //время прошлого запроса
                        };
                        //данные сохранения игры // save game data
                        if (Data.SaveFileData != null && Data.SaveFileData.Length > 0)
                        {
                            Data.AddTimeCheckTimerFail = true;
                            saveFileDataToSend = Data.SaveFileData;
                            toServ.SaveFileData = saveFileDataToSend;
                            toServ.SingleSave = Data.SingleSave;
                        }
                        errorNum += "00 ";

                        //метод не выполняется когда игра свернута
                        if (!ModBaseData.RunMainThreadSync(UpdateWorldController.PrepareInMainThread, 1, true))
                        {
                            Data.AddTimeCheckTimerFail = false;
                            if (saveFileDataToSend != null)
                            {
                                SaveUploadRetryCount++;
                                LastSaveUploadError = "main-thread busy";
                                if (NetworkDebugEnabled)
                                {
                                    AddNetworkDebugEvent("Save postponed (main thread busy)");
                                }
                            }
                            ScheduleNextUpdateWorld(true);
                            return;
                        }

                        errorNum += "1 ";
                        //собираем данные с планеты // collecting data from the planet
                        if (!firstRun) UpdateWorldController.SendToServer(toServ, firstRun, null);
                        //Послать на серверв
                        errorNum += "2 ";
                        if (firstRun)
                        {
                            GetPlayersInfoCountRequest = 0;
                            ModelGameServerInfo gameServerInfo = connect.GetGameServerInfo();
                            errorNum += "3 ";
                            UpdateWorldController.SendToServer(toServ, firstRun, gameServerInfo);
                            errorNum += "4 ";
                        }

                        errorNum += "5 ";
                        //запрос на информацию об игроках. Можно будет ограничить редкое получение для тех кто оффлайн
                        //request for information about players. It will be possible to limit the rare receipt for those who are offline
                        if (Data.Chats != null && Data.Chats[0].PartyLogin != null)
                        {
                            if (Data.Players == null || Data.Players.Count == 0
                                || GetPlayersInfoCountRequest % 5 == 0)
                            {
                                //в начале и раз в пол минуты (5 сек между UpdateWorld * 5) получаем инфу обо всех
                                toServ.GetPlayersInfo = Data.Chats[0].PartyLogin;
                            }
                            else
                            {
                                //в промежутках о тех кто онлайн
                                toServ.GetPlayersInfo = Data.Players.Values.Where(p => p.Online).Select(p => p.Public.Login).ToList();
                            }
                            GetPlayersInfoCountRequest++;
                            //Loger.Log("Client " + My.Login + " UpdateWorld* " + (toServ.GetPlayersInfo.Count.ToString()) 
                            //    + " " + (toServ.GetPlayersInfo.Any(p => p == SessionClientController.My.Login) ? "1" : "0"));
                        }

                        errorNum += "6 ";
                        //отправляем на сервер, получаем ответ
                        //we send to the server, we get a response2
                        ModelPlayToClient fromServ = connect.PlayInfo(toServ);
                        if (saveFileDataToSend != null)
                        {
                            if (ReferenceEquals(Data.SaveFileData, saveFileDataToSend))
                            {
                                Data.SaveFileData = null;
                            }
                            LastSaveUploadedAt = DateTime.UtcNow;
                            LastSaveUploadedBytes = saveFileDataToSend.LongLength;
                            SaveUploadRetryCount = 0;
                            LastSaveUploadError = null;
                            if (NetworkDebugEnabled)
                            {
                                AddNetworkDebugEvent("Save uploaded " + FormatBytes(saveFileDataToSend.LongLength));
                            }
                        }
                        if (Data.AddTimeCheckTimerFail)
                        {
                            if (Timers != null) Timers.LastLoop = DateTime.UtcNow; //сбрасываем, чтобы проверка по диссконекту не сбросла подключение
                            Data.AddTimeCheckTimerFail = false;
                        }
                        //Loger.Log("Client UpdateWorld 5 ");

                        errorNum += "7 ";
                        Loger.Log($"Client {My.Login} UpdateWorld myWO->{toServ.WObjects?.Count}"
                            + ((toServ.WObjectsToDelete?.Count ?? 0) > 0 ? " myWOToDelete->" + toServ.WObjectsToDelete.Count : "")
                            + (toServ.SaveFileData == null || toServ.SaveFileData.Length == 0 ? "" : " SaveData->" + toServ.SaveFileData.Length)
                            + ((fromServ.Mails?.Count ?? 0) > 0 ? " Mail<-" + fromServ.Mails.Count : "")
                            + (fromServ.AreAttacking ? " Attacking!" : "")
                            + (fromServ.NeedSaveAndExit ? " Disconnect command!" : "")
                            + (fromServ.PlayersInfo != null ? " Players<-" + fromServ.PlayersInfo.Count : "")
                            + (fromServ.States != null ? " States<-" + fromServ.States.Count : "")
                            + ((fromServ.WObjects?.Count ?? 0) > 0 ? " WO<-" + fromServ.WObjects.Count : "")
                            + ((fromServ.WObjectsToDelete?.Count ?? 0) > 0 ? " WOToDelete<-" + fromServ.WObjectsToDelete.Count : "")
                            + ((fromServ.FactionOnlineList?.Count ?? 0) > 0 ? " Faction<-" + fromServ.FactionOnlineList.Count : "")
                            + ((fromServ.WObjectOnlineList?.Count ?? 0) > 0 ? " NonPWO<-" + fromServ.WObjectOnlineList.Count : "")
                            );
                        LastUpdateWorldDebugAt = DateTime.UtcNow;
                        LastUpdateWorldDebugSummary =
                            "outWO:" + (toServ.WObjects?.Count ?? 0)
                            + " inWO:" + (fromServ.WObjects?.Count ?? 0)
                            + " inMail:" + (fromServ.Mails?.Count ?? 0)
                            + " del:" + (fromServ.WObjectsToDelete?.Count ?? 0);

                        //сохраняем время актуальности данных
                        Data.UpdateTime = fromServ.UpdateTime;
                        Data.UpdateTimeLocalTime = DateTime.UtcNow;

                        if (!string.IsNullOrEmpty(fromServ.KeyReconnect)) Data.KeyReconnect = fromServ.KeyReconnect;

                        //обновляем информацию по игрокам
                        if (fromServ.PlayersInfo != null && fromServ.PlayersInfo.Count > 0)
                        {
                            foreach (var pi in fromServ.PlayersInfo)
                            {
                                if (pi.Login == null) continue;
                                Data.Players[pi.Login] = new PlayerClient() { Public = pi };
                                if (pi.Login == My.Login)
                                {
                                    My = pi;
                                    Data.MyEx = Data.Players[pi.Login];
                                    //Loger.Log("Client " + My.Login + " UpdateWorld* " + My.LastOnlineTime.ToString("o") + " " + DateTime.UtcNow.ToString("o")
                                    //   + " " + (toServ.GetPlayersInfo.Any(p => p == My.Login) ? "1" : "0"));
                                }
                            }
                        }
                        //приватная информация про самого игрока
                        Data.CashlessBalance = fromServ.CashlessBalance;
                        Data.StorageBalance = fromServ.StorageBalance;

                        //обновляем информацию о государствах
                        if (fromServ.States != null) Data.States = fromServ.States.Where(s => s?.Name != null).ToDictionary(s => s.Name);

                        errorNum += "8 ";
                        //обновляем планету // updating the planet
                        UpdateWorldController.LoadFromServer(fromServ, firstRun);

                        errorNum += "9 ";
                        //обновляем инфу по поселениям
                        var allWObjects = Find.WorldObjects.AllWorldObjects
                            .Select(o => o as CaravanOnline)
                            .Where(o => o != null)
                            .ToList();
                        foreach (var pi in Data.Players)
                        {
                            if (pi.Value.Public.Login == My.Login) continue;
                            pi.Value.WObjects = allWObjects.Where(wo => wo.OnlinePlayerLogin == pi.Key).ToList();
                        }

                        errorNum += "10 ";
                        //Сохраняем и выходим
                        if (fromServ.NeedSaveAndExit)
                        {
                            if (!SessionClientController.Data.BackgroundSaveGameOff)
                            {
                                SessionClientController.SaveGameNow(false, () =>
                                {
                                    SessionClientController.Disconnected("OCity_SessionCC_Shutdown_Command_ProgressSaved".Translate());
                                });
                            }
                            else
                                SessionClientController.Disconnected("OCity_SessionCC_Shutdown_Command".Translate());
                        }

                        //если на нас напали запускаем процесс
                        if (fromServ.AreAttacking && GameAttackHost.AttackMessage())
                        {
                            GameAttackHost.Get.Start(connect);
                        }

                        var hasActivity = HasNetworkActivity(toServ, fromServ, firstRun);
                        ScheduleNextUpdateWorld(hasActivity);
                        if (NetworkDebugEnabled && hasActivity)
                        {
                            AddNetworkDebugEvent((firstRun ? "UpdateWorld first sync " : "UpdateWorld activity ")
                                + LastUpdateWorldDebugSummary);
                        }
                        Data.CountReconnectBeforeUpdate = 0;
                    }
                    catch (Exception ex)
                    {
                        Data.AddTimeCheckTimerFail = false;
                        if (saveFileDataToSend != null)
                        {
                            SaveUploadRetryCount++;
                            LastSaveUploadError = ex.Message;
                            if (NetworkDebugEnabled)
                            {
                                AddNetworkDebugEvent("Save upload retry: " + ex.Message);
                            }
                        }
                        Loger.Log("Client  Exception errorNum = " + errorNum);
                        throw;
                    }
                });
            }
        }

        /// <summary>
        /// Аналогично Command((connect) => ActionCommand);
        /// Но с 3 повторами. При неудаче в некоторых случая лучше вызвать Disconnected("OCity_SessionCC_Disconnected".Translate() + " " + errorMessage);
        /// </summary>
        /// <param name="ActionCommand"></param>
        /// <returns></returns>
        public static string CommandSafely(Func<SessionClient, bool> ActionCommand)
        {
            var errorMessage = "";
            Loger.Log("Client CommandSafely try ");
            int repeat = 0;
            do
            {
                Command((connect) =>
                {
                    if (ActionCommand(connect))
                    {
                        repeat = 1000;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(connect.ErrorMessage)) errorMessage = connect.ErrorMessage?.ServerTranslate();
                        Thread.Sleep(1000);
                        if (repeat > 0) Thread.Sleep(3000);
                        Loger.Log($"Client CommandSafely try again (error: {errorMessage})");
                    }
                });
            }
            while (++repeat < 3); //делаем 3 попытки включая первую
            return repeat >= 1000 ? null : errorMessage;
        }

        /// <summary>
        /// Аналогично SaveGameNow(true, () => { Command((connect) => ActionCommand); } );
        /// Но безопасный вариант с повтором при переподключении
        /// </summary>
        public static void SaveGameNowSingleAndCommandSafely(
            Func<SessionClient, bool> ActionCommand,
            Action FinishGood,
            Action FinishBad,
            bool single = true)
        {
            bool actIsRuned = false;
            Action act = () =>
            {
                Data.ActionAfterReconnect = null;
                if (actIsRuned)
                {
                    Loger.Log("Client SaveGameNowSingleAndCommandSafely cancel by actIsRuned ");
                    return;
                }
                actIsRuned = true;

                if (CommandSafely(ActionCommand) != null)
                {
                    if (FinishBad != null) FinishBad();
                }
                else
                {
                    if (FinishGood != null) FinishGood();
                }
            };
            Data.ActionAfterReconnect = () =>
            {
                //повторить, если во время сохранения произошла ошибка, если не выйдет, то ошибка
                Data.ActionAfterReconnect = FinishBad;
                SaveGameNow(single, act);
            };
            SaveGameNow(single, act);
        }

        private static byte[] SaveGameCoreFromMemory()
        {
            try
            {
                ScribeSaver_InitSaving_Patch.Enable = true;
                GameDataSaveLoader.SaveGame(SaveName);
                return ScribeSaver_InitSaving_Patch.SaveData?.ToArray();
            }
            finally
            {
                ScribeSaver_InitSaving_Patch.Enable = false;
                ScribeSaver_InitSaving_Patch.SaveData = null;
            }
        }

        private static byte[] SaveGameCoreFromDisk()
        {
            ScribeSaver_InitSaving_Patch.Enable = false;
            ScribeSaver_InitSaving_Patch.SaveData = null;

            GameDataSaveLoader.SaveGame(SaveName);
            if (!File.Exists(SaveFullName)) return null;
            return File.ReadAllBytes(SaveFullName);
        }

        private static byte[] SaveGameCore()
        {
            byte[] content = null;
            Exception memorySaveException = null;

            if (SaveInMemoryEnabled)
            {
                try
                {
                    content = SaveGameCoreFromMemory();
                }
                catch (Exception ex)
                {
                    memorySaveException = ex;
                    SaveInMemoryEnabled = false;
                    Loger.Log("Client SaveGameCore memory mode disabled: " + ex);
                }

                if (content == null || content.Length <= 1024)
                {
                    SaveInMemoryEnabled = false;
                    Loger.Log("Client SaveGameCore memory mode returned too small data. Falling back to disk.");
                }
            }

            if (content == null || content.Length <= 1024)
            {
                content = SaveGameCoreFromDisk();
            }

            if (content == null || content.Length <= 1024)
            {
                if (memorySaveException != null) throw memorySaveException;
                throw new ApplicationException("Client SaveGameCore failed: save data is empty.");
            }

            try
            {
                File.WriteAllBytes(SaveFullName, content);
            }
            catch
            {
            }

            return content;
        }

        private static void SaveGame(Action<byte[]> saved)
        {
            Loger.Log("Client SaveGame() ");
            LongEventHandler.QueueLongEvent(() =>
            {
                saved(SaveGameCore());
            }, "Autosaving", false, null);
        }

        /// <summary>
        /// Немедленно сохраняет игру и передает на сервер.
        /// </summary>
        /// <param name="single">Будут удалены остальные Варианты сохранений, кроме этого</param>
        public static void SaveGameNow(bool single = false, Action after = null)
        {
            // checkConfigsBeforeSave(); 
            Loger.Log("Client SaveGameNow single=" + single.ToString());
            SaveGame((content) =>
            {
                if (content.Length > 1024)
                {
                    Data.SaveFileData = content;
                    Data.SingleSave = single;
                    UpdateWorld(false);

                    Loger.Log("Client SaveGameNow OK");
                }
                if (after != null) after();
            });
        }

        /// <summary>
        /// Немедленно сохраняет игру и передает на сервер. Должно запускаться уже в потоке LongEventHandler.QueueLongEvent, ожидает окончания соханения
        /// </summary>
        /// <param name="single">Будут удалены остальные Варианты сохранений, кроме этого</param>
        public static void SaveGameNowInEvent(bool single = false)
        {
            Loger.Log($"Client {SessionClientController.My.Login} SaveGameNowInEvent single=" + single.ToString());

            var content = SaveGameCore();

            if (content.Length > 1024)
            {
                Data.SaveFileData = content;
                Data.SingleSave = single;
                UpdateWorld(false);

                //записываем файл только для удобства игрока, чтобы он у него был
                File.WriteAllBytes(SaveFullName, content);

                Loger.Log($"Client {SessionClientController.My.Login} SaveGameNowInEvent OK");
            }
        }

        private static void BackgroundSaveGame()
        {
            if (Data.BackgroundSaveGameOff) return;

            var tick = (long)Find.TickManager.TicksGame;
            if (Data.LastSaveTick == tick)
            {
                Loger.Log($"Client {SessionClientController.My.Login} BackgroundSaveGame() Cancel in pause");
                return;
            }
            Loger.Log($"Client {SessionClientController.My.Login} BackgroundSaveGame()");
            Data.LastSaveTick = tick;

            SaveGame((content) =>
            {
                Data.SaveFileData = content;
                Data.SingleSave = false;
            });
        }

        private static void PingServer()
        {
            try
            {
                Command((connect) =>
                {
                    connect.ServicePing();
                    Data.CountReconnectBeforeUpdate = 0;
                });
            }
            catch
            {
            }
        }

        private static void UpdateGlobalTooltip()
        {
            try
            {
                GlobalControlsUtility_DoDate_Patch.Update = DateTime.UtcNow;
                GlobalControlsUtility_DoDate_Patch.OutText =
                    new List<string>() {
                        (SessionClient.IsRelogin || Data.LastServerConnectFail
                            ? (string)("(!) " + "OCity_Dialog_Connecting".TranslateCache() + " ")
                            : (SessionClient.Get?.IsLogined ?? false)
                            ? (string)($"OK " + (int)Data.Ping.TotalMilliseconds + new TaggedString("ms")) : "X ")
                        + " " + SessionClientController.My.Login
                        };
                GlobalControlsUtility_DoDate_Patch.TooltipText = string.Format(
                    "OC_SessionCC_Info".Translate() +"." + "OC_SessionCC_Balance".Translate()
                    , Data.ServerName + " (" + (ModBaseData.GlobalData?.LastIP?.Value ?? "") + ")", My.Login);

                var saveLine = GetSaveOverlayLine();
                if (!string.IsNullOrEmpty(saveLine))
                {
                    GlobalControlsUtility_DoDate_Patch.OutText.Add(saveLine);
                }

                var debugLines = GetNetworkDebugOverlayLines();
                if (debugLines != null && debugLines.Count > 0)
                {
                    GlobalControlsUtility_DoDate_Patch.OutText.AddRange(debugLines);
                }

                var debugTooltip = GetNetworkDebugTooltipText();
                if (!string.IsNullOrEmpty(debugTooltip))
                {
                    GlobalControlsUtility_DoDate_Patch.TooltipText += Environment.NewLine + debugTooltip;
                }

                if (!SessionClient.IsRelogin && !Data.LastServerConnectFail && Data.CashlessBalance != 0)
                {
                    var mon = Data.CashlessBalance.ToString(); //ToStringMoney();
                    GlobalControlsUtility_DoDate_Patch.OutText.Add(mon + " ");
                    GlobalControlsUtility_DoDate_Patch.OutInLastLine = GameUtils.TextureCashlessBalance;
                    GlobalControlsUtility_DoDate_Patch.TooltipText += mon;
                }
                else
                {
                    GlobalControlsUtility_DoDate_Patch.OutInLastLine = null;
                    GlobalControlsUtility_DoDate_Patch.TooltipText += "-";
                }

                if ((DateTime.UtcNow - SessionClientController.My.LastSaveTime).TotalMinutes < 60)
                {
                    GlobalControlsUtility_DoDate_Patch.TooltipText += " " + Environment.NewLine
                        + "OC_SessionCC_MinSave".Translate() + " "
                        + (int)(DateTime.UtcNow - SessionClientController.My.LastSaveTime).TotalMinutes;
                }
            }
            catch (Exception ex)
            {
                GlobalControlsUtility_DoDate_Patch.Update = DateTime.MinValue;
                Loger.Log("Exception UpdateGlobalTooltip " + ex.ToString());
            }
            try
            {
                SetPauseWithRelogin();
            }
            catch { }
        }
        private static bool ReloginPauseActived = false;
        private static bool ReloginPauseShowDialog = false;
        private static void SetPauseWithRelogin()
        {
            if (SessionClient.IsRelogin
                || Data.LastServerConnectFail
                || Data.Ping.TotalMilliseconds == 0
                || Data.Ping.TotalMilliseconds > 9000)
            {
                //при проблемах подклюения
                if (!Find.TickManager.Paused)
                {
                    Find.TickManager.Pause();
                    if (!ReloginPauseActived)
                    {
                        //первый раз при обнаружении идущей игры ставим паузу молча
                        ReloginPauseActived = true;
                    }
                    else
                    {
                        //второй раз выдаем предупреждение
                        if (!ReloginPauseShowDialog)
                        {
                            ReloginPauseShowDialog = true;
                            var form = new Dialog_Input("OCity_SessionCC_Synchronization".TranslateCache(), "OCity_SessionCC_SynchronizationText".TranslateCache(), true);
                            form.PostCloseAction = () =>
                            {
                                ReloginPauseShowDialog = false;
                            };
                            Find.WindowStack.Add(form);
                        }
                    }
                }
            }
            else
            {
                ReloginPauseActived = false;
            }
        }

        private static volatile bool ChatIsUpdating = false;
        private static void UpdateChats()
        {
            //Loger.Log("Client UpdateChating...");
            Command((connect) =>
            {
                // Пока не обработали старый запрос, новый не отправляем, иначе ответ не успевает отправиться и следом еще один запрос на изменения
                if (ChatIsUpdating)
                {
                    return;
                }

                ChatIsUpdating = true;
                try
                {
                    var timeFrom = DateTime.UtcNow;
                    var test = connect.ServiceCheck();
                    Data.Ping = DateTime.UtcNow - timeFrom;

                    if (test != null)
                    {
                        Data.LastServerConnectFail = false;
                        Data.LastServerConnect = DateTime.UtcNow;

                        //обновляем чат
                        if (test.Value || Data.ChatCountSkipUpdate > 60) // 60 * 500ms = принудительно раз в пол минуты
                        {
                            //Loger.Log("Client UpdateChats f0");
                            var dc = connect.UpdateChat(Data.ChatsTime);
                            if (dc != null)
                            {
                                Data.ServetTimeDelta = dc.Time - DateTime.UtcNow;
                                Data.ChatsTime.Time = dc.Time;
                                //Loger.Log("Client UpdateChats: " + dc.Chats.Count.ToString() //+ " - " + dc.Time.Ticks //dc.Time.ToString(Loger.Culture)
                                //    + "   " + (dc.Chats.Count == 0 ? "" : dc.Chats[0].Posts.Count.ToString()));

                                if (Data.ApplyChats(dc) && !test.Value)
                                {
                                    Loger.Log("Client UpdateChats: ServiceCheck fail ");
                                }
                            }
                            else
                            {
                                Disconnected("Unknown error in UpdateChats");
                            }

                            Data.ChatCountSkipUpdate = 0;
                        }
                        else
                            Data.ChatCountSkipUpdate++;

                        //в этой же процедуре делаем фоновую загрузку изображений с сервера
                        GeneralTexture.Get.Update(connect);
                    }
                    else
                    {
                        //Loger.Log("Client UpdateChats f2");
                        Data.LastServerConnectFail = true;
                        if (!Data.ServerConnected)
                        {
                            /*
                            var th = new Thread(() =>
                            {
                                Loger.Log("Client ReconnectWithTimers not ping");
                                if (!ReconnectWithTimers())
                                {
                                    Loger.Log("Client Disconnected after try reconnect");
                                    Disconnected("OCity_SessionCC_Disconnected".Translate());
                                }
                            });
                            th.IsBackground = true;
                            th.Start();
                            */
                            Thread.Sleep(5000); //Ждем, когда CheckReconnectTimer срубит этот поток основного таймера 
                        }
                    }
                    //to do Сделать сброс крутяшки после обновления чата (см. Dialog_MainOnlineCity)
                    UpdateColonyScreen();
                }
                catch (Exception ex)
                {
                    Loger.Log(ex.ToString());
                }
                finally
                {
                    ChatIsUpdating = false;
                    UpdateGlobalTooltip();
                }
            });
        }

        private static Dictionary<long, long> UpdateColonyScreenLastTickBySettlementID;
        private static void UpdateColonyScreen()
        {
            if (!SessionClientController.Data.GeneralSettings.ColonyScreenEnable) return;

            //обновляем только между запросами обновлений
            if (Data.UpdateTimeLocalTime == DateTime.MinValue) return;
            var msUpdate = (int)((DateTime.UtcNow - Data.UpdateTimeLocalTime).TotalMilliseconds);
            if (msUpdate < 2000 || msUpdate > 3000) return;

            //делаем скрины колонии днем, учитывая их игровой часовои пояс
            var ticksGame = GenTicks.TicksAbs;// особые тики, не общие (long)Find.TickManager.TicksGame;
            var settlements = ExchengeUtils.WorldObjectsPlayer()
                .Where(o => o is Settlement)
                .ToList();
            foreach (Settlement settlement in settlements)
            {
                var vector = Find.WorldGrid.LongLatOf(settlement.Tile);
                var settlementTick = ticksGame + GenDate.LocalTicksOffsetFromLongitude(vector.x);
                
                long lastTick;
                if (!UpdateColonyScreenLastTickBySettlementID.TryGetValue(settlement.ID, out lastTick)) lastTick = 0;
                UpdateColonyScreenLastTickBySettlementID[settlement.ID] = settlementTick;

                if (lastTick == 0) continue;

                //Loger.Log($"UpdateColonyScreen diff={GenDate.LocalTicksOffsetFromLongitude(vector.x)} last={lastTick} {lastTick / 60000} {lastTick % 60000} settlementTick={settlementTick} {settlementTick / 60000} {settlementTick % 60000} ");
                if (CalcUtils.OnMidday(lastTick, settlementTick) //после полудня
                    || lastTick / 60000 < settlementTick / 60000 //или как-то прошли уже сутки без скрина и сейчас светлое время суток после полудня
                        && settlementTick % 60000 > 60000 / 2
                        && settlementTick % 60000 < 60000 * 3 / 4)
                {
                    var dayByYear = (settlementTick / 60000) % 60;
                    Loger.Log($"UpdateColonyScreen day={dayByYear} delayDays={SessionClientController.Data.GeneralSettings.ColonyScreenDelayDays}");
                    if (SessionClientController.Data.GeneralSettings.ColonyScreenDelayDays > 1
                        && dayByYear % SessionClientController.Data.GeneralSettings.ColonyScreenDelayDays != 0) continue;

                    lock (UpdatingWorld)
                    {
                        //делаем скрин и отправляем
                        try
                        {
                            Loger.Log($"UpdateColonyScreen Screen {settlement.Name} (localID={settlement.ID})");
                            var sc = new SnapshotColony();
                            sc.Background = true;
                            sc.HighQuality = SessionClientController.Data.GeneralSettings.ColonyScreenHighQuality;
                            sc.Exec(settlement);
                            Loger.Log($"UpdateColonyScreen Screen OK");
                        }
                        catch (Exception ex)
                        {
                            Loger.Log(ex.ToString());
                        }
                    }
                }
            }
        }

        public static string Connect(string addr)
        {
            TimersStop();

            int port = 0;
            if (addr.Contains(":")
                && int.TryParse(addr.Substring(addr.LastIndexOf(":") + 1), out port))
            {
                addr = addr.Substring(0, addr.LastIndexOf(":"));
            }

            var logMsg = "Connecting to server. Addr: " + addr + ". Port: " + (port == 0 ? SessionClient.DefaultPort : port).ToString();
            Loger.Log("Client " + logMsg);
            Log.Warning(logMsg);
            var connect = SessionClient.Get;
            if (!connect.Connect(addr, port))
            {
                logMsg = "Connection fail: " + connect.ErrorMessage?.ServerTranslate();
                Loger.Log("Client " + logMsg);
                Log.Warning(logMsg);
                Find.WindowStack.Add(new Dialog_Input("OCity_SessionCC_ConnectionFailTitle".Translate(), connect.ErrorMessage?.ServerTranslate(), true));
                //Close();
                return connect.ErrorMessage?.ServerTranslate();
            }
            else
            {
                logMsg = "Connection OK";
                Loger.Log("Client " + logMsg);
                Log.Warning(logMsg);
            }

            return null;
        }

        private static string GetSaffix()
        {
            return "@@@"
                + "11" + FileChecker.GetCheckSum("y39¤`"
                    + Environment.UserName + "*" + Environment.MachineName
                    ).Replace("==", "").Substring(4, 19);
        }

        /// <summary>
        /// Подключаемся.
        /// </summary>
        /// <returns>null, или текст произошедшей ошибки</returns>
        public static string Login(string addr, string login, string password, Func<bool, bool> LoginOK)
        {
            var msgError = Connect(addr);
            if (msgError != null) return msgError;

            ConnectAddr = addr;

            var logMsg = "Login: " + login;
            Loger.Log("Client " + logMsg);
            Log.Warning(logMsg);
            My = null;
            var pass = new CryptoProvider().GetHash(password);

            var connect = SessionClient.Get;
            if (!connect.Login(login, pass, GetSaffix()))
            {
                if (connect.ErrorMessage == "User not approve")
                {
                    Loger.Log("Client Login: User not approve");
                    LoginOK(true);
                    return "";
                }

                logMsg = "Login fail: " + connect.ErrorMessage?.ServerTranslate();
                Loger.Log("Client " + logMsg);
                Log.Warning(logMsg);
                Find.WindowStack.Add(new Dialog_Input("OCity_SessionCC_LoginFailTitle".Translate(), connect.ErrorMessage?.ServerTranslate(), true));
                return connect.ErrorMessage?.ServerTranslate();
            }
            else
            {
                logMsg = "Login OK";
                Loger.Log("Client " + logMsg);
                Log.Warning(logMsg);
                if (LoginOK(false))
                    InitConnectedIntro();
                else
                    return "";
            }

            return null;
        }

        /// <summary>
        /// Регистрация
        /// </summary>
        /// <returns>null, или текст произошедшей ошибки</returns>
        public static string Registration(string addr, string login, string password, string email, string discord, Action LoginOK)
        {
            var msgError = Connect(addr);
            if (msgError != null) return msgError;

            ConnectAddr = addr;

            var logMsg = "Registration. Login: " + login;
            Loger.Log("Client " + logMsg);
            Log.Warning(logMsg);
            My = null;
            var pass = new CryptoProvider().GetHash(password);

            var connect = SessionClient.Get;
            if (!connect.Registration(login, pass, email + GetSaffix(), discord))
            {
                if (connect.ErrorMessage == "User not approve")
                {
                    Loger.Log("Client Registration: User not approve");
                    Find.WindowStack.Add(new Dialog_LoginForm(true));
                    return null;
                }

                logMsg = "Registration fail: " + connect.ErrorMessage?.ServerTranslate();
                Loger.Log("Client " + logMsg);
                Log.Warning(logMsg);
                Find.WindowStack.Add(new Dialog_Input("OCity_SessionCC_RegFailTitle".Translate(), connect.ErrorMessage?.ServerTranslate(), true));
                return connect.ErrorMessage?.ServerTranslate();
            }
            else
            {
                MainMenuDrawer_DoMainMenuControls_Patch.DontDisconnectTime = DateTime.UtcNow;
                logMsg = "Registration OK";
                Loger.Log("Client " + logMsg);
                Log.Warning(logMsg);
                LoginOK();
                InitConnectedIntro();
            }

            return null;
        }

        /// <summary>
        /// Отбрасывание старого подключения, создание нового и аутэнтификация. Перед вызовом убедиться, что поток таймера остановлен
        /// </summary>
        /// <returns></returns>
        public static bool Reconnect()
        {
            if (string.IsNullOrEmpty(Data?.KeyReconnect))
            {
                Loger.Log("Client Reconnect fail: no KeyReconnect ");
                return false;
            }
            if (string.IsNullOrEmpty(My?.Login))
            {
                Loger.Log("Client Reconnect fail: no Login ");
                return false;
            }
            if (string.IsNullOrEmpty(ConnectAddr))
            {
                Loger.Log("Client Reconnect fail: no ConnectAddr ");
                return false;
            }

            //Connect {
            var addr = ConnectAddr;
            int port = 0;
            if (addr.Contains(":")
                && int.TryParse(addr.Substring(addr.LastIndexOf(":") + 1), out port))
            {
                addr = addr.Substring(0, addr.LastIndexOf(":"));
            }
            var logMsg = "Reconnect to server. Addr: " + addr + ". Port: " + (port == 0 ? SessionClient.DefaultPort : port).ToString();
            Loger.Log("Client " + logMsg);
            Log.Warning(logMsg);
            var connect = new SessionClient();
            if (!connect.Connect(addr, port))
            {
                logMsg = "Reconnect net fail: " + connect.ErrorMessage?.ServerTranslate();
                Loger.Log("Client " + logMsg);
                Log.Warning(logMsg);
                return false;
            }
            SessionClient.Recreate(connect);
            // }

            logMsg = "Reconnect login: " + My.Login;
            Loger.Log("Client " + logMsg);
            Log.Warning(logMsg);
            //var connect = SessionClient.Get;
            if (!connect.Reconnect(My.Login, Data.KeyReconnect, GetSaffix()))
            {
                logMsg = "Reconnect login fail: " + connect.ErrorMessage?.ServerTranslate();
                Loger.Log("Client " + logMsg);
                Log.Warning(logMsg);
                //Find.WindowStack.Add(new Dialog_Input("OCity_SessionCC_LoginFailTitle".Translate(), connect.ErrorMessage?.ServerTranslate(), true));
                return false;
            }
            else
            {
                logMsg = "Reconnect OK";
                Loger.Log("Client " + logMsg);
                Log.Warning(logMsg);
                return true;
            }
        }

        /// <summary>
        /// Проверяет подключение и немедленно вызывает netAct
        /// </summary>
        public static void Command(Action<SessionClient> netAct)
        {
            int time = 0;
            while (SessionClient.IsRelogin && time < 40000)
            {
                UpdateGlobalTooltip();
                Thread.Sleep(500);
                time += 500;
            }
            if (SessionClient.IsRelogin)
            {
                Loger.Log("Client Command: fail wait IsRelogin. Try to continue", Loger.LogLevel.WARNING);
            }
            var connect = SessionClient.Get;
            netAct(connect);
        }

        private static Thread SingleCommandThread = null;

        public static bool SingleCommandIsBusy => SingleCommandThread != null;
        /// <summary>
        /// Проверяет подключение и вызывает netAct в потоке, если оне не занят, иначе возвращает false
        /// </summary>
        public static bool SingleCommand(Action<SessionClient> netAct)
        {
            if (SingleCommandIsBusy) return false;
            SingleCommandThread = new Thread(() =>
            {
                try
                {
                    Command(netAct);
                }
                catch (Exception ext)
                {
                    Loger.Log("Exception SingleCommand: " + ext.ToString());
                }
                finally
                { 
                    SingleCommandThread = null; 
                }
            });
            SingleCommandThread.IsBackground = true;
            SingleCommandThread.Start();
            return true;
        }

        private static void TimersStop()
        {
            ReconnectSupportRuning = false;
            ResetUpdateWorldSchedule();
            Loger.Log("Client TimersStop b");
            if (TimerReconnect != null) TimerReconnect.Stop();
            TimerReconnect = null;

            if (Timers != null) Timers.Stop();
            Timers = null;
            Loger.Log("Client TimersStop e");
        }

        public static Scenario GetScenarioByName(string scenarioName)
        {
            var list = GameUtils.AllowedScenarios();

            var scenario = list.FirstOrDefault(s => s.Value.name == scenarioName).Value;
            if (scenario == null)
            {
                Loger.Log("error: scenario not found: " + scenarioName);
            }
            return scenario;
        }

        public static string GetScenarioPlayer(Action<string> selected)
        {
            string scenario = null;
            var form = new Dialog_Scenario();
            form.PostCloseAction = () =>
            {
                if (form.ResultOK)
                {
                    scenario = form.InputScenario;
                    selected(scenario);
                }
            };
            Find.WindowStack.Add(form);
            //Loger.Log("Client choosed scenario: " + scenario);
            return scenario;
        }

        public static Storyteller GetStoryteller(string difficultyName, StorytellerDef teller = null)
        {
            var list = DefDatabase<DifficultyDef>.AllDefs.ToList();
            var difficulty = list.FirstOrDefault(d => d.defName == difficultyName);
            if (difficulty == null)
            {
                difficulty = DifficultyDefOf.Easy;
                //throw new ApplicationException($"Dont find difficulty: {difficultyName}");
                //difficulty = list[list.Count - 1];
            }

            if (teller == null) teller = StorytellerDefOf.Cassandra;
            return new Storyteller(teller, difficulty);
        }

        public static void InitConnectedIntro()
        {
            MainMenuDrawer_DoMainMenuControls_Patch.DontDisconnectTime = DateTime.UtcNow;
            if (SessionClientController.LoginInNewServerIP)
            {
                //Текстовое оповещение о возможном обновлении с сервера
                var form = new Dialog_Input("OCity_SessionCC_InitConnectedIntro_Title".Translate()
                    , "OCity_SessionCC_InitConnectedIntro_Text".Translate());
                form.PostCloseAction = () =>
                {
                    if (form.ResultOK)
                    {
                        InitConnected();
                    }
                    else
                    {
                        Disconnected("OCity_DialogInput_Cancele".Translate());
                    }
                };
                Find.WindowStack.Add(form);
            }
            else
            {
                InitConnected();
            }
        }

        public static void SetFullInfo(ModelInfo serverInfo)
        {
            My = serverInfo.My;
            Data.ServerName = serverInfo.ServerName;
            Data.DelaySaveGame = serverInfo.DelaySaveGame;
            if (Data.DelaySaveGame == 0) Data.DelaySaveGame = 15;
            if (Data.DelaySaveGame < 5) Data.DelaySaveGame = 5;
            Data.IsAdmin = serverInfo.IsAdmin;
            Data.DisableDevMode = !serverInfo.IsAdmin && serverInfo.DisableDevMode;
            Data.MinutesIntervalBetweenPVP = serverInfo.MinutesIntervalBetweenPVP;
            Data.TimeChangeEnablePVP = serverInfo.TimeChangeEnablePVP;
            Data.GeneralSettings = serverInfo.GeneralSettings;
            Data.ProtectingNovice = serverInfo.ProtectingNovice;
            MainHelper.OffAllLog = serverInfo.EnableFileLog;
        }

        /// <summary>
        /// После успешной регистрации или входа
        /// </summary>
        public static void InitConnected()
        {
            try
            {
                Loger.Log("Client InitConnected()");
                Data = new ClientData();
                TimersStop();
                Timers = new WorkTimer();
                TimerReconnect = new WorkTimer();

                var connect = SessionClient.Get;
                ModelInfo serverInfo = connect.GetInfo(ServerInfoType.Full);
                ServerTimeDelta = serverInfo.ServerTime - DateTime.UtcNow;
                SetFullInfo(serverInfo);

                Loger.Log($"Server time difference {ServerTimeDelta:hh\\:mm\\:ss\\.ffff}. Server UTC time: {serverInfo.ServerTime:yyyy-MM-dd HH:mm:ss.ffff}");
                Loger.Log("Client ServerName=" + serverInfo.ServerName);
                Loger.Log("Client ServerVersion=" + serverInfo.VersionInfo + " (" + serverInfo.VersionNum + ")");
                Loger.Log("Client IsAdmin=" + serverInfo.IsAdmin
                    + " Seed=" + serverInfo.Seed
                    + " Scenario=" + serverInfo.ScenarioName
                    + " NeedCreateWorld=" + serverInfo.NeedCreateWorld
                    + " DelaySaveGame=" + Data.DelaySaveGame
                    + " DisableDevMode=" + Data.DisableDevMode);
                Loger.Log("Client Grants=" + serverInfo.My.Grants.ToString());

                if (SessionClientController.Data.DisableDevMode)
                {
                    if (Prefs.DevMode) Prefs.DevMode = false;
                    if (IdeoUIUtility.devEditMode) IdeoUIUtility.devEditMode = false;
                    DebugSettingsDefault.SetDefault();
                    // также ещё можно подписаться в метод PrefsData.Apply() и следить за изменениями оттуда
                }

                if (!serverInfo.IsModsWhitelisted) 
                {
                    InitConnectedPart2(serverInfo, null);
                    return;
                }

                if (!string.IsNullOrEmpty(Data.GeneralSettings.EntranceWarning))
                {
                    //определяем локализованные сообщения
                    var entranceWarning = MainHelper.CultureFromGame.StartsWith("Russian")
                        ? Data.GeneralSettings.EntranceWarningRussian
                        : null;
                    //если на нужном языке нет, то выводим по умолчанию
                    if (string.IsNullOrEmpty(entranceWarning))
                    {
                        entranceWarning = Data.GeneralSettings.EntranceWarning;
                    }

                    var form = new Dialog_Input(serverInfo.ServerName + " (" + (ModBaseData.GlobalData?.LastIP?.Value ?? "") + ")", entranceWarning, false);
                    Find.WindowStack.Add(form);
                    form.PostCloseAction = () =>
                    {
                        if (!form.ResultOK)
                        {
                            Disconnected("OCity_SessionCC_MsgCanceledCreateW".Translate(), () => ModsConfig.RestartFromChangedMods());
                            return;
                        }

                        CheckFiles((resultCheckFiles) => InitConnectedPart2(serverInfo, resultCheckFiles));
                    };
                }
                else
                {
                    CheckFiles((resultCheckFiles) => InitConnectedPart2(serverInfo, resultCheckFiles));
                }
            }
            catch (Exception ext)
            {
                Loger.Log("Exception InitConnected: " + ext.ToString());
            }
        }

        private static void InitConnectedPart2(ModelInfo serverInfo, string resultCheckFiles)
        {
            try
            {
                if (resultCheckFiles != null)
                {
                    var missingRequiredContentMessage = serverInfo.IsModsWhitelisted
                        ? GetMissingRequiredContentMessage()
                        : null;
                    if (!string.IsNullOrEmpty(missingRequiredContentMessage))
                    {
                        Disconnected(resultCheckFiles
                            + Environment.NewLine + Environment.NewLine
                            + missingRequiredContentMessage);
                        return;
                    }

                    //var msg = "OCity_SessionCC_FilesUpdated".Translate() + Environment.NewLine
                    //     + (UpdateModsWindow.SummaryList == null ? ""
                    //        : Environment.NewLine
                    //            + "OC_Hash_Complete".Translate().ToString() + Environment.NewLine
                    //            + string.Join(Environment.NewLine, UpdateModsWindow.SummaryList));
                    //Не все файлы прошли проверку, надо инициировать перезагрузку всех модов
                    Disconnected(resultCheckFiles, () => ModsConfig.RestartFromChangedMods());
                    return;
                }

                if (MainHelper.VersionNum < serverInfo.VersionNum)
                {
                    Disconnected("OCity_SessionCC_Client_UpdateNeeded".Translate() + serverInfo.VersionInfo);
                    return;
                }

                //создаем мир, если мы админ
                if (serverInfo.IsModsWhitelisted)
                {
                    var missingRequiredContentMessage = GetMissingRequiredContentMessage();
                    if (!string.IsNullOrEmpty(missingRequiredContentMessage))
                    {
                        Disconnected(missingRequiredContentMessage);
                        return;
                    }
                }

                if (serverInfo.IsAdmin && serverInfo.Seed == "")
                {
                    Loger.Log("Client InitConnected() IsAdmin");
                    var form = new Dialog_CreateWorld();
                    form.PostCloseAction = () =>
                    {
                        if (!form.ResultOK)
                        {
                            GetScenarioByName(form.InputScenario);
                            Disconnected("OCity_SessionCC_MsgCanceledCreateW".Translate());
                            return;
                        }

                        GameStarter.SetMapSize = int.Parse(form.InputMapSize);
                        GameStarter.SetPlanetCoverage = form.InputPlanetCoverage / 100f;
                        GameStarter.SetSeed = form.InputSeed;
                        GameStarter.SetDifficulty = form.InputDifficultyDefName;
                        GameStarter.SetScenario = GetScenarioByName(form.InputScenario);
                        GameStarter.SetScenarioName = form.InputScenarioKey;
                        ChoosedTeller = form.InputStorytellerDef;

                        GameStarter.AfterStart = CreatingServerWorld;
                        GameStarter.GameGeneration();
                    };

                    Find.WindowStack.Add(form);
                    return;
                }

                if (serverInfo.NeedCreateWorld)
                {
                    CreatePlayerWorld(serverInfo);
                    return;
                }

                LoadPlayerWorld(serverInfo);
            }
            catch (Exception ext)
            {
                Loger.Log("Exception InitConnectedPart2: " + ext.ToString());
            }
        }

        private static void LoadPlayerWorld(ModelInfo serverInfo)
        {
            Loger.Log("Client LoadPlayerWorld");
            UpdateModsWindow.WindowsTitle = "Online City";
            UpdateModsWindow.Title = "Loading world from server";
            UpdateModsWindow.HashStatus = "Please wait, this may take a while";
            UpdateModsWindow.SummaryList = null;
            UpdateModsWindow.ResetProgress();
            UpdateModsWindow.SetIndeterminateProgress("Loading...");

            var form = new UpdateModsWindow()
            {
                doCloseX = false,
                HideOK = true
            };
            Find.WindowStack.Add(form);

            AddNetworkDebugEvent("WorldLoad start");

            Task.Factory.StartNew(() =>
            {
                ModelInfo worldData = null;
                Exception worldLoadException = null;
                try
                {
                    var connect = SessionClient.Get;
                    worldData = connect.WorldLoad();
                }
                catch (Exception ex)
                {
                    worldLoadException = ex;
                    Loger.Log("Client LoadPlayerWorld exception: " + ex);
                }

                ModBaseData.RunMainThread(() =>
                {
                    if (worldLoadException != null)
                    {
                        UpdateModsWindow.CompletedAndClose = true;
                        AddNetworkDebugEvent("WorldLoad fail " + worldLoadException.Message);
                        Disconnected("Error " + worldLoadException.Message);
                        return;
                    }

                    if (worldData == null || worldData.SaveFileData == null || worldData.SaveFileData.Length == 0)
                    {
                        UpdateModsWindow.CompletedAndClose = true;
                        AddNetworkDebugEvent("WorldLoad fail empty data");
                        Disconnected("Error world data is empty");
                        return;
                    }

                    UpdateModsWindow.SetProgress(1d, "100%");
                    UpdateModsWindow.CompletedAndClose = true;
                    AddNetworkDebugEvent("WorldLoad ok " + FormatBytes(worldData.SaveFileData.Length));
                    LoadPlayerWorldData(worldData);
                });
            });
        }

        private static void LoadPlayerWorldData(ModelInfo worldData)
        {
            Action loadAction = () =>
            {
                LongEventHandler.QueueLongEvent(delegate
                {
                    Current.Game = new Game { InitData = new GameInitData { gameToLoad = SaveName } };

                    Current.Game.storyteller = GetStoryteller(Data.GeneralSettings.Difficulty, GameUtils.GetStorytallerByName(Data.GeneralSettings.StorytellerDef));
                    Loger.Log($"storyteller: {Current.Game.storyteller.def.defName}      difficulty: {Current.Game.storyteller.difficultyDef.defName}");
                    GameLoades.AfterLoad = () =>
                    {
                        GameLoades.AfterLoad = null;

                        ScribeLoader_InitLoading_Patch.Enable = false;
                        ScribeLoader_InitLoading_Patch.LoadData = null;

                        InitGame();
                    };
                }, "Play", "LoadingLongEvent", false, null);
            };

            ScribeLoader_InitLoading_Patch.Enable = true;
            ScribeLoader_InitLoading_Patch.LoadData = worldData.SaveFileData;

            PreLoadUtility.CheckVersionAndLoad(SaveFullName, ScribeMetaHeaderUtility.ScribeHeaderMode.Map, loadAction);
        }

        //Create world for regular player
        private static void CreatePlayerWorld(ModelInfo serverInfo)
        {
            Loger.Log("Client InitConnected() ExistMap0");
            if (SessionClientController.Data.GeneralSettings.ScenarioAviable)
            {
                GetScenarioPlayer((sce) => CreatePlayerWorldPart1(serverInfo, sce));
            }
            else
            {
                CreatePlayerWorldPart1(serverInfo, serverInfo.ScenarioName);
            }
        }
        private static void CreatePlayerWorldPart1(ModelInfo serverInfo, string choosed_scenario)
        {
            //создать поселение
            GameStarter.SetMapSize = serverInfo.MapSize;
            GameStarter.SetPlanetCoverage = serverInfo.PlanetCoverage;
            GameStarter.SetSeed = serverInfo.Seed;
            if (choosed_scenario == null)
            {
                GameStarter.SetScenario = GetScenarioByName(serverInfo.ScenarioName);
            }
            else
            {
                GameStarter.SetScenario = GetScenarioByName(choosed_scenario);
                GameStarter.SetScenarioName = choosed_scenario;
            }
            GameStarter.SetDifficulty = serverInfo.Difficulty;
            GameStarter.AfterStart = CreatePlayerMap;
            ChoosedTeller = GameUtils.GetStorytallerByName(serverInfo.Storyteller);

            GameStarter.GameGeneration(false);

            //выбор места на планете. Код из события завершения выбора параметров планеты Page_CreateWorldParams
            Loger.Log($"Client InitConnected() ExistMap1 Scenario={choosed_scenario ?? serverInfo.ScenarioName}({GameStarter.SetScenario.name}/{GameStarter.SetScenario.fileName})" +
                $" Difficulty={GameStarter.SetDifficulty}" + $" Storyteller={serverInfo.Storyteller}");

            MainMenuDrawer_DoMainMenuControls_Patch.DontDisconnectTime = DateTime.UtcNow;

            Current.Game = new Game();
            Current.Game.InitData = new GameInitData();
            //Current.Game.storyteller
            Current.Game.Scenario = GameStarter.SetScenario;
            Current.Game.Scenario.PreConfigure();
            /* Current.Game.storyteller = new Storyteller(StorytellerDefOf.Cassandra
                , GameStarter.SetDifficulty == 0 ? DifficultyDefOf.Easy
                    : DifficultyDefOf.Rough); */
            Current.Game.storyteller = GetStoryteller(GameStarter.SetDifficulty, ChoosedTeller);

            Loger.Log("Client InitConnected() ExistMap2");
            Current.Game.World = WorldGenerator.GenerateWorld(
                GameStarter.SetPlanetCoverage,
                GameStarter.SetSeed,
                GameStarter.SetOverallRainfall,
                GameStarter.SetOverallTemperature,
                OverallPopulation.Little
                );

            Loger.Log("Client InitConnected() ExistMap3");
            //после создания мира запускаем его обработку, загружаем поселения др. игроков
            UpdateWorldController.InitGame();
            StartInitialWorldSyncWithProgress(() =>
            {
                Timers.Add(10000, PingServer);

                Loger.Log("Client InitConnected() ExistMap4");
                var form = GetFirstConfigPage(true);
                if (form != null)
                {
                    Find.WindowStack.Add(form);
                }

                Loger.Log("Client InitConnected() ExistMap5");

                MemoryUtility.UnloadUnusedUnityAssets();

                Loger.Log("Client InitConnected() ExistMap6");
                Find.World.renderer.RegenerateAllLayersNow();

                Loger.Log("Client InitConnected() ExistMap7");
            });

        }

        private static void StartInitialWorldSyncWithProgress(Action onComplete)
        {
            UpdateModsWindow.WindowsTitle = "Online City";
            UpdateModsWindow.Title = "Synchronizing world";
            UpdateModsWindow.HashStatus = "Please wait, this may take a while";
            UpdateModsWindow.SummaryList = null;
            UpdateModsWindow.ResetProgress();
            UpdateModsWindow.SetIndeterminateProgress("Preparing...");

            Find.WindowStack.Add(new UpdateModsWindow()
            {
                doCloseX = false,
                HideOK = true
            });

            var stopProgressFlag = 0;
            Task.Factory.StartNew(() =>
            {
                while (Interlocked.CompareExchange(ref stopProgressFlag, 0, 0) == 0)
                {
                    try
                    {
                        var connect = SessionClient.Get;
                        var total = connect?.Client?.CurrentRequestLength ?? 0L;
                        var done = connect?.Client?.CurrentRequestProgressLength ?? 0L;
                        if (done < 0) done = 0;
                        if (total > 0 && done > total) done = total;

                        if (total <= 0)
                        {
                            UpdateModsWindow.SetIndeterminateProgress("Syncing...");
                        }
                        else
                        {
                            var ratio = Math.Max(0d, Math.Min(1d, (double)done / total));
                            var progress = 0.1d + ratio * 0.85d;
                            var progressText = ((int)Math.Round(ratio * 100d)).ToString()
                                + "% (" + FormatBytes(done) + "/" + FormatBytes(total) + ")";
                            UpdateModsWindow.SetProgress(progress, progressText);
                        }
                    }
                    catch
                    {
                    }

                    Thread.Sleep(150);
                }
            });

            Task.Factory.StartNew(() =>
            {
                Exception syncException = null;
                try
                {
                    UpdateWorld(true);
                }
                catch (Exception ex)
                {
                    syncException = ex;
                    Loger.Log("Client StartInitialWorldSyncWithProgress exception: " + ex);
                }
                finally
                {
                    Interlocked.Exchange(ref stopProgressFlag, 1);
                }

                ModBaseData.RunMainThread(() =>
                {
                    UpdateModsWindow.SetProgress(1d, "100%");
                    UpdateModsWindow.CompletedAndClose = true;

                    if (syncException != null)
                    {
                        Disconnected("Error " + syncException.Message);
                        return;
                    }

                    if (!SessionClient.Get.IsLogined)
                    {
                        return;
                    }

                    onComplete?.Invoke();
                });
            });
        }

        public static WorldObjectOnline GetWorldObjects(WorldObject obj)
        {
            var worldObject = new WorldObjectOnline();
            worldObject.Name = obj.LabelCap;
            worldObject.Tile = obj.Tile;
            worldObject.FactionGroup = obj?.Faction?.def?.LabelCap;
            return worldObject;
        }

        public static void CheckFiles(Action<string> done)
        {
            if (ClientFileCheckers == null || ClientFileCheckers.Any(x => x == null))
            {
                done("Error not files");
                return;
            }

            UpdateModsWindow.ResetProgress();
            UpdateModsWindow.SetIndeterminateProgress("Preparing...");

            var form = new UpdateModsWindow()
            {
                doCloseX = false
            };
            Find.WindowStack.Add(form);
            form.HideOK = true;

            AddNetworkDebugEvent("Mod sync start");

            Task.Factory.StartNew(() =>
            {
                var fc = new ClientHashChecker(SessionClient.Get);
                fc.Report = new ClientHashCheckerResult();
                var approveModList = true;
                foreach (var clientFileChecker in ClientFileCheckers)
                {
                    var res = fc.GenerateRequestAndDoJob(clientFileChecker);
                    approveModList = approveModList && res;
                }

                AddNetworkDebugEvent("Mod sync done");
                UpdateModsWindow.CompletedAndClose = true;
                form.OnCloseed = () =>
                { 
                    done(fc.Report.ReportComplete());
                };
            });

        }
        private static string GetMissingRequiredContentMessage()
        {
            var requiredPackageIds = GetRequiredLudeonPackageIdsFromModsConfig();
            if (requiredPackageIds.Count == 0)
            {
                return null;
            }

            var installedPackageIds = ModLister.AllInstalledMods
                .Select(modMeta => modMeta?.PackageId)
                .Where(packageId => !string.IsNullOrEmpty(packageId))
                .Select(packageId => packageId.Trim().ToLowerInvariant())
                .ToHashSet();

            var missingPackageIds = requiredPackageIds
                .Where(packageId => !installedPackageIds.Contains(packageId))
                .Distinct()
                .OrderBy(packageId => packageId)
                .ToList();
            if (missingPackageIds.Count == 0)
            {
                return null;
            }

            var text = new StringBuilder();
            text.AppendLine("Missing required DLC/content from server ModsConfig.xml:");
            foreach (var packageId in missingPackageIds)
            {
                text.AppendLine(packageId);
            }
            text.Append("Install and enable the listed DLC/content, then restart the game.");
            return text.ToString();
        }

        private static List<string> GetRequiredLudeonPackageIdsFromModsConfig()
        {
            var modsConfigPath = Path.Combine(GenFilePaths.ConfigFolderPath, ModsConfigFileName);
            if (!File.Exists(modsConfigPath))
            {
                return new List<string>();
            }

            try
            {
                return XDocument.Load(modsConfigPath)
                    .Descendants("li")
                    .Select(li => (li.Value ?? string.Empty).Trim().ToLowerInvariant())
                    .Where(packageId => packageId.StartsWith("ludeon.rimworld.")
                        && packageId != "ludeon.rimworld")
                    .Distinct()
                    .ToList();
            }
            catch (Exception ext)
            {
                Loger.Log("Client GetRequiredLudeonPackageIdsFromModsConfig exception: " + ext.ToString());
                return new List<string>();
            }
        }

        private static bool IsClientWorldSetupPage(Page page)
        {
            var pageName = page?.GetType()?.Name;
            return pageName == "Page_SelectStoryteller"
                || pageName == "Page_ChooseStoryteller"
                || pageName == "Page_CreateWorldParams";
        }

        private static Page SkipClientWorldSetupPages(Page firstPage)
        {
            if (firstPage == null) return null;

            Page firstKept = null;
            Page previousKept = null;
            var current = firstPage;
            while (current != null)
            {
                var next = current.next;
                if (!IsClientWorldSetupPage(current))
                {
                    if (firstKept == null)
                    {
                        firstKept = current;
                    }

                    if (previousKept != null && !ReferenceEquals(previousKept.next, current))
                    {
                        previousKept.next = current;
                        previousKept.nextAct = null;
                    }

                    previousKept = current;
                }

                current = next;
            }

            if (previousKept != null)
            {
                previousKept.next = null;
            }

            return firstKept;
        }

        public static Page GetFirstConfigPage(bool skipClientWorldSetupPages = false)
        {
            try
            {
                var scenario = Current.Game?.Scenario ?? GameStarter.SetScenario;
                var scenarioPage = scenario?.GetFirstConfigPage();
                if (skipClientWorldSetupPages)
                {
                    scenarioPage = SkipClientWorldSetupPages(scenarioPage);
                }
                if (scenarioPage != null)
                {
                    var lastPage = scenarioPage;
                    while (lastPage.next != null)
                    {
                        lastPage = lastPage.next;
                    }
                    if (lastPage.nextAct == null)
                    {
                        lastPage.nextAct = delegate
                        {
                            PageUtility.InitGameStart();
                        };
                    }
                    return scenarioPage;
                }
            }
            catch (Exception ext)
            {
                Loger.Log("Client GetFirstConfigPage fallback: " + ext.ToString());
            }

            //fallback to the custom page chain for edge cases
            List<Page> list = new List<Page>();
            list.Add(new Page_SelectStartingSite());
            if (ModsConfig.IdeologyActive)
            {
                list.Add(new Page_ChooseIdeoPreset());
            }
            list.Add(new Page_ConfigureStartingPawns());
            Page page = PageUtility.StitchedPages(list);
            if (page != null)
            {
                Page page2 = page;
                while (page2.next != null)
                {
                    page2 = page2.next;
                }
                page2.nextAct = delegate
                {
                    PageUtility.InitGameStart();
                };
            }

            return page;
        }

        /// <summary>
        /// Запускается, когда админ первый раз заходит на сервер, выберет параметры нового мира, и
        /// первое поселение этого мира стартовало.
        /// Здесь происходит чтение созданного мира и сохранение его на сервере, само поселение игнорируется.
        /// </summary>
        private static void CreatingServerWorld()
        {
            Loger.Log("Client CreatingServerWorld()");
            //Удаление лишнего, добавление того, что нужно в пустом новом мире на сервере
            //Remove unnecessary, add what you need in an empty new world on the server

            var allWorldObjects = GameUtils.GetAllWorldObjects();
            var nonPlayerSettlements = allWorldObjects
                .Where(wo => wo is Settlement)
                .Where(wo => wo.HasName && !(wo.Faction?.IsPlayer ?? false))
                .ToList();
            var nonPlayerFactions = Find.FactionManager.AllFactionsListForReading
                .Where(f => !f.IsPlayer)
                .ToList();

            //передаем полученное
            var toServ = new ModelCreateWorld();
            toServ.MapSize = GameStarter.SetMapSize;
            toServ.PlanetCoverage = GameStarter.SetPlanetCoverage;
            toServ.Seed = GameStarter.SetSeed;
            toServ.ScenarioName = GameStarter.SetScenarioName;
            toServ.Difficulty = GameStarter.SetDifficulty;
            toServ.Storyteller = ChoosedTeller.defName;
            toServ.WObjectOnlineList = nonPlayerSettlements
                .Select(UpdateWorldController.GetWorldObjects)
                .ToList();
            toServ.FactionOnlineList = nonPlayerFactions
                .Select(UpdateWorldController.GetFactions)
                .ToList();

            var connect = SessionClient.Get;
            string msg;
            if (!connect.CreateWorld(toServ))
            {
                msg = "OCity_SessionCC_MsgCreateWorlErr".Translate()
                    + Environment.NewLine + connect.ErrorMessage?.ServerTranslate();
            }
            else
            {
                msg = "OCity_SessionCC_MsgCreateWorlGood".Translate();
            }

            Find.WindowStack.Add(new Dialog_Input("OCity_SessionCC_MsgCreatingServer".Translate(), msg, true)
            {
                PostCloseAction = () =>
                {
                    GenScene.GoToMainMenu();
                }
            });
        }

        /// <summary>
        /// Запускается, когда игрок выбрал поселенцев, создался мир и игра запустилась
        /// </summary>
        private static void CreatePlayerMap()
        {
            Loger.Log("Client CreatePlayerMap()");
            GameStarter.AfterStart = null;
            //сохраняем в основном потоке и передаем на сервер в UpdateWorld() внутри InitGame()
            SaveGame((content) =>
            {
                Data.SaveFileData = content;
                Data.SingleSave = true;
                InitGame();
            });
        }

        public static void Disconnected(string msg, Action actionOnDisctonnect = null)
        {
            if (actionOnDisctonnect == null)
            {
                actionOnDisctonnect = () => GenScene.GoToMainMenu();
            }

            Loger.Log("Client Disconected :( " + msg);
            GameExit.BeforeExit = null;
            TimersStop();
            SessionClient.Get.Disconnect();
            if (msg == null)
                actionOnDisctonnect();
            else
                Find.WindowStack.Add(new Dialog_Input("OCity_SessionCC_Disconnect".Translate(), msg, true)
                {
                    PostCloseAction = actionOnDisctonnect
                });
        }

        private static void UpdateFastTimer()
        {
            if (Current.Game == null) return;
            if (!SessionClient.Get.IsLogined) return;

            if (SessionClientController.Data.DisableDevMode)
            {
                if (Prefs.DevMode) Prefs.DevMode = false;
                if (IdeoUIUtility.devEditMode) IdeoUIUtility.devEditMode = false;
                // также ещё можно подписаться в метод PrefsData.Apply() и следить за изменениями оттуда
            }

            if (UpdateWorldSafelyRun != null)
            {
                Loger.Log("Client UpdateWorldSafelyRun() b");
                var fun = UpdateWorldSafelyRun;
                UpdateWorldSafelyRun = null;
                UpdateWorld(false);
                fun();
                Loger.Log("Client UpdateWorldSafelyRun() e");
            }
        }
        #region На время реконнекта запускаем поток, который будет обновлять статус в интерфейсе запуская UpdateGlobalTooltip();
        private static Thread ReconnectSupportThread = null;
        private static bool ReconnectSupportRuning = false;
        private static void ReconnectSupportInit()
        {
            if (ReconnectSupportThread == null)
            {
                ReconnectSupportThread = new Thread(() =>
                {
                    while (true)
                    {
                        Thread.Sleep(900);
                        if (!SessionClient.Get.IsLogined)
                        {
                            ReconnectSupportRuning = false;
                            ReconnectSupportThread = null;
                            return;
                        }
                        if (!ReconnectSupportRuning) continue;

                        UpdateGlobalTooltip();
                    }
                });
                ReconnectSupportThread.IsBackground = true;
                ReconnectSupportThread.Start();
            }
        }
        #endregion
        public static bool ReconnectWithTimers()
        {
            Timers.Pause = true;
            SessionClient.IsRelogin = true;
            ReconnectSupportInit();
            ReconnectSupportRuning = true;
            AddNetworkDebugEvent("Reconnect start");
            try
            {
                var repeat = 3;
                while (repeat-- > 0)
                {
                    Loger.Log("Client CheckReconnectTimer() " + (3 - repeat).ToString());
                    try
                    {
                        if (Reconnect())
                        {
                            Data.LastServerConnectFail = false;
                            Loger.Log($"Client CheckReconnectTimer() OK #{Data.CountReconnectBeforeUpdate}");
                            AddNetworkDebugEvent("Reconnect ok #" + Data.CountReconnectBeforeUpdate);
                            if (Data.ActionAfterReconnect != null)
                            {
                                var aar = Data.ActionAfterReconnect;
                                Data.ActionAfterReconnect = null;
                                ModBaseData.RunMainThread(aar);
                            }
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Loger.Log("Client CheckReconnectTimer() Exception:" + ex.ToString());
                        AddNetworkDebugEvent("Reconnect exception " + ex.Message);
                    }
                    var sleep = 7000;
                    while (sleep > 0)
                    { 
                        Data.Ping = DateTime.UtcNow - Data.LastServerConnect;
                        UpdateGlobalTooltip();
                        Thread.Sleep(500);
                        sleep -= 500;
                    }
                }
                AddNetworkDebugEvent("Reconnect failed");
                return false;
            }
            finally
            {
                Timers.Pause = false;
                SessionClient.IsRelogin = false;
                ReconnectSupportRuning = false;
            }
        }

        private static long GetReconnectTimeoutSeconds(long requestLength)
        {
            if (requestLength < 1024L * 512L) return 20;
            if (requestLength < 1024L * 1024L * 2L) return 60;
            if (requestLength < 1024L * 1024L * 10L) return 240;
            if (requestLength < 1024L * 1024L * 50L) return 900;
            return 1800;
        }

        /// <summary>
        /// Проведура работает в отдельном потоке и проверяет активно ли основное сетевое подключение.
        /// Если это не так, то не меняя контекста пересоздается подключение, срубается поток основного таймера, и запускается снова
        /// </summary>
        public static void CheckReconnectTimer()
        {
            //Условие, когда подключение считается зависшим: не срабатывает собития чата (оно только из основного таймера) 
            // и долгое время текущего получения ответа

            try
            {
                //обновление стауса
                if ((DateTime.UtcNow - Data.LastServerConnect).TotalMilliseconds > 1000)
                {
                    Data.Ping = DateTime.UtcNow - Data.LastServerConnect;
                    UpdateGlobalTooltip();
                }

                //Loger.Log("Client TestBagSD CRTb");
                var connect = SessionClient.Get;
                var needReconnect = false;
                string reconnectReason = null;
                var requestInProgress = connect.Client.CurrentRequestStart != DateTime.MinValue;
                //проверка коннекта
                if (requestInProgress)
                {
                    var sec = (long)(DateTime.UtcNow - connect.Client.CurrentRequestStart).TotalSeconds;
                    var len = connect.Client.CurrentRequestLength;
                    var requestTimeout = GetReconnectTimeoutSeconds(len);
                    if (sec > requestTimeout)
                    {
                        needReconnect = true;
                        reconnectReason = "request timeout";
                        Loger.Log($"Client ReconnectWithTimers len={len} sec={sec} timeout={requestTimeout} noPing={Data.LastServerConnectFail}");
                    }
                }
                //проверка пропажи пинга
                if (!needReconnect && !requestInProgress && Data.LastServerConnectFail)
                {
                    needReconnect = true;
                    reconnectReason = "no ping";
                    Loger.Log($"Client ReconnectWithTimers noPing");
                }
                //проверка не завис ли поток с таймером
                if (!needReconnect && !requestInProgress && !Data.DontCheckTimerFail && !Timers.IsStop && Timers.LastLoop != DateTime.MinValue)
                {
                    var sec = (long)(DateTime.UtcNow - Timers.LastLoop).TotalSeconds;
                    if (sec > (Data.AddTimeCheckTimerFail ? 120 : 30))
                    {
                        needReconnect = true;
                        reconnectReason = "timer stall";
                        Loger.Log($"Client ReconnectWithTimers timerFail {sec}");
                        Timers.LastLoop = DateTime.UtcNow; //сбрасываем, т.к. поток в таймере продолжает ждать наш коннект
                    }
                }
                if (needReconnect)
                {
                    AddNetworkDebugEvent("Reconnect trigger " + (reconnectReason ?? "unknown"));
                    //котострофа
                    if (++Data.CountReconnectBeforeUpdate > 4 || !ReconnectWithTimers())
                    {
                        Loger.Log("Client CheckReconnectTimer Disconnected after try reconnect");
                        AddNetworkDebugEvent("Disconnected after reconnect tries");
                        Disconnected("OCity_SessionCC_Disconnected".Translate()
                            , Data.CountReconnectBeforeUpdate > 4 ? () =>
                            {
                                Environment.Exit(0);
                            }
                        : (Action)null);
                    }
                }
            }
            catch (Exception e)
            {
                //Никогда не должен был сюда заходить, но как то раз зашел, почему - так и не разобрались. Но теперь этот код тут :)
                Loger.Log("Client CheckReconnectTimer exception: " + e.ToString(), Loger.LogLevel.ERROR);
                AddNetworkDebugEvent("CheckReconnectTimer exception " + e.Message);
                try
                {
                    Disconnected("OCity_SessionCC_Disconnected".Translate());
                }
                catch
                {
                    Environment.Exit(0);
                }
            }
            //Loger.Log("Client TestBagSD CRTe");
        }

        /// <summary>
        /// Инициализация после получения всех данных и уже запущенной игре
        /// </summary>
        public static void InitGame()
        {
            try
            {
                Loger.Log("Client InitGame()");
                //Data.ChatsTime = (DateTime.UtcNow + ServerTimeDelta).AddDays(-1); //без этого указания будут получены все сообщения с каналов

                //Делаем запрос на главный поток, чтобы дождаться когда игра прогрузится и начнуться регулярный обновления интерфейся
                ModBaseData.RunMainThreadSync(() => Loger.Log("Client InitGame MainThread check OK"), 60 * 5);
                Loger.Log("Client InitGame MainThread check end");

                GeneralTexture.Init();

                UpdateColonyScreenLastTickBySettlementID = new Dictionary<long, long>();

                //сбрасываем с мышки выбранный инструмент DevMode
                LudeonTK.DebugTools.curTool = null;

                MainButtonWorker_OC.ShowOnStart();
                UpdateWorldController.ClearWorld();
                UpdateWorldController.InitGame();
                ChatController.Init(true);
                Data.UpdateTime = DateTime.MinValue;
                ResetUpdateWorldSchedule();
                //UpdateWorld Синхронизация мира
                UpdateWorld(true);
                Data.LastServerConnect = DateTime.MinValue;

                Timers.Add(100, UpdateFastTimer);

                Timers.Add(500, UpdateChats);
                //Обновление мира
                Timers.Add(1000, UpdateWorldAdaptiveTimer);
                //Пинг раз в 10 сек для поддержания соединения
                Timers.Add(10000, PingServer);
                //Обновление мира
                Timers.Add(60000 * Data.DelaySaveGame, BackgroundSaveGame);
                //Следит за 1 таймером, создаёт новое соединение
                TimerReconnect.Add(1000, CheckReconnectTimer);
                
                //устанавливаем событие на выход из игры
                GameExit.BeforeExit = () =>
                {
                    try
                    {
                        Loger.Log("Client BeforeExit ");
                        GameExit.BeforeExit = null;
                        TimersStop();
                        if (Current.Game == null) return;

                        if (!Data.BackgroundSaveGameOff)
                        {
                            Loger.Log($"Client {SessionClientController.My.Login} SaveGameBeforeExit ");

                            SaveGameNowInEvent();
                        }
                        SessionClient.Get.Disconnect();
                    }
                    catch (Exception e)
                    {
                        Loger.Log("Client BeforeExit Exception: " + e.ToString(), Loger.LogLevel.ERROR);
                        throw;
                    }
                };



                /*
                // <<<<<<<<<<<<<<<<<<<<<<<<<<
                //отладка!!!
                Find.WindowStack.Add(new Dialog_InputImage()
                {
                    SelectImageAction = (img, data) =>
                    {
                        Command((connect) =>
                        {
                            connect.FileSharingUpload(FileSharingCategory.PlayerIcon, My.Login, data);
                            var p = connect.FileSharingDownload(FileSharingCategory.PlayerIcon, My.Login);


                            connect.FileSharingUpload(FileSharingCategory.ColonyScreen, My.Login + "@6", data); // Login_serverId
                            var p2 = connect.FileSharingDownload(FileSharingCategory.ColonyScreen, My.Login + "@6");

                            Disconnected("test " + data.Length + " " + p.Data.Length + " " + (p2.Data?.Length ?? 0));
                        });
                    }
                });
                // <<<<<<<<<<<<<<<<<<<<<<<<<<
                */


            }
            catch (Exception e)
            {
                ExceptionUtil.ExceptionLog(e, "Client InitGame Error");
                Disconnected(null);
                //GameExit.BeforeExit = null;
                //TimersStop();
                //if (Current.Game == null) return;
                //SessionClient.Get.Disconnect();
            }
        }

    }
}
