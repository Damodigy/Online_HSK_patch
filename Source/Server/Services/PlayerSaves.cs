using OCUnion.Transfer.Model;
using ServerOnlineCity.Model;
using Transfer;

namespace ServerOnlineCity.Services
{
    internal sealed class PlayerSaves : IGenerateResponseContainer
    {
        public int RequestTypePackage => (int)PackageType.Request51Free;

        public int ResponseTypePackage => (int)PackageType.Response52Free;

        public ModelContainer GenerateModelContainer(ModelContainer request, ServiceContext context)
        {
            if (context.Player == null) return null;

            var packet = request.Packet as ModelPlayerSaveRequest;
            var response = new ModelPlayerSaveResponse()
            {
                Status = 1,
                Message = "Некорректный запрос.",
            };

            if (packet != null)
            {
                response = HandleRequest(packet, context);
            }

            return new ModelContainer()
            {
                TypePacket = ResponseTypePackage,
                Packet = response
            };
        }

        private ModelPlayerSaveResponse HandleRequest(ModelPlayerSaveRequest packet, ServiceContext context)
        {
            lock (context.Player)
            {
                var login = context.Player.Public.Login;
                switch (packet.RequestType)
                {
                    case PlayerSaveRequestType.GetList:
                        return Repository.GetSaveData.GetPlayerSaves(login);

                    case PlayerSaveRequestType.Rename:
                        {
                            var ok = Repository.GetSaveData.RenamePlayerSaveSlot(login, packet.Slot, packet.IsAuto, packet.Name, out var message);
                            var response = Repository.GetSaveData.GetPlayerSaves(login);
                            response.Status = ok ? 0 : 1;
                            response.Message = message;
                            return response;
                        }

                    case PlayerSaveRequestType.SetActive:
                        {
                            var response = Repository.GetSaveData.GetPlayerSaves(login);
                            if (!Repository.GetSaveData.SetActiveSaveSlot(login, packet.Slot, packet.IsAuto, out var message))
                            {
                                response.Status = 1;
                                response.Message = message;
                                return response;
                            }

                            response = Repository.GetSaveData.GetPlayerSaves(login);
                            response.Status = 0;
                            response.Message = message;
                            return response;
                        }

                    default:
                        {
                            var response = Repository.GetSaveData.GetPlayerSaves(login);
                            response.Status = 1;
                            response.Message = "Неизвестная операция со слотами сохранений.";
                            return response;
                        }
                }
            }
        }
    }
}
