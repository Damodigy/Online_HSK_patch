using OCUnion;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Transfer;
using Util;

namespace ServerOnlineCity
{
    public class RepositorySaveData
    {
        /// <summary>
        /// Количество ручных слотов сохранения (N).
        /// </summary>
        public int CountSaveDataPlayer => Math.Max(1, ServerManager.ServerSettings?.CountSaveDataPlayer ?? 3);

        /// <summary>
        /// Количество автослотов сохранения (M).
        /// </summary>
        public int CountAutoSaveDataPlayer => Math.Max(0, ServerManager.ServerSettings?.CountAutoSaveDataPlayer ?? 3);

        private enum SaveKind : byte
        {
            Manual = 1,
            Auto = 2,
        }

        private sealed class SaveSlotsMeta
        {
            public int ActiveManualSlot { get; set; } = 1;
            public int ActiveAutoSlot { get; set; } = 1;
            public int NextAutoSlot { get; set; } = 1;
            public SaveKind LastKind { get; set; } = SaveKind.Manual;
            public Dictionary<string, string> Names { get; } = new Dictionary<string, string>();
        }

        private readonly Repository MainRepository;

        public RepositorySaveData(Repository repository)
        {
            MainRepository = repository;
        }

        private string GetFileNameBase(string login)
        {
            return Path.Combine(MainRepository.SaveFolderDataPlayers, Repository.NormalizeLogin(login) + ".dat");
        }

        private string GetManualFileName(string login, int slot)
        {
            return GetFileNameBase(login) + slot.ToString().NormalizePath();
        }

        private string GetAutoFileName(string login, int slot)
        {
            return GetFileNameBase(login) + ".auto" + slot.ToString().NormalizePath();
        }

        private string GetMetaFileName(string login)
        {
            return GetFileNameBase(login) + ".meta";
        }

        private int NormalizeManualSlot(int slot)
        {
            if (slot < 1) return 1;
            if (slot > CountSaveDataPlayer) return CountSaveDataPlayer;
            return slot;
        }

        private int NormalizeAutoSlot(int slot)
        {
            if (CountAutoSaveDataPlayer <= 0) return 1;
            if (slot < 1) return 1;
            if (slot > CountAutoSaveDataPlayer) return CountAutoSaveDataPlayer;
            return slot;
        }

        private static string NormalizeSaveName(string name)
        {
            if (name == null) return null;
            var txt = name.Trim();
            if (txt.Length == 0) return null;
            if (txt.Length > 64) txt = txt.Substring(0, 64);
            return txt;
        }

        private static string MakeNameKey(bool isAuto, int slot)
        {
            return (isAuto ? "a:" : "m:") + slot.ToString();
        }

        private void MigrateLegacySaveFileIfNeeded(string login)
        {
            var legacyFileName = GetFileNameBase(login);
            var slot1FileName = GetManualFileName(login, 1);
            if (!File.Exists(legacyFileName) || File.Exists(slot1FileName)) return;
            File.Move(legacyFileName, slot1FileName);
        }

        private SaveSlotsMeta ReadMeta(string login)
        {
            var meta = new SaveSlotsMeta();
            var metaFileName = GetMetaFileName(login);
            if (!File.Exists(metaFileName)) return meta;

            try
            {
                foreach (var lineRaw in File.ReadAllLines(metaFileName, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(lineRaw)) continue;
                    var line = lineRaw.Trim();

                    if (line.StartsWith("activeManual=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(line.Substring("activeManual=".Length), out var manual))
                            meta.ActiveManualSlot = NormalizeManualSlot(manual);
                        continue;
                    }

                    if (line.StartsWith("activeAuto=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(line.Substring("activeAuto=".Length), out var autoSlot))
                            meta.ActiveAutoSlot = NormalizeAutoSlot(autoSlot);
                        continue;
                    }

                    if (line.StartsWith("nextAuto=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(line.Substring("nextAuto=".Length), out var nextAuto))
                            meta.NextAutoSlot = NormalizeAutoSlot(nextAuto);
                        continue;
                    }

                    if (line.StartsWith("lastKind=", StringComparison.OrdinalIgnoreCase))
                    {
                        var kind = line.Substring("lastKind=".Length).Trim().ToLowerInvariant();
                        meta.LastKind = kind == "auto" ? SaveKind.Auto : SaveKind.Manual;
                        continue;
                    }

                    // Совместимость со старым форматом
                    if (line.StartsWith("active=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(line.Substring("active=".Length), out var active))
                            meta.ActiveManualSlot = NormalizeManualSlot(active);
                        continue;
                    }

                    if (!line.StartsWith("name=", StringComparison.OrdinalIgnoreCase)) continue;
                    var payload = line.Substring("name=".Length);
                    // Формат строки: "name=<key>:<base64>", где key может быть "m:1"/"a:2".
                    // Поэтому разделяем по последнему ':', иначе ключ обрезается до "m" и имя не читается.
                    var splitterIndex = payload.LastIndexOf(':');
                    if (splitterIndex <= 0) continue;

                    var key = payload.Substring(0, splitterIndex);
                    var base64 = payload.Substring(splitterIndex + 1);
                    if (string.IsNullOrWhiteSpace(base64)) continue;

                    bool isAuto;
                    int slot;
                    if (key.StartsWith("m:", StringComparison.OrdinalIgnoreCase))
                    {
                        isAuto = false;
                        if (!int.TryParse(key.Substring(2), out slot)) continue;
                        slot = NormalizeManualSlot(slot);
                    }
                    else if (key.StartsWith("a:", StringComparison.OrdinalIgnoreCase))
                    {
                        isAuto = true;
                        if (!int.TryParse(key.Substring(2), out slot)) continue;
                        slot = NormalizeAutoSlot(slot);
                    }
                    else
                    {
                        // Старый формат: просто номер -> ручной слот
                        isAuto = false;
                        if (!int.TryParse(key, out slot)) continue;
                        slot = NormalizeManualSlot(slot);
                    }

                    try
                    {
                        var name = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                        name = NormalizeSaveName(name);
                        if (name != null)
                        {
                            meta.Names[MakeNameKey(isAuto, slot)] = name;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Loger.Log("Server ReadMeta ошибка: " + ex.Message);
            }

            meta.ActiveManualSlot = NormalizeManualSlot(meta.ActiveManualSlot);
            meta.ActiveAutoSlot = NormalizeAutoSlot(meta.ActiveAutoSlot);
            meta.NextAutoSlot = NormalizeAutoSlot(meta.NextAutoSlot);
            return meta;
        }

        private void WriteMeta(string login, SaveSlotsMeta meta)
        {
            var lines = new List<string>
            {
                "activeManual=" + NormalizeManualSlot(meta.ActiveManualSlot).ToString(),
                "activeAuto=" + NormalizeAutoSlot(meta.ActiveAutoSlot).ToString(),
                "nextAuto=" + NormalizeAutoSlot(meta.NextAutoSlot).ToString(),
                "lastKind=" + (meta.LastKind == SaveKind.Auto ? "auto" : "manual")
            };

            foreach (var pair in meta.Names.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var saveName = NormalizeSaveName(pair.Value);
                if (saveName == null) continue;
                var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(saveName));
                lines.Add("name=" + pair.Key + ":" + base64);
            }

            File.WriteAllLines(GetMetaFileName(login), lines, Encoding.UTF8);
        }

        private string GetSlotFileName(string login, bool isAuto, int slot)
        {
            return isAuto ? GetAutoFileName(login, slot) : GetManualFileName(login, slot);
        }

        private static byte[] ReadAndDecodeSaveFile(string fileName)
        {
            var info = new FileInfo(fileName);
            if (!info.Exists || info.Length < 10) return null;

            bool readAsXml;
            using (var file = File.OpenRead(fileName))
            {
                var buff = new byte[10];
                file.Read(buff, 0, 10);
                readAsXml = Encoding.ASCII.GetString(buff, 0, 10).Contains("<?xml");
            }

            var saveFileData = File.ReadAllBytes(fileName);
            return readAsXml ? saveFileData : GZip.UnzipByteByte(saveFileData);
        }

        private byte[] LoadSlotData(string login, bool isAuto, int slot)
        {
            var safeSlot = isAuto ? NormalizeAutoSlot(slot) : NormalizeManualSlot(slot);
            return ReadAndDecodeSaveFile(GetSlotFileName(login, isAuto, safeSlot));
        }

        private static string GetDefaultSlotName(bool isAuto, int slot)
        {
            return isAuto ? "Автослот " + slot.ToString() : "Слот " + slot.ToString();
        }

        private bool TryGetNewestSave(string login, out bool isAuto, out int slot)
        {
            isAuto = false;
            slot = 1;
            var bestDate = DateTime.MinValue;
            var found = false;

            for (int i = 1; i <= CountSaveDataPlayer; i++)
            {
                var info = new FileInfo(GetManualFileName(login, i));
                if (!info.Exists || info.Length < 10) continue;
                if (!found || info.LastWriteTimeUtc > bestDate)
                {
                    found = true;
                    bestDate = info.LastWriteTimeUtc;
                    isAuto = false;
                    slot = i;
                }
            }

            for (int i = 1; i <= CountAutoSaveDataPlayer; i++)
            {
                var info = new FileInfo(GetAutoFileName(login, i));
                if (!info.Exists || info.Length < 10) continue;
                if (!found || info.LastWriteTimeUtc > bestDate)
                {
                    found = true;
                    bestDate = info.LastWriteTimeUtc;
                    isAuto = true;
                    slot = i;
                }
            }

            return found;
        }

        /// <summary>
        /// Получить данные по ручному слоту сохранения пользователя.
        /// </summary>
        public byte[] LoadPlayerData(string login, int numberSave)
        {
            if (numberSave < 1 || numberSave > CountSaveDataPlayer) return null;
            MigrateLegacySaveFileIfNeeded(login);

            var data = LoadSlotData(login, false, numberSave);
            if (data != null) return data;

            for (int slot = 1; slot <= CountSaveDataPlayer; slot++)
            {
                if (slot == numberSave) continue;
                data = LoadSlotData(login, false, slot);
                if (data != null) return data;
            }
            return null;
        }

        public byte[] LoadActivePlayerData(string login)
        {
            MigrateLegacySaveFileIfNeeded(login);
            var meta = ReadMeta(login);

            if (meta.LastKind == SaveKind.Auto && CountAutoSaveDataPlayer > 0)
            {
                var autoData = LoadSlotData(login, true, meta.ActiveAutoSlot);
                if (autoData != null) return autoData;
            }
            else
            {
                var manualData = LoadSlotData(login, false, meta.ActiveManualSlot);
                if (manualData != null) return manualData;
            }

            if (TryGetNewestSave(login, out var isAuto, out var slot))
            {
                return LoadSlotData(login, isAuto, slot);
            }

            return null;
        }

        public int GetActiveSaveSlot(string login)
        {
            MigrateLegacySaveFileIfNeeded(login);
            var meta = ReadMeta(login);
            var slot = NormalizeManualSlot(meta.ActiveManualSlot);
            if (LoadSlotData(login, false, slot) != null) return slot;

            for (int i = 1; i <= CountSaveDataPlayer; i++)
                if (LoadSlotData(login, false, i) != null) return i;

            return slot;
        }

        public int GetActiveAutoSaveSlot(string login)
        {
            if (CountAutoSaveDataPlayer <= 0) return 1;
            MigrateLegacySaveFileIfNeeded(login);
            var meta = ReadMeta(login);
            var slot = NormalizeAutoSlot(meta.ActiveAutoSlot);
            if (LoadSlotData(login, true, slot) != null) return slot;

            for (int i = 1; i <= CountAutoSaveDataPlayer; i++)
                if (LoadSlotData(login, true, i) != null) return i;

            return slot;
        }

        public bool SetActiveSaveSlot(string login, int slot, bool isAuto, out string message)
        {
            MigrateLegacySaveFileIfNeeded(login);

            if (isAuto && CountAutoSaveDataPlayer <= 0)
            {
                message = "Автослоты отключены в настройках сервера.";
                return false;
            }

            var safeSlot = isAuto ? NormalizeAutoSlot(slot) : NormalizeManualSlot(slot);
            var slotData = LoadSlotData(login, isAuto, safeSlot);
            if (slotData == null || slotData.Length < 10)
            {
                message = "Выбранный слот пуст.";
                return false;
            }

            var meta = ReadMeta(login);
            if (isAuto)
            {
                meta.ActiveAutoSlot = safeSlot;
                meta.LastKind = SaveKind.Auto;
            }
            else
            {
                meta.ActiveManualSlot = safeSlot;
                meta.LastKind = SaveKind.Manual;
            }
            WriteMeta(login, meta);

            message = isAuto
                ? "Активный автослот установлен: #" + safeSlot.ToString() + "."
                : "Активный ручной слот установлен: #" + safeSlot.ToString() + ".";
            return true;
        }

        public bool RenamePlayerSaveSlot(string login, int slot, bool isAuto, string name, out string message)
        {
            if (isAuto && CountAutoSaveDataPlayer <= 0)
            {
                message = "Автослоты отключены в настройках сервера.";
                return false;
            }

            var saveName = NormalizeSaveName(name);
            var safeSlot = isAuto ? NormalizeAutoSlot(slot) : NormalizeManualSlot(slot);
            var key = MakeNameKey(isAuto, safeSlot);
            var meta = ReadMeta(login);

            if (saveName == null)
            {
                meta.Names.Remove(key);
                message = "Название очищено.";
            }
            else
            {
                meta.Names[key] = saveName;
                message = "Название сохранено.";
            }

            WriteMeta(login, meta);
            return true;
        }

        public ModelPlayerSaveResponse GetPlayerSaves(string login)
        {
            MigrateLegacySaveFileIfNeeded(login);
            var meta = ReadMeta(login);

            var response = new ModelPlayerSaveResponse()
            {
                Status = 0,
                Message = "OK",
                MaxSlots = CountSaveDataPlayer,
                ActiveSlot = GetActiveSaveSlot(login),
                ManualSlotsCount = CountSaveDataPlayer,
                AutoSlotsCount = CountAutoSaveDataPlayer,
                ActiveAutoSlot = GetActiveAutoSaveSlot(login),
            };

            for (int slot = 1; slot <= CountSaveDataPlayer; slot++)
            {
                var fileName = GetManualFileName(login, slot);
                var info = new FileInfo(fileName);
                var exists = info.Exists && info.Length >= 10;
                var key = MakeNameKey(false, slot);
                if (!meta.Names.TryGetValue(key, out var name) || string.IsNullOrWhiteSpace(name))
                    name = GetDefaultSlotName(false, slot);

                response.Saves.Add(new ModelPlayerSaveSlot()
                {
                    Slot = slot,
                    IsAuto = false,
                    Name = name,
                    Exists = exists,
                    LastWriteTimeUtc = exists ? info.LastWriteTimeUtc : DateTime.MinValue,
                    SizeBytes = exists ? info.Length : 0
                });
            }

            for (int slot = 1; slot <= CountAutoSaveDataPlayer; slot++)
            {
                var fileName = GetAutoFileName(login, slot);
                var info = new FileInfo(fileName);
                var exists = info.Exists && info.Length >= 10;
                var key = MakeNameKey(true, slot);
                if (!meta.Names.TryGetValue(key, out var name) || string.IsNullOrWhiteSpace(name))
                    name = GetDefaultSlotName(true, slot);

                response.Saves.Add(new ModelPlayerSaveSlot()
                {
                    Slot = slot,
                    IsAuto = true,
                    Name = name,
                    Exists = exists,
                    LastWriteTimeUtc = exists ? info.LastWriteTimeUtc : DateTime.MinValue,
                    SizeBytes = exists ? info.Length : 0
                });
            }

            return response;
        }

        /// <summary>
        /// Сохраняем игровые данные игрока в ручной слот 1.
        /// </summary>
        public void SavePlayerData(string login, byte[] data, bool single)
        {
            SavePlayerData(login, data, single, 1, false);
        }

        /// <summary>
        /// Сохраняем игровые данные игрока в ручной слот.
        /// </summary>
        public void SavePlayerData(string login, byte[] data, bool single, int saveNumber)
        {
            SavePlayerData(login, data, single, saveNumber, false);
        }

        /// <summary>
        /// Сохраняем игровые данные игрока в выбранный ручной или авто слот.
        /// </summary>
        /// <param name="saveNumber">
        /// Для ручного режима: номер слота от 1 до N.
        /// Для авто режима: номер автослота от 1 до M, если 0 — используется фоновая ротация.
        /// </param>
        /// <param name="saveIsAuto">Истина для автослотов.</param>
        public void SavePlayerData(string login, byte[] data, bool single, int saveNumber, bool saveIsAuto)
        {
            if (data == null || data.Length < 10) return;
            MigrateLegacySaveFileIfNeeded(login);

            if (saveIsAuto && CountAutoSaveDataPlayer <= 0)
            {
                Loger.Log("Server User " + login + " автосохранение пропущено: автослоты отключены.");
                return;
            }

            var meta = ReadMeta(login);
            var requestedSlot = saveNumber;
            int slot;
            if (saveIsAuto)
            {
                slot = requestedSlot > 0 ? NormalizeAutoSlot(requestedSlot) : NormalizeAutoSlot(meta.NextAutoSlot);
            }
            else
            {
                slot = NormalizeManualSlot(requestedSlot);
            }

            var fileName = GetSlotFileName(login, saveIsAuto, slot);
            var logSlotType = saveIsAuto ? "auto" : "manual";

            try
            {
                var lastData = LoadSlotData(login, saveIsAuto, slot);
                if (lastData != null
                    && lastData.Length == data.Length
                    && lastData.SequenceEqual(data))
                {
                    Loger.Log("Server User " + Path.GetFileNameWithoutExtension(fileName)
                        + " пропуск сохранения (без изменений, " + logSlotType + ").");
                    return;
                }
            }
            catch (Exception ex)
            {
                Loger.Log("Server SavePlayerData ошибка сравнения, продолжаем сохранение: " + ex.Message);
            }

            if (!saveIsAuto && single)
            {
                for (int i = 1; i <= CountSaveDataPlayer; i++)
                {
                    if (i == slot) continue;
                    DeleteFileAndBackup(GetManualFileName(login, i));
                }
            }

            if (File.Exists(fileName))
            {
                DeleteFileAndBackup(fileName);
            }

            var dataToSave = GZip.ZipByteByte(data);
            File.WriteAllBytes(fileName, dataToSave);

            if (saveIsAuto)
            {
                meta.ActiveAutoSlot = slot;
                // Не меняем LastKind при сохранении.
                // LastKind переключается только через SetActiveSaveSlot (явный выбор игроком слота загрузки),
                // чтобы фоновые автосейвы не сбивали выбранный для входа слот.
                if (CountAutoSaveDataPlayer > 0)
                {
                    meta.NextAutoSlot = slot % CountAutoSaveDataPlayer + 1;
                }
            }
            else
            {
                meta.ActiveManualSlot = slot;
                // См. комментарий выше: сохранение не должно менять выбор слота загрузки.
            }
            WriteMeta(login, meta);

            Loger.Log("Server User " + Path.GetFileNameWithoutExtension(fileName) + " saved (" + logSlotType + ").");
        }

        /// <summary>
        /// Удаляем файл, но перед этим сохраняем его копию так, чтобы былка копия более 12 часов назад
        /// </summary>
        private void DeleteFileAndBackup(string fileName)
        {
            var fi = new FileInfo(fileName);
            if (!fi.Exists) return;
            if ((DateTime.UtcNow - fi.LastWriteTimeUtc).TotalHours < 12)
            {
                fi.Delete();
                return;
            }
            var backupFileName = fileName;
            var d1 = new FileInfo(backupFileName + ".day1");
            var d2 = new FileInfo(backupFileName + ".day2");
            if (!d1.Exists || (fi.LastWriteTimeUtc - d1.LastWriteTimeUtc).TotalHours > 12)
            {
                //нужно записать файл fileName в fileName.day1
                if (d1.Exists)
                {
                    //разбираемся с существующим fileName.day1
                    if (!d2.Exists || (d1.LastWriteTimeUtc - d2.LastWriteTimeUtc).TotalHours > 12)
                    {
                        //нужно записать файл fileName.day1 в fileName.day2
                        if (d2.Exists)
                        {
                            //не разбираемся с существующим fileName.day2
                            d2.Delete();
                        }
                        File.Move(d1.FullName, d2.FullName);
                    }
                    else
                    {
                        d1.Delete();
                    }
                }
                File.Move(fi.FullName, d1.FullName);
            }
            else
            {
                fi.Delete();
            }
        }

        public void DeletePlayerData(string login)
        {
            for (int num = 1; num <= CountSaveDataPlayer; num++)
            {
                var fileName = GetManualFileName(login, num);
                if (!File.Exists(fileName)) continue;
                if (num == 1)
                {
                    var backupFileName = fileName + ".bak";
                    if (File.Exists(backupFileName)) File.Delete(backupFileName);
                    File.Move(fileName, backupFileName);
                }
                else
                {
                    File.Delete(fileName);
                }
            }

            for (int num = 1; num <= CountAutoSaveDataPlayer; num++)
            {
                var fileName = GetAutoFileName(login, num);
                if (File.Exists(fileName)) File.Delete(fileName);
            }

            var metaFileName = GetMetaFileName(login);
            if (File.Exists(metaFileName)) File.Delete(metaFileName);
        }

        /// <summary>
        /// Возвращает список доступных файлов сохранений игрока (ручных и авто) в виде дат.
        /// </summary>
        public List<string> GetListPlayerDatas(string login)
        {
            MigrateLegacySaveFileIfNeeded(login);
            var result = new List<string>();

            for (int num = 1; num <= CountSaveDataPlayer; num++)
            {
                var info = new FileInfo(GetManualFileName(login, num));
                if (!info.Exists || info.Length < 10) continue;
                result.Add(info.LastWriteTime.ToString("yyyy-MM-dd"));
            }

            for (int num = 1; num <= CountAutoSaveDataPlayer; num++)
            {
                var info = new FileInfo(GetAutoFileName(login, num));
                if (!info.Exists || info.Length < 10) continue;
                result.Add(info.LastWriteTime.ToString("yyyy-MM-dd"));
            }

            return result;
        }
    }
}
