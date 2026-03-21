using Model;
using System;
using System.Collections.Generic;

namespace ServerOnlineCity.Model
{
    [Serializable]
    public class GuildOrder
    {
        public long Id { get; set; }

        /// <summary>
        /// Логин владельца заказа.
        /// </summary>
        public string OwnerLogin { get; set; }

        /// <summary>
        /// Тайл, с которого был создан заказ.
        /// </summary>
        public int Tile { get; set; }

        /// <summary>
        /// Предметы на продажу.
        /// </summary>
        public List<ThingTrade> Things { get; set; }

        /// <summary>
        /// Запрошенная цена в безнале.
        /// </summary>
        public int Price { get; set; }

        /// <summary>
        /// Комиссия гильдии (в безнале), уже оплачена при создании.
        /// </summary>
        public int Fee { get; set; }

        /// <summary>
        /// Время создания заказа (UTC).
        /// </summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>
        /// Время истечения заказа (UTC). После этого предметы возвращаются владельцу.
        /// </summary>
        public DateTime ExpiresAtUtc { get; set; }

        /// <summary>
        /// Статус: Active, Sold, Expired, Cancelled.
        /// </summary>
        public GuildOrderStatus Status { get; set; }

        /// <summary>
        /// Логин покупателя (заполняется при продаже).
        /// </summary>
        public string BuyerLogin { get; set; }

        /// <summary>
        /// Количество попыток предложения NPC-торговцем.
        /// </summary>
        public int OfferAttempts { get; set; }

        /// <summary>
        /// Последнее время попытки предложения.
        /// </summary>
        public DateTime LastOfferAtUtc { get; set; }

        public override string ToString()
        {
            return $"GuildOrder#{Id} owner={OwnerLogin} price={Price} fee={Fee} status={Status} things={Things?.Count ?? 0}";
        }
    }

    public enum GuildOrderStatus
    {
        Active = 0,
        Sold = 1,
        Expired = 2,
        Cancelled = 3
    }
}
