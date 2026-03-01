using Model;
using OCUnion;
using RimWorld;
using RimWorld.Planet;
using RimWorldOnlineCity.GameClasses;
using RimWorldOnlineCity.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Transfer;
using Transfer.ModelMails;
using Verse;

namespace RimWorldOnlineCity
{
    static class MailController
    {
        private static Dictionary<Type, Action<ModelMail>> TypeMailProcessing = new Dictionary<Type, Action<ModelMail>>()
        {
            { typeof(ModelMailTrade), MailProcessCreateThings},
            { typeof(ModelMailDeleteWO), MailProcessDeleteByServerId},
            { typeof(ModelMailAttackCancel), MailProcessAttackCancel},
            { typeof(ModelMailAttackTechnicalVictory), MailProcessAttackTechnicalVictory},
            { typeof(ModelMailStartIncident), MailProcessStartIncident},
            { typeof(ModelMailBarterOffer), MailProcessBarterOffer},
            { typeof(ModelMailStoryLog), MailProcessStoryLog},
            { typeof(ModelMailMessadge), MailProcessMessadge}
        };

        public static void MailArrived(ModelMail mail)
        {
            if (mail == null) return;

            if (!IsMainThread())
            {
                var queuedMail = mail;
                ModBaseData.RunMainThread(() => MailArrived(queuedMail));
                return;
            }

            try
            {
                if (mail.To == null
                    || mail.To.Login != SessionClientController.My.Login) return;

                Action<ModelMail> action;
                if (TypeMailProcessing.TryGetValue(mail.GetType(), out action))
                {
                    Loger.Log($"Mail {mail.GetType().Name} "
                        + (mail.From == null ? "-" : mail.From.Login) + "->"
                        + (mail.To == null ? "-" : mail.To.Login) + ":"
                        + " hash=" + mail.GetHash());
                    action(mail);
                }
                else
                {
                    Loger.Log("Mail fail: error type " + mail.GetType().Name, Loger.LogLevel.ERROR);
                }
            }
            catch(Exception e)
            {
                Loger.Log("Mail Exception: " + e.ToString(), Loger.LogLevel.ERROR);
            }
        }

        private static bool IsMainThread()
        {
            if (ModBaseData.GlobalData == null) return true;
            return ModBaseData.GlobalData.MainThreadNum == Thread.CurrentThread.ManagedThreadId;
        }

        private static void PauseForDialog()
        {
            if (Find.TickManager == null) return;
            if (Find.TickManager.Paused) return;
            Find.TickManager.Pause();
        }


        #region MailProcessMessadge
        public static void MailProcessMessadge(ModelMail incoming)
        {
            var msg = (ModelMailMessadge)incoming;
            LetterDef def;
            switch (msg.type)
            {
                case ModelMailMessadge.MessadgeTypes.ThreatBig:
                    def = LetterDefOf.ThreatBig;
                    break;
                case ModelMailMessadge.MessadgeTypes.ThreatSmall:
                    def = LetterDefOf.ThreatSmall;
                    break;
                case ModelMailMessadge.MessadgeTypes.Death:
                    def = LetterDefOf.Death;
                    break;
                case ModelMailMessadge.MessadgeTypes.Negative:
                    def = LetterDefOf.NegativeEvent;
                    break;
                case ModelMailMessadge.MessadgeTypes.Neutral:
                    def = LetterDefOf.NeutralEvent;
                    break;
                case ModelMailMessadge.MessadgeTypes.Positive:
                    def = LetterDefOf.PositiveEvent;
                    break;
                case ModelMailMessadge.MessadgeTypes.Visitor:
                    def = LetterDefOf.AcceptVisitors;
                    break;
                case ModelMailMessadge.MessadgeTypes.GoldenLetter:
                    def = OC_LetterDefOf.GoldenLetter;
                    break;
                case ModelMailMessadge.MessadgeTypes.GreyGoldenLetter:
                    def = OC_LetterDefOf.GreyGoldenLetter;
                    break;
                default:
                    def = LetterDefOf.NeutralEvent;
                    break;
            }

            var place = ExchengeUtils.GetPlace(msg);
            GlobalTargetInfo? ti = null;
            if (place != null)
                ti = new GlobalTargetInfo(place);
            else
                if (msg.Tile != 0) ti = new GlobalTargetInfo(msg.Tile);

            if (ti == null)
            {
                Find.LetterStack.ReceiveLetter(ChatController.ServerCharTranslate(msg.label)
                    , ChatController.ServerCharTranslate(msg.text)
                    , def);
            }
            else
            {
                Find.LetterStack.ReceiveLetter(ChatController.ServerCharTranslate(msg.label)
                    , ChatController.ServerCharTranslate(msg.text)
                    , def
                    , ti);
            }
        }
        #endregion

        #region MailProcessStartIncident
        public static void MailProcessStartIncident(ModelMail incoming)
        {
            Loger.Log("IncidentLod MailController.MailProcessStartIncident 1");
            var mail = (ModelMailStartIncident)incoming;

            PauseForDialog();
            
            var incident = new OCIncidentFactory().GetIncident(mail.IncidentType);
            incident.mult = mail.IncidentMult;
            //incident.arrivalMode = mail.IncidentArrivalMode;
            //incident.strategy = mail.IncidentStrategy;
            //incident.faction = mail.IncidentFaction;
            incident.incidentParams = mail.IncidentParams;
            incident.attacker = mail.From.Login;
            incident.place = ExchengeUtils.GetPlace(mail);
            incident.TryExecuteEvent();

            if (!SessionClientController.Data.BackgroundSaveGameOff) SessionClientController.SaveGameNow(true);
            Loger.Log("IncidentLod MailController.MailProcessStartIncident 2");
        }
        #endregion

        #region CreateThings
        public static void MailProcessCreateThings(ModelMail incoming)
        {
            ModelMailTrade mail = (ModelMailTrade)incoming;

            if (mail.Things == null
                || mail.Things.Count == 0
                || mail.PlaceServerId <= 0)
            {
                Loger.Log("Mail fail: no data");
                return;
            }

            var place = ExchengeUtils.GetPlace(mail);
            if (place != null)
            {
                DropToWorldObject(place, mail.Things, (mail.From == null ? "-" : mail.From.Login));
            }
        }

        private static void DropToWorldObject(WorldObject place, List<ThingEntry> things, string from)
        {
            var text = string.Format("OCity_UpdateWorld_TradeDetails".Translate()
                    , from
                    , place.LabelCap
                    , things.ToStringLabel());
            /*
            GlobalTargetInfo ti = new GlobalTargetInfo(place);
            if (place is Settlement && ((Settlement)place).Map != null)
            {
                var cell = GameUtils.GetTradeCell(((Settlement)place).Map);
                ti = new GlobalTargetInfo(cell, ((Settlement)place).Map);
            }
            */
            PauseForDialog();
            GameUtils.ShowDialodOKCancel("OCity_UpdateWorld_Trade".Translate()
                , text
                , () => ExchengeUtils.SpawnToWorldObject(place, things, text)
                , () => Log.Message("Drop Mail from " + from + ": " + text)
            );
        }

        #endregion CreateThings

        #region BarterOffer
        public static void MailProcessBarterOffer(ModelMail incoming)
        {
            var mail = incoming as ModelMailBarterOffer;
            if (mail == null
                || mail.OrderId <= 0
                || mail.CountReady <= 0
                || (mail.SellThings?.Count ?? 0) == 0
                || (mail.BuyThings?.Count ?? 0) == 0)
            {
                Loger.Log("Mail fail: no barter offer data");
                return;
            }

            PauseForDialog();
            Find.WindowStack.Add(new Dialog_BarterOffer(mail));
        }
        #endregion BarterOffer

        #region StoryLog
        public static void MailProcessStoryLog(ModelMail incoming)
        {
            var mail = incoming as ModelMailStoryLog;
            if (mail == null)
            {
                Loger.Log("Mail fail: no story log data");
                return;
            }

            var title = ChatController.ServerCharTranslate(string.IsNullOrWhiteSpace(mail.Title) ? "Журнал" : mail.Title);
            var summary = ChatController.ServerCharTranslate(string.IsNullOrWhiteSpace(mail.Summary) ? "Доступны новые записи." : mail.Summary);
            PushStoryLogLetter(mail, title, summary);

            if (mail.PopupOnReceive)
            {
                Find.WindowStack.Add(new Dialog_StoryLog(mail));
                return;
            }
        }

        private static void PushStoryLogLetter(ModelMailStoryLog mail, string title, string summary)
        {
            var letter = new Letter_StoryLog(mail, title, summary, OC_LetterDefOf.GreyGoldenLetter);
            Find.LetterStack.ReceiveLetter(letter);
        }
        #endregion StoryLog

        #region DeleteByServerId
        public static void MailProcessDeleteByServerId(ModelMail incoming)
        {
            var mail = (ModelMailDeleteWO)incoming;

            Loger.Log("Client MailProcessDeleteByServerId " + mail.PlaceServerId);

            if (mail.PlaceServerId <= 0)
            {
                Loger.Log("Mail fail: no data");
                return;
            }

            var place = ExchengeUtils.GetPlace(mail, false, true);
            if (place != null)
            {
                Find.WorldObjects.Remove(place);

                //автосейв с единым сохранением
                SessionClientController.SaveGameNow(true);
            }
        }
        #endregion DeleteByServerId

        #region AttackCancel
        public static void MailProcessAttackCancel(ModelMail incoming)
        {
            var mail = (ModelMailAttackCancel)incoming;

            Loger.Log("Client MailProcessAttackCancel");

            GameAttackTrigger_Patch.ForceSpeed = -1f; //на всякий случай
            if (SessionClientController.Data.AttackModule != null) SessionClientController.Data.AttackModule.Clear();
            if (SessionClientController.Data.AttackUsModule != null) SessionClientController.Data.AttackUsModule.Clear();

            SessionClientController.Disconnected("OCity_GameAttacker_Dialog_ErrorMessage".Translate());
        }
        #endregion AttackCancel

        #region TechnicalVictory
        public static void MailProcessAttackTechnicalVictory(ModelMail incoming)
        {
            var mail = (ModelMailAttackTechnicalVictory)incoming;

            Loger.Log("Client MailProcessAttackTechnicalVictory");

            if (SessionClientController.Data.AttackModule != null)
            {
                SessionClientController.Data.AttackModule.Finish(true);
            }
            if (SessionClientController.Data.AttackUsModule != null)
            {
                SessionClientController.Data.AttackUsModule.Finish(false);
            }

        }
        #endregion TechnicalVictory
    }
}
