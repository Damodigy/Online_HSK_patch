using Model;
using OCUnion;
using RimWorld;
using System;
using System.Collections.Generic;
using Transfer.ModelMails;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimWorldOnlineCity.UI
{
    public class Dialog_BarterOffer : Window
    {
        private const float HeaderHeight = 90f;
        private const float BottomHeight = 48f;
        private const float PanelHeaderHeight = 24f;
        private const float LineHeight = 30f;

        private readonly ModelMailBarterOffer Offer;
        private Vector2 ScrollGive = Vector2.zero;
        private Vector2 ScrollGet = Vector2.zero;
        private bool ActiveElementBlock;

        public override Vector2 InitialSize => new Vector2(860f, 520f);

        public Dialog_BarterOffer(ModelMailBarterOffer offer)
        {
            Offer = offer ?? new ModelMailBarterOffer();
            closeOnCancel = false;
            closeOnAccept = false;
            doCloseButton = false;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var repeat = Offer.CountReady > 0 ? Offer.CountReady : 1;

            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "OCity_Dialog_Exchenge_Counterproposal".Translate());

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.Label(new Rect(0f, 38f, inRect.width, 24f),
                "OCity_Dialog_Exchenge_Seller".Translate() + ": " + (Offer.From?.Login ?? "-"));
            Widgets.Label(new Rect(0f, 58f, inRect.width / 2f, 24f),
                "OCity_Dialog_Exchenge_TradeCount".Translate() + ": x" + repeat);
            Widgets.Label(new Rect(inRect.width / 2f, 58f, inRect.width / 2f, 24f),
                "OCity_Dialog_Exchenge_Tile".Translate() + " " + Offer.Tile);

            var panelTop = HeaderHeight;
            var panelHeight = inRect.height - HeaderHeight - BottomHeight;
            var panelGap = 10f;
            var panelWidth = (inRect.width - panelGap) / 2f;

            var givePanel = new Rect(0f, panelTop, panelWidth, panelHeight);
            var getPanel = new Rect(panelWidth + panelGap, panelTop, panelWidth, panelHeight);

            DrawThingsPanel(givePanel
                , "OCity_Dialog_Exchenge_We_Give".Translate().ToString()
                , Offer.BuyThings
                , repeat
                , ref ScrollGive);
            DrawThingsPanel(getPanel
                , "OCity_Dialog_Exchenge_We_Get".Translate().ToString()
                , Offer.SellThings
                , repeat
                , ref ScrollGet);

            var btnWidth = 160f;
            var btnHeight = 36f;
            var btnY = inRect.height - btnHeight;
            var btnAccept = new Rect(inRect.width / 2f - btnWidth - 6f, btnY, btnWidth, btnHeight);
            var btnReject = new Rect(inRect.width / 2f + 6f, btnY, btnWidth, btnHeight);

            if (ActiveElementBlock) GUI.color = Color.gray;
            if (Widgets.ButtonText(btnAccept, "AcceptButton".Translate()))
            {
                GUI.color = Color.white;
                if (ActiveElementBlock) return;
                AcceptOffer(repeat);
                return;
            }
            GUI.color = Color.white;

            if (ActiveElementBlock) GUI.color = Color.gray;
            if (Widgets.ButtonText(btnReject, "RejectLetter".Translate()))
            {
                GUI.color = Color.white;
                if (ActiveElementBlock) return;
                SoundDefOf.Tick_Low.PlayOneShotOnCamera(null);
                Close(false);
                return;
            }
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void AcceptOffer(int repeat)
        {
            ActiveElementBlock = true;
            SoundDefOf.Tick_High.PlayOneShotOnCamera(null);

            SessionClientController.Command((connect) =>
            {
                if (!connect.ExchengeBuy(Offer.OrderId, repeat))
                {
                    ActiveElementBlock = false;
                    Loger.Log("Client Dialog_BarterOffer accept error: " + connect.ErrorMessage?.ServerTranslate(), Loger.LogLevel.ERROR);
                    Find.WindowStack.Add(new Dialog_Input(
                        "OCity_Dialog_Exchenge_Action_Not_CarriedOut".Translate().ToString(),
                        connect.ErrorMessage?.ServerTranslate(),
                        true));
                    return;
                }

                Close(false);
            });
        }

        private static void DrawThingsPanel(Rect rect, string title, List<ThingTrade> things, int repeat, ref Vector2 scrollPosition)
        {
            Widgets.DrawMenuSection(rect);
            var inner = rect.ContractedBy(6f);
            var headerRect = new Rect(inner.x, inner.y, inner.width, PanelHeaderHeight);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(headerRect, title);
            Text.Anchor = TextAnchor.UpperLeft;

            var listRect = new Rect(inner.x, inner.y + PanelHeaderHeight + 2f, inner.width, inner.height - PanelHeaderHeight - 2f);
            var lineCount = Math.Max((things?.Count ?? 0), 1);
            var contentHeight = lineCount * LineHeight;
            var viewRect = new Rect(0f, 0f, listRect.width - 16f, Math.Max(contentHeight, listRect.height));

            Widgets.BeginScrollView(listRect, ref scrollPosition, viewRect);
            if (things == null || things.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(0f, 0f, viewRect.width, LineHeight), "OCity_Dialog_Exchenge_No_Exchanges".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
            }
            else
            {
                for (int i = 0; i < things.Count; i++)
                {
                    var lineRect = new Rect(0f, i * LineHeight, viewRect.width, LineHeight);
                    DrawThingLine(lineRect, things[i], repeat);
                }
            }
            Widgets.EndScrollView();
        }

        private static void DrawThingLine(Rect rect, ThingTrade thing, int repeat)
        {
            if (thing == null) return;

            var totalCount = Math.Max(thing.Count, 0) * repeat;
            var countText = repeat > 1
                ? thing.Count + "x" + repeat + "=" + totalCount
                : totalCount.ToString();

            var iconRect = new Rect(rect.x + 2f, rect.y + 3f, 24f, 24f);
            GameUtils.DravLineThing(iconRect, thing, true);

            var countWidth = Text.CalcSize("9999x99=999999").x;
            var countRect = new Rect(rect.xMax - countWidth - 2f, rect.y, countWidth, rect.height);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(countRect, countText);

            var labelRect = new Rect(iconRect.xMax + 6f, rect.y, countRect.xMin - iconRect.xMax - 8f, rect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(labelRect, thing.Name ?? thing.LabelTextShort);

            TooltipHandler.TipRegion(rect, thing.LabelText);
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
