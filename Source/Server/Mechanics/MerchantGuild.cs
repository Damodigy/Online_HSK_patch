using Model;
using OCUnion;
using ServerOnlineCity.Common;
using ServerOnlineCity.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using Transfer;
using Transfer.ModelMails;

namespace ServerOnlineCity.Mechanics
{
    /// <summary>
    /// Гильдия торговцев — NPC-торговцы предлагают предметы игроков другим онлайн-игрокам.
    /// </summary>
    internal static class MerchantGuild
    {
        private static readonly Random Rnd = new Random();

        /// <summary>
        /// Процент комиссии гильдии от запрошенной цены.
        /// </summary>
        private static int GetFeePercent() => 15;

        /// <summary>
        /// Время жизни заказа в минутах (7 дней).
        /// </summary>
        private static int GetOrderTtlMinutes() => 7 * 24 * 60;

        /// <summary>
        /// Минимальная пауза между попытками предложения одного заказа (минуты).
        /// </summary>
        private static int GetOfferCooldownMinutes() => 60;

        /// <summary>
        /// Шанс (%) что в данный тик будет попытка предложения.
        /// </summary>
        private static int GetOfferChancePercent() => 25;

        /// <summary>
        /// Максимальное количество попыток предложения до автоотмены.
        /// </summary>
        private static int GetMaxOfferAttempts() => 50;

        /// <summary>
        /// Вызывается из основного тика рассказчика.
        /// </summary>
        public static void Tick(BaseContainer data)
        {
            if (data == null) return;
            if (data.GuildOrders == null) data.GuildOrders = new List<GuildOrder>();

            var now = DateTime.UtcNow;

            // Обработка истёкших заказов
            ProcessExpiredOrders(data, now);

            // Попытка предложить товар случайному онлайн-игроку
            TryOfferToPlayers(data, now);
        }

        /// <summary>
        /// Создать новый заказ гильдии.
        /// </summary>
        public static GuildOrderCreateResult CreateOrder(BaseContainer data, PlayerServer owner, int tile, List<ThingTrade> things, int price)
        {
            if (data == null || owner == null || things == null || things.Count == 0 || price <= 0)
                return new GuildOrderCreateResult { Success = false, Message = "Invalid parameters" };

            var feePercent = GetFeePercent();

            // Скидка за репутацию владельца (0..5 процентных пунктов)
            var reputationDiscount = owner.GetReputationFeeDiscount();
            feePercent = Math.Max(0, feePercent - reputationDiscount);

            // Событие «Неделя торговли»: комиссия вдвое меньше
            if (data.ActiveGlobalEvent?.Type == GlobalEventType.TradeWeek
                && data.ActiveGlobalEvent.IsActive(DateTime.UtcNow))
                feePercent = Math.Max(0, feePercent / 2);

            var fee = Math.Max(1, price * feePercent / 100);

            // Проверяем и списываем комиссию
            if (owner.CashlessBalance < fee)
                return new GuildOrderCreateResult { Success = false, Message = $"Not enough balance for fee ({fee})" };

            // Изымаем предметы из торгового хранилища
            var taken = data.OrderOperator.GetFromStorage(tile, owner, things);
            if (taken == null)
                return new GuildOrderCreateResult { Success = false, Message = "Items not found in storage" };

            owner.CashlessBalance -= fee;

            var order = new GuildOrder
            {
                Id = ++data.MaxGuildOrderId,
                OwnerLogin = owner.Public.Login,
                Tile = tile,
                Things = things.Select(t => { var c = (ThingTrade)t.Clone(); return c; }).ToList(),
                Price = price,
                Fee = fee,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(GetOrderTtlMinutes()),
                Status = GuildOrderStatus.Active,
                OfferAttempts = 0,
                LastOfferAtUtc = DateTime.MinValue
            };

            if (data.GuildOrders == null) data.GuildOrders = new List<GuildOrder>();
            data.GuildOrders.Add(order);

            Loger.Log($"MerchantGuild: Created order {order}", Loger.LogLevel.EXCHANGE);

            HelperMailMessadge.Send(
                data.PlayerSystem
                , owner
                , "OC_MerchantGuild_OrderCreated"
                , $"OC_MerchantGuild_OrderCreatedText {price} OC_MerchantGuild_Fee {fee} OC_MerchantGuild_Expires {order.ExpiresAtUtc:g}"
                , ModelMailMessadge.MessadgeTypes.GoldenLetter
                , tile);

            return new GuildOrderCreateResult { Success = true, OrderId = order.Id, Fee = fee };
        }

        /// <summary>
        /// Игрок принимает предложение гильдии.
        /// </summary>
        public static bool AcceptOffer(BaseContainer data, PlayerServer buyer, long orderId)
        {
            if (data?.GuildOrders == null) return false;

            var order = data.GuildOrders.FirstOrDefault(o => o.Id == orderId && o.Status == GuildOrderStatus.Active);
            if (order == null) return false;
            if (buyer.Public.Login == order.OwnerLogin) return false;

            // Списываем цену с покупателя
            if (buyer.CashlessBalance < order.Price) return false;
            buyer.CashlessBalance -= order.Price;

            // Отправляем предметы покупателю в хранилище на его ближайшем тайле
            var buyerTile = GetPlayerSettlementTile(buyer);
            if (buyerTile <= 0) buyerTile = order.Tile;
            data.OrderOperator.SendToStorage(buyerTile, buyer, order.Things);

            // Зачисляем деньги владельцу (цена минус уже оплаченной комиссии)
            var ownerPlayer = Repository.GetPlayerByLogin(order.OwnerLogin);
            if (ownerPlayer != null)
            {
                ownerPlayer.CashlessBalance += order.Price;

                HelperMailMessadge.Send(
                    data.PlayerSystem
                    , ownerPlayer
                    , "OC_MerchantGuild_Sold"
                    , $"OC_MerchantGuild_SoldText {order.Price} OC_MerchantGuild_Buyer {buyer.Public.Login}"
                    , ModelMailMessadge.MessadgeTypes.GoldenLetter
                    , order.Tile);
            }

            // Уведомляем покупателя
            HelperMailMessadge.Send(
                data.PlayerSystem
                , buyer
                , "OC_MerchantGuild_Bought"
                , $"OC_MerchantGuild_BoughtText {order.Things?.Count ?? 0} OC_MerchantGuild_AtTile {buyerTile} OC_MerchantGuild_Price {order.Price}"
                , ModelMailMessadge.MessadgeTypes.GoldenLetter
                , buyerTile);

            order.Status = GuildOrderStatus.Sold;
            order.BuyerLogin = buyer.Public.Login;

            Loger.Log($"MerchantGuild: Order sold {order} buyer={buyer.Public.Login}", Loger.LogLevel.EXCHANGE);

            return true;
        }

        /// <summary>
        /// Отмена заказа владельцем. Предметы возвращаются.
        /// </summary>
        public static bool CancelOrder(BaseContainer data, PlayerServer owner, long orderId)
        {
            if (data?.GuildOrders == null) return false;

            var order = data.GuildOrders.FirstOrDefault(o => o.Id == orderId
                && o.Status == GuildOrderStatus.Active
                && o.OwnerLogin == owner.Public.Login);
            if (order == null) return false;

            // Возвращаем предметы
            data.OrderOperator.SendToStorage(order.Tile, owner, order.Things);
            // Комиссия не возвращается
            order.Status = GuildOrderStatus.Cancelled;

            Loger.Log($"MerchantGuild: Order cancelled {order}", Loger.LogLevel.EXCHANGE);

            return true;
        }

        /// <summary>
        /// Получить список активных заказов гильдии.
        /// </summary>
        public static List<GuildOrder> GetActiveOrders(BaseContainer data)
        {
            return data?.GuildOrders?.Where(o => o.Status == GuildOrderStatus.Active).ToList()
                ?? new List<GuildOrder>();
        }

        private static void ProcessExpiredOrders(BaseContainer data, DateTime now)
        {
            var expired = data.GuildOrders
                .Where(o => o.Status == GuildOrderStatus.Active && o.ExpiresAtUtc <= now)
                .ToList();

            foreach (var order in expired)
            {
                order.Status = GuildOrderStatus.Expired;

                var ownerPlayer = Repository.GetPlayerByLogin(order.OwnerLogin);
                if (ownerPlayer != null)
                {
                    data.OrderOperator.SendToStorage(order.Tile, ownerPlayer, order.Things);

                    HelperMailMessadge.Send(
                        data.PlayerSystem
                        , ownerPlayer
                        , "OC_MerchantGuild_Expired"
                        , "OC_MerchantGuild_ExpiredText"
                        , ModelMailMessadge.MessadgeTypes.Neutral
                        , order.Tile);
                }

                Loger.Log($"MerchantGuild: Order expired {order}", Loger.LogLevel.EXCHANGE);
            }
        }

        private static void TryOfferToPlayers(BaseContainer data, DateTime now)
        {
            if (Rnd.Next(100) >= GetOfferChancePercent()) return;

            var active = data.GuildOrders
                .Where(o => o.Status == GuildOrderStatus.Active
                    && o.OfferAttempts < GetMaxOfferAttempts()
                    && (now - o.LastOfferAtUtc).TotalMinutes >= GetOfferCooldownMinutes())
                .ToList();
            if (active.Count == 0) return;

            var order = active[Rnd.Next(active.Count)];

            // Находим онлайн-игроков (кроме владельца)
            var onlinePlayers = data.GetPlayersAll?
                .Where(p => p?.Public?.Login != null
                    && p.Public.Login != order.OwnerLogin
                    && p.Public.Login != "system"
                    && p.LastSaveReceivedUtc > now.AddMinutes(-30))
                .ToList();

            if (onlinePlayers == null || onlinePlayers.Count == 0) return;

            var target = onlinePlayers[Rnd.Next(onlinePlayers.Count)];

            order.OfferAttempts++;
            order.LastOfferAtUtc = now;

            // Отправляем предложение через письмо
            var thingsLabel = order.Things?.Select(t => $"{t.Name ?? t.DefName} x{t.Count}").ToList();
            var thingsText = thingsLabel != null ? string.Join(", ", thingsLabel) : "?";

            var mail = new ModelMailMessadge()
            {
                From = data.PlayerSystem.Public,
                To = target.Public,
                type = ModelMailMessadge.MessadgeTypes.GoldenLetter,
                label = "OC_MerchantGuild_Offer",
                text = $"OC_MerchantGuild_OfferText {thingsText} OC_MerchantGuild_Price {order.Price} OC_MerchantGuild_OfferOwner {order.OwnerLogin} OC_MerchantGuild_OrderId {order.Id}",
                Tile = GetPlayerSettlementTile(target)
            };

            lock (target)
            {
                target.Mails.Add(mail);
            }

            Loger.Log($"MerchantGuild: Offered order {order.Id} to {target.Public.Login} (attempt {order.OfferAttempts})", Loger.LogLevel.EXCHANGE);
        }

        private static int GetPlayerSettlementTile(PlayerServer player)
        {
            if (player?.Public?.Login == null) return 0;
            return player.TradeThingStorages?.FirstOrDefault()?.Tile ?? 0;
        }

        public struct GuildOrderCreateResult
        {
            public bool Success;
            public long OrderId;
            public int Fee;
            public string Message;
        }
    }
}
