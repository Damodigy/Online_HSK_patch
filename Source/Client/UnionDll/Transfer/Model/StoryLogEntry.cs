using System;

namespace Model
{
    [Serializable]
    public class StoryLogEntry
    {
        public DateTime CreatedUtc { get; set; }

        /// <summary>
        /// Категория записи: storyteller/messages/players...
        /// </summary>
        public string Category { get; set; }

        public string Label { get; set; }
        public string Text { get; set; }
        public int Tile { get; set; }
    }
}
