using Model;
using RimWorld;
using System;
using System.Collections.Generic;
using Transfer.ModelMails;
using UnityEngine;
using Verse;

namespace RimWorldOnlineCity.UI
{
    public class Dialog_StoryLog : Window
    {
        private const float HeaderHeight = 70f;
        private const float SummaryHeight = 54f;
        private const float BottomHeight = 44f;
        private const float RowMinHeight = 36f;
        private const float RowGap = 2f;
        private const float RowTopPadding = 6f;
        private const float RowBottomPadding = 6f;
        private const float MaxBodyHeight = 68f;
        private const float HeaderIconSize = 28f;
        private const float EntryIconSize = 20f;

        private static readonly Texture2D IconNarrative = LoadTexture("OCStory/IconNarrative");
        private static readonly Texture2D IconNotifications = LoadTexture("OCStory/IconNotifications");
        private static readonly Texture2D IconMixed = LoadTexture("OCStory/IconMixed");

        private static readonly Texture2D EntryDefault = LoadTexture("OCStory/EntryDefault");
        private static readonly Texture2D EntryStoryteller = LoadTexture("OCStory/EntryStoryteller");
        private static readonly Texture2D EntryMessages = LoadTexture("OCStory/EntryMessages");
        private static readonly Texture2D EntryPlayers = LoadTexture("OCStory/EntryPlayers");
        private static readonly Texture2D EntryDiplomacy = LoadTexture("OCStory/EntryDiplomacy");
        private static readonly Texture2D EntryBarter = LoadTexture("OCStory/EntryBarter");

        private static readonly Texture2D PointCamp = LoadTexture("OCStory/PointCamp");
        private static readonly Texture2D PointTradeCamp = LoadTexture("OCStory/PointTradeCamp");
        private static readonly Texture2D PointOutpost = LoadTexture("OCStory/PointOutpost");
        private static readonly Texture2D PointSettlement = LoadTexture("OCStory/PointSettlement");

        private readonly ModelMailStoryLog Mail;
        private Vector2 ScrollPosition;

        private struct EntryLayout
        {
            public StoryLogEntry Entry;
            public float Top;
            public float Height;
        }

        public override Vector2 InitialSize => new Vector2(960f, 680f);

        public Dialog_StoryLog(ModelMailStoryLog mail)
        {
            Mail = mail ?? new ModelMailStoryLog();
            forcePause = false;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            doCloseX = true;
            doCloseButton = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            DrawHeaderTitle(new Rect(0f, 0f, inRect.width, 34f), T(Mail.Title, "Журнал событий"), ResolveKindIcon(Mail.Kind));

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, 38f, inRect.width, 24f), BuildMetaLine());

            var summaryRect = new Rect(0f, HeaderHeight, inRect.width, SummaryHeight);
            Widgets.DrawMenuSection(summaryRect);
            Widgets.Label(summaryRect.ContractedBy(8f), T(Mail.Summary, string.Empty));

            var listRect = new Rect(0f, HeaderHeight + SummaryHeight + 6f, inRect.width, inRect.height - HeaderHeight - SummaryHeight - BottomHeight - 8f);
            DrawEntries(listRect);

            var closeRect = new Rect(inRect.width / 2f - 80f, inRect.height - 36f, 160f, 34f);
            if (Widgets.ButtonText(closeRect, "CloseButton".Translate()))
            {
                Close(false);
            }

            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void DrawEntries(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            var inner = rect.ContractedBy(6f);
            var entries = Mail.Entries ?? new List<StoryLogEntry>();
            var layouts = BuildEntryLayout(entries, inner.width - 16f);
            var contentHeight = Math.Max(inner.height, layouts.Count == 0 ? RowMinHeight : layouts[layouts.Count - 1].Top + layouts[layouts.Count - 1].Height);
            var contentRect = new Rect(0f, 0f, inner.width - 16f, contentHeight);

            Widgets.BeginScrollView(inner, ref ScrollPosition, contentRect);

            if (entries.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(0f, 0f, contentRect.width, RowMinHeight), "Записей нет.");
                Text.Anchor = TextAnchor.UpperLeft;
                Widgets.EndScrollView();
                return;
            }

            for (int i = 0; i < layouts.Count; i++)
            {
                var rowRect = new Rect(0f, layouts[i].Top, contentRect.width, layouts[i].Height);
                if (i % 2 == 0)
                {
                    Widgets.DrawBoxSolid(rowRect, new Color(1f, 1f, 1f, 0.04f));
                }

                DrawEntry(rowRect, layouts[i].Entry);
            }

            Widgets.EndScrollView();
        }

        private static List<EntryLayout> BuildEntryLayout(List<StoryLogEntry> entries, float width)
        {
            var result = new List<EntryLayout>();
            if (entries == null || entries.Count == 0) return result;

            var y = 0f;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var rowHeight = CalculateEntryHeight(width, entry);
                result.Add(new EntryLayout()
                {
                    Entry = entry,
                    Top = y,
                    Height = rowHeight
                });
                y += rowHeight + RowGap;
            }

            return result;
        }

        private static float CalculateEntryHeight(float rowWidth, StoryLogEntry entry)
        {
            if (entry == null) return RowMinHeight;

            var iconWidth = 6f + EntryIconSize + 6f;
            var timeWidth = 82f;
            var labelWidth = 210f;
            var buttonWidth = 66f;
            var paddings = 8f + 12f + 6f + 6f;
            var bodyWidth = Mathf.Max(120f, rowWidth - iconWidth - timeWidth - labelWidth - buttonWidth - paddings);

            var body = NormalizeBody(T(entry.Text, string.Empty));
            var oldWordWrap = Text.WordWrap;
            Text.WordWrap = true;
            var bodyHeight = Text.CalcHeight(body, bodyWidth);
            Text.WordWrap = oldWordWrap;

            bodyHeight = Mathf.Min(bodyHeight, MaxBodyHeight);
            var computed = RowTopPadding + bodyHeight + RowBottomPadding;
            return Mathf.Max(RowMinHeight, computed);
        }

        private static void DrawEntry(Rect rect, StoryLogEntry entry)
        {
            if (entry == null) return;

            var iconRect = new Rect(rect.x + 6f, rect.y + 8f, EntryIconSize, EntryIconSize);
            var timeRect = new Rect(iconRect.xMax + 6f, rect.y + RowTopPadding, 82f, 20f);
            var labelRect = new Rect(timeRect.xMax + 6f, rect.y + RowTopPadding, 210f, 20f);
            var buttonRect = new Rect(rect.xMax - 72f, rect.y + 4f, 66f, rect.height - 8f);
            var textRect = new Rect(labelRect.xMax + 8f, rect.y + RowTopPadding, buttonRect.x - labelRect.xMax - 12f, rect.height - RowTopPadding - RowBottomPadding);
            var label = NormalizeSingleLine(T(entry.Label, "Событие"));
            var body = NormalizeBody(T(entry.Text, string.Empty));

            DrawIcon(iconRect, ResolveEntryIcon(entry));
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(timeRect, FormatEntryTime(entry.CreatedUtc));
            Widgets.Label(labelRect, Cut(label, 48));
            var oldWordWrap = Text.WordWrap;
            Text.WordWrap = true;
            Widgets.Label(textRect, body);
            Text.WordWrap = oldWordWrap;

            if (entry.Tile > 0 && Widgets.ButtonText(buttonRect, "Тайл"))
            {
                JumpToTile(entry.Tile);
            }
            else if (entry.Tile <= 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(buttonRect, "-");
                GUI.color = Color.white;
            }

            var tooltipText = label
                + Environment.NewLine
                + body
                + (entry.Tile > 0 ? Environment.NewLine + "Тайл: " + entry.Tile : string.Empty);
            TooltipHandler.TipRegion(rect, tooltipText);
        }

        private string BuildMetaLine()
        {
            var kind = Mail.Kind == StoryLogKind.Notifications ? "Уведомления" : "Повествование";
            if (Mail.Kind == StoryLogKind.Mixed) kind = "Смешанный";
            var total = Mail.TotalCount > 0 ? Mail.TotalCount : (Mail.Entries?.Count ?? 0);
            var shown = Mail.ShownCount > 0 ? Mail.ShownCount : (Mail.Entries?.Count ?? 0);
            return $"{kind}. Всего: {total}. Показано: {shown}.";
        }

        private static void DrawHeaderTitle(Rect rect, string title, Texture2D icon)
        {
            var text = string.IsNullOrWhiteSpace(title) ? "Журнал событий" : title;
            var titleWidth = Text.CalcSize(text).x;
            var hasIcon = icon != null;
            var totalWidth = titleWidth + (hasIcon ? HeaderIconSize + 8f : 0f);
            var startX = rect.x + (rect.width - totalWidth) / 2f;

            if (hasIcon)
            {
                var iconRect = new Rect(startX, rect.y + (rect.height - HeaderIconSize) / 2f, HeaderIconSize, HeaderIconSize);
                DrawIcon(iconRect, icon);
            }

            Text.Anchor = TextAnchor.MiddleLeft;
            var labelRect = new Rect(startX + (hasIcon ? HeaderIconSize + 8f : 0f), rect.y, titleWidth + 4f, rect.height);
            Widgets.Label(labelRect, text);
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static Texture2D ResolveKindIcon(StoryLogKind kind)
        {
            switch (kind)
            {
                case StoryLogKind.Notifications:
                    return IconNotifications;
                case StoryLogKind.Mixed:
                    return IconMixed;
                default:
                    return IconNarrative;
            }
        }

        private static Texture2D ResolveEntryIcon(StoryLogEntry entry)
        {
            if (entry == null) return EntryDefault;

            var worldPoint = ResolveWorldPointIcon(entry);
            if (worldPoint != null) return worldPoint;

            var category = (entry.Category ?? string.Empty).Trim().ToLowerInvariant();
            switch (category)
            {
                case "storyteller":
                    return EntryStoryteller;
                case "messages":
                case "message":
                case "mail":
                    return EntryMessages;
                case "players":
                case "player":
                    return EntryPlayers;
                case "diplomacy":
                    return EntryDiplomacy;
                case "barter":
                case "trade":
                case "trading":
                    return EntryBarter;
            }

            var combined = ((entry.Label ?? string.Empty) + " " + (entry.Text ?? string.Empty)).ToLowerInvariant();
            if (combined.Contains("barter") || combined.Contains("обмен")) return EntryBarter;
            if (combined.Contains("диплом") || combined.Contains("alliance")) return EntryDiplomacy;
            return EntryDefault;
        }

        private static Texture2D ResolveWorldPointIcon(StoryLogEntry entry)
        {
            var text = ((entry.Label ?? string.Empty) + " " + (entry.Text ?? string.Empty) + " " + (entry.Category ?? string.Empty))
                .ToLowerInvariant();

            if (text.Contains("торговый лагерь") || text.Contains("trade_camp")) return PointTradeCamp;
            if (text.Contains("форпост") || text.Contains("outpost")) return PointOutpost;
            if (text.Contains("военная база") || text.Contains("military_base")) return PointOutpost;
            if (text.Contains("шахта") || text.Contains("mine")) return PointCamp;
            if (text.Contains("ферм") || text.Contains("farm")) return PointCamp;
            if (text.Contains("промзон") || text.Contains("industrial_site")) return PointSettlement;
            if (text.Contains("исследовательский узел") || text.Contains("research_hub")) return PointSettlement;
            if (text.Contains("логистический узел") || text.Contains("logistics_hub")) return PointTradeCamp;
            if (text.Contains("город") || text.Contains("city")) return PointSettlement;
            if (text.Contains("укрепленное поселение") || text.Contains("fortified settlement")) return PointSettlement;
            if (text.Contains("поселени") || text.Contains("settlement")) return PointSettlement;
            if (text.Contains("лагерь") || text.Contains("camp")) return PointCamp;
            return null;
        }

        private static Texture2D LoadTexture(string path)
        {
            return ContentFinder<Texture2D>.Get(path, false);
        }

        private static void DrawIcon(Rect rect, Texture2D texture)
        {
            if (texture == null) return;
            Widgets.DrawTextureFitted(rect, texture, 1f);
        }

        private static string T(string text, string fallback)
        {
            return ChatController.ServerCharTranslate(string.IsNullOrWhiteSpace(text) ? fallback : text);
        }

        private static string NormalizeSingleLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return text
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }

        private static string NormalizeBody(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return text
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Trim();
        }

        private static string Cut(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLen) return text;
            return text.Substring(0, maxLen - 3) + "...";
        }

        private static string FormatEntryTime(DateTime createdUtc)
        {
            if (createdUtc <= DateTime.MinValue.AddYears(1)) return "--.-- --:--";

            try
            {
                var utc = createdUtc.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc)
                    : createdUtc;
                return utc.ToLocalTime().ToString("dd.MM HH:mm");
            }
            catch
            {
                return "--.-- --:--";
            }
        }

        private static void JumpToTile(int tile)
        {
            if (tile <= 0) return;

            var settlement = Find.WorldObjects?.SettlementAt(tile);
            if (settlement != null)
            {
                GameUtils.CameraJump(settlement);
                return;
            }

            if (UpdateWorldController.EnsureOnlineSettlementAtTile(tile))
            {
                settlement = Find.WorldObjects?.SettlementAt(tile);
                if (settlement != null)
                {
                    GameUtils.CameraJump(settlement);
                    return;
                }
            }

            GameUtils.CameraJump(tile);
            Messages.Message(
                "На этом тайле сейчас нет активной точки (событие могло завершиться или точка еще не синхронизировалась).",
                MessageTypeDefOf.NeutralEvent);
        }
    }
}
