using Microsoft.VisualStudio.TestTools.UnitTesting;
using ServerCore.Model;
using ServerOnlineCity;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ServerOnlineCity.Tests
{
    [TestClass]
    public class RepositorySaveDataTests
    {
        private string _tempRoot;
        private Repository _repository;
        private RepositorySaveData _saveData;
        private const string Login = "slot_user";

        [TestInitialize]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "OnlineCity_SaveSlots_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);

            ServerManager.ServerSettings = new ServerSettings()
            {
                CountSaveDataPlayer = 2,
                CountAutoSaveDataPlayer = 3
            };

            _repository = new Repository();
            _repository.SaveFileName = Path.Combine(_tempRoot, "World.dat");
            Directory.CreateDirectory(_repository.SaveFolderDataPlayers);

            _saveData = new RepositorySaveData(_repository);
        }

        [TestCleanup]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_tempRoot) && Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }

        [TestMethod]
        public void RenamePlayerSaveSlot_PersistsManualAndAutoNames()
        {
            // Подготавливаем заполненные ручной и авто-слоты.
            _saveData.SavePlayerData(Login, BuildSaveData("manual-slot-1"), false, 1, false);
            _saveData.SavePlayerData(Login, BuildSaveData("auto-slot-1"), false, 1, true);

            var renameManual = _saveData.RenamePlayerSaveSlot(Login, 1, false, "База-основа", out _);
            var renameAuto = _saveData.RenamePlayerSaveSlot(Login, 1, true, "Авто-цикл-1", out _);

            Assert.IsTrue(renameManual, "Переименование ручного слота должно быть успешным.");
            Assert.IsTrue(renameAuto, "Переименование авто-слота должно быть успешным.");

            var response = _saveData.GetPlayerSaves(Login);
            var manualSlot = response.Saves.Single(s => !s.IsAuto && s.Slot == 1);
            var autoSlot = response.Saves.Single(s => s.IsAuto && s.Slot == 1);

            Assert.AreEqual("База-основа", manualSlot.Name, "Имя ручного слота должно читаться из meta-файла.");
            Assert.AreEqual("Авто-цикл-1", autoSlot.Name, "Имя авто-слота должно читаться из meta-файла.");
        }

        [TestMethod]
        public void SavePlayerData_BackgroundAutoRotation_DoesNotOverwriteManualSlots()
        {
            // Ручной слот игрока, который не должен перезаписываться фоном.
            var manualData = BuildSaveData("manual-slot-2");
            _saveData.SavePlayerData(Login, manualData, false, 2, false);

            // Четыре фоновых автосохранения при M=3 должны дать ротацию 1->2->3->1.
            _saveData.SavePlayerData(Login, BuildSaveData("auto-1"), false, 0, true);
            _saveData.SavePlayerData(Login, BuildSaveData("auto-2"), false, 0, true);
            _saveData.SavePlayerData(Login, BuildSaveData("auto-3"), false, 0, true);
            _saveData.SavePlayerData(Login, BuildSaveData("auto-4"), false, 0, true);

            var responseAfterRotation = _saveData.GetPlayerSaves(Login);
            Assert.AreEqual(1, responseAfterRotation.ActiveAutoSlot, "Активный авто-слот после 4-го автосейва должен быть A#1.");

            CollectionAssert.AreEqual(manualData, _saveData.LoadPlayerData(Login, 2),
                "Фоновая авто-ротация не должна перезаписывать ручной слот.");
            Assert.IsTrue(_saveData.SetActiveSaveSlot(Login, 1, true, out _), "Слот A#1 должен быть доступен для чтения.");
            CollectionAssert.AreEqual(BuildSaveData("auto-4"), _saveData.LoadActivePlayerData(Login),
                "После 4-го автосейва слот A#1 должен содержать последние данные.");
            Assert.IsTrue(_saveData.SetActiveSaveSlot(Login, 2, true, out _), "Слот A#2 должен быть доступен для чтения.");
            CollectionAssert.AreEqual(BuildSaveData("auto-2"), _saveData.LoadActivePlayerData(Login),
                "Слот A#2 должен содержать данные второго автосейва.");
            Assert.IsTrue(_saveData.SetActiveSaveSlot(Login, 3, true, out _), "Слот A#3 должен быть доступен для чтения.");
            CollectionAssert.AreEqual(BuildSaveData("auto-3"), _saveData.LoadActivePlayerData(Login),
                "Слот A#3 должен содержать данные третьего автосейва.");
        }

        [TestMethod]
        public void SavePlayerData_DoesNotOverrideChosenLoadSlotKind()
        {
            _saveData.SavePlayerData(Login, BuildSaveData("manual-a"), false, 1, false);
            _saveData.SavePlayerData(Login, BuildSaveData("auto-a"), false, 1, true);

            Assert.IsTrue(_saveData.SetActiveSaveSlot(Login, 1, false, out _), "Должен установиться активный ручной слот.");
            _saveData.SavePlayerData(Login, BuildSaveData("auto-b"), false, 1, true);
            CollectionAssert.AreEqual(BuildSaveData("manual-a"), _saveData.LoadActivePlayerData(Login),
                "Автосохранение не должно менять выбранный вручную тип слота загрузки.");

            Assert.IsTrue(_saveData.SetActiveSaveSlot(Login, 1, true, out _), "Должен установиться активный автослот.");
            _saveData.SavePlayerData(Login, BuildSaveData("manual-b"), false, 1, false);
            CollectionAssert.AreEqual(BuildSaveData("auto-b"), _saveData.LoadActivePlayerData(Login),
                "Ручное сохранение не должно менять выбранный автослот загрузки.");
        }

        private static byte[] BuildSaveData(string marker)
        {
            // Данные похожи на XML-сейв и гарантированно больше 10 байт.
            return Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><save marker=\"{marker}\"/>");
        }
    }
}
