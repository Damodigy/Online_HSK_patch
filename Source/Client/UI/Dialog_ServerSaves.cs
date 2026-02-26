using OCUnion;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using Transfer;
using UnityEngine;
using Verse;

namespace RimWorldOnlineCity
{
    /// <summary>
    /// Диалог управления серверными слотами сохранений (ручные и авто).
    /// </summary>
    public class Dialog_ServerSaves : Window
    {
        private Vector2 ScrollPosition = Vector2.zero;
        private ModelPlayerSaveResponse SavesResponse;
        private bool SelectedIsAuto;
        private int SelectedSlot = 1;
        private string SlotNameInput = string.Empty;
        private string StatusText = string.Empty;

        public override Vector2 InitialSize => new Vector2(820f, 600f);

        public Dialog_ServerSaves()
        {
            closeOnCancel = true;
            closeOnAccept = false;
            doCloseButton = false;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            RefreshSavesFromServer();
        }

        private static string GetDefaultSlotName(bool isAuto, int slot)
        {
            return isAuto ? "Автослот " + slot.ToString() : "Слот " + slot.ToString();
        }

        private int GetSlotsCount(bool isAuto)
        {
            if (SavesResponse == null) return isAuto ? 0 : 1;
            if (isAuto) return Math.Max(0, SavesResponse.AutoSlotsCount);
            return Math.Max(1, SavesResponse.ManualSlotsCount);
        }

        private int ClampSlot(bool isAuto, int slot)
        {
            var maxSlots = GetSlotsCount(isAuto);
            if (isAuto)
            {
                if (maxSlots <= 0) return 1;
                if (slot < 1) return 1;
                if (slot > maxSlots) return maxSlots;
                return slot;
            }

            if (slot < 1) return 1;
            if (slot > maxSlots) return maxSlots;
            return slot;
        }

        private IEnumerable<ModelPlayerSaveSlot> GetSlotsOrdered()
        {
            if (SavesResponse?.Saves == null) yield break;

            foreach (var slot in SavesResponse.Saves.Where(s => !s.IsAuto).OrderBy(s => s.Slot))
            {
                yield return slot;
            }

            foreach (var slot in SavesResponse.Saves.Where(s => s.IsAuto).OrderBy(s => s.Slot))
            {
                yield return slot;
            }
        }

        private ModelPlayerSaveSlot GetSlotInfo(bool isAuto, int slot)
        {
            return SavesResponse?.Saves?.FirstOrDefault(s => s.IsAuto == isAuto && s.Slot == slot);
        }

        private void SelectSlot(bool isAuto, int slot)
        {
            if (isAuto && GetSlotsCount(true) <= 0)
            {
                isAuto = false;
                slot = 1;
            }

            SelectedIsAuto = isAuto;
            SelectedSlot = ClampSlot(SelectedIsAuto, slot);

            var save = GetSlotInfo(SelectedIsAuto, SelectedSlot);
            SlotNameInput = save?.Name ?? GetDefaultSlotName(SelectedIsAuto, SelectedSlot);
        }

        private void RefreshSavesFromServer()
        {
            try
            {
                ModelPlayerSaveResponse response = null;
                SessionClientController.Command(connect =>
                {
                    response = connect.GetPlayerSaves();
                });

                if (response == null)
                {
                    StatusText = "Не удалось получить список серверных сохранений.";
                    return;
                }

                ApplySavesResponse(response, false);
                StatusText = response.Status == 0
                    ? "Список сохранений обновлён."
                    : (response.Message ?? "Ошибка обновления списка сохранений.");
            }
            catch (Exception ex)
            {
                StatusText = "Ошибка загрузки списка сохранений: " + ex.Message;
                Loger.Log("Client Dialog_ServerSaves RefreshSavesFromServer ошибка: " + ex);
            }
        }

        private void ApplySavesResponse(ModelPlayerSaveResponse response, bool keepSelection)
        {
            SavesResponse = response ?? new ModelPlayerSaveResponse();
            if (SavesResponse.ManualSlotsCount <= 0)
            {
                SavesResponse.ManualSlotsCount = SavesResponse.MaxSlots > 0 ? SavesResponse.MaxSlots : 1;
            }
            if (SavesResponse.AutoSlotsCount < 0) SavesResponse.AutoSlotsCount = 0;
            if (SavesResponse.MaxSlots <= 0) SavesResponse.MaxSlots = SavesResponse.ManualSlotsCount;
            if (SavesResponse.Saves == null) SavesResponse.Saves = new List<ModelPlayerSaveSlot>();

            if (SessionClientController.Data != null)
            {
                SessionClientController.Data.SaveSlotsCount = SavesResponse.ManualSlotsCount;
                SessionClientController.Data.AutoSaveSlotsCount = SavesResponse.AutoSlotsCount;

                SessionClientController.Data.SaveSlotNumber = ClampSlot(false,
                    SavesResponse.ActiveSlot > 0 ? SavesResponse.ActiveSlot : SessionClientController.Data.SaveSlotNumber);

                if (SavesResponse.AutoSlotsCount > 0)
                {
                    SessionClientController.Data.AutoSaveSlotNumber = ClampSlot(true,
                        SavesResponse.ActiveAutoSlot > 0 ? SavesResponse.ActiveAutoSlot : SessionClientController.Data.AutoSaveSlotNumber);
                }
                else
                {
                    SessionClientController.Data.AutoSaveSlotNumber = 1;
                }
            }

            if (!keepSelection)
            {
                var manualActive = SavesResponse.ActiveSlot > 0 ? SavesResponse.ActiveSlot : 1;
                SelectSlot(false, manualActive);
                return;
            }

            if (SelectedIsAuto && SavesResponse.AutoSlotsCount <= 0)
            {
                SelectedIsAuto = false;
                SelectedSlot = SavesResponse.ActiveSlot > 0 ? SavesResponse.ActiveSlot : 1;
            }

            SelectSlot(SelectedIsAuto, SelectedSlot);
        }

        private void RenameSelectedSlot()
        {
            try
            {
                ModelPlayerSaveResponse response = null;
                SessionClientController.Command(connect =>
                {
                    response = connect.RenamePlayerSave(SelectedSlot, SelectedIsAuto, SlotNameInput);
                });

                if (response == null)
                {
                    StatusText = "Не удалось переименовать слот.";
                    return;
                }

                ApplySavesResponse(response, true);
                StatusText = response.Status == 0
                    ? (string.IsNullOrWhiteSpace(response.Message) ? "Название слота обновлено." : response.Message)
                    : (response.Message ?? "Ошибка переименования слота.");
            }
            catch (Exception ex)
            {
                StatusText = "Ошибка переименования слота: " + ex.Message;
                Loger.Log("Client Dialog_ServerSaves RenameSelectedSlot ошибка: " + ex);
            }
        }

        private void SaveToSelectedSlot()
        {
            if (SessionClientController.Data == null)
            {
                StatusText = "Данные клиента недоступны.";
                return;
            }

            if (SelectedIsAuto && SessionClientController.Data.AutoSaveSlotsCount <= 0)
            {
                StatusText = "Автослоты отключены на сервере.";
                return;
            }

            if (!TryApplySlotNameBeforeSave())
            {
                return;
            }

            if (SelectedIsAuto)
            {
                SessionClientController.Data.AutoSaveSlotNumber = SelectedSlot;
            }
            else
            {
                SessionClientController.Data.SaveSlotNumber = SelectedSlot;
            }

            SessionClientController.SaveGameNow(false, () =>
            {
                var slotText = SelectedIsAuto ? "автослот #" + SelectedSlot : "слот #" + SelectedSlot;
                Messages.Message("Сохранение отправлено на сервер в " + slotText + ".", MessageTypeDefOf.PositiveEvent);
            }, SelectedIsAuto, SelectedSlot);
            Close();
        }

        /// <summary>
        /// Если игрок ввёл имя слота перед сохранением, отправляем это имя на сервер сразу,
        /// чтобы не требовалось отдельное нажатие кнопки "Переименовать".
        /// </summary>
        private bool TryApplySlotNameBeforeSave()
        {
            var desiredName = string.IsNullOrWhiteSpace(SlotNameInput) ? null : SlotNameInput.Trim();

            var currentInfo = GetSlotInfo(SelectedIsAuto, SelectedSlot);
            var currentName = currentInfo?.Name;
            if (string.IsNullOrWhiteSpace(currentName))
            {
                currentName = GetDefaultSlotName(SelectedIsAuto, SelectedSlot);
            }
            currentName = string.IsNullOrWhiteSpace(currentName) ? null : currentName.Trim();

            if (string.Equals(desiredName ?? string.Empty, currentName ?? string.Empty, StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                ModelPlayerSaveResponse response = null;
                SessionClientController.Command(connect =>
                {
                    response = connect.RenamePlayerSave(SelectedSlot, SelectedIsAuto, desiredName);
                });

                if (response == null)
                {
                    StatusText = "Не удалось сохранить имя слота перед сохранением мира.";
                    return false;
                }

                ApplySavesResponse(response, true);
                if (response.Status != 0)
                {
                    StatusText = response.Message ?? "Ошибка сохранения имени слота.";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(response.Message))
                {
                    StatusText = response.Message;
                }
                return true;
            }
            catch (Exception ex)
            {
                StatusText = "Ошибка сохранения имени слота: " + ex.Message;
                Loger.Log("Client Dialog_ServerSaves TryApplySlotNameBeforeSave ошибка: " + ex);
                return false;
            }
        }

        private void LoadSelectedSlot()
        {
            if (SessionClientController.Data == null)
            {
                StatusText = "Данные клиента недоступны.";
                return;
            }

            try
            {
                ModelPlayerSaveResponse response = null;
                SessionClientController.Command(connect =>
                {
                    response = connect.SetActivePlayerSave(SelectedSlot, SelectedIsAuto);
                });

                if (response == null)
                {
                    StatusText = "Не удалось выбрать слот загрузки.";
                    return;
                }

                ApplySavesResponse(response, true);
                if (response.Status != 0)
                {
                    StatusText = response.Message ?? "Ошибка выбора слота загрузки.";
                    return;
                }

                var slotText = SelectedIsAuto
                    ? "автослот #" + SelectedSlot
                    : "слот #" + SelectedSlot;
                StatusText = string.IsNullOrWhiteSpace(response.Message)
                    ? "Выбран " + slotText + " для загрузки."
                    : response.Message;

                Messages.Message("Запрошена загрузка " + slotText + ". Выполняем переподключение.", MessageTypeDefOf.NeutralEvent);
                Close();
                SessionClientController.ReloadWorldFromServerSave(slotText);
            }
            catch (Exception ex)
            {
                StatusText = "Ошибка выбора слота загрузки: " + ex.Message;
                Loger.Log("Client Dialog_ServerSaves LoadSelectedSlot ошибка: " + ex);
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 180f, 36f), "Серверные сохранения");
            Text.Font = GameFont.Small;

            if (Widgets.ButtonText(new Rect(inRect.width - 170f, inRect.y, 170f, 32f), "Обновить список"))
            {
                RefreshSavesFromServer();
            }

            var manualCount = GetSlotsCount(false);
            var autoCount = GetSlotsCount(true);
            Widgets.Label(new Rect(inRect.x, inRect.y + 38f, inRect.width, 24f),
                "Ручных слотов: " + manualCount + "    Автослотов: " + autoCount);

            var listRect = new Rect(inRect.x, inRect.y + 64f, inRect.width, inRect.height - 210f);
            Widgets.DrawMenuSection(listRect);

            var slots = GetSlotsOrdered().ToList();
            var viewRect = listRect.ContractedBy(8f);
            const float rowHeight = 34f;
            var contentHeight = Math.Max(viewRect.height, slots.Count * rowHeight + 6f);
            var outRect = new Rect(0f, 0f, viewRect.width - 16f, contentHeight);

            Widgets.BeginScrollView(viewRect, ref ScrollPosition, outRect);
            var y = 0f;
            foreach (var slotInfo in slots)
            {
                var rowRect = new Rect(0f, y, outRect.width, rowHeight - 2f);
                var isSelected = slotInfo.IsAuto == SelectedIsAuto && slotInfo.Slot == SelectedSlot;

                if (isSelected)
                {
                    Widgets.DrawHighlightSelected(rowRect);
                }
                else if (Mouse.IsOver(rowRect))
                {
                    Widgets.DrawHighlight(rowRect);
                }

                if (Widgets.ButtonInvisible(rowRect))
                {
                    SelectSlot(slotInfo.IsAuto, slotInfo.Slot);
                }

                var slotName = string.IsNullOrWhiteSpace(slotInfo.Name)
                    ? GetDefaultSlotName(slotInfo.IsAuto, slotInfo.Slot)
                    : slotInfo.Name;

                var stateText = slotInfo.Exists
                    ? slotInfo.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "  " + FormatBytes(slotInfo.SizeBytes)
                    : "Пусто";

                var slotPrefix = slotInfo.IsAuto ? "A" : "M";
                Widgets.Label(new Rect(rowRect.x + 8f, rowRect.y + 8f, 60f, 24f), slotPrefix + "#" + slotInfo.Slot);
                Widgets.Label(new Rect(rowRect.x + 72f, rowRect.y + 8f, outRect.width - 320f, 24f), slotName);
                Widgets.Label(new Rect(outRect.width - 240f, rowRect.y + 8f, 230f, 24f), stateText);

                y += rowHeight;
            }
            Widgets.EndScrollView();

            var selectedInfo = GetSlotInfo(SelectedIsAuto, SelectedSlot);
            var selectedName = string.IsNullOrWhiteSpace(selectedInfo?.Name)
                ? GetDefaultSlotName(SelectedIsAuto, SelectedSlot)
                : selectedInfo.Name;
            var selectedType = SelectedIsAuto ? "автослот" : "слот";

            Widgets.Label(new Rect(inRect.x, inRect.height - 140f, inRect.width, 24f),
                "Выбран " + selectedType + " #" + SelectedSlot + ": " + selectedName);

            Widgets.Label(new Rect(inRect.x, inRect.height - 114f, 170f, 24f), "Название слота:");
            SlotNameInput = GUI.TextField(new Rect(inRect.x + 174f, inRect.height - 116f, inRect.width - 330f, 28f), SlotNameInput ?? string.Empty, 128);

            if (Widgets.ButtonText(new Rect(inRect.width - 148f, inRect.height - 116f, 148f, 28f), "Переименовать"))
            {
                RenameSelectedSlot();
            }

            var activeManual = SessionClientController.Data?.SaveSlotNumber ?? 1;
            var activeAuto = SessionClientController.Data?.AutoSaveSlotNumber ?? 1;
            var activeText = autoCount > 0
                ? "Активные слоты: ручной #" + activeManual + ", авто #" + activeAuto
                : "Активный ручной слот: #" + activeManual + " (автослоты отключены)";
            Widgets.Label(new Rect(inRect.x, inRect.height - 84f, inRect.width, 24f), activeText);

            if (!string.IsNullOrWhiteSpace(StatusText))
            {
                Widgets.Label(new Rect(inRect.x, inRect.height - 60f, inRect.width, 24f), StatusText);
            }

            var saveButtonText = SelectedIsAuto
                ? "Сохранить в выбранный автослот"
                : "Сохранить в выбранный слот";
            if (Widgets.ButtonText(new Rect(inRect.x, inRect.height - 34f, 280f, 34f), saveButtonText))
            {
                SaveToSelectedSlot();
            }

            var loadButtonText = SelectedIsAuto
                ? "Загрузить выбранный автослот"
                : "Загрузить выбранный слот";
            if (Widgets.ButtonText(new Rect(inRect.x + 290f, inRect.height - 34f, 280f, 34f), loadButtonText))
            {
                LoadSelectedSlot();
            }

            if (Widgets.ButtonText(new Rect(inRect.width - 140f, inRect.height - 34f, 140f, 34f), "Закрыть"))
            {
                Close();
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024L) return bytes.ToString() + " B";
            if (bytes < 1024L * 1024L) return (bytes / 1024L).ToString() + " KB";
            return (bytes / (1024L * 1024L)).ToString() + " MB";
        }
    }
}
