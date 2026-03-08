using Model;
using OCUnion;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Verse;

namespace RimWorldOnlineCity
{
    public class CaravanArrivalAction_VisitOnline : CaravanArrivalAction//, ITrader
    {
        private CaravanOnline сaravanOnline;

        private string mode;

        public CaravanArrivalAction_VisitOnline()
        {
        }

        public CaravanArrivalAction_VisitOnline(CaravanOnline сaravanOnline, string mode)
        {
            this.сaravanOnline = сaravanOnline;
            this.mode = mode;
        }

        //Пример: посетить
        public override string Label
        {
            get
            {
                if (сaravanOnline == null) return "";
                return string.Format(mode == "exchangeOfGoods" ? "OCity_Caravan_GoTrade".Translate()
                        : mode == "barter" ? "OCity_Dialog_Exchenge_Counterproposal".Translate()
                        : mode == "visit" ? "OCity_Caravan_GoTrade2".Translate()
                        : mode == "pveVisit" ? "OCity_Caravan_Practive".Translate()
                        : mode == "attack" ? "OCity_Caravan_Go_Attack_Target".Translate()
                        : "OCity_Caravan_GoTrade2".Translate()
                    , сaravanOnline.Label);
            }
        }

        //Пример: посещает
        public override string ReportString
        {
            get
            {
                return Label;
            }
        }

        //public override bool ShouldFail { get { return false; } }

        public override void Arrived(Caravan caravan)
        {
            if (mode == "exchangeOfGoods")
            {
                ExchengeUtils.ExchangeOfGoods_DoAction(сaravanOnline, caravan);
            }
            else if (mode == "barter")
            {
                ExchengeUtils.Barter_DoAction(сaravanOnline, caravan);
            }
            else if (mode == "visit" || mode == "pveVisit")
            {
                if (!GameAttacker.CanStart
                    || SessionClientController.Data.AttackUsModule != null
                    || SessionClientController.Data.VisitHostResponding
                    || SessionClientController.Data.VisitModule != null)
                {
                    GameUtils.ShowDialodOKCancel(
                        "OCity_Caravan_GoTrade2".Translate().ToString(),
                        "Visit session is already active",
                        () => { },
                        null);
                    return;
                }

                var baseOnline = сaravanOnline as BaseOnline;
                if (baseOnline == null)
                {
                    GameUtils.ShowDialodOKCancel(
                        "OCity_Caravan_GoTrade2".Translate().ToString(),
                        "Visit is available only for player bases",
                        () => { },
                        null);
                    return;
                }

                if (!GameAttacker.Create() || GameAttacker.Get == null)
                {
                    GameUtils.ShowDialodOKCancel(
                        "OCity_Caravan_GoTrade2".Translate().ToString(),
                        "Visit session initialization failed",
                        () => { },
                        null);
                    return;
                }

                GameAttacker.Get.Start(caravan, baseOnline, true);
            }
            else if (mode == "attack")
            {
                if (!GameAttacker.CanStart
                    || SessionClientController.Data.AttackUsModule != null
                    || SessionClientController.Data.VisitHostResponding
                    || SessionClientController.Data.VisitModule != null)
                {
                    GameUtils.ShowDialodOKCancel(
                        "OCity_Caravan_GoTrade2".Translate().ToString(),
                        "Visit session is already active",
                        () => { },
                        null);
                    return;
                }

                var baseOnline = сaravanOnline as BaseOnline;
                if (baseOnline == null)
                {
                    GameUtils.ShowDialodOKCancel(
                        "OCity_Caravan_GoTrade2".Translate().ToString(),
                        "Visit is available only for player bases",
                        () => { },
                        null);
                    return;
                }

                if (!GameAttacker.Create() || GameAttacker.Get == null)
                {
                    GameUtils.ShowDialodOKCancel(
                        "OCity_Caravan_GoTrade2".Translate().ToString(),
                        "Visit session initialization failed",
                        () => { },
                        null);
                    return;
                }

                GameAttacker.Get.Start(caravan, baseOnline, true);
            }
        }

    }
}
