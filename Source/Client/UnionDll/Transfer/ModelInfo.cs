using Model;
using OCUnion;
using System;

namespace Transfer
{
    [Serializable]
    public class ModelInfo
    {
        public Player My { get; set; }
        public bool IsAdmin { get; set; }
        public string VersionInfo { get; set; }
        public long VersionNum { get; set; }
        public string ServerName { get; set; }
        /// <summary>
        /// Будет ли выполняться проверка хеша файлов на клиенте
        /// </summary>
        public bool IsModsWhitelisted { get; set; }

        public bool NeedCreateWorld { get; set; }

        public string Seed { get; set; }
        public string ScenarioName { get; set; }
        public string Storyteller { get; set; }
        public int MapSize { get; set; }
        public float PlanetCoverage { get; set; }
        public string Difficulty { get; set; }
        public int DelaySaveGame { get; set; }
        public bool DisableDevMode { get; set; }
        public int MinutesIntervalBetweenPVP { get; set; }
        public bool EnableFileLog { get; set; }
        public DateTime TimeChangeEnablePVP { get; set; }
        public bool ProtectingNovice { get; set; }
        public ServerGeneralSettings GeneralSettings { get; set; }
        /// <summary>
        /// Максимальное число серверных слотов сохранения для игрока.
        /// </summary>
        public int SaveSlotsCount { get; set; } = 1;
        /// <summary>
        /// Количество ручных слотов сохранения (N).
        /// </summary>
        public int ManualSaveSlotsCount { get; set; } = 1;
        /// <summary>
        /// Количество автослотов сохранения (M).
        /// </summary>
        public int AutoSaveSlotsCount { get; set; } = 0;
        /// <summary>
        /// Активный серверный слот сохранения (от 1).
        /// </summary>
        public int ActiveSaveSlot { get; set; } = 1;
        /// <summary>
        /// Активный автослот сохранения (от 1).
        /// </summary>
        public int ActiveAutoSaveSlot { get; set; } = 1;

        public DateTime ServerTime { get; set; }

        /// <summary>
        /// Описание сервера, которые читается с настроек, используется в дискорд боте
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Заполняется только при WorldLoad() GetInfo = 3
        /// </summary>
        public byte[] SaveFileData { get; set; }

    }
}
