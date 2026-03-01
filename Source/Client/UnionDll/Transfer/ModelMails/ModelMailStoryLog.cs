using Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Transfer.ModelMails
{
    [Serializable]
    public class ModelMailStoryLog : ModelMail
    {
        public string Title { get; set; }
        public string Summary { get; set; }
        public StoryLogKind Kind { get; set; }
        public bool PopupOnReceive { get; set; }
        public int TotalCount { get; set; }
        public int ShownCount { get; set; }
        public List<StoryLogEntry> Entries { get; set; }

        public override string GetHash()
        {
            var first = Entries?.FirstOrDefault();
            return $"K{Kind}T{TotalCount}S{ShownCount}F{first?.CreatedUtc.Ticks ?? 0}I{first?.Tile ?? 0}";
        }

        public override string ContentString()
        {
            return $"{Title} total:{TotalCount} shown:{ShownCount}";
        }
    }

    public enum StoryLogKind
    {
        Narrative = 0,
        Notifications = 1,
        Mixed = 2
    }
}
