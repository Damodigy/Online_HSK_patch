using System;
using System.Linq;
using OCUnion;
using OCUnion.Transfer.Model;
using ServerOnlineCity.Model;
using Transfer;
using Transfer.ModelMails;

namespace ServerOnlineCity.Services
{
    internal sealed class SendThings : IGenerateResponseContainer
    {
        public int RequestTypePackage => (int)PackageType.Request15;

        public int ResponseTypePackage => (int)PackageType.Response16;

        public ModelContainer GenerateModelContainer(ModelContainer request, ServiceContext context)
        {
            if (context.Player == null) return null;
            var result = new ModelContainer() { TypePacket = ResponseTypePackage };
            result.Packet = sendThings((ModelMailTrade)request.Packet, context);
            return result;
        }

        public ModelStatus sendThings(ModelMailTrade packet, ServiceContext context)
        {
            if (packet == null)
            {
                return new ModelStatus()
                {
                    Status = 1,
                    Message = "Empty trade packet"
                };
            }
            if (context?.Player?.Public == null)
            {
                return new ModelStatus()
                {
                    Status = 1,
                    Message = "Sender is not initialized"
                };
            }
            if (packet.PlaceServerId <= 0)
            {
                Loger.Log("Mail SendThings reject: destination place is not specified", Loger.LogLevel.WARNING);
                return new ModelStatus()
                {
                    Status = 1,
                    Message = "Destination place is not specified"
                };
            }
            if (packet.Things == null || packet.Things.Count == 0)
            {
                Loger.Log("Mail SendThings reject: no items", Loger.LogLevel.WARNING);
                return new ModelStatus()
                {
                    Status = 1,
                    Message = "No items to send"
                };
            }

            PlayerServer toPlayer;

            lock (context.Player)
            {
                var data = Repository.GetData;

                toPlayer = ResolveRecipient(packet, data);
                if (toPlayer == null)
                {
                    var rejectLogin = packet.To?.Login ?? "-";
                    Loger.Log($"Mail SendThings reject: destination not found login={rejectLogin} place={packet.PlaceServerId}", Loger.LogLevel.WARNING);
                    return new ModelStatus()
                    {
                        Status = 1,
                        Message = "Destination not found"
                    };
                }

                // Use authenticated sender from current session; do not trust packet.From from client.
                packet.From = context.Player.Public;
                packet.To = toPlayer.Public;
                packet.Created = DateTime.UtcNow;
                packet.NeedSaveGame = true;
            }
            lock (toPlayer)
            {
                toPlayer.Mails.Add(packet);
            }
            var fromLogin = packet.From?.Login ?? "-";
            var toLogin = packet.To?.Login ?? "-";
            Loger.Log($"Mail SendThings {fromLogin}->{toLogin} {packet.ContentString()}");
            return new ModelStatus()
            {
                Status = 0,
                Message = "Load shipped"
            };
        }

        private static PlayerServer ResolveRecipient(ModelMailTrade packet, BaseContainer data)
        {
            if (packet == null || data == null) return null;

            var requestedLogin = packet.To?.Login;
            PlayerServer toPlayer = null;
            if (!string.IsNullOrWhiteSpace(requestedLogin))
            {
                toPlayer = Repository.GetPlayerByLogin(requestedLogin)
                    ?? data.GetPlayersAll.FirstOrDefault(p =>
                        p?.Public != null
                        && string.Equals(p.Public.Login, requestedLogin, StringComparison.OrdinalIgnoreCase));
            }
            if (toPlayer != null) return toPlayer;

            if (packet.PlaceServerId > 0)
            {
                var ownerLogin = data.WorldObjects?
                    .FirstOrDefault(wo => wo != null && wo.PlaceServerId == packet.PlaceServerId)
                    ?.LoginOwner;
                if (!string.IsNullOrWhiteSpace(ownerLogin))
                {
                    toPlayer = Repository.GetPlayerByLogin(ownerLogin)
                        ?? data.GetPlayersAll.FirstOrDefault(p =>
                            p?.Public != null
                            && string.Equals(p.Public.Login, ownerLogin, StringComparison.OrdinalIgnoreCase));
                }
            }

            return toPlayer;
        }
    }
}
