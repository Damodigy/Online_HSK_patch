using System;

namespace ServerOnlineCity.Model
{
    [Serializable]
    public class ServerStoryEvent
    {
        public long Id { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Category { get; set; }
        public string Key { get; set; }
        public string Label { get; set; }
        public string Text { get; set; }
        public int Tile { get; set; }
    }
}
