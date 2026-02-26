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
using System.Security.Cryptography;
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
        private const int SaveDebugEventsMax = 5;
        private static readonly object SaveDebugLock = new object();
        private static readonly Queue<string> SaveDebugEvents = new Queue<string>();
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
        private static string LastSaveQueuedFingerprint = null;
        private static string LastSaveUploadedFingerprint = null;
        private static string LastSaveQueuedTargetKey = null;
        private static string LastSaveUploadedTargetKey = null;
        private static bool SkipRestoreMapViewAfterLoadOnce = false;
        /// <summary>
        /// Флаг одноразового пропуска GameExit.BeforeExit при служебной загрузке выбранного серверного сейва.
        /// Нужен, чтобы переход в главное меню не делал автосейв старого мира и не разрывал уже восстановленное соединение.
        /// </summary>
        private static bool SkipBeforeExitForServerReloadOnce = false;

        public static string NetworkDebugHotkey => "Ctrl+Alt+Shift+D";
        public static bool IsNetworkDebugEnabled => NetworkDebugEnabled;

        public static bool ToggleNetworkDebugMode()
        {
            NetworkDebugEnabled = !NetworkDebugEnabled;
            AddNetworkDebugEvent("Режим отладки " + (NetworkDebugEnabled ? "включен" : "выключен"));
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

        private static void AddSaveDebugEvent(string status, long bytes = 0, string details = null)
        {
            if (string.IsNullOrWhiteSpace(status)) return;
            var line = DateTime.UtcNow.ToString("HH:mm:ss") + " " + status;
            if (bytes > 0)
            {
                line += " " + FormatBytes(bytes);
            }
            if (!string.IsNullOrWhiteSpace(details))
            {
                line += " " + details;
            }
            lock (SaveDebugLock)
            {
                SaveDebugEvents.Enqueue(line);
                while (SaveDebugEvents.Count > SaveDebugEventsMax)
                {
                    SaveDebugEvents.Dequeue();
                }
            }
        }

        private static List<string> GetRecentSaveDebugEvents(int count)
        {
            if (count <= 0) return new List<string>();
            lock (SaveDebugLock)
            {
                return SaveDebugEvents.Reverse().Take(count).ToList();
            }
        }

        private static string CropDebugMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;
            var text = message.Replace(Environment.NewLine, " ").Trim();
            if (text.Length > 64) text = text.Substring(0, 64) + "...";
            return text;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0B";
            if (bytes < 1024L) return bytes + "B";
            if (bytes < 1024L * 1024L) return (bytes / 1024L) + "KB";
            return (bytes / (1024L * 1024L)) + "MB";
        }

        private static string GetSaveFingerprint(byte[] content)
        {
            if (content == null || content.Length == 0) return "0";
            using (var sha1 = SHA1.Create())
            {
                return content.Length + ":" + BitConverter.ToString(sha1.ComputeHash(content)).Replace("-", string.Empty);
            }
        }

        private static int NormalizeQueuedSaveSlot(bool isAuto, int slot)
        {
            if (Data == null) return isAuto ? 0 : 1;

            if (isAuto)
            {
                var maxAutoSlots = Math.Max(0, Data.AutoSaveSlotsCount);
                if (maxAutoSlots == 0) return 0;
                if (slot == 0) return 0; // 0 = фоновая ротация автослотов на сервере
                if (slot < 1) return 1;
                if (slot > maxAutoSlots) return maxAutoSlots;
                return slot;
            }

            var maxManualSlots = Math.Max(1, Data.SaveSlotsCount);
            if (slot < 1) return 1;
            if (slot > maxManualSlots) return maxManualSlots;
            return slot;
        }

        private static string GetSaveTargetKey(bool isAuto, int slot)
        {
            var safeSlot = NormalizeQueuedSaveSlot(isAuto, slot);
            var slotPart = safeSlot == 0 ? "auto" : safeSlot.ToString();
            return (isAuto ? "A:" : "M:") + slotPart;
        }

        private static string GetSaveTargetText(bool isAuto, int slot)
        {
            var safeSlot = NormalizeQueuedSaveSlot(isAuto, slot);
            if (isAuto)
            {
                return safeSlot == 0 ? "автослот:авто" : "автослот:" + safeSlot;
            }

            return "слот:" + (safeSlot < 1 ? 1 : safeSlot);
        }

        private static string GetSaveTargetTextFromKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "слот:1";
            if (key.StartsWith("A:", StringComparison.Ordinal))
            {
                var autoSlot = key.Length > 2 ? key.Substring(2) : "auto";
                return autoSlot == "auto" ? "автослот:авто" : "автослот:" + autoSlot;
            }

            var manualSlot = key.StartsWith("M:", StringComparison.Ordinal) && key.Length > 2 ? key.Substring(2) : "1";
            return "слот:" + manualSlot;
        }

        private static void QueueSaveForUpload(byte[] content, bool single, string source, bool saveIsAuto, int saveSlot)
        {
            if (content == null || content.Length <= 1024)
            {
                LastSaveUploadError = "пустые данные сейва";
                Loger.Log("Client QueueSaveForUpload пропуск (пустые данные сейва)");
                AddSaveDebugEvent("пропуск-пусто", 0, source);
                return;
            }
            if (Data == null)
            {
                LastSaveUploadError = "данные клиента недоступны";
                Loger.Log("Client QueueSaveForUpload пропуск (данные клиента недоступны)");
                AddSaveDebugEvent("пропуск-нет-данных", 0, source);
                return;
            }

            if (saveIsAuto && Data.AutoSaveSlotsCount <= 0)
            {
                LastSaveUploadError = "автослоты отключены на сервере";
                Loger.Log("Client QueueSaveForUpload пропуск (автослоты отключены)");
                AddSaveDebugEvent("пропуск-нет-автослотов", 0, source);
                return;
            }

            var fingerprint = GetSaveFingerprint(content);
            var normalizedSlot = NormalizeQueuedSaveSlot(saveIsAuto, saveSlot);
            var targetKey = GetSaveTargetKey(saveIsAuto, normalizedSlot);
            var targetText = GetSaveTargetText(saveIsAuto, normalizedSlot);

            if (Data.SaveFileData != null
                && Data.SaveFileData.Length > 0
                && string.Equals(LastSaveQueuedFingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(LastSaveQueuedTargetKey, targetKey, StringComparison.Ordinal))
            {
                AddSaveDebugEvent("пропуск-дубль-в-очереди", content.LongLength, source);
                if (NetworkDebugEnabled)
                {
                    AddNetworkDebugEvent("Сейв пропущен: дубль в очереди " + source);
                }
                return;
            }

            if ((Data.SaveFileData == null || Data.SaveFileData.Length == 0)
                && string.Equals(LastSaveUploadedFingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(LastSaveUploadedTargetKey, targetKey, StringComparison.Ordinal))
            {
                AddSaveDebugEvent("пропуск-без-изменений", content.LongLength, source);
                if (NetworkDebugEnabled)
                {
                    AddNetworkDebugEvent("Сейв пропущен: без изменений " + source);
                }
                return;
            }

            Data.SaveFileData = content;
            Data.SingleSave = single;
            Data.PendingSaveIsAuto = saveIsAuto;
            Data.PendingSaveSlotNumber = normalizedSlot;
            if (saveIsAuto)
            {
                if (normalizedSlot > 0) Data.AutoSaveSlotNumber = normalizedSlot;
            }
            else
            {
                Data.SaveSlotNumber = normalizedSlot < 1 ? 1 : normalizedSlot;
            }
            LastSaveQueuedFingerprint = fingerprint;
            LastSaveQueuedTargetKey = targetKey;
            LastSaveQueuedAt = DateTime.UtcNow;
            LastSaveQueuedBytes = content.LongLength;
            SaveUploadRetryCount = 0;
            LastSaveUploadError = null;
            AddSaveDebugEvent("в-очереди", content.LongLength, source + (single ? " одиночный" : "") + " " + targetText);

            if (NetworkDebugEnabled)
            {
                AddNetworkDebugEvent("Сейв в очереди " + source + " " + FormatBytes(content.LongLength)
                    + (single ? " одиночный" : "") + " " + targetText);
            }
        }

        private static string GetSaveOverlayLine()
        {
            var pendingSave = Data?.SaveFileData;
            var pendingTargetText = GetSaveTargetText(Data?.PendingSaveIsAuto ?? false, Data?.PendingSaveSlotNumber ?? 1);
            if (pendingSave != null && pendingSave.Length > 0)
            {
                return "СЕЙВ в очереди " + FormatBytes(pendingSave.LongLength)
                    + (SaveUploadRetryCount > 0 ? " повтор:" + SaveUploadRetryCount : "")
                    + " " + pendingTargetText;
            }

            if (!string.IsNullOrEmpty(LastSaveUploadError) && LastSaveQueuedAt != DateTime.MinValue)
            {
                var sec = (int)Math.Max(0d, (DateTime.UtcNow - LastSaveQueuedAt).TotalSeconds);
                return "СЕЙВ повтор " + sec + "с"
                    + (LastSaveQueuedBytes > 0 ? " " + FormatBytes(LastSaveQueuedBytes) : "")
                    + " " + pendingTargetText;
            }

            if (LastSaveUploadedAt != DateTime.MinValue)
            {
                var sec = (int)Math.Max(0d, (DateTime.UtcNow - LastSaveUploadedAt).TotalSeconds);
                return "СЕЙВ отправлен " + sec + "с"
                    + (LastSaveUploadedBytes > 0 ? " " + FormatBytes(LastSaveUploadedBytes) : "")
                    + " " + GetSaveTargetTextFromKey(LastSaveUploadedTargetKey);
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
                "ОТЛ " + NetworkDebugHotkey,
                "ОБН простой:" + UpdateWorldIdleLevel + " след:" + (nextUpdateSeconds < 0 ? "-" : nextUpdateSeconds + "с"),
                requestInProgress ? "ЗАПРОС " + requestSeconds + "с " + FormatBytes(requestLength) : "ЗАПРОС простой",
                "ПЕРЕПОДКЛ " + (SessionClient.IsRelogin ? "перелогин" : "норма") + " попытка:" + (Data?.CountReconnectBeforeUpdate ?? 0)
            };

            if (LastUpdateWorldDebugAt != DateTime.MinValue)
            {
                var lastUpdateSeconds = (int)Math.Max(0d, (now - LastUpdateWorldDebugAt).TotalSeconds);
                lines.Add("ПоследнийОБН " + lastUpdateSeconds + "с " + LastUpdateWorldDebugSummary);
            }

            var lastSaveDebug = GetRecentSaveDebugEvents(1).FirstOrDefault();
            if (!string.IsNullOrEmpty(lastSaveDebug))
            {
                lines.Add("СЕЙВ " + lastSaveDebug);
            }

            foreach (var evt in GetRecentNetworkDebugEvents(2))
            {
                lines.Add("СОБЫТИЕ " + evt);
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
                "[Отладка сети]",
                "Горячая клавиша: " + NetworkDebugHotkey,
                "Пинг: " + pingMs + "мс",
                "Сбой последнего соединения: " + (lastServerConnectFail ? "да" : "нет")
            };

            var events = GetRecentNetworkDebugEvents(8);
            if (events.Count > 0)
            {
                lines.Add("События:");
                lines.AddRange(events);
            }

            var saveEvents = GetRecentSaveDebugEvents(SaveDebugEventsMax);
            if (saveEvents.Count > 0)
            {
                lines.Add("События сейва:");
                lines.AddRange(saveEvents);
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static void ResetUpdateWorldSchedule()
        {
            UpdateWorldIdleLevel = 0;
            UpdateWorldNextRunAt = DateTime.MinValue;
        }

        private static void ResetSaveUploadState()
        {
            LastSaveQueuedAt = DateTime.MinValue;
            LastSaveUploadedAt = DateTime.MinValue;
            LastSaveQueuedBytes = 0;
            LastSaveUploadedBytes = 0;
            SaveUploadRetryCount = 0;
            LastSaveUploadError = null;
            LastSaveQueuedFingerprint = null;
            LastSaveUploadedFingerprint = null;
            LastSaveQueuedTargetKey = null;
            LastSaveUploadedTargetKey = null;
            lock (SaveDebugLock)
            {
                SaveDebugEvents.Clear();
            }
            if (Data != null)
            {
                if (Data.SaveSlotNumber < 1) Data.SaveSlotNumber = 1;
                if (Data.AutoSaveSlotNumber < 1) Data.AutoSaveSlotNumber = 1;
                Data.PendingSaveIsAuto = false;
                Data.PendingSaveSlotNumber = Data.SaveSlotNumber;
            }
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
                        //Собираем пакет для отправки на сервер
                        var toServ = new ModelPlayToServer()
                        {
                            UpdateTime = Data.UpdateTime, //время прошлого запроса
                        };
                        //Данные сохранения игры
                        if (Data.SaveFileData != null && Data.SaveFileData.Length > 0)
                        {
                            Data.AddTimeCheckTimerFail = true;
                            saveFileDataToSend = Data.SaveFileData;
                            toServ.SaveFileData = saveFileDataToSend;
                            toServ.SingleSave = Data.SingleSave;
                            toServ.SaveIsAuto = Data.PendingSaveIsAuto;
                            toServ.SaveNumber = Data.PendingSaveIsAuto
                                ? Data.PendingSaveSlotNumber
                                : (Data.PendingSaveSlotNumber > 0 ? Data.PendingSaveSlotNumber : 1);
                        }
                        errorNum += "00 ";

                        //метод не выполняется когда игра свернута
                        if (!ModBaseData.RunMainThreadSync(UpdateWorldController.PrepareInMainThread, 1, true))
                        {
                            Data.AddTimeCheckTimerFail = false;
                            if (saveFileDataToSend != null)
                            {
                                SaveUploadRetryCount++;
                                LastSaveUploadError = "основной поток занят";
                                AddSaveDebugEvent("повтор-основной-поток", saveFileDataToSend.LongLength, "повтор:" + SaveUploadRetryCount);
                                if (NetworkDebugEnabled)
                                {
                                    AddNetworkDebugEvent("Сейв отложен (основной поток занят)");
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
                        //Запрос информации об игроках. Для оффлайн-игроков можно позже ограничить частоту обновления.
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
                        //Отправляем на сервер и получаем ответ
                        ModelPlayToClient fromServ = connect.PlayInfo(toServ);
                        if (saveFileDataToSend != null)
                        {
                            var uploadedFingerprint = GetSaveFingerprint(saveFileDataToSend);
                            var uploadedTargetKey = GetSaveTargetKey(toServ.SaveIsAuto, toServ.SaveNumber);
                            var uploadedTargetText = GetSaveTargetText(toServ.SaveIsAuto, toServ.SaveNumber);
                            if (ReferenceEquals(Data.SaveFileData, saveFileDataToSend))
                            {
                                Data.SaveFileData = null;
                                LastSaveQueuedFingerprint = null;
                                LastSaveQueuedTargetKey = null;
                            }
                            LastSaveUploadedFingerprint = uploadedFingerprint;
                            LastSaveUploadedTargetKey = uploadedTargetKey;
                            LastSaveUploadedAt = DateTime.UtcNow;
                            LastSaveUploadedBytes = saveFileDataToSend.LongLength;
                            SaveUploadRetryCount = 0;
                            LastSaveUploadError = null;
                            AddSaveDebugEvent("отправлен", saveFileDataToSend.LongLength,
                                (toServ.SingleSave ? "одиночный " : "") + uploadedTargetText);
                            if (NetworkDebugEnabled)
                            {
                                AddNetworkDebugEvent("Сейв отправлен " + FormatBytes(saveFileDataToSend.LongLength) + " " + uploadedTargetText);
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
                            + (toServ.SaveFileData == null || toServ.SaveFileData.Length == 0 ? "" : " SaveData->" + toServ.SaveFileData.Length + " " + GetSaveTargetText(toServ.SaveIsAuto, toServ.SaveNumber))
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
                        //Обновляем планету
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
                            AddNetworkDebugEvent((firstRun ? "UpdateWorld первая синхронизация " : "UpdateWorld активность ")
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
                            AddSaveDebugEvent("повтор-ошибка", saveFileDataToSend.LongLength, CropDebugMessage(ex.Message));
                            if (NetworkDebugEnabled)
                            {
                                AddNetworkDebugEvent("Повтор отправки сейва: " + ex.Message);
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
            var usedDiskFallback = false;

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
                    Loger.Log("Client SaveGameCore режим памяти отключен: " + ex);
                    AddSaveDebugEvent("генерация-память-ошибка", 0, CropDebugMessage(ex.Message));
                }

                if (content == null || content.Length <= 1024)
                {
                    SaveInMemoryEnabled = false;
                    Loger.Log("Client SaveGameCore режим памяти вернул слишком мало данных. Переход на диск.");
                    AddSaveDebugEvent("генерация-память-пусто");
                }
            }

            if (content == null || content.Length <= 1024)
            {
                usedDiskFallback = true;
                content = SaveGameCoreFromDisk();
            }

            if (content == null || content.Length <= 1024)
            {
                AddSaveDebugEvent("генерация-ошибка", 0, CropDebugMessage(memorySaveException?.Message));
                if (memorySaveException != null) throw memorySaveException;
                throw new ApplicationException("Client SaveGameCore: данные сохранения пусты.");
            }

            AddSaveDebugEvent(usedDiskFallback ? "генерация-диск" : "генерация-память", content.LongLength);

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
            }, "Автосохранение", false, null);
        }

        /// <summary>
        /// Немедленно сохраняет игру и передает на сервер.
        /// </summary>
        /// <param name="single">Будут удалены остальные Варианты сохранений, кроме этого</param>
        public static void SaveGameNow(bool single = false, Action after = null, bool saveIsAuto = false, int saveSlot = 0)
        {
            // checkConfigsBeforeSave(); 
            Loger.Log("Client SaveGameNow single=" + single.ToString() + " saveIsAuto=" + saveIsAuto.ToString() + " saveSlot=" + saveSlot.ToString());
            SaveGame((content) =>
            {
                if (content.Length > 1024)
                {
                    var slotToSave = saveSlot != 0
                        ? saveSlot
                        : (saveIsAuto ? Data.AutoSaveSlotNumber : Data.SaveSlotNumber);
                    var saveSource = saveIsAuto ? "ручной-авто" : "ручной";
                    QueueSaveForUpload(content, single, saveSource, saveIsAuto, slotToSave);
                    UpdateWorld(false);

                    Loger.Log("Client SaveGameNow OK");
                }
                else
                {
                    LastSaveUploadError = "данные сохранения слишком малы";
                    AddSaveDebugEvent("пропуск-малый-размер", content.LongLength, saveIsAuto ? "ручной-авто" : "ручной");
                    Loger.Log("Client SaveGameNow пропуск отправки: данные сохранения слишком малы");
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
            Loger.Log($"Client {SessionClientController.My.Login} SaveGameNowInEvent одиночный=" + single.ToString());

            var content = SaveGameCore();

            if (content.Length > 1024)
            {
                QueueSaveForUpload(content, single, "событие", false, Data.SaveSlotNumber);
                UpdateWorld(false);
                Loger.Log($"Client {SessionClientController.My.Login} SaveGameNowInEvent успешно");
            }
            else
            {
                LastSaveUploadError = "данные сохранения слишком малы";
                AddSaveDebugEvent("пропуск-малый-размер", content.LongLength, "событие");
                Loger.Log($"Client {SessionClientController.My.Login} SaveGameNowInEvent пропуск отправки: данные сохранения слишком малы");
            }
        }

        private static void BackgroundSaveGame()
        {
            if (Data.BackgroundSaveGameOff) return;

            var tick = (long)Find.TickManager.TicksGame;
            if (Data.LastSaveTick == tick)
            {
                Loger.Log($"Client {SessionClientController.My.Login} BackgroundSaveGame() отменено из-за паузы");
                return;
            }
            Loger.Log($"Client {SessionClientController.My.Login} BackgroundSaveGame()");
            Data.LastSaveTick = tick;

            SaveGame((content) =>
            {
                QueueSaveForUpload(content, false, "фон", true, 0);
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
                                Disconnected("Неизвестная ошибка в UpdateChats");
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
                Loger.Log("Client Reconnect не выполнен: отсутствует KeyReconnect");
                return false;
            }
            if (string.IsNullOrEmpty(My?.Login))
            {
                Loger.Log("Client Reconnect не выполнен: отсутствует Login");
                return false;
            }
            if (string.IsNullOrEmpty(ConnectAddr))
            {
                Loger.Log("Client Reconnect не выполнен: отсутствует адрес подключения");
                return false;
            }

            //Подключение {
            var addr = ConnectAddr;
            int port = 0;
            if (addr.Contains(":")
                && int.TryParse(addr.Substring(addr.LastIndexOf(":") + 1), out port))
            {
                addr = addr.Substring(0, addr.LastIndexOf(":"));
            }
            var logMsg = "Переподключение к серверу. Адрес: " + addr + ". Порт: " + (port == 0 ? SessionClient.DefaultPort : port).ToString();
            Loger.Log("Client " + logMsg);
            Log.Warning(logMsg);
            var connect = new SessionClient();
            if (!connect.Connect(addr, port))
            {
                logMsg = "Сетевое переподключение не выполнено: " + connect.ErrorMessage?.ServerTranslate();
                Loger.Log("Client " + logMsg);
                Log.Warning(logMsg);
                return false;
            }
            SessionClient.Recreate(connect);
            // }

            logMsg = "Переподключение, вход: " + My.Login;
            Loger.Log("Client " + logMsg);
            Log.Warning(logMsg);
            //var connect = SessionClient.Get;
            if (!connect.Reconnect(My.Login, Data.KeyReconnect, GetSaffix()))
            {
                logMsg = "Переподключение, вход не выполнен: " + connect.ErrorMessage?.ServerTranslate();
                Loger.Log("Client " + logMsg);
                Log.Warning(logMsg);
                //Find.WindowStack.Add(new Dialog_Input("OCity_SessionCC_LoginFailTitle".Translate(), connect.ErrorMessage?.ServerTranslate(), true));
                return false;
            }
            else
            {
                logMsg = "Переподключение выполнено";
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
            ResetSaveUploadState();
            Loger.Log("Client TimersStop b");
            if (TimerReconnect != null) TimerReconnect.Stop();
            TimerReconnect = null;

            if (Timers != null) Timers.Stop();
            Timers = null;
            Loger.Log("Client TimersStop e");
        }

        /// <summary>
        /// Перезагружает текущий мир из активного серверного слота игрока.
        /// Используется после выбора слота загрузки в окне серверных сохранений.
        /// </summary>
        public static void ReloadWorldFromServerSave(string selectedSlotText = null)
        {
            var slotText = string.IsNullOrWhiteSpace(selectedSlotText) ? "выбранного слота" : selectedSlotText;

            if (Current.Game == null || !SessionClient.Get.IsLogined)
            {
                Messages.Message("Загрузка " + slotText + " недоступна: нет активного подключения.", MessageTypeDefOf.RejectInput);
                return;
            }

            if (Data == null || My == null)
            {
                Messages.Message("Загрузка " + slotText + " недоступна: данные сессии не готовы.", MessageTypeDefOf.RejectInput);
                return;
            }

            if (SingleCommandIsBusy)
            {
                Messages.Message("Ожидается завершение другой сетевой операции.", MessageTypeDefOf.RejectInput);
                return;
            }

            if (Timers != null) Timers.Pause = true;
            if (TimerReconnect != null) TimerReconnect.Pause = true;

            Messages.Message("Загрузка " + slotText + ": выполняем переподключение...", MessageTypeDefOf.NeutralEvent);
            AddNetworkDebugEvent("Загрузка выбранного сейва: старт " + slotText);

            Task.Factory.StartNew(() =>
            {
                try
                {
                    if (!Reconnect())
                    {
                        var reconnectMessage = SessionClient.Get?.ErrorMessage?.ServerTranslate();
                        throw new ApplicationException("переподключение не выполнено"
                            + (string.IsNullOrWhiteSpace(reconnectMessage) ? string.Empty : ": " + reconnectMessage));
                    }

                    var connect = SessionClient.Get;
                    var serverInfo = connect.GetInfo(ServerInfoType.Full);
                    if (serverInfo == null)
                    {
                        var infoError = connect.ErrorMessage?.ServerTranslate();
                        throw new ApplicationException("сервер не вернул информацию о мире"
                            + (string.IsNullOrWhiteSpace(infoError) ? string.Empty : ": " + infoError));
                    }

                    SetFullInfo(serverInfo);
                    AddNetworkDebugEvent("Загрузка выбранного сейва: подключение восстановлено");

                    ModBaseData.RunMainThread(() =>
                    {
                        StartLoadWorldFromMainMenu(serverInfo, slotText);
                    });
                }
                catch (Exception ex)
                {
                    SkipRestoreMapViewAfterLoadOnce = false;
                    SkipBeforeExitForServerReloadOnce = false;
                    Loger.Log("Client ReloadWorldFromServerSave ошибка: " + ex, Loger.LogLevel.ERROR);
                    AddNetworkDebugEvent("Загрузка выбранного сейва: ошибка " + ex.Message);
                    ModBaseData.RunMainThread(() =>
                    {
                        Disconnected("Ошибка загрузки выбранного сохранения: " + ex.Message);
                    });
                }
            });
        }


        /// <summary>
        /// Безопасная загрузка выбранного серверного сейва через переход в главное меню.
        /// Такой режим стабилен для рендера карты и повторяет успешный сценарий «выйти и зайти снова».
        /// </summary>
        private static void StartLoadWorldFromMainMenu(ModelInfo serverInfo, string slotText)
        {
            if (serverInfo == null)
            {
                Disconnected("Ошибка загрузки: сервер не вернул данные мира.");
                return;
            }

            TimersStop();
            // После TimersStop() таймеры равны null, а InitGame() ожидает, что они уже созданы.
            Timers = new WorkTimer();
            TimerReconnect = new WorkTimer();

            SkipRestoreMapViewAfterLoadOnce = true;
            SkipBeforeExitForServerReloadOnce = true;
            MainMenuDrawer_DoMainMenuControls_Patch.DontDisconnectTime = DateTime.UtcNow.AddMinutes(2);

            try
            {
                GenScene.GoToMainMenu();
            }
            catch (Exception exMenu)
            {
                    SkipRestoreMapViewAfterLoadOnce = false;
                SkipBeforeExitForServerReloadOnce = false;
                Loger.Log("Client StartLoadWorldFromMainMenu ошибка перехода в меню: " + exMenu, Loger.LogLevel.ERROR);
                Disconnected("Ошибка перехода в меню перед загрузкой: " + exMenu.Message);
                return;
            }

            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    MainMenuDrawer_DoMainMenuControls_Patch.DontDisconnectTime = DateTime.UtcNow.AddMinutes(2);
                    AddNetworkDebugEvent("Загрузка выбранного сейва: безопасная загрузка через главное меню");
                    Messages.Message("Загрузка " + slotText + ": выполняем безопасный вход в мир...", MessageTypeDefOf.NeutralEvent);
                    LoadPlayerWorld(serverInfo);
                }
                catch (Exception exLoad)
                {
                    SkipRestoreMapViewAfterLoadOnce = false;
                    SkipBeforeExitForServerReloadOnce = false;
                    Loger.Log("Client StartLoadWorldFromMainMenu ошибка запуска загрузки: " + exLoad, Loger.LogLevel.ERROR);
                    Disconnected("Ошибка запуска загрузки мира: " + exLoad.Message);
                }
            });
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
            var manualSlots = serverInfo.ManualSaveSlotsCount > 0
                ? serverInfo.ManualSaveSlotsCount
                : (serverInfo.SaveSlotsCount > 0 ? serverInfo.SaveSlotsCount : 1);
            Data.SaveSlotsCount = manualSlots;
            Data.SaveSlotNumber = serverInfo.ActiveSaveSlot > 0 ? serverInfo.ActiveSaveSlot : 1;
            if (Data.SaveSlotNumber > Data.SaveSlotsCount) Data.SaveSlotNumber = Data.SaveSlotsCount;
            Data.AutoSaveSlotsCount = serverInfo.AutoSaveSlotsCount >= 0 ? serverInfo.AutoSaveSlotsCount : 0;
            Data.AutoSaveSlotNumber = serverInfo.ActiveAutoSaveSlot > 0 ? serverInfo.ActiveAutoSaveSlot : 1;
            if (Data.AutoSaveSlotsCount <= 0) Data.AutoSaveSlotNumber = 1;
            if (Data.AutoSaveSlotsCount > 0 && Data.AutoSaveSlotNumber > Data.AutoSaveSlotsCount) Data.AutoSaveSlotNumber = Data.AutoSaveSlotsCount;
            Data.PendingSaveIsAuto = false;
            Data.PendingSaveSlotNumber = Data.SaveSlotNumber;
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
                    + " DisableDevMode=" + Data.DisableDevMode
                    + " ManualSaveSlots=" + Data.SaveSlotsCount
                    + " ActiveManualSlot=" + Data.SaveSlotNumber
                    + " AutoSaveSlots=" + Data.AutoSaveSlotsCount
                    + " ActiveAutoSlot=" + Data.AutoSaveSlotNumber);
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
            UpdateModsWindow.Title = "Загрузка мира с сервера";
            UpdateModsWindow.HashStatus = "Пожалуйста, подождите, это может занять время";
            UpdateModsWindow.SummaryList = null;
            UpdateModsWindow.ResetProgress();
            UpdateModsWindow.SetIndeterminateProgress("Загрузка...");

            var form = new UpdateModsWindow()
            {
                doCloseX = false,
                HideOK = true
            };
            Find.WindowStack.Add(form);

            AddNetworkDebugEvent("Загрузка мира: старт");

            Task.Factory.StartNew(() =>
            {
                ModelInfo worldData = null;
                Exception worldLoadException = null;
                try
                {
                    for (var attempt = 1; attempt <= 2; attempt++)
                    {
                        var connect = SessionClient.Get;
                        worldData = connect.WorldLoad();
                        if (worldData != null && worldData.SaveFileData != null && worldData.SaveFileData.Length > 0)
                        {
                            break;
                        }

                        if (attempt < 2)
                        {
                            Loger.Log("Client LoadPlayerWorld: пустые данные мира, повтор после переподключения");
                            if (!Reconnect())
                            {
                                break;
                            }
                        }
                    }
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
                        SkipRestoreMapViewAfterLoadOnce = false;
                        SkipBeforeExitForServerReloadOnce = false;
                        UpdateModsWindow.CompletedAndClose = true;
                        AddNetworkDebugEvent("Загрузка мира: ошибка " + worldLoadException.Message);
                        Disconnected("Ошибка: " + worldLoadException.Message);
                        return;
                    }

                    if (worldData == null || worldData.SaveFileData == null || worldData.SaveFileData.Length == 0)
                    {
                        SkipRestoreMapViewAfterLoadOnce = false;
                        SkipBeforeExitForServerReloadOnce = false;
                        UpdateModsWindow.CompletedAndClose = true;
                        AddNetworkDebugEvent("Загрузка мира: пустые данные");
                        Disconnected("Ошибка: данные мира пусты");
                        return;
                    }

                    UpdateModsWindow.SetProgress(1d, "100%");
                    UpdateModsWindow.CompletedAndClose = true;
                    AddNetworkDebugEvent("Загрузка мира: успешно " + FormatBytes(worldData.SaveFileData.Length));
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
                        if (SkipBeforeExitForServerReloadOnce)
                        {
                            // Подстраховка: флаг должен сбрасываться в GameExit.BeforeExit.
                            SkipBeforeExitForServerReloadOnce = false;
                            Loger.Log("Client LoadPlayerWorldData: аварийный сброс SkipBeforeExitForServerReloadOnce");
                        }
                        if (SkipRestoreMapViewAfterLoadOnce)
                        {
                            // После полной безопасной перезагрузки через меню карта уже в консистентном состоянии.
                            // Дополнительное принудительное восстановление не выполняем.
                    SkipRestoreMapViewAfterLoadOnce = false;
                            Loger.Log("Client LoadPlayerWorldData: пропуск RestoreMapViewAfterLoad (безопасная загрузка через меню)");
                        }
                        else
                        {
                            LongEventHandler.ExecuteWhenFinished(RestoreMapViewAfterLoad);
                        }
                    };
                }, "Play", "LoadingLongEvent", false, null);
            };

            ScribeLoader_InitLoading_Patch.Enable = true;
            ScribeLoader_InitLoading_Patch.LoadData = worldData.SaveFileData;

            PreLoadUtility.CheckVersionAndLoad(SaveFullName, ScribeMetaHeaderUtility.ScribeHeaderMode.Map, loadAction);
        }

        /// <summary>
        /// Восстанавливает отображение карты после загрузки сейва с сервера.
        /// Нужен как страховка от черного экрана, если сторонние моды ломают камеру/GUI в момент загрузки.
        /// </summary>
        private static void RestoreMapViewAfterLoad()
        {
            try
            {
                if (Current.Game == null) return;
                if (Find.Maps == null || Find.Maps.Count == 0)
                {
                    Loger.Log("Client RestoreMapViewAfterLoad: карты не найдены", Loger.LogLevel.WARNING);
                    return;
                }

                var targetMap = Find.Maps.FirstOrDefault(m => m != null && m.IsPlayerHome)
                    ?? Find.CurrentMap
                    ?? Find.Maps[0];
                if (targetMap == null)
                {
                    Loger.Log("Client RestoreMapViewAfterLoad: целевая карта не определена", Loger.LogLevel.WARNING);
                    return;
                }

                RebuildMapRenderManagers(targetMap);

                Current.Game.CurrentMap = targetMap;
                try
                {
                    CameraJumper.TryJump(targetMap.Center, targetMap);
                }
                catch (Exception exJump)
                {
                    Loger.Log("Client RestoreMapViewAfterLoad: ошибка прыжка камеры " + exJump.Message
                        , Loger.LogLevel.WARNING);
                }

                try
                {
                    targetMap.mapDrawer?.RegenerateEverythingNow();
                }
                catch (Exception exMapDrawer)
                {
                    Loger.Log("Client RestoreMapViewAfterLoad: ошибка регенерации карты " + exMapDrawer.Message
                        , Loger.LogLevel.WARNING);
                }

                try
                {
                    var driver = Find.CameraDriver;
                    if (driver != null)
                    {
                        var center = targetMap.Center.ToVector3Shifted();
                        var rootSize = Mathf.Max(24f, Mathf.Min(targetMap.Size.x, targetMap.Size.z) / 3f);
                        driver.SetRootPosAndSize(center, rootSize);
                    }
                }
                catch (Exception exCamera)
                {
                    Loger.Log("Client RestoreMapViewAfterLoad: ошибка позиционирования камеры " + exCamera.Message
                        , Loger.LogLevel.WARNING);
                }
                Loger.Log($"Client RestoreMapViewAfterLoad: карта восстановлена tile={targetMap.Tile} size={targetMap.Size}");
            }
            catch (Exception ex)
            {
                Loger.Log("Client RestoreMapViewAfterLoad ошибка: " + ex, Loger.LogLevel.ERROR);
            }
        }

        /// <summary>
        /// Полностью пересоздает менеджеры отрисовки карты.
        /// Это совместимый обход для случаев, когда после загрузки сейва сторонние патчи оставляют рендер в сломанном состоянии.
        /// </summary>
        private static void RebuildMapRenderManagers(Map targetMap)
        {
            if (targetMap == null) return;

            try
            {
                targetMap.dynamicDrawManager = new DynamicDrawManager(targetMap);
                var allThings = targetMap.listerThings?.AllThings;
                if (allThings != null)
                {
                    for (int i = 0; i < allThings.Count; i++)
                    {
                        var thing = allThings[i];
                        if (thing == null) continue;
                        try
                        {
                            targetMap.dynamicDrawManager.RegisterDrawable(thing);
                        }
                        catch
                        {
                            // Некоторые вещи могут быть частично восстановлены после загрузки модов; пропускаем их.
                        }
                    }
                }

                targetMap.mapDrawer = new MapDrawer(targetMap);
                try
                {
                    targetMap.mapDrawer.RegenerateEverythingNow();
                }
                catch (Exception exRegenerate)
                {
                    Loger.Log("Client RestoreMapViewAfterLoad: ошибка RegenerateEverythingNow " + exRegenerate.Message
                        , Loger.LogLevel.WARNING);
                    try
                    {
                        targetMap.mapDrawer = new MapDrawer(targetMap);
                        targetMap.mapDrawer.RegenerateEverythingNow();
                    }
                    catch
                    {
                        // Ничего страшного: главное не уронить кадр в момент восстановления.
                    }
                }
                Loger.Log("Client RestoreMapViewAfterLoad: менеджеры отрисовки карты пересозданы");
            }
            catch (Exception ex)
            {
                Loger.Log("Client RestoreMapViewAfterLoad: ошибка пересоздания менеджеров отрисовки " + ex
                    , Loger.LogLevel.WARNING);
            }
        }

        //Создание мира для обычного игрока
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
            UpdateModsWindow.Title = "Синхронизация мира";
            UpdateModsWindow.HashStatus = "Пожалуйста, подождите, это может занять время";
            UpdateModsWindow.SummaryList = null;
            UpdateModsWindow.ResetProgress();
            UpdateModsWindow.SetIndeterminateProgress("Подготовка...");

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
                            UpdateModsWindow.SetIndeterminateProgress("Синхронизация...");
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
                        Disconnected("Ошибка: " + syncException.Message);
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
                done("Ошибка: отсутствуют файлы");
                return;
            }

            UpdateModsWindow.ResetProgress();
            UpdateModsWindow.SetIndeterminateProgress("Подготовка...");

            var form = new UpdateModsWindow()
            {
                doCloseX = false
            };
            Find.WindowStack.Add(form);
            form.HideOK = true;

            AddNetworkDebugEvent("Синхронизация модов: старт");

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

                AddNetworkDebugEvent("Синхронизация модов: завершено");
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
            text.AppendLine("Отсутствует обязательный DLC/контент из серверного ModsConfig.xml:");
            foreach (var packageId in missingPackageIds)
            {
                text.AppendLine(packageId);
            }
            text.Append("Установите и включите перечисленный DLC/контент, затем перезапустите игру.");
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

            //Резервный переход к стандартной цепочке страниц для редких случаев
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
            //Удаляем лишнее и добавляем нужное для нового пустого мира на сервере

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
                QueueSaveForUpload(content, true, "создание-карты", false, Data?.SaveSlotNumber ?? 1);
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
            AddNetworkDebugEvent("Переподключение: старт");
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
                            AddNetworkDebugEvent("Переподключение: успешно #" + Data.CountReconnectBeforeUpdate);
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
                        AddNetworkDebugEvent("Переподключение: исключение " + ex.Message);
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
                AddNetworkDebugEvent("Переподключение: не удалось");
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
                        reconnectReason = "таймаут запроса";
                        Loger.Log($"Client ReconnectWithTimers len={len} sec={sec} timeout={requestTimeout} нетПинга={Data.LastServerConnectFail}");
                    }
                }
                //проверка пропажи пинга
                if (!needReconnect && !requestInProgress && Data.LastServerConnectFail)
                {
                    needReconnect = true;
                    reconnectReason = "нет пинга";
                    Loger.Log("Client ReconnectWithTimers нет пинга");
                }
                //проверка не завис ли поток с таймером
                if (!needReconnect && !requestInProgress && !Data.DontCheckTimerFail && !Timers.IsStop && Timers.LastLoop != DateTime.MinValue)
                {
                    var sec = (long)(DateTime.UtcNow - Timers.LastLoop).TotalSeconds;
                    if (sec > (Data.AddTimeCheckTimerFail ? 120 : 30))
                    {
                        needReconnect = true;
                        reconnectReason = "завис таймер";
                        Loger.Log($"Client ReconnectWithTimers timerFail {sec}");
                        Timers.LastLoop = DateTime.UtcNow; //сбрасываем, т.к. поток в таймере продолжает ждать наш коннект
                    }
                }
                if (needReconnect)
                {
                    AddNetworkDebugEvent("Переподключение: триггер " + (reconnectReason ?? "неизвестно"));
                    //котострофа
                    if (++Data.CountReconnectBeforeUpdate > 4 || !ReconnectWithTimers())
                    {
                        Loger.Log("Client CheckReconnectTimer Disconnected after try reconnect");
                        AddNetworkDebugEvent("Отключение после попыток переподключения");
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
                AddNetworkDebugEvent("Проверка переподключения: исключение " + e.Message);
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
                ResetSaveUploadState();
                if (Timers == null || TimerReconnect == null)
                {
                    // Защита от сценариев, где таймеры были остановлены при переподключении/перезагрузке мира.
                    if (Timers == null) Timers = new WorkTimer();
                    if (TimerReconnect == null) TimerReconnect = new WorkTimer();
                    Loger.Log("Client InitGame: таймеры были пустыми и восстановлены");
                }
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
                        if (SkipBeforeExitForServerReloadOnce)
                        {
                            SkipBeforeExitForServerReloadOnce = false;
                            Loger.Log("Client BeforeExit: служебный переход в меню для загрузки сейва, сохранение и disconnect пропущены");
                            return;
                        }
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

