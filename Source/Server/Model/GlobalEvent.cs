using System;

namespace ServerOnlineCity.Model
{
    /// <summary>
    /// Типы глобальных событий мира.
    /// </summary>
    public enum GlobalEventType
    {
        None = 0,

        /// <summary>
        /// Неделя торговли: комиссия MerchantGuild снижена на 50%.
        /// </summary>
        TradeWeek = 1,

        /// <summary>
        /// Нашествие: рассказчик усиленно спавнит военные объекты.
        /// </summary>
        Invasion = 2,

        /// <summary>
        /// Перемирие: CallIncident заблокирован.
        /// </summary>
        Truce = 3,
    }

    /// <summary>
    /// Активное глобальное событие мира. Хранится в BaseContainer.
    /// </summary>
    [Serializable]
    public class GlobalEvent
    {
        public GlobalEventType Type { get; set; }

        /// <summary>
        /// Время начала события (UTC).
        /// </summary>
        public DateTime StartsAtUtc { get; set; }

        /// <summary>
        /// Время окончания события (UTC).
        /// </summary>
        public DateTime EndsAtUtc { get; set; }

        /// <summary>
        /// True если событие ещё активно.
        /// </summary>
        public bool IsActive(DateTime now) => Type != GlobalEventType.None && now < EndsAtUtc;

        public override string ToString()
            => $"GlobalEvent {Type} [{StartsAtUtc:g} - {EndsAtUtc:g}]";
    }
}
