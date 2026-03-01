using Model;
using OCUnion;
using OCUnion.Transfer.Model;
using ServerOnlineCity.Model;
using System;
using System.Linq;
using Transfer;

namespace ServerOnlineCity.Services
{
    internal sealed class AttackOnlineInitiator : IGenerateResponseContainer
    {
        public int RequestTypePackage => (int)PackageType.Request27;

        public int ResponseTypePackage => (int)PackageType.Response28;

        public ModelContainer GenerateModelContainer(ModelContainer request, ServiceContext context)
        {
            if (context.Player == null) return null;
            var result = new ModelContainer() { TypePacket = ResponseTypePackage };
            result.Packet = attackOnlineInitiator((AttackInitiatorToSrv)request.Packet, context);
            return result;
        }

        public AttackInitiatorFromSrv attackOnlineInitiator(AttackInitiatorToSrv fromClient, ServiceContext context)
        {
            lock (context.Player)
            {
                var data = Repository.GetData;
                var res = new AttackInitiatorFromSrv();

                if (fromClient.StartHostPlayer != null)
                {
                    // PvP combat is disabled; only safe test-mode visit sync is allowed.
                    if (!fromClient.TestMode)
                    {
                        return new AttackInitiatorFromSrv()
                        {
                            ErrorText = "PvP/Attack module is temporarily disabled on this server"
                        };
                    }

                    if (context.Player.AttackData != null)
                    {
                        res.ErrorText = "There is an active attack";
                        return res;
                    }
                    var hostPlayer = data.GetPlayersAll.FirstOrDefault(p => p.Public.Login == fromClient.StartHostPlayer);

                    if (hostPlayer == null)
                    {
                        res.ErrorText = "Player not found";
                        return res;
                    }

                    if (hostPlayer.AttackData != null)
                    {
                        res.ErrorText = "The player participates in the attack";
                        return res;
                    }

                    var att = new AttackServer();
                    var err = att.New(context.Player, hostPlayer, fromClient, fromClient.TestMode);
                    if (err != null)
                    {
                        res.ErrorText = err;
                        return res;
                    }
                }

                if (context.Player.AttackData == null)
                {
                    // cleanup/ping after session closed - no hard error.
                    if (fromClient.State == AttackServer.VisitCleanupState)
                    {
                        return new AttackInitiatorFromSrv() { State = AttackServer.VisitCleanupState };
                    }

                    Loger.Log("Server AttackOnlineInitiator Unexpected error, no data", Loger.LogLevel.ERROR);
                    res.ErrorText = "Unexpected error, no data";
                    return res;
                }

                return context.Player.AttackData.RequestInitiator(fromClient);
            }
        }
    }
}
