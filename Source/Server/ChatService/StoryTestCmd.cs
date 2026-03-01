using Model;
using OCUnion;
using OCUnion.Transfer.Types;
using ServerOnlineCity.Mechanics;
using ServerOnlineCity.Model;
using ServerOnlineCity.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Transfer;

namespace ServerOnlineCity.ChatService
{
    internal sealed class StoryTestCmd : IChatCmd
    {
        public string CmdID => "storytest";

        public Grants GrantsForRun => Grants.SuperAdmin | Grants.Moderator | Grants.DiscordBot;

        public string Help => ChatManager.prefix + "storytest {random|spawn|spawn_city|grow_city|evolve|spread|conflict|diplomacy|log} [count 1..1000]";

        private readonly ChatManager _chatManager;

        public StoryTestCmd(ChatManager chatManager)
        {
            _chatManager = chatManager;
        }

        public ModelStatus Execute(ref PlayerServer player, Chat chat, List<string> argsM, ServiceContext context)
        {
            var login = player.Public.Login;
            var mode = argsM.Count > 0 ? argsM[0] : "random";
            var count = 1;

            if (argsM.Count > 1 && !int.TryParse(argsM[1], out count))
            {
                return _chatManager.PostCommandPrivatPostActivChat(ChatCmdResult.IncorrectSubCmd, login, chat, "storytest: count должен быть числом.");
            }
            if (count < 1)
            {
                return _chatManager.PostCommandPrivatPostActivChat(ChatCmdResult.IncorrectSubCmd, login, chat, "storytest: count должен быть больше 0.");
            }

            var clipped = false;
            if (count > 1000)
            {
                count = 1000;
                clipped = true;
            }

            var lines = new List<string>();
            var data = Repository.GetData;
            lock (data)
            {
                for (int i = 0; i < count; i++)
                {
                    lines.Add(ServerStoryteller.TriggerDebugEvent(data, mode, login));
                }
                Repository.Get.ChangeData = true;
            }

            if (clipped)
            {
                lines.Insert(0, "storytest: count ограничен до 1000 за один вызов.");
            }

            var summary = "Storyteller test:" + Environment.NewLine + string.Join(Environment.NewLine, lines.Where(l => !string.IsNullOrWhiteSpace(l)));
            return _chatManager.PostCommandPrivatPostActivChat(0, login, chat, summary);
        }
    }
}
