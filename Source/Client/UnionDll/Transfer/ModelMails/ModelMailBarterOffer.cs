using Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Transfer.ModelMails
{
    /// <summary>
    /// Предложение бартера через приватный ордер.
    /// </summary>
    [Serializable]
    public class ModelMailBarterOffer : ModelMail, IModelPlace
    {
        /// <summary>
        /// Идентификатор ордера на сервере.
        /// </summary>
        public long OrderId { get; set; }

        public int Tile { get; set; }
        public long PlaceServerId { get; set; }

        /// <summary>
        /// Сколько повторов сделки доступно к выкупу.
        /// </summary>
        public int CountReady { get; set; }

        /// <summary>
        /// Что владелец ордера отдает.
        /// </summary>
        public List<ThingTrade> SellThings { get; set; }

        /// <summary>
        /// Что владелец ордера получает.
        /// </summary>
        public List<ThingTrade> BuyThings { get; set; }

        public override string GetHash()
        {
            return $"O{OrderId}T{Tile}P{PlaceServerId}R{CountReady} " + ContentString();
        }

        public override string ContentString()
        {
            var sell = SellThings == null ? "" : SellThings.ToStringLabel();
            var buy = BuyThings == null ? "" : BuyThings.ToStringLabel();
            return $"sell:{sell} buy:{buy}";
        }
    }
}
