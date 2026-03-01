using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Transfer
{
    /// <summary>
    /// Таймер фоново поддерживающий открытое соединение при простое
    /// </summary>
    public static class ConnectSaver
    {
        private static Dictionary<ConnectClient, Action<ConnectClient>> Clients = new Dictionary<ConnectClient, Action<ConnectClient>>();

        private static Thread Worker;

        public static void AddClient(ConnectClient client, Action<ConnectClient> ping)
        {
            lock (Clients)
            {
                Clients[client] = ping;
            }
            StartWorker();
        }

        private static void StartWorker()
        {
            if (Worker != null) return;
            Worker = new Thread(WorkerDo);
            Worker.IsBackground = true;
            Worker.Start();
        }

        private static void WorkerDo()
        {
            while (true)
            {
                Thread.Sleep(60000);
                var now = DateTime.UtcNow.AddMinutes(2);
                List<KeyValuePair<ConnectClient, Action<ConnectClient>>> clientsSnapshot;
                lock (Clients)
                {
                    if (Clients.Count == 0)
                    {
                        Worker = null;
                        return;
                    }
                    clientsSnapshot = Clients.ToList();
                }

                var toRemove = new List<ConnectClient>();

                foreach (var kvp in clientsSnapshot)
                {
                    var client = kvp.Key;
                    if (client == null || client.Client == null || !client.Client.Connected)
                    {
                        toRemove.Add(client);
                        continue;
                    }
                    if (now > client.LastSend)
                    {
                        try
                        {
                            //запуск пинга через 2-3 мин после последнего обращения (в т.ч. пинга)
                            kvp.Value(client);
                        }
                        catch
                        {
                            toRemove.Add(client);
                        }
                    }
                }

                if (toRemove.Count > 0)
                {
                    lock (Clients)
                    {
                        foreach (var client in toRemove)
                        {
                            if (client != null) Clients.Remove(client);
                        }
                    }
                }
            }
        }
    }
}
