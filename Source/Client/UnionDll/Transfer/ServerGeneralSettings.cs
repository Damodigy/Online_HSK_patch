using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OCUnion
{
    [Serializable]
    public struct ServerGeneralSettings
    {
        /// <summary>
        /// Деф рассказчика.
        /// </summary>
        public string StorytellerDef { get; set; }

        /// <summary>
        /// Сложность.
        /// </summary>
        public string Difficulty { get; set; }

        /// <summary>
        /// Включить режим нападения игроков друг на друга онлайн с ограниченным управлением
        /// </summary>
        public bool EnablePVP { get; set; }

        /// <summary>
        /// Запретить менять настройки рассказчика и модификаций в игре
        /// </summary>
        public bool DisableGameSettings { get; set; }

        /// <summary>
        /// Разрешены ли инценденты
        /// </summary>
        public bool IncidentEnable { get; set; }

        /// <summary>
        /// Сколько разрешено инцендентов в очереди
        /// </summary>
        public int IncidentCountInOffline { get; set; }

        /// <summary>
        /// Максимальный коэф. силы инцендентов
        /// </summary>
        public int IncidentMaxMult { get; set; }

        /// <summary>
        /// Минимальная пауза между инциндентами в тиках (1 день = 60000)
        /// </summary>
        public int IncidentTickDelayBetween { get; set; }

        /// <summary>
        /// Модификатор стоимости найма в процентах
        /// </summary>
        public int IncidentCostPrecent { get; set; }

        /// <summary>
        /// Модификатор силы рейдов в процентах
        /// </summary>
        public int IncidentPowerPrecent { get; set; }

        /// <summary>
        /// Модификатор передышки после рейда
        /// </summary>
        public int IncidentCoolDownPercent { get; set; }

        /// <summary>
        /// Модификатор предупреждения перед рейдом
        /// </summary>
        public int IncidentAlarmInHours { get; set; }

        /// <summary>
        /// Включить синхронизацию объектов на планете между всеми игроками (WIP)
        /// </summary>
        public bool EquableWorldObjects { get; set; }

        /// <summary>
        /// Включить серверного рассказчика мира (глобальные точки/события на планете).
        /// </summary>
        public bool StorytellerEnable { get; set; }

        /// <summary>
        /// Период тика серверного рассказчика в секундах.
        /// </summary>
        public int StorytellerTickIntervalSeconds { get; set; }

        /// <summary>
        /// Базовый шанс генерации новой глобальной точки за тик, в процентах.
        /// </summary>
        public int StorytellerSpawnChancePercent { get; set; }

        /// <summary>
        /// Максимум одновременно существующих серверно-сгенерированных глобальных точек.
        /// </summary>
        public int StorytellerMaxWorldObjects { get; set; }

        /// <summary>
        /// Время жизни лагерей рассказчика в часах (для временных точек).
        /// </summary>
        public int StorytellerCampLifetimeHours { get; set; }

        /// <summary>
        /// Время жизни форпостов рассказчика в часах (для временных точек).
        /// </summary>
        public int StorytellerOutpostLifetimeHours { get; set; }

        /// <summary>
        /// Шанс, что временный лагерь эволюционирует в постоянное поселение, в процентах.
        /// </summary>
        public int StorytellerCampUpgradeChancePercent { get; set; }

        /// <summary>
        /// Лимит хранения записей в серверном журнале повествования.
        /// </summary>
        public int StorytellerEventHistoryLimit { get; set; }

        /// <summary>
        /// Вес события "лагерь у границ игрока", в процентах.
        /// </summary>
        public int StorytellerPlayerCampWeightPercent { get; set; }

        /// <summary>
        /// Вес события "торговый лагерь", в процентах.
        /// </summary>
        public int StorytellerTradeCampWeightPercent { get; set; }

        /// <summary>
        /// Вес события "новое постоянное поселение", в процентах.
        /// </summary>
        public int StorytellerSettlementWeightPercent { get; set; }

        /// <summary>
        /// Вес события "новый форпост", в процентах.
        /// </summary>
        public int StorytellerOutpostWeightPercent { get; set; }

        /// <summary>
        /// Шанс фракционного конфликта рассказчика за тик, в процентах.
        /// </summary>
        public int StorytellerConflictChancePercent { get; set; }

        /// <summary>
        /// Шанс дипломатического события рассказчика за тик, в процентах.
        /// </summary>
        public int StorytellerDiplomacyChancePercent { get; set; }

        /// <summary>
        /// Шанс эволюции постоянного поселения рассказчика за шаг, в процентах.
        /// </summary>
        public int StorytellerSettlementEvolutionChancePercent { get; set; }

        /// <summary>
        /// Шанс распространения влияния развитого поселения (создание новых точек) за шаг, в процентах.
        /// </summary>
        public int StorytellerSettlementSpreadChancePercent { get; set; }

        /// <summary>
        /// При экспансии города (уровень 3+) и наличии рядом враждебных/чужих соседей:
        /// шанс выбрать военную базу вместо обычной ветки.
        /// </summary>
        public int StorytellerSpreadCityEnemyMilitaryBaseChancePercent { get; set; }

        /// <summary>
        /// При экспансии города (уровень 3+) и наличии рядом союзных соседей:
        /// шанс выбрать торговый пост вместо обычной ветки.
        /// </summary>
        public int StorytellerSpreadCityAllyTradeCampChancePercent { get; set; }

        /// <summary>
        /// При экспансии поселений уровня 1-2 и наличии рядом враждебных/чужих соседей:
        /// шанс выбрать форпост вместо обычной ветки.
        /// </summary>
        public int StorytellerSpreadLowEnemyOutpostChancePercent { get; set; }

        /// <summary>
        /// При экспансии поселений уровня 1-2 и наличии рядом союзных соседей:
        /// шанс выбрать торговый пост вместо обычной ветки.
        /// </summary>
        public int StorytellerSpreadLowAllyTradeCampChancePercent { get; set; }

        /// <summary>
        /// Кулдаун между шагами эволюции/экспансии поселения рассказчика, в минутах.
        /// </summary>
        public int StorytellerSettlementActionCooldownMinutes { get; set; }

        /// <summary>
        /// Кулдаун повторных сюжетных событий про взаимодействия игроков, в минутах.
        /// </summary>
        public int StorytellerInteractionCooldownMinutes { get; set; }

        /// <summary>
        /// Включает публикацию сюжетных уведомлений о перемещениях/встречах караванов игроков.
        /// Если false, события типа "кто кого посетил" не попадают в журнал.
        /// </summary>
        public bool StorytellerPlayerInteractionEventsEnabled { get; set; }

        /// <summary>
        /// Через сколько минут отсутствия игрок считается "долго отсутствующим" для дайджеста.
        /// </summary>
        public int StoryDigestOfflineMinutes { get; set; }

        /// <summary>
        /// Если новых сюжетных событий не больше этого значения и игрок не отсутствовал долго,
        /// отправляются отдельные уведомления вместо единого журнала.
        /// </summary>
        public int StoryDigestImmediateEventsMax { get; set; }

        /// <summary>
        /// Максимум строк, показываемых в журнале повествования.
        /// </summary>
        public int StoryDigestMaxLines { get; set; }

        /// <summary>
        /// При достижении этого числа уведомлений они объединяются в один журнал уведомлений.
        /// </summary>
        public int MessageDigestThreshold { get; set; }

        /// <summary>
        /// Максимум строк, показываемых в журнале уведомлений.
        /// </summary>
        public int MessageDigestMaxLines { get; set; }

        /// <summary>
        /// Бонус к целевой стоимости лута за уровень сюжетной точки (в процентах за уровень сверх первого).
        /// </summary>
        public int StoryPointLootLevelMarketBonusPercent { get; set; }

        /// <summary>
        /// Профиль лута для торговых лагерей: количество кэшей.
        /// </summary>
        public int StoryPointLootTradeCampCacheCount { get; set; }

        /// <summary>
        /// Профиль лута для торговых лагерей: минимум предметов в кэше.
        /// </summary>
        public int StoryPointLootTradeCampItemsMin { get; set; }

        /// <summary>
        /// Профиль лута для торговых лагерей: максимум предметов в кэше.
        /// </summary>
        public int StoryPointLootTradeCampItemsMax { get; set; }

        /// <summary>
        /// Профиль лута для торговых лагерей: минимум целевой стоимости кэша.
        /// </summary>
        public int StoryPointLootTradeCampMarketMin { get; set; }

        /// <summary>
        /// Профиль лута для торговых лагерей: максимум целевой стоимости кэша.
        /// </summary>
        public int StoryPointLootTradeCampMarketMax { get; set; }

        /// <summary>
        /// Профиль лута для поселений: количество кэшей.
        /// </summary>
        public int StoryPointLootSettlementCacheCount { get; set; }

        /// <summary>
        /// Профиль лута для поселений: минимум предметов в кэше.
        /// </summary>
        public int StoryPointLootSettlementItemsMin { get; set; }

        /// <summary>
        /// Профиль лута для поселений: максимум предметов в кэше.
        /// </summary>
        public int StoryPointLootSettlementItemsMax { get; set; }

        /// <summary>
        /// Профиль лута для поселений: минимум целевой стоимости кэша.
        /// </summary>
        public int StoryPointLootSettlementMarketMin { get; set; }

        /// <summary>
        /// Профиль лута для поселений: максимум целевой стоимости кэша.
        /// </summary>
        public int StoryPointLootSettlementMarketMax { get; set; }

        /// <summary>
        /// Профиль лута для городов: количество кэшей.
        /// </summary>
        public int StoryPointLootCityCacheCount { get; set; }

        /// <summary>
        /// Профиль лута для городов: минимум предметов в кэше.
        /// </summary>
        public int StoryPointLootCityItemsMin { get; set; }

        /// <summary>
        /// Профиль лута для городов: максимум предметов в кэше.
        /// </summary>
        public int StoryPointLootCityItemsMax { get; set; }

        /// <summary>
        /// Профиль лута для городов: минимум целевой стоимости кэша.
        /// </summary>
        public int StoryPointLootCityMarketMin { get; set; }

        /// <summary>
        /// Профиль лута для городов: максимум целевой стоимости кэша.
        /// </summary>
        public int StoryPointLootCityMarketMax { get; set; }

        /// <summary>
        /// Профиль лута для форпостов: количество кэшей.
        /// </summary>
        public int StoryPointLootOutpostCacheCount { get; set; }

        /// <summary>
        /// Профиль лута для форпостов: минимум предметов в кэше.
        /// </summary>
        public int StoryPointLootOutpostItemsMin { get; set; }

        /// <summary>
        /// Профиль лута для форпостов: максимум предметов в кэше.
        /// </summary>
        public int StoryPointLootOutpostItemsMax { get; set; }

        /// <summary>
        /// Профиль лута для форпостов: минимум целевой стоимости кэша.
        /// </summary>
        public int StoryPointLootOutpostMarketMin { get; set; }

        /// <summary>
        /// Профиль лута для форпостов: максимум целевой стоимости кэша.
        /// </summary>
        public int StoryPointLootOutpostMarketMax { get; set; }

        /// <summary>
        /// Профиль лута для универсальных точек: количество кэшей.
        /// </summary>
        public int StoryPointLootGenericCacheCount { get; set; }

        /// <summary>
        /// Профиль лута для универсальных точек: минимум предметов в кэше.
        /// </summary>
        public int StoryPointLootGenericItemsMin { get; set; }

        /// <summary>
        /// Профиль лута для универсальных точек: максимум предметов в кэше.
        /// </summary>
        public int StoryPointLootGenericItemsMax { get; set; }

        /// <summary>
        /// Профиль лута для универсальных точек: минимум целевой стоимости кэша.
        /// </summary>
        public int StoryPointLootGenericMarketMin { get; set; }

        /// <summary>
        /// Профиль лута для универсальных точек: максимум целевой стоимости кэша.
        /// </summary>
        public int StoryPointLootGenericMarketMax { get; set; }

        /// <summary>
        /// Вес еды/медицины для торгового профиля (проценты, 100 = без изменений).
        /// </summary>
        public int StoryPointLootWeightTradeFoodMedicinePercent { get; set; }

        /// <summary>
        /// Вес технологий для торгового профиля (проценты, 100 = без изменений).
        /// </summary>
        public int StoryPointLootWeightTradeTechPercent { get; set; }

        /// <summary>
        /// Вес еды для профиля поселений (проценты, 100 = без изменений).
        /// </summary>
        public int StoryPointLootWeightSettlementFoodPercent { get; set; }

        /// <summary>
        /// Вес мебели/материалов для профиля поселений (проценты, 100 = без изменений).
        /// </summary>
        public int StoryPointLootWeightSettlementFurniturePercent { get; set; }

        /// <summary>
        /// Вес оружия для городского профиля (проценты, 100 = без изменений).
        /// </summary>
        public int StoryPointLootWeightCityWeaponPercent { get; set; }

        /// <summary>
        /// Вес технологий для городского профиля (проценты, 100 = без изменений).
        /// </summary>
        public int StoryPointLootWeightCityTechPercent { get; set; }

        /// <summary>
        /// Вес оружия для профиля форпостов (проценты, 100 = без изменений).
        /// </summary>
        public int StoryPointLootWeightOutpostWeaponPercent { get; set; }

        /// <summary>
        /// Вес протезов для профиля форпостов (проценты, 100 = без изменений).
        /// </summary>
        public int StoryPointLootWeightOutpostProstheticPercent { get; set; }

        /// <summary>
        /// Вес турельных ресурсов для профиля форпостов (проценты, 100 = без изменений).
        /// </summary>
        public int StoryPointLootWeightOutpostTurretResourcePercent { get; set; }

        /// <summary>
        /// Включить биржу (WIP)
        /// </summary>
        public bool ExchengeEnable { get; set; }

        /// <summary>
        /// включить выбор старта
        /// </summary>
        public bool ScenarioAviable { get; set; }

        /// <summary>
        /// Стоимость вещей на бирже, в сделках и на счету для рейдов на 1000 серебра (50 - это 5%). Рекомендация 1000 или 1200.
        /// </summary>
        public int ExchengePrecentWealthForIncident { get; set; }

        /// <summary>
        /// Комиссия перевода 1000 серебра его в безналичный счет или назад (50 - это 5%)
        /// </summary>
        public int ExchengePrecentCommissionConvertToCashlessCurrency { get; set; }

        /// <summary>
        /// Стоимость доставки 1000 серебра на расстояние 100 клеток (это примерно 1/15 планеты по экватору, а если /10 то сколько стоит доставка соседям)
        /// </summary>
        public int ExchengeCostCargoDelivery { get; set; }

        /// <summary>
        /// Процент надбавки цены за быструю доставку
        /// </summary>
        public int ExchengeAddPrecentCostForFastCargoDelivery { get; set; }

        /// <summary>
        /// Перечисленные через запятую defName вещей, которые запрещены к передаче.
        /// </summary>
        public string ExchengeForbiddenDefNames { get; set; }
        [NonSerialized]
        private HashSet<string> ExchengeForbiddenDefNamesListData;
        public HashSet<string> ExchengeForbiddenDefNamesList
        {
            get
            {
                if (ExchengeForbiddenDefNamesListData == null)
                    if (string.IsNullOrEmpty(ExchengeForbiddenDefNames))
                        ExchengeForbiddenDefNamesListData = new HashSet<string>();
                    else
                        ExchengeForbiddenDefNamesListData = new HashSet<string>(ExchengeForbiddenDefNames.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                return ExchengeForbiddenDefNamesListData;
            }
        }

        /// <summary>
        /// Назначит стартовый год в игре вместо 5500
        /// </summary>
        public int StartGameYear { get; set; }

        /// <summary>
        /// Предупреждение при входе на сервер.
        /// </summary>
        public string EntranceWarning { get; set; }

        /// <summary>
        /// Предупреждение при входе на сервер. На русском.
        /// </summary>
        public string EntranceWarningRussian { get; set; }

        /// <summary>
        /// Делать снимки колоний каждый полдень
        /// </summary>
        public bool ColonyScreenEnable { get; set; }

        /// <summary>
        /// Высокое качество снимков колоний
        /// </summary>
        public bool ColonyScreenHighQuality { get; set; }

        /// <summary>
        /// Через сколько дней делать снимки колоний. От 1 до 60 (больше 59 снимок 1 раз в год)
        /// </summary>
        public int ColonyScreenDelayDays { get; set; }

        public ServerGeneralSettings SetDefault()
        {
            StorytellerDef = "";

            Difficulty = "";

            EnablePVP = false;

            DisableGameSettings = false;

            IncidentEnable = true;

            IncidentCountInOffline = 2;

            IncidentMaxMult = 10;

            IncidentTickDelayBetween = 60000;

            IncidentCostPrecent = 100;

            IncidentPowerPrecent = 100;

            IncidentCoolDownPercent = 100;

            IncidentAlarmInHours = 10;

            EquableWorldObjects = false;

            StorytellerEnable = true;

            StorytellerTickIntervalSeconds = 90;

            StorytellerSpawnChancePercent = 30;

            StorytellerMaxWorldObjects = 24;

            StorytellerCampLifetimeHours = 72;

            StorytellerOutpostLifetimeHours = 120;

            StorytellerCampUpgradeChancePercent = 18;

            StorytellerEventHistoryLimit = 3000;

            StorytellerPlayerCampWeightPercent = 45;

            StorytellerTradeCampWeightPercent = 30;

            StorytellerSettlementWeightPercent = 25;

            StorytellerOutpostWeightPercent = 18;

            StorytellerConflictChancePercent = 8;

            StorytellerDiplomacyChancePercent = 10;

            StorytellerSettlementEvolutionChancePercent = 11;

            StorytellerSettlementSpreadChancePercent = 15;

            StorytellerSpreadCityEnemyMilitaryBaseChancePercent = 85;

            StorytellerSpreadCityAllyTradeCampChancePercent = 75;

            StorytellerSpreadLowEnemyOutpostChancePercent = 80;

            StorytellerSpreadLowAllyTradeCampChancePercent = 70;

            StorytellerSettlementActionCooldownMinutes = 540;

            StorytellerInteractionCooldownMinutes = 45;

            StorytellerPlayerInteractionEventsEnabled = false;

            StoryDigestOfflineMinutes = 30;

            StoryDigestImmediateEventsMax = 3;

            StoryDigestMaxLines = 40;

            MessageDigestThreshold = 12;

            MessageDigestMaxLines = 30;

            StoryPointLootLevelMarketBonusPercent = 20;

            StoryPointLootTradeCampCacheCount = 5;
            StoryPointLootTradeCampItemsMin = 4;
            StoryPointLootTradeCampItemsMax = 8;
            StoryPointLootTradeCampMarketMin = 450;
            StoryPointLootTradeCampMarketMax = 1200;

            StoryPointLootSettlementCacheCount = 5;
            StoryPointLootSettlementItemsMin = 3;
            StoryPointLootSettlementItemsMax = 7;
            StoryPointLootSettlementMarketMin = 500;
            StoryPointLootSettlementMarketMax = 1400;

            StoryPointLootCityCacheCount = 6;
            StoryPointLootCityItemsMin = 4;
            StoryPointLootCityItemsMax = 9;
            StoryPointLootCityMarketMin = 900;
            StoryPointLootCityMarketMax = 2600;

            StoryPointLootOutpostCacheCount = 6;
            StoryPointLootOutpostItemsMin = 4;
            StoryPointLootOutpostItemsMax = 8;
            StoryPointLootOutpostMarketMin = 700;
            StoryPointLootOutpostMarketMax = 1700;

            StoryPointLootGenericCacheCount = 3;
            StoryPointLootGenericItemsMin = 3;
            StoryPointLootGenericItemsMax = 5;
            StoryPointLootGenericMarketMin = 350;
            StoryPointLootGenericMarketMax = 1000;

            StoryPointLootWeightTradeFoodMedicinePercent = 160;
            StoryPointLootWeightTradeTechPercent = 125;
            StoryPointLootWeightSettlementFoodPercent = 210;
            StoryPointLootWeightSettlementFurniturePercent = 180;
            StoryPointLootWeightCityWeaponPercent = 220;
            StoryPointLootWeightCityTechPercent = 200;
            StoryPointLootWeightOutpostWeaponPercent = 220;
            StoryPointLootWeightOutpostProstheticPercent = 200;
            StoryPointLootWeightOutpostTurretResourcePercent = 180;

            ScenarioAviable = true;

            ExchengeEnable = true;

            ExchengePrecentWealthForIncident = 1000;

            ExchengePrecentCommissionConvertToCashlessCurrency = 50;

            ExchengeCostCargoDelivery = 1000;

            ExchengeAddPrecentCostForFastCargoDelivery = 100;

            StartGameYear = -1;

            ColonyScreenEnable = true;
            
            ColonyScreenHighQuality = true;

            ColonyScreenDelayDays = 1;

            return this;
        }

    }
}
