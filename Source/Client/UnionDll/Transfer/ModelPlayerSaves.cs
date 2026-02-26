using System;
using System.Collections.Generic;

namespace Transfer
{
    [Serializable]
    public enum PlayerSaveRequestType : byte
    {
        GetList = 1,
        Rename = 2,
        SetActive = 3
    }

    [Serializable]
    public class ModelPlayerSaveRequest
    {
        public PlayerSaveRequestType RequestType { get; set; }
        public int Slot { get; set; }
        public bool IsAuto { get; set; }
        public string Name { get; set; }
    }

    [Serializable]
    public class ModelPlayerSaveSlot
    {
        public int Slot { get; set; }
        public bool IsAuto { get; set; }
        public string Name { get; set; }
        public bool Exists { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public long SizeBytes { get; set; }
    }

    [Serializable]
    public class ModelPlayerSaveResponse : ModelStatus
    {
        public int MaxSlots { get; set; }
        public int ActiveSlot { get; set; }
        public int ManualSlotsCount { get; set; }
        public int AutoSlotsCount { get; set; }
        public int ActiveAutoSlot { get; set; }
        public List<ModelPlayerSaveSlot> Saves { get; set; } = new List<ModelPlayerSaveSlot>();
    }
}
