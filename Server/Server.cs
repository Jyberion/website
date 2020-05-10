using System.Diagnostics;
using System;
using System.IO;
using System.Linq;
using System.Timers;
using System.Collections.Generic;

using EF;
using ENet;

namespace Valk.Networking
{
    class Server
    {
        public Host server;
        public Timer positionUpdatePump;

        public static byte channelID = 0;

        private ushort port;
        private int maxClients;

        private Clients clients;

        private bool serverRunning;

        public Server(ushort port, int maxClients)
        {
            this.port = port;
            this.maxClients = maxClients;

            clients = new Clients();

            positionUpdatePump = new Timer(1000, PositionUpdates);
            positionUpdatePump.Start();
        }

        public void Start()
        {
            Library.Initialize();

            server = new Host();

            var address = new Address();
            address.Port = port;
            server.Create(address, maxClients);
            serverRunning = true;

            Logger.Log($"Server listening on {port}");

            //int packetCounter = 0;

            Event netEvent;

            while (serverRunning)
            {
                bool polled = false;

                while (!polled)
                {
                    if (!server.IsSet)
                        return;

                    if (server.CheckEvents(out netEvent) <= 0)
                    {
                        if (server.Service(15, out netEvent) <= 0)
                            break;

                        polled = true;
                    }

                    switch (netEvent.Type)
                    {
                        case EventType.None:
                            break;

                        case EventType.Connect:
                            Logger.Log("Client connected - ID: " + netEvent.Peer.ID + ", IP: " + netEvent.Peer.IP);
                            clients.Add += ClientAdd;
                            //clients.Add(netEvent);
                            break;

                        case EventType.Disconnect:
                            Logger.Log("Client disconnected - ID: " + netEvent.Peer.ID + ", IP: " + netEvent.Peer.IP);
                            break;

                        case EventType.Timeout:
                            Logger.Log("Client timeout - ID: " + netEvent.Peer.ID + ", IP: " + netEvent.Peer.IP);
                            clients.Remove(netEvent);
                            break;

                        case EventType.Receive:
                            //Logger.Log($"{packetCounter++} Packet received from - ID: {netEvent.Peer.ID}, IP: {netEvent.Peer.IP}, Channel ID: {netEvent.ChannelID}, Data length: {netEvent.Packet.Length}");
                            HandlePacket(netEvent);
                            netEvent.Packet.Dispose();
                            break;
                    }
                }

                server.Flush();
            }

            CleanUp();
        }

        static void ClientAdd(object sender, ClientAddEventArgs e) 
        {
            e.Clients.Add(new Client(e.netEvent.Peer));
        }

        private void PositionUpdates(Object source, ElapsedEventArgs e)
        {
            if (clients.Count <= 0)
                return;

            int playerPropCount = 3;

            object[] values = new object[clients.Count * playerPropCount + 1];
            values[0] = clients.Count;

            for (int i = 0; i < clients.Count; i++)
            {
                values[1 + (i * playerPropCount)] = clients[i].ID;
                values[2 + (i * playerPropCount)] = clients[i].x;
                values[3 + (i * playerPropCount)] = clients[i].y;
            }

            Network.Broadcast(server, Packet.Create(PacketType.ServerPositionUpdate, PacketFlags.None, values));
        }

        private void HandlePacket(Event netEvent)
        {
            try
            {
                var readBuffer = new byte[1024];
                var readStream = new MemoryStream(readBuffer);
                var reader = new BinaryReader(readStream);

                readStream.Position = 0;
                netEvent.Packet.CopyTo(readBuffer);
                var packetID = (PacketType)reader.ReadByte();

                if (packetID == PacketType.ClientCreateAccount)
                {
                    var name = reader.ReadString();
                    var pass = reader.ReadString();

                    using (var db = new UserContext())
                    {
                        User user = db.Users.ToList().Find(x => x.Name.Equals(name));
                        if (user != null) // Account already exists in database
                        {
                            Logger.Log($"Client '{netEvent.Peer.ID}' tried to make an account '{name}' but its already registered");

                            Network.Send(ref netEvent, Packet.Create(PacketType.ServerCreateAccountDenied, PacketFlags.Reliable, "Account name already registered"));

                            return;
                        }

                        // Account is unique, creating...
                        Logger.Log($"Client '{netEvent.Peer.ID}' successfully created a new account '{name}'");
                        Logger.Log($"Registering account '{name}' to database");
                        db.Add(new User { Name = name, Pass = pass });
                        db.SaveChanges();

                        Network.Send(ref netEvent, Packet.Create(PacketType.ServerCreateAccountAccepted, PacketFlags.Reliable));
                    }
                }

                if (packetID == PacketType.ClientLoginAccount)
                {
                    var name = reader.ReadString();
                    var pass = reader.ReadString();

                    using (var db = new UserContext())
                    {
                        User user = db.Users.ToList().Find(x => x.Name.Equals(name));

                        if (user == null) // User login does not exist
                        {
                            Network.Send(ref netEvent, Packet.Create(PacketType.ServerLoginDenied, PacketFlags.Reliable, "User login does not exist"));
                            Logger.Log($"Client '{netEvent.Peer.ID}' tried to login to a non-existant account called '{name}'");
                            return;
                        }

                        // User login exists
                        if (!user.Pass.Equals(pass))
                        {
                            // Logged in with wrong password
                            Network.Send(ref netEvent, Packet.Create(PacketType.ServerLoginDenied, PacketFlags.Reliable, "Wrong password"));
                            Logger.Log($"Client '{netEvent.Peer.ID}' tried to log into account '{name}' but typed in the wrong password");
                            return;
                        }

                        // Logged in with correct password
                        Network.Send(ref netEvent, Packet.Create(PacketType.ServerLoginAccepted, PacketFlags.Reliable));
                        Logger.Log($"Client '{netEvent.Peer.ID}' successfully logged into account '{name}'");
                        clients.Find(x => x.ID.Equals(netEvent.Peer.ID)).ClientStatus = ClientStatus.InGame;
                    }
                }

                if (packetID == PacketType.ClientPositionUpdate)
                {
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    //Logger.Log($"Recieved x {x}, y {y}");

                    uint id = netEvent.Peer.ID;
                    Client client = clients.Find(x => x.ID.Equals(id));
                    client.x = x;
                    client.y = y;
                    Logger.Log(client);
                }

                readStream.Dispose();
                reader.Dispose();
            }

            catch (ArgumentOutOfRangeException)
            {
                Logger.Log($"Received packet from client '{netEvent.Peer.ID}' but buffer was too long. {netEvent.Packet.Length}");
            }
        }

        public void Stop()
        {
            serverRunning = false;
        }

        private void CleanUp() 
        {
            positionUpdatePump.Dispose();
            server.Dispose();
            Library.Deinitialize();
        }
    }
}
