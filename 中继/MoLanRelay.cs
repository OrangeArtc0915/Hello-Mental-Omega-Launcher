// MO 局域网「游戏内 UDP 中继」
//
// 作用：樱花 Frp 只开一条 TCP 隧道（公网 → 本机 1233），游戏的 UDP 走不出去。
//       本中继把 gamemd 发出的 UDP 包封进大厅**已有的那条 TCP 连接**里转发，
//       对端解包后再注入它本机的 gamemd。
//
// 寻址：客户端的 spawn.ini 被改成
//          [OtherN] Ip   = 127.0.0.1
//          [OtherN] Port = BasePort + N        (N = 该对端在 [Other] 里的序号)
//       gamemd 于是把发给第 N 个玩家的包发到 127.0.0.1:(BasePort+N)，
//       本中继从同一个端口把回包注入回去 —— 源地址与 spawn.ini 一致，游戏不会丢包。
//
// 拓扑：星型（经房主）。2 人时就是最简单的一跳；3 人以上由房主分发。
//
// 帧格式（走大厅的文本协议）：MO-RELAY \u0001 <tag> \u0001 <base64>
//   · 客机 → 房主：tag = 目标玩家名（请房主转发）
//   · 房主 → 客机：tag = 源玩家名（请对方注入）
//   （同一条消息的方向决定 tag 的含义：我是房主就当它是"目标"，否则当"源"。）

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DTAClient.Domain.Multiplayer.LAN;

namespace MoLanRelay
{
    public static class Relay
    {
        public const int BasePort = 12340;   // 每个对端一个本地 UDP 端口
        public const int GamePort = 1234;    // gamemd 监听的端口
        public const string Tag = "MO-RELAY";
        // 每次改动都改这个号：日志开头的 "MoLanRelay vX" 就是它。
        // 三台机器版本不一致会导致"有的能收不能发"这类诡异现象，先看这个号。
        public const string Version = "2026-10-04.4";
        const int MaxPeers = 8;

        class Peer
        {
            public string Name;
            public int Index;
            public UdpClient Sock;
            public IPEndPoint Game;
            public volatile bool Alive;    // Rebuild 时置 false，让旧 LocalLoop 线程退出
            public long PacketsIn;
            public long PacketsOut;
        }

        static IList players;                    // 客户端的 Players（List<PlayerInfo>）
        static readonly List<Peer> peers = new List<Peer>();
        static readonly object sendLock = new object();
        static TcpClient hostClient;             // 客机：通往房主的那条 TCP 连接（由 LANGameLobby::SetUp 注册）
        static Encoding hostEncoding;            // 与 hostClient 配套的编码
        static bool loggedNoHost;
        static string selfName = "";
        static bool isHost;
        static volatile bool running;
        static string logPath;
        static string currentKey = "";
        static long totalSent;          // 我尝试发出去的帧数
        static long totalFwd;           // 房主转发出去的帧数
        static long totalRecv;          // 我收到的帧数
        static long totalSendFail;      // 发送时被丢掉/失败的帧数（诊断用）
        static bool loggedFirstIn;      // 本机游戏第一包
        static bool loggedFirstRecv;    // 第一条对端帧

        // ------------------------------------------------------------------
        // 由客户端 IL 调用
        // ------------------------------------------------------------------

        /// <summary>写 spawn.ini 时调用（此时玩家表已固定）。同一局重复调用会直接返回，不会重建端口。</summary>
        public static void Start(IList playersList, string myName, string logFilePath)
        {
            logPath = logFilePath;
            players = playersList;
            selfName = Clean(myName);

            string key = BuildKey(playersList, selfName);
            if (running && key == currentKey) return;
            currentKey = key;
            running = true;

            // 先把 SetUp 阶段攒下的日志落盘，再打版本横幅 —— 顺序就是"版本在最上面"。
            // 放在 guard 之后，保证一局只打一次。
            Log("======== MoLanRelay v" + Version + " ========");
            Log("载入的 DLL: " + SelfPath());

            // 房主判定用两个互相独立的信号，任一成立即认为是房主（3 人以上时更稳）：
            //   · byOrder ：玩家表里排在第一个的是不是我自己（房主最先入列）
            //   · byConn  ：所有对端的 LANPlayerInfo.TcpClient 都有值
            //              （房主侧是 accept 出来的连接；客机侧这些都是 null）
            bool byOrder = IsSelfFirst(playersList, selfName);
            bool byConn = LooksLikeHost(playersList, selfName);
            isHost = byConn || byOrder;

            Rebuild();
            Log("start: self=" + selfName + " host=" + isHost
                + " (byOrder=" + byOrder + " byConn=" + byConn + ")"
                + " 玩家数=" + CountPeers(playersList) + " 本机端口表=[" + PeerMap() + "]");
        }

        /// <summary>当前实际加载的中继 DLL 路径 —— 三台机器是否用同一份，看这行。</summary>
        static string SelfPath()
        {
            try { return typeof(Relay).Assembly.Location; }
            catch { return "?"; }
        }

        /// <summary>房主侧：每一个对端的 LANPlayerInfo 都挂着 accept 出来的 TcpClient。
        /// 客机侧这些字段是 null（客机只持有 LANGameLobby::client，由 RegisterHostClient 单独注册）。</summary>
        static bool LooksLikeHost(IList list, string self)
        {
            if (list == null) return false;
            int others = 0, withConn = 0;
            foreach (object o in list)
            {
                LANPlayerInfo lp = o as LANPlayerInfo;
                if (lp == null) continue;
                string n = Clean(lp.Name);
                if (n.Length == 0 || n == self) continue;
                others++;
                try { if (lp.TcpClient != null) withConn++; } catch { }
            }
            return others > 0 && withConn == others;
        }

        static bool IsSelfFirst(IList list, string self)
        {
            if (list == null) return false;
            foreach (object o in list)
            {
                LANPlayerInfo lp = o as LANPlayerInfo;
                if (lp == null) continue;
                string n = Clean(lp.Name);
                if (n.Length == 0) continue;
                return n == self;
            }
            return false;
        }

        /// <summary>对端集合的指纹，用来判断"是不是同一局"。</summary>
        static string BuildKey(IList list, string self)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(self).Append('#');
            if (list != null)
            {
                foreach (object o in list)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null) continue;
                    string n = Clean(lp.Name);
                    if (n.Length == 0 || n == self) continue;
                    sb.Append(n).Append('|');
                }
            }
            return sb.ToString();
        }

        /// <summary>客户端收到以 MO-RELAY 开头的消息时调用（fromName = 发送者名字）。</summary>
        public static void OnFrame(string fromName, string message)
        {
            try
            {
                if (!running) return;
                string[] parts = message.Split('\u0001');
                // parts[0] = "MO-RELAY", parts[1] = tag, parts[2] = base64
                if (parts.Length < 3) return;
                string tag = Clean(parts[1]);
                byte[] data;
                try { data = Convert.FromBase64String(parts[2]); }
                catch { return; }
                totalRecv++;
                if (!loggedFirstRecv)
                {
                    loggedFirstRecv = true;
                    Log("第一次收到对端帧（来自 " + fromName + "，tag=" + tag + "，长度 " + data.Length + "）");
                }

                if (isHost)
                {
                    // 房主：tag = 目标
                    if (SameName(tag, selfName))
                        Inject(Clean(fromName), data);           // 目标就是房主自己 → 注入本机 gamemd
                    else
                        ForwardTo(tag, Clean(fromName), data);   // 转发给另一个客机（tag 改写成源）
                }
                else
                {
                    // 客机：tag = 源，直接注入本机 gamemd
                    Inject(tag, data);
                }
            }
            catch (Exception ex) { Log("OnFrame error: " + ex.Message); }
        }

        public static string BuildFrame(string tag, byte[] data)
        {
            return Tag + "\u0001" + Clean(tag) + "\u0001" + Convert.ToBase64String(data);
        }

        public static bool IsRelayMessage(string message)
        {
            return message != null && message.StartsWith(Tag + "\u0001", StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // 内部
        // ------------------------------------------------------------------

        static string Clean(string s)
        {
            if (s == null) return "";
            return s.Replace('\u0001', '_').Replace('\u0002', '_').Trim();
        }

        /// <summary>名字比较：先按原样，再忽略大小写兜底（玩家名里偶发的前后空格/大小写差异不该导致丢包）。</summary>
        static bool SameName(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a == b) return true;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>本机端口表，例如 "mmm→12341 zzz→12342"，出问题时一眼能看出谁在哪。</summary>
        static string PeerMap()
        {
            StringBuilder sb = new StringBuilder();
            lock (peers) { foreach (Peer p in peers) sb.Append(p.Name).Append('→').Append(BasePort + p.Index).Append(' '); }
            return sb.ToString();
        }

        // 只警告一次的名字，避免 3 人以上时日志被同一句刷爆
        static readonly HashSet<string> warnedNames = new HashSet<string>();
        static void WarnOnce(string what, string key, string line)
        {
            lock (warnedNames)
            {
                if (!warnedNames.Add(what + "|" + key)) return;
            }
            Log(line);
        }

        static int CountPeers(IList list)
        {
            int n = 0;
            if (list == null) return 0;
            foreach (object o in list)
            {
                LANPlayerInfo lp = o as LANPlayerInfo;
                if (lp == null) continue;
                string nm = Clean(lp.Name);
                if (nm.Length == 0) continue;
                n++;
            }
            return n;
        }

        /// <summary>按 [OtherN] 的顺序（players 顺序、跳过自己）建立本地端口。</summary>
        static void Rebuild()
        {
            lock (peers)
            {
                // 关掉旧的一批：先把 Alive 置 false，让 LocalLoop 自己退出，再关套接字。
                foreach (Peer p in peers) { p.Alive = false; try { p.Sock.Close(); } catch { } }
                peers.Clear();
                if (players == null) return;

                int idx = 0;
                foreach (object o in players)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null) continue;
                    string name = Clean(lp.Name);
                    if (name.Length == 0 || name == selfName) continue;
                    if (idx >= MaxPeers)
                    {
                        WarnOnce("maxpeers", name, "对端超过 " + MaxPeers + " 个，'" + name + "' 及之后的不再建端口");
                        break;
                    }

                    // 注意：原版客户端给 spawn.ini 的段是 [Other1]、[Other2]…（从 1 开始）
                    // 所以本地端口必须是 BasePort + 序号，序号 = idx + 1，否则会差 1，包全落空。
                    int portNo = idx + 1;
                    Peer p = new Peer();
                    p.Name = name;
                    p.Index = portNo;
                    p.Alive = true;
                    p.Game = new IPEndPoint(IPAddress.Loopback, GamePort);
                    try
                    {
                        p.Sock = new UdpClient(new IPEndPoint(IPAddress.Loopback, BasePort + portNo));
                    }
                    catch (Exception ex)
                    {
                        // 单个端口建不起来不该拖垮其他人（端口被占 / 上次残留），跳过它继续。
                        Log("bind 127.0.0.1:" + (BasePort + portNo) + " 失败（对端 '" + name + "' 这一路会不通）: " + ex.Message);
                        idx++;
                        continue;
                    }
                    peers.Add(p);
                    idx++;
                }

                // 逐个起接收线程
                foreach (Peer p in peers)
                {
                    Peer captured = p;
                    Thread t = new Thread(delegate() { LocalLoop(captured); });
                    t.IsBackground = true;
                    t.Name = "MoRelay-" + captured.Index;
                    t.Start();
                }
            }

            Log("端口映射: " + PeerMap());

            // 统计线程（每 5 秒，有变化才写）—— 用来判断卡在哪一环
            if (statsThread == null)
            {
                statsThread = new Thread(delegate() { StatsLoop(); });
                statsThread.IsBackground = true;
                statsThread.Start();
            }
        }

        static Thread statsThread;
        static long lastIn, lastOut, lastSent, lastRecv, lastFail;

        static void StatsLoop()
        {
            while (true)
            {
                Thread.Sleep(5000);
                try
                {
                    long ti = 0, to = 0;
                    StringBuilder per = new StringBuilder();
                    lock (peers)
                    {
                        foreach (Peer p in peers)
                        {
                            ti += p.PacketsIn; to += p.PacketsOut;
                            if (p.PacketsIn != 0 || p.PacketsOut != 0)
                                per.Append(p.Name).Append("(收").Append(p.PacketsIn).Append("/注").Append(p.PacketsOut).Append(") ");
                        }
                    }
                    if (ti != lastIn || to != lastOut || totalSent != lastSent || totalRecv != lastRecv || totalSendFail != lastFail)
                    {
                        Log("stats: 本机游戏发出的包=" + ti + " 已注入本机游戏=" + to
                            + " 已发出帧=" + totalSent + " 已转发帧=" + totalFwd + " 已收到帧=" + totalRecv
                            + (totalSendFail > 0 ? " 发送失败=" + totalSendFail : "")
                            + (per.Length > 0 ? " | 分对端: " + per.ToString().TrimEnd() : ""));
                        lastIn = ti; lastOut = to; lastSent = totalSent; lastRecv = totalRecv; lastFail = totalSendFail;
                    }
                }
                catch { }
            }
        }

        /// <summary>从本机 gamemd 收包 → 封帧 → 走 TCP 发出去。</summary>
        static void LocalLoop(Peer peer)
        {
            Log("peer[" + peer.Index + "] " + peer.Name + " listening 127.0.0.1:" + (BasePort + peer.Index));
            while (running && peer.Alive)
            {
                try
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = peer.Sock.Receive(ref from);
                    if (data == null || data.Length == 0) continue;
                    peer.PacketsIn++;
                    if (!loggedFirstIn)
                    {
                        loggedFirstIn = true;
                        Log("第一次收到本机游戏发出的包（来自 " + from + "，长度 " + data.Length + "）");
                    }

                    string frame;
                    if (isHost)
                    {
                        // 房主：直接发给该对端，tag = 源 = 房主自己（客机据此知道"这是房主发来的"）
                        frame = BuildFrame(selfName, data);
                        totalSent++;
                        SendToPlayer(peer.Name, frame);
                    }
                    else
                    {
                        // 客机：发给房主，tag = 目标 = 该对端（请房主转发）
                        frame = BuildFrame(peer.Name, data);
                        totalSent++;
                        SendToHost(frame);
                    }
                }
                catch (Exception ex)
                {
                    if (!running || !peer.Alive) break;     // Rebuild 关掉的旧线程，安静退出
                    // 游戏刚启动时它的 1234 还没绑定，注入会收到 ICMP 端口不可达 —— 属于正常过渡，
                    // 节流记录，避免日志被刷屏。
                    WarnOnce("recverr", peer.Name + peer.Index,
                        "peer[" + peer.Index + "] " + peer.Name + " recv error（游戏可能还没起来 / 1234 未绑定）: " + ex.Message);
                    Thread.Sleep(50);
                }
            }
        }

        /// <summary>把对端发来的包注入本机 gamemd（源端口 = 该对端的本地中继端口）。</summary>
        static void Inject(string peerName, byte[] data)
        {
            Peer p = FindPeer(peerName);
            if (p == null)
            {
                WarnOnce("inject", peerName,
                    "注入失败：找不到对端 '" + peerName + "'（已知：" + PeerNames() + "）");
                return;
            }
            try
            {
                p.Sock.Send(data, data.Length, p.Game);   // 从 (BasePort+Index) 发出 → 源与 spawn.ini 一致
                p.PacketsOut++;
            }
            catch (Exception ex) { WarnOnce("injectex", peerName, "inject to " + peerName + " failed: " + ex.Message); }
        }

        static string PeerNames()
        {
            StringBuilder sb = new StringBuilder();
            lock (peers) { foreach (Peer p in peers) sb.Append(p.Name).Append(' '); }
            return sb.ToString();
        }

        /// <summary>房主：把客机 A 的包转发给客机 B（tag 改写成 A 的名字）。</summary>
        static void ForwardTo(string targetName, string sourceName, byte[] data)
        {
            totalFwd++;
            if (FindPlayer(targetName) == null)
            {
                WarnOnce("fwd", targetName,
                    "转发失败：玩家表里找不到目标 '" + targetName + "'（来自身 " + sourceName + "，已知：" + PeerNames() + "）");
                return;
            }
            SendToPlayer(targetName, BuildFrame(sourceName, data));
        }

        static Peer FindPeer(string name)
        {
            lock (peers)
            {
                foreach (Peer p in peers) if (SameName(p.Name, name)) return p;
            }
            return null;
        }

        static LANPlayerInfo FindPlayer(string name)
        {
            if (players == null) return null;
            foreach (object o in players)
            {
                LANPlayerInfo lp = o as LANPlayerInfo;
                if (lp == null) continue;
                if (SameName(Clean(lp.Name), name)) return lp;
            }
            return null;
        }

        static LANPlayerInfo HostPlayer()
        {
            if (players == null) return null;
            // 注意：玩家表里"自己"排在第一个（两边都是），所以绝不能拿 players[0] 当房主。
            // 客机只与房主有一条 TCP 连接（隧道那条），因此"不是自己、且有 TcpClient 的对端"就是房主。
            foreach (object o in players)
            {
                LANPlayerInfo lp = o as LANPlayerInfo;
                if (lp == null) continue;
                if (SameName(Clean(lp.Name), selfName)) continue;
                if (lp.TcpClient == null) continue;
                return lp;
            }
            // 兜底：第一个不是自己的
            foreach (object o in players)
            {
                LANPlayerInfo lp = o as LANPlayerInfo;
                if (lp == null) continue;
                if (SameName(Clean(lp.Name), selfName)) continue;
                return lp;
            }
            return null;
        }

        static void SendToHost(string frame)
        {
            // 客机唯一可靠的"通往房主"的写口就是 LANGameLobby 的那条连接（SetUp 时注册进来）。
            // Players 里的 LANPlayerInfo 在客机侧 TcpClient 是空的，拿它发等于静默丢弃。
            TcpClient tcp = hostClient;
            if (tcp != null) { SendClientLocked(tcp, hostEncoding, frame); return; }

            LANPlayerInfo host = HostPlayer();
            if (host != null) { SendRaw(host, frame); return; }

            if (!loggedNoHost)
            {
                loggedNoHost = true;
                Log("SendToHost: 还没有可用的房主连接（LANGameLobby::SetUp 未注册），帧被丢弃");
            }
            totalSendFail++;
        }

        /// <summary>客户端建立/加入大厅时调用（由 LANGameLobby::SetUp 注入），缓存"通往房主"的连接。</summary>
        public static void RegisterHostClient(TcpClient client, Encoding encoding)
        {
            if (client == null) return;
            if (encoding != null) hostEncoding = encoding;
            if (ReferenceEquals(hostClient, client)) return;
            hostClient = client;
            string ep = "?";
            bool ok = false;
            try { ok = client.Connected; ep = client.Client.RemoteEndPoint.ToString(); } catch { }
            Log("register host client: " + (ok ? "connected" : "not-connected") + " / " + ep);
        }

        /// <summary>对"通往房主的那条连接"的唯一写入口 —— 与 SendLocked 共用同一把锁，
        /// 保证大厅聊天与游戏 UDP 帧不会交错写坏同一条流。</summary>
        public static void SendClientLocked(TcpClient client, Encoding encoding, string message)
        {
            if (client == null || message == null) { totalSendFail++; return; }
            try
            {
                if (!client.Connected) { totalSendFail++; return; }
                Encoding enc = encoding == null ? Encoding.UTF8 : encoding;
                byte[] buffer = enc.GetBytes(message + '\u0002');
                lock (sendLock)
                {
                    NetworkStream ns = client.GetStream();
                    ns.Write(buffer, 0, buffer.Length);
                    ns.Flush();
                }
            }
            catch (Exception ex) { totalSendFail++; Log("send-to-host failed: " + ex.Message); }
        }

        static void SendToPlayer(string name, string frame)
        {
            LANPlayerInfo lp = FindPlayer(name);
            if (lp == null)
            {
                WarnOnce("sendplayer", name, "发送失败：玩家表里找不到 '" + name + "'（已知：" + PeerNames() + "）");
                return;
            }
            SendRaw(lp, frame);
        }

        static void SendRaw(LANPlayerInfo lp, string frame)
        {
            SendLocked(lp, frame);
        }

        /// <summary>
        /// 对某个玩家连接的唯一写入口。
        /// 大厅自己的发送也会被客户端侧改到走这里（同一个 CilBody），
        /// 这样"聊天"和"游戏数据"共用一个流时不会交错写坏。
        /// </summary>
        public static void SendLocked(LANPlayerInfo lp, string message)
        {
            if (lp == null || message == null) return;
            try
            {
                if (lp.TcpClient == null)
                {
                    // 房主侧理论上不会走到这；出现就说明这个玩家的连接还没建立/已断开
                    WarnOnce("nullconn", Clean(lp.Name) + "",
                        "发送失败：玩家 '" + Clean(lp.Name) + "' 的 TcpClient 为空（连接未建立或已断开）");
                    totalSendFail++;
                    return;
                }
                byte[] buffer = Encoding.UTF8.GetBytes(message + '\u0002');
                lock (sendLock)
                {
                    TcpClient tcp = lp.TcpClient;
                    if (tcp == null) { totalSendFail++; return; }
                    NetworkStream ns = tcp.GetStream();
                    ns.Write(buffer, 0, buffer.Length);
                    ns.Flush();
                }
                lp.TimeSinceLastSentMessage = TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                totalSendFail++;
                WarnOnce("sendex", Clean(lp.Name) + "", "send to '" + Clean(lp.Name) + "' failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // 日志（出问题时能查）
        // ------------------------------------------------------------------

        static readonly object logLock = new object();
        // logPath 还没设置时（SetUp 阶段）先把日志攒着，等 Relay.Start 拿到路径后一起落盘。
        // 否则 "register host client" 这行永远看不到，就无法判断客机有没有拿到房主连接。
        static readonly List<string> pendingLog = new List<string>();

        static void Log(string line)
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss.fff") + " [MoLanRelay] " + line;
            if (string.IsNullOrEmpty(logPath))
            {
                lock (pendingLog) { if (pendingLog.Count < 500) pendingLog.Add(stamp); }
                return;
            }
            try
            {
                lock (logLock)
                {
                    lock (pendingLog)
                    {
                        if (pendingLog.Count > 0)
                        {
                            File.AppendAllText(logPath, string.Join(Environment.NewLine, pendingLog.ToArray()) + Environment.NewLine);
                            pendingLog.Clear();
                        }
                    }
                    File.AppendAllText(logPath, stamp + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
