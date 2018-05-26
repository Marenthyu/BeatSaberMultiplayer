﻿using BeatSaberMultiplayerServer.Misc;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BeatSaberMultiplayerServer
{
    

    class ServerMain
    {
        public static string version = "0.2";

        private static string IP;
        private static Settings _settings;

        static TcpListener _listener;

        public static List<Client> clients = new List<Client>();

        public static ServerState serverState = ServerState.Lobby;

        public static int[] availableSongsIDs;
        public static List<CustomSongInfo> availableSongs = new List<CustomSongInfo>();

        public static int currentSongIndex = -1;
        private static int lastSelectedSong = -1;

        public static TimeSpan playTime = new TimeSpan();

        private static TcpClient _serverHubClient;

        static void Main(string[] args)
        {
            Logger.Instance.Log("Beat Saber Multiplayer Server v"+version);

            IP = GetPublicIPv4();
            Logger.Instance.Log($"Current IP: {IP}");

            _settings = Settings.Instance;

            Logger.Instance.Log("Current port: "+_settings.IP.Port);

            availableSongsIDs = _settings.AvailableSongs.Songs;

            Logger.Instance.Log("Downloading songs from BeatSaver...");
            DownloadSongs();

            if(availableSongsIDs == null || availableSongsIDs.Length == 0)
            {
                Logger.Instance.Error("No songs specified!");
                return;
            }

            Logger.Instance.Log("Starting server...");
            _listener = new TcpListener(IPAddress.Any, _settings.IP.Port);

            _listener.Start();

            Logger.Instance.Log("Waiting for clients...");

            Thread _listenerThread = new Thread(AcceptClientThread);
            _listenerThread.Start();

            Thread _serverStateThread = new Thread(ServerStateControllerThread);
            _serverStateThread.Start();

            try
            {
                ConnectToServerHub(_settings.IP.ServerHubIP, _settings.IP.ServerHubPort);
            }catch(Exception e)
            {
                Logger.Instance.Error("Can't connect to ServerHub! Exception: "+e);
            }

        }

        static public void ConnectToServerHub(string serverHubIP, int serverHubPort)
        {
            _serverHubClient = new TcpClient(serverHubIP, serverHubPort);

            DataPacket packet = new DataPacket();

            packet.IPv4 = IP;
            packet.Port = _settings.IP.Port;
            packet.Name = _settings.IP.ServerName;

            byte[] packetBytes = packet.ToBytes();

            _serverHubClient.GetStream().Write(packetBytes, 0, packetBytes.Length);

            //client.Close();

        }

        static string GetPublicIPv4()
        {
            using (var client = new WebClient())
            {
                return client.DownloadString("https://api.ipify.org");
            }
        }

        private static void DownloadSongs()
        {
            if(!Directory.Exists("AvailableSongs"))
            {
                Directory.CreateDirectory("AvailableSongs");
            }

            if(availableSongsIDs == null)
            {
                return;
            }

            using (var client = new WebClient())
            {
                client.Headers["User-Agent"] = "Mozilla/4.0 (Compatible; Windows NT 5.1; MSIE 6.0) " +
                                                "(compatible; MSIE 6.0; Windows NT 5.1; " +
                                                ".NET CLR 1.1.4322; .NET CLR 2.0.50727)";

                foreach(string dir in Directory.GetDirectories("AvailableSongs/"))
                {
                    Directory.Delete(dir,true);
                }

                foreach (int id in availableSongsIDs)
                {
                    if (!File.Exists("AvailableSongs/"+id + ".zip"))
                    {
                        Logger.Instance.Log("Downloading "+id+".zip");
                        client.DownloadFile("https://beatsaver.com/dl.php?id=" + id, "AvailableSongs/" + id + ".zip");

                        FastZip zip = new FastZip();
                        Logger.Instance.Log("Extracting "+id+".zip...");
                        zip.ExtractZip("AvailableSongs/" + id + ".zip", "AvailableSongs",null);
                        

                    }
                    else
                    {
                        string downloadedSongPath = "";

                        using (var zf = new ZipFile("AvailableSongs/" + id + ".zip"))
                        {
                            foreach (ZipEntry ze in zf)
                            {
                                if (ze.IsFile)
                                {
                                    if (string.IsNullOrEmpty(downloadedSongPath) && ze.Name.IndexOf('/') != -1)
                                    {
                                        downloadedSongPath = "AvailableSongs/" + ze.Name.Substring(0, ze.Name.IndexOf('/'));
                                    }
                                }
                                else if (ze.IsDirectory)
                                {
                                    downloadedSongPath = "AvailableSongs/" + ze.Name;
                                }
                            }
                        }

                        if (downloadedSongPath.Contains("/autosaves"))
                        {
                            downloadedSongPath = downloadedSongPath.Replace("/autosaves", "");
                        }

                        if (!Directory.Exists(downloadedSongPath))
                        {

                            FastZip zip = new FastZip();
                            Logger.Instance.Log("Extracting " + id + ".zip...");
                            zip.ExtractZip("AvailableSongs/" + id + ".zip", "AvailableSongs", null);
                        }
                        
                    }
                    
                }

                Logger.Instance.Log("All songs downloaded!");

                List<CustomSongInfo> _songs = SongLoader.RetrieveAllSongs();

                foreach(CustomSongInfo song in _songs)
                {
                    Logger.Instance.Log("Processing "+song.songName+" "+song.songSubName);
                    using (NVorbis.VorbisReader vorbis = new NVorbis.VorbisReader(song.path + "/" + song.difficultyLevels[0].audioPath))
                    {
                        song.duration = vorbis.TotalTime;
                    }

                    availableSongs.Add(song);

                }

                Logger.Instance.Log("Done!");
                
            }
            
        }

        static void ServerStateControllerThread()
        {
            Stopwatch _timer = new Stopwatch();
            _timer.Start();
            int _timerSeconds = 0;
            TimeSpan _lastTime = new TimeSpan();

            float lobbyTimer = 0;
            float sendTimer = 0;

            int lobbyTime = 60;

            TimeSpan deltaTime;

            while (true)
            {
                deltaTime = (_timer.Elapsed - _lastTime);

                _lastTime = _timer.Elapsed;

                switch (serverState)
                {
                    case ServerState.Lobby: {

                            lobbyTimer += (float)deltaTime.TotalSeconds;
                            
                            if(clients.Count == 0)
                            {
                                lobbyTimer = 0;
                            }
                            
                            if((int)Math.Ceiling(lobbyTimer) > _timerSeconds && _timerSeconds > -1)
                            {
                                _timerSeconds = (int)Math.Ceiling(lobbyTimer);
                                SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(ServerCommandType.SetLobbyTimer,Math.Max(lobbyTime-_timerSeconds,0))));
                            }
                            
                            

                            if(lobbyTimer >= lobbyTime/2 && currentSongIndex == -1)
                            {
                                currentSongIndex = lastSelectedSong;
                                currentSongIndex++;
                                if (currentSongIndex >= availableSongs.Count)
                                {
                                    currentSongIndex = 0;
                                }
                                SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(ServerCommandType.SetSelectedSong,_selectedLevelID: availableSongs[currentSongIndex].levelId, _difficulty: GetPreferredDifficulty(availableSongs[currentSongIndex]))));
                                
                            }

                            if(lobbyTimer >=lobbyTime)
                            {
                                SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(ServerCommandType.SetSelectedSong, _selectedLevelID: availableSongs[currentSongIndex].levelId, _difficulty: GetPreferredDifficulty(availableSongs[currentSongIndex]))));
                                SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(ServerCommandType.StartSelectedSongLevel)));

                                serverState = ServerState.Playing;
                                Logger.Instance.Log("Starting song "+ availableSongs[currentSongIndex] .songName+" "+ availableSongs[currentSongIndex] .songSubName+ "...");
                                _timerSeconds = 0;
                                lobbyTimer = 0;


                            }

                        };break;
                    case ServerState.Playing: {
                            sendTimer += (float)deltaTime.TotalSeconds;
                            playTime += deltaTime;

                            if(sendTimer >= 1f)
                            {
                                SendToAllClients(JsonConvert.SerializeObject(new ServerCommand(ServerCommandType.SetPlayerInfos, _playerInfos: (clients.Where(x => x.playerInfo != null).OrderByDescending(x => x.playerInfo.playerScore).Select(x => JsonConvert.SerializeObject(x.playerInfo))).ToArray(), _selectedSongDuration: availableSongs[currentSongIndex].duration.TotalSeconds, _selectedSongPlayTime: playTime.TotalSeconds, _selectedLevelID: availableSongs[currentSongIndex].levelId)));
                                sendTimer = 0f;
                            }

                            if(playTime.TotalSeconds >= availableSongs[currentSongIndex].duration.TotalSeconds+10f)
                            {
                                playTime = new TimeSpan();
                                sendTimer = 0f;
                                serverState = ServerState.Lobby;
                                lastSelectedSong = currentSongIndex;
                                currentSongIndex = -1;
                                Logger.Instance.Log("Returning to lobby...");
                            }

                            if(clients.Where(x => x.state == ClientState.Playing).Count() == 0 && playTime.TotalSeconds > 10f)
                            {
                                playTime = new TimeSpan();
                                sendTimer = 0f;
                                serverState = ServerState.Lobby;
                                lastSelectedSong = currentSongIndex;
                                currentSongIndex = -1;

                                Logger.Instance.Log("Returning to lobby(NO PLAYERS)...");
                            }

                        };break;
                }


                Thread.Sleep(2);
            }
        }

        static int GetPreferredDifficulty(CustomSongInfo _song)
        {
            int difficulty = 0;

            foreach(CustomSongInfo.DifficultyLevel diff in _song.difficultyLevels)
            {
                if ((int)Enum.Parse(typeof(Difficulty), diff.difficulty) <= 3 && (int)Enum.Parse(typeof(Difficulty), diff.difficulty) >= difficulty)
                {
                    difficulty = (int)Enum.Parse(typeof(Difficulty), diff.difficulty);
                }
            }

            return difficulty;

        }

        static void SendToAllClients(string message, bool retryOnError = false)
        {
            try
            {
                for (int i = 0; i < clients.Count; i++)
                {
                    if(clients[i] != null)
                    {
                        if(clients[i].state == ClientState.Playing || clients[i].state == ClientState.Connected)
                        {
                            clients[i].SendToClient(message);
                        }
                    }
                        
                }
            }catch(Exception e)
            {
                Logger.Instance.Exception("Can't send message to all clients! Exception: "+e);
            }
        }

        static void AcceptClientThread()
        {
            while (true)
            {
                Thread _thread = new Thread(new ParameterizedThreadStart(ClientThread));

                _thread.Start(_listener.AcceptTcpClient());
            }
        }

        static void ClientThread(Object stateInfo)
        {
            clients.Add(new Client((TcpClient)stateInfo));
        }
    }

    class Client
    {
        TcpClient _client;
        public PlayerInfo playerInfo;

        public ClientState state = ClientState.Disconnected;

        int playerScore;
        string playerId;
        string playerName;

        Thread _clientLoopThread;

        public Client(TcpClient client)
        {
            _client = client;

            _clientLoopThread = new Thread(ClientLoop);
            _clientLoopThread.Start();
                      
        }
        
        void ClientLoop()
        {
            int pingTimer = 0;

            Logger.Instance.Log("Client connected!");

            state = ClientState.Connected;

            while (true)
            {
                if (_client != null && _client.Connected)
                {
                    pingTimer++;
                    if (pingTimer > 180)
                    {
                        SendToClient(JsonConvert.SerializeObject(new ServerCommand(ServerCommandType.Ping)));
                        pingTimer = 0;
                    }

                    string[] commands = ReceiveFromClient(true);

                    if (commands != null)
                    {

                        foreach (string data in commands)
                        {
                            ClientCommand command = JsonConvert.DeserializeObject<ClientCommand>(data);

                            if(command.version != ServerMain.version)
                            {
                                state = ClientState.UpdateRequired;
                                SendToClient(JsonConvert.SerializeObject(new ServerCommand(ServerCommandType.UpdateRequired)));
                                return;
                            }

                            switch (command.commandType)
                            {
                                case ClientCommandType.SetPlayerInfo: {

                                        PlayerInfo receivedPlayerInfo = JsonConvert.DeserializeObject<PlayerInfo>(command.playerInfo);
                                        
                                        if (receivedPlayerInfo != null)
                                        {
                                            state = ClientState.Playing;

                                            if (playerId == null)
                                            {
                                                playerId = receivedPlayerInfo.playerId;
                                                Logger.Instance.Log("New player: " + receivedPlayerInfo.playerName + " : " + receivedPlayerInfo.playerId);
                                            }
                                            else if (playerId != receivedPlayerInfo.playerId)
                                            {
                                                return;
                                            }

                                            playerInfo = receivedPlayerInfo;

                                            if (playerName == null)
                                            {
                                                playerName = receivedPlayerInfo.playerName;
                                            }
                                            else if (playerName != receivedPlayerInfo.playerName)
                                            {
                                                return;
                                            }

                                            playerScore = receivedPlayerInfo.playerScore;
                                        }

                                    };break;
                                case ClientCommandType.GetServerState: {
                                        if (ServerMain.serverState != ServerState.Playing)
                                        {
                                            SendToClient(JsonConvert.SerializeObject(new ServerCommand(ServerCommandType.SetServerState)));
                                        }
                                        else
                                        {
                                            SendToClient(JsonConvert.SerializeObject(new ServerCommand(ServerCommandType.SetServerState,_selectedLevelID: ServerMain.availableSongs[ServerMain.currentSongIndex].levelId, _selectedSongDuration: ServerMain.availableSongs[ServerMain.currentSongIndex].duration.TotalSeconds, _selectedSongPlayTime: ServerMain.playTime.TotalSeconds)));
                                        }
                                    };break;
                                case ClientCommandType.GetAvailableSongs: {

                                        SendToClient(JsonConvert.SerializeObject(new ServerCommand(ServerCommandType.DownloadSongs, _songs: ServerMain.availableSongs.Select(x => x.levelId).ToArray())));


                                    };break;
                            }

                        }
                    }
                }
                else
                {
                    ServerMain.clients.Remove(this);
                    if(_client != null)
                    {
                        _client.Close();
                        _client = null;
                    }
                    Logger.Instance.Log("Client disconnected!");
                    return;
                }
                Thread.Sleep(16);
            }
        }

        public string[] ReceiveFromClient(bool waitIfNoData = true)
        {
            
            if (_client.Available == 0)
            {
                if (waitIfNoData)
                {
                    while (_client.Available == 0 && _client.Connected)
                    {
                        Thread.Sleep(16);
                    }
                }
                else
                {
                    return null;
                }
            }


            if (_client == null || !_client.Connected)
            {
                return null;
            }
            NetworkStream stream = _client.GetStream();

            string receivedJson;
            byte[] buffer = new byte[_client.ReceiveBufferSize];
            int length;

            length = stream.Read(buffer, 0, buffer.Length);

            receivedJson = Encoding.Unicode.GetString(buffer).Trim('\0');

            string[] strBuffer = receivedJson.Trim('\0').Replace("}{", "}#{").Split('#');

            return strBuffer;
            
            
        }

        public bool SendToClient(string message)
        {
            if (_client == null || !_client.Connected)
            {
                return false;
            }

            byte[] buffer = Encoding.Unicode.GetBytes(message);
            try
            {
                _client.GetStream().Write(buffer, 0, buffer.Length);
            }catch(Exception e)
            {
                return false;
            }
            return true;

        }


        void DestroyClient()
        {
            if (_client != null)
            {
                ServerMain.clients.Remove(this);
                _client.Close();
            }
        }
    }
}
