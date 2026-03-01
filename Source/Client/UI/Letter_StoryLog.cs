using Model;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Text;
using Transfer.ModelMails;
using Verse;

namespace RimWorldOnlineCity.UI
{
    public sealed class Letter_StoryLog : ChoiceLetter
    {
        private string storyTitle;
        private string storySummary;
        private StoryLogKind storyKind;
        private int storyTotalCount;
        private int storyShownCount;
        private List<StoryLogEntry> storyEntries;

        public Letter_StoryLog()
        {
        }

        public Letter_StoryLog(ModelMailStoryLog mail, string title, string summary, LetterDef letterDef)
        {
            SetData(mail, title, summary, letterDef);
        }

        public void SetData(ModelMailStoryLog mail, string title, string summary, LetterDef letterDef)
        {
            storyTitle = title ?? "Журнал";
            storySummary = summary ?? "Доступны новые записи.";
            storyKind = mail?.Kind ?? StoryLogKind.Narrative;
            storyTotalCount = mail?.TotalCount ?? 0;
            storyShownCount = mail?.ShownCount ?? 0;
            storyEntries = CloneEntries(mail?.Entries);

            Label = storyTitle;
            Text = storySummary;
            def = letterDef ?? LetterDefOf.NeutralEvent;
        }

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                yield return BuildOpenLogOption();

                var jumpOption = Option_JumpToLocation;
                if (jumpOption != null)
                {
                    yield return jumpOption;
                }

                var closeOption = Option_Close;
                if (closeOption != null)
                {
                    yield return closeOption;
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_Values.Look(ref storyTitle, "oc_storyTitle", "Журнал");
            Scribe_Values.Look(ref storySummary, "oc_storySummary", "Доступны новые записи.");
            Scribe_Values.Look(ref storyTotalCount, "oc_storyTotalCount", 0);
            Scribe_Values.Look(ref storyShownCount, "oc_storyShownCount", 0);
            Scribe_Values.Look(ref storyKind, "oc_storyKind", StoryLogKind.Narrative);

            List<string> packedEntries = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                packedEntries = PackEntries(storyEntries);
            }
            Scribe_Collections.Look(ref packedEntries, "oc_storyEntries", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                storyEntries = UnpackEntries(packedEntries);
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (storyEntries == null) storyEntries = new List<StoryLogEntry>();
                Label = storyTitle ?? "Журнал";
                Text = storySummary ?? "Доступны новые записи.";
                if (def == null) def = OC_LetterDefOf.GreyGoldenLetter ?? LetterDefOf.NeutralEvent;
            }
        }

        private DiaOption BuildOpenLogOption()
        {
            var option = new DiaOption("Открыть журнал");
            option.action = () =>
            {
                var mail = BuildMail();
                if ((mail.Entries?.Count ?? 0) == 0)
                {
                    Messages.Message("Журнал пуст или недоступен.", MessageTypeDefOf.RejectInput);
                    return;
                }

                Find.WindowStack.Add(new Dialog_StoryLog(mail));
            };
            option.resolveTree = true;
            return option;
        }

        private ModelMailStoryLog BuildMail()
        {
            return new ModelMailStoryLog()
            {
                Kind = storyKind,
                Title = storyTitle,
                Summary = storySummary,
                TotalCount = storyTotalCount,
                ShownCount = storyShownCount,
                Entries = CloneEntries(storyEntries)
            };
        }

        private static List<StoryLogEntry> CloneEntries(List<StoryLogEntry> source)
        {
            if (source == null || source.Count == 0) return new List<StoryLogEntry>();

            return source
                .Where(e => e != null)
                .Select(e => new StoryLogEntry()
                {
                    CreatedUtc = e.CreatedUtc,
                    Category = e.Category,
                    Label = e.Label,
                    Text = e.Text,
                    Tile = e.Tile
                })
                .ToList();
        }

        private static List<string> PackEntries(List<StoryLogEntry> source)
        {
            var result = new List<string>();
            if (source == null) return result;

            foreach (var entry in source)
            {
                if (entry == null) continue;
                var line = string.Join("\t", new[]
                {
                    entry.CreatedUtc.Ticks.ToString(),
                    SanitizePackedField(entry.Category),
                    SanitizePackedField(entry.Label),
                    SanitizePackedField(entry.Text),
                    entry.Tile.ToString()
                });
                result.Add(line);
            }

            return result;
        }

        private static List<StoryLogEntry> UnpackEntries(List<string> packed)
        {
            var result = new List<StoryLogEntry>();
            if (packed == null) return result;

            foreach (var line in packed)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 5) continue;

                long ticks;
                int tile;
                if (!long.TryParse(parts[0], out ticks)) ticks = DateTime.UtcNow.Ticks;
                if (!int.TryParse(parts[4], out tile)) tile = 0;

                result.Add(new StoryLogEntry()
                {
                    CreatedUtc = new DateTime(ticks, DateTimeKind.Utc),
                    Category = RestorePackedField(parts[1]),
                    Label = RestorePackedField(parts[2]),
                    Text = RestorePackedField(parts[3]),
                    Tile = tile
                });
            }

            return result;
        }

        private static string SanitizePackedField(string value)
        {
            var safe = value ?? string.Empty;
            return System.Convert.ToBase64String(Encoding.UTF8.GetBytes(safe));
        }

        private static string RestorePackedField(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            try
            {
                var bytes = System.Convert.FromBase64String(value);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
