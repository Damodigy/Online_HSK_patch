using OCUnion;
using OCUnion.Transfer;
using RimWorld;
using RimWorldOnlineCity.UI;
using System;
using System.Linq;
using UnityEngine;
using Verse;

namespace RimWorldOnlineCity
{
    public class PanelDebug : DialogControlBase
    {
        private bool IsAdmin =>
            SessionClientController.Data?.IsAdmin == true
            || ((SessionClientController.My?.Grants ?? Grants.NoPermissions) & (Grants.SuperAdmin | Grants.Moderator)) != Grants.NoPermissions;

        public void Drow(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(inRect, "Отладка");

            Text.Font = GameFont.Small;
            var topOffset = 36f;
            Rect rect;

            var status = SessionClientController.IsNetworkDebugEnabled ? "ВКЛ" : "ВЫКЛ";
            rect = new Rect(inRect.x + 20f, inRect.y + topOffset, inRect.width - 40f, 25f);
            Widgets.Label(rect, "Сетевой дебаг: " + status + "  |  Горячая клавиша: " + SessionClientController.NetworkDebugHotkey);
            topOffset += 30f;

            rect = new Rect(inRect.x + 20f, inRect.y + topOffset, 240f, 30f);
            var debugButtonText = SessionClientController.IsNetworkDebugEnabled
                ? "Выключить сетевой дебаг"
                : "Включить сетевой дебаг";
            if (Widgets.ButtonText(rect, debugButtonText))
            {
                var enabled = SessionClientController.ToggleNetworkDebugMode();
                Messages.Message("Сетевой дебаг " + (enabled ? "включен" : "выключен")
                    + " (" + SessionClientController.NetworkDebugHotkey + ")",
                    MessageTypeDefOf.NeutralEvent);
            }
            topOffset += 44f;

            if (!IsAdmin)
            {
                rect = new Rect(inRect.x + 20f, inRect.y + topOffset, inRect.width - 40f, 25f);
                Widgets.Label(rect, "Админ-команды доступны только модератору/суперадмину.");
                return;
            }

            rect = new Rect(inRect.x + 20f, inRect.y + topOffset, inRect.width - 40f, 25f);
            Widgets.Label(rect, "Админ-команды рассказчика:");
            topOffset += 30f;

            var buttons = new[]
            {
                new DebugCommandButton("Случайное событие", "random"),
                new DebugCommandButton("Точка мира", "spawn"),
                new DebugCommandButton("Конфликт", "conflict"),
                new DebugCommandButton("Спавн города", "spawn_city"),
                new DebugCommandButton("Рост города", "grow_city"),
                new DebugCommandButton("Эволюция", "evolve"),
                new DebugCommandButton("Экспансия", "spread"),
                new DebugCommandButton("Дипломатия", "diplomacy"),
                new DebugCommandButton("Запись в лог", "log")
            };

            var buttonWidth = 180f;
            var buttonHeight = 30f;
            var gapX = 8f;
            var gapY = 6f;
            var columns = Math.Max(1, (int)Math.Floor((inRect.width - 40f + gapX) / (buttonWidth + gapX)));
            if (columns > 3) columns = 3;

            for (int i = 0; i < buttons.Length; i++)
            {
                var row = i / columns;
                var col = i % columns;
                var x = inRect.x + 20f + col * (buttonWidth + gapX);
                var y = inRect.y + topOffset + row * (buttonHeight + gapY);
                rect = new Rect(x, y, buttonWidth, buttonHeight);
                if (Widgets.ButtonText(rect, buttons[i].Label))
                {
                    SendStorytellerTestCommand(buttons[i].Mode);
                }
            }
        }

        private void SendStorytellerTestCommand(string mode)
        {
            var mainChat = SessionClientController.Data?.Chats?.FirstOrDefault();
            if (mainChat == null)
            {
                Messages.Message("Не найден чат для отправки команды storyteller.", MessageTypeDefOf.RejectInput);
                return;
            }

            var command = "/storytest " + mode;
            SessionClientController.Command((connect) =>
            {
                var result = connect.PostingChat(mainChat.Id, command);
                var isOk = result != null && result.Status == 0;
                var message = isOk
                    ? (string.IsNullOrWhiteSpace(result.Message)
                        ? "Команда отправлена: " + command
                        : result.Message.ServerTranslate())
                    : "Ошибка storyteller: " + (result?.Message?.ServerTranslate() ?? connect.ErrorMessage?.ServerTranslate() ?? "неизвестная ошибка");

                Messages.Message(message, isOk ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.RejectInput);
            });
        }

        private readonly struct DebugCommandButton
        {
            public readonly string Label;
            public readonly string Mode;

            public DebugCommandButton(string label, string mode)
            {
                Label = label;
                Mode = mode;
            }
        }
    }
}
