using Model;
using OCUnion;
using OCUnion.Transfer.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Transfer.ModelMails;

namespace Transfer
{
    [Serializable]
    public class ModelPlayToClient
    {
        //public long TypeInfo { get; set; }
        public DateTime UpdateTime { get; set; }
        public List<WorldObjectEntry> WObjects { get; set; }
        public List<WorldObjectEntry> WObjectsToDelete { get; set; }
        public List<TradeWorldObjectEntry> WTObjects { get; set; }
        public List<TradeWorldObjectEntry> WTObjectsToDelete { get; set; }
        public List<ModelMail> Mails { get; set; }
        public List<Player> PlayersInfo { get; set; }
        public float CashlessBalance { get; set; }
        public float StorageBalance { get; set; }
        public bool AreAttacking { get; set; }
        /// <summary>
        /// Логин инициатора входящего visit-sync запроса (если есть).
        /// </summary>
        public string IncomingVisitAttackerLogin { get; set; }
        public bool NeedSaveAndExit { get; set; }
        /// <summary>
        /// Эхо идентификатора чанковой передачи сейва.
        /// </summary>
        public string SaveTransferId { get; set; }
        /// <summary>
        /// Сколько байт сейва принято сервером в рамках текущего transferId.
        /// </summary>
        public int SaveTransferAcceptedOffset { get; set; }
        /// <summary>
        /// Полный размер сейва для текущего transferId.
        /// </summary>
        public int SaveTransferTotalLength { get; set; }
        /// <summary>
        /// Истина, если сервер завершил сборку и запись сейва.
        /// </summary>
        public bool SaveTransferCompleted { get; set; }
        /// <summary>
        /// Текст ошибки чанковой передачи (если была).
        /// </summary>
        public string SaveTransferError { get; set; }
        public string KeyReconnect { get; set; }
        public List<WorldObjectOnline> WObjectOnlineList { get; set; }
        public List<WorldObjectOnline> WObjectOnlineToAdd { get; set; }
        public List<WorldObjectOnline> WObjectOnlineToDelete { get; set; }

        public List<FactionOnline> FactionOnlineList { get; set; }
        public List<FactionOnline> FactionOnlineToAdd { get; set; }
        public List<FactionOnline> FactionOnlineToDelete { get; set; }
        public List<StateInfo> States { get; set; }
    }
}
