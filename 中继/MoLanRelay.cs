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
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
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
        // 语义化版本 vX.Y.Z，主菜单右下角显示的也是它。
        public const string Version = "0.17.1";
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
            public double RttMs;          // 到该对端的往返延迟（PING/PONG 测得）
            public string TagPrefix;      // "MO-RELAY\u0001名字\u0001"，每包复用，避免重复拼接
        }

        static IList players;                    // 客户端的 Players（List<PlayerInfo>）
        static readonly List<Peer> peers = new List<Peer>();

        // 每条 TCP 连接各自一把写锁：房主给 A 发游戏帧时不该把给 B 的发送也堵住。
        // （以前是一把全局锁，3 人以上时所有发送串行。）
        static readonly Dictionary<TcpClient, object> sendLocks = new Dictionary<TcpClient, object>();
        static readonly object sendLocksGate = new object();

        // 每线程一个发送缓冲，避免每个包都 new byte[] + 拼字符串（高频包时 GC 会抖）。
        [ThreadStatic] static byte[] txBuffer;

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
        static string selfTagPrefix = Tag + "\u0001\u0001";

        // ------------------------------------------------------------------
        // 由客户端 IL 调用
        // ------------------------------------------------------------------

        /// <summary>写 spawn.ini 时调用（此时玩家表已固定）。同一局重复调用会直接返回，不会重建端口。</summary>
        public static void Start(IList playersList, string myName, string logFilePath)
        {
            try
            {
                logPath = logFilePath;
                players = playersList;
                selfName = Clean(myName);
                selfTagPrefix = Tag + "\u0001" + selfName + "\u0001";

                // 房主判定用两个互相独立的信号，任一成立即认为是房主（3 人以上时更稳）：
                //   · byOrder ：玩家表里排在第一个的是不是我自己（房主最先入列）
                //   · byConn  ：所有对端的 LANPlayerInfo.TcpClient 都有值
                //              （房主侧是 accept 出来的连接；客机侧这些都是 null）
                // 必须在"同局早退"之前算：同一批人再开一局时角色可能反了（这次他建房），
                // 早退后不重算就会沿用上一局的 isHost，整局包全发错方向。
                bool byOrder = IsSelfFirst(playersList, selfName);
                bool byConn = LooksLikeHost(playersList, selfName);
                bool hostNow = byConn || byOrder;
                bool hostChanged = hostNow != isHost;
                isHost = hostNow;

                string key = BuildKey(playersList, selfName);
                if (running && key == currentKey && !hostChanged) return;
                currentKey = key;
                running = true;

                // 先把 SetUp 阶段攒下的日志落盘，再打版本横幅 —— 顺序就是"版本在最上面"。
                Log("======== MoLanRelay v" + Version + " ========");
                Log("载入的 DLL: " + SelfPath());

                Rebuild();
                Log("start: self=" + selfName + " host=" + isHost
                    + " (byOrder=" + byOrder + " byConn=" + byConn + ")"
                    + " 玩家数=" + CountPeers(playersList) + " 本机端口表=[" + PeerMap() + "]");

                SnapshotMatch(key);
            }
            catch (Exception ex)
            {
                // Start 是被注入到客户端 WriteSpawnIni 中间的，绝不能让异常穿出去影响开局
                running = false;
                try { Log("start: 初始化失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>当前实际加载的中继 DLL 路径 —— 三台机器是否用同一份，看这行。</summary>
        static string SelfPath()
        {
            try { return typeof(Relay).Assembly.Location; }
            catch { return "?"; }
        }

        // ------------------------------------------------------------------
        // 对局记录
        //
        // 这个客户端**完全没有录像功能**（clientdx.exe 里连 "record" 字样都搜不到），
        // 所以能留下的只有"这局是什么样"：开局几秒后把 spawn.ini 复制一份，
        // 再把地图 / 模式 / 玩家追加到 index.txt —— 出问题时拿来复盘谁在场、什么设置。
        // ------------------------------------------------------------------

        static string lastSnapshotKey = "";

        static void SnapshotMatch(string key)
        {
            try
            {
                if (string.IsNullOrEmpty(key) || key == lastSnapshotKey) return;
                lastSnapshotKey = key;
                if (string.IsNullOrEmpty(logPath)) return;
                string logDir = Path.GetDirectoryName(logPath);
                if (string.IsNullOrEmpty(logDir)) return;

                Thread t = new Thread(delegate()
                {
                    try
                    {
                        Thread.Sleep(6000);      // 等客户端把 spawn.ini 写完、游戏真正起来
                        string src = Path.Combine(logDir, "spawn.ini");
                        if (!File.Exists(src)) return;

                        string text = File.ReadAllText(src);
                        string map = IniValue(text, "UIMapName");
                        string mode = IniValue(text, "UIGameMode");
                        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");

                        string dir = Path.Combine(logDir, "对局记录");
                        Directory.CreateDirectory(dir);

                        string safe = SafeFileName(map);
                        if (safe.Length == 0) safe = "未知地图";
                        string dst = Path.Combine(dir, stamp + "_" + safe + ".ini");
                        File.Copy(src, dst, true);

                        string players = string.Join("、", PlayerNames(text));
                        File.AppendAllText(Path.Combine(dir, "index.txt"),
                            stamp + "  |  " + map + "  |  " + mode + "  |  " + players + Environment.NewLine,
                            new UTF8Encoding(false));

                        Log("match: 已保存对局记录 " + Path.GetFileName(dst) + "（" + players + "）");
                    }
                    catch (Exception ex) { Log("match: 保存对局记录失败: " + ex.Message); }
                });
                t.IsBackground = true;
                t.Start();
            }
            catch { }
        }

        /// <summary>从 ini 文本里取一个键的值（找不到返回空串）。</summary>
        static string IniValue(string text, string key)
        {
            try
            {
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '[') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                        return line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return "";
        }

        /// <summary>spawn.ini 里所有 Name= 的值（含 [Settings] 的主机自己和 [OtherN]）。</summary>
        static List<string> PlayerNames(string spawnText)
        {
            List<string> names = new List<string>();
            try
            {
                foreach (string raw in spawnText.Split('\n'))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    if (!string.Equals(k, "Name", StringComparison.OrdinalIgnoreCase)) continue;
                    string v = line.Substring(eq + 1).Trim();
                    if (v.Length > 0 && !names.Contains(v)) names.Add(v);
                }
            }
            catch { }
            return names;
        }

        /// <summary>把地图名变成合法文件名（去掉 [] 之类）。</summary>
        static string SafeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in s)
            {
                bool ok = true;
                for (int i = 0; i < bad.Length; i++) if (c == bad[i]) { ok = false; break; }
                if (ok && c != '[' && c != ']' && c != '\\' && c != '/') sb.Append(c);
            }
            string r = sb.ToString().Trim();
            return r.Length > 40 ? r.Substring(0, 40) : r;
        }

        // ------------------------------------------------------------------
        // 局域网踢人
        //
        // 背景：大厅里每个玩家格子那个"名字下拉框"选中第 2 项会调
        //       GameLobbyBase::KickPlayer(int) —— 在线大厅重写了它（发 IRC 踢人消息），
        //       局域网大厅没有重写，走的是基类的空实现，所以"点了没反应"。
        //       我们只把基类的空实现换掉 → 只影响局域网，在线大厅有自己的重写、不受影响。
        //
        // 做法：直接关掉那个玩家的连接。
        //       · 对方：读连接失败 → 自动退回局域网大厅
        //       · 房主：触发已有的 ConnectionLost → 清理 + 移除玩家 + 广播玩家列表
        //       也就是说"踢出去"之后的一系列善后全部复用现有机制，不用自己写。
        // ------------------------------------------------------------------

        /// <summary>由 GameLobbyBase::KickPlayer(int) 的补丁调用。lobby = 大厅实例。</summary>
        public static void KickPlayer(object lobby, int index)
        {
            try
            {
                if (lobby == null) return;
                if (index < 0) return;

                IList players = GetMember(lobby, "Players") as IList;
                if (players == null)
                {
                    Log("kick: 拿不到 Players，放弃");
                    return;
                }
                if (index >= players.Count)
                {
                    Log("kick: 下标 " + index + " 越界（玩家数 " + players.Count + "）");
                    return;
                }

                LANPlayerInfo lp = players[index] as LANPlayerInfo;
                if (lp == null) { Log("kick: 第 " + index + " 个不是局域网玩家（可能是 AI），忽略"); return; }

                string name = Clean(lp.Name);
                if (name.Length == 0) { Log("kick: 第 " + index + " 个还没有名字，忽略"); return; }

                // 只能踢"有连接"的玩家 —— 房主侧才成立；客机侧这些 TcpClient 都是 null，
                // 所以客机那边点了也什么都不会发生（本来也只有房主能点）。
                if (lp.TcpClient == null) { Log("kick: '" + name + "' 没有连接（自己不是房主？），忽略"); return; }

                string me = MyPlayerName();
                if (me.Length > 0 && SameName(name, me))
                {
                    Log("kick: 不能踢自己，忽略");
                    return;
                }

                Log("kick: 踢出玩家 '" + name + "'（下标 " + index + "）");

                // 先尽力通知（失败不影响踢人本身）
                try { BroadcastKickNotice(lobby, name); }
                catch (Exception ex) { Log("kick: 通知广播失败（不影响踢出）: " + ex.Message); }

                // 断开连接 —— 后面所有善后都是现成机制。
                //
                // 注意：这里绝对不能用 TcpClient.Close()！
                // Close() 会把 socket 置为"已释放"，但这个玩家对象此刻**还在 Players 列表里**，
                // 紧接着的任何大厅广播都会走到
                //     LANGameLobby.BroadcastPlayerOptions → LANPlayerInfo.IPAddress → Socket.RemoteEndPoint
                // 碰到已释放的 socket 就抛 ObjectDisposedException，直接把客户端搞崩。
                // Shutdown 只做"优雅断开"（对端读到 EOF 会退回大厅、房主侧的断开逻辑会把它
                // 从玩家列表移除并释放），socket 对象在此期间仍然可用。
                try { lp.TcpClient.Client.Shutdown(SocketShutdown.Both); }
                catch (Exception ex) { Log("kick: 断开 '" + name + "' 的连接失败: " + ex.Message); }
            }
            catch (Exception ex) { Log("kick error: " + ex.Message); }
        }

        /// <summary>尽力而为：给大家发一条"xx 已被主机踢出"的系统消息 + 房主自己弹一条提示。</summary>
        static void BroadcastKickNotice(object lobby, string name)
        {
            string text = name + " 已被主机踢出房间";

            // 1) 广播给所有客机（聊天区）
            try
            {
                MethodInfo bm = FindMethod(lobby.GetType(), "BroadcastMessage", 1);
                if (bm != null && bm.GetParameters()[0].ParameterType == typeof(string))
                    bm.Invoke(lobby, new object[] { "GLCHAT System\u00010\u0001" + text });
            }
            catch { }

            // 2) 房主自己也弹一条
            try
            {
                MethodInfo am = FindMethod(lobby.GetType(), "AddNotice", 2);
                if (am != null)
                {
                    object color = MakeColor("Red");
                    if (color != null) am.Invoke(lobby, new object[] { text, color });
                }
            }
            catch { }
        }

        /// <summary>在类型及其基类里找一个名字匹配、参数个数匹配的方法。</summary>
        static MethodInfo FindMethod(Type t, string name, int paramCount)
        {
            while (t != null)
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (m.Name == name && m.GetParameters().Length == paramCount) return m;
                t = t.BaseType;
            }
            return null;
        }

        /// <summary>反射读字段或属性（含基类）。</summary>
        static object GetMember(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            while (t != null)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(o);
                PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p.GetValue(o, null);
                t = t.BaseType;
            }
            return null;
        }

        /// <summary>反射读 int 成员，读不到/类型不对就返回默认值（绝不抛异常）。</summary>
        static int GetInt(object o, string name, int def)
        {
            try
            {
                object v = GetMember(o, name);
                return v == null ? def : Convert.ToInt32(v);
            }
            catch { return def; }
        }

        /// <summary>
        /// 玩家表的快照。网络线程（UDP/TCP 接收、发送线程）绝对不能直接 foreach 客户端那个
        /// List —— 玩家进出房间时 UI 线程会改它，边改边遍历会抛 InvalidOperationException。
        /// CopyTo 不看枚举器，看到的是某一瞬间的数组，不会抛。
        /// </summary>
        static IList PlayerList()
        {
            IList list = players;
            if (list == null) return new object[0];
            try
            {
                object[] arr = new object[list.Count];
                list.CopyTo(arr, 0);
                return arr;
            }
            catch { return new object[0]; }
        }

        // ------------------------------------------------------------------
        // 跨线程改 UI 的收件箱
        //
        // 客户端没有任何跨线程封送：往通知列表/聊天框写内容必须发生在 Update（UI 线程）里。
        // 网络线程（OnCtrl/OnHello/FileHashResult）只往这里投递，Tick 每帧统一执行。
        // ------------------------------------------------------------------
        static readonly Queue<Action> uiActions = new Queue<Action>();

        static void PostUi(Action action)
        {
            if (action == null) return;
            lock (uiActions) { if (uiActions.Count < 64) uiActions.Enqueue(action); }
        }

        static void FlushUi()
        {
            while (true)
            {
                Action a;
                lock (uiActions)
                {
                    if (uiActions.Count == 0) return;
                    a = uiActions.Dequeue();
                }
                try { a(); } catch { }
            }
        }

        /// <summary>拿 Microsoft.Xna.Framework.Color 的某个静态颜色（拿不到就返回 null）。</summary>
        static object MakeColor(string name)
        {
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type ct = a.GetType("Microsoft.Xna.Framework.Color");
                    if (ct == null) continue;
                    PropertyInfo p = ct.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                    if (p != null) return p.GetValue(null, null);
                }
            }
            catch { }
            return null;
        }

        static string cachedMyName;
        static bool warnedNoName;

        /// <summary>自己的玩家名：优先用中继已知的（Start 之后），其次从 ClientCore.ProgramConstants 取一次并缓存。
        /// 语音每秒会调好几次，所以绝不能在每次调用里遍历程序集。</summary>
        static string MyPlayerName()
        {
            if (!string.IsNullOrEmpty(selfName)) return selfName;
            if (cachedMyName != null) return cachedMyName;

            string found = "";
            string src = "";

            // 1) ClientCore.ProgramConstants.PLAYERNAME —— 局域网握手用的就是它，
            //    但主菜单赋值之前它还是默认值 "No name"，必须剔除，否则 @ 永远匹配不上。
            try
            {
                Type t = Type.GetType("ClientCore.ProgramConstants, ClientCore");
                if (t == null)
                {
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        t = a.GetType("ClientCore.ProgramConstants");
                        if (t != null) break;
                    }
                }
                if (t != null)
                {
                    PropertyInfo p = t.GetProperty("PLAYERNAME", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (p != null)
                    {
                        string v = Clean(p.GetValue(null, null) as string);
                        if (v.Length > 0 && !v.Equals("No name", StringComparison.OrdinalIgnoreCase))
                        {
                            found = v;
                            src = "ProgramConstants.PLAYERNAME";
                        }
                    }
                }
            }
            catch { }

            // 2) 兜底：读设置文件里的 [MultiPlayer] Handle（局域网里的显示名同源）
            if (found.Length == 0)
            {
                try
                {
                    string v = ReadHandleFromSettings();
                    if (v.Length > 0) { found = v; src = "设置文件 [MultiPlayer] Handle"; }
                }
                catch { }
            }

            if (found.Length > 0)
            {
                cachedMyName = found;
                Log("@：自己的名字 = '" + found + "'（来源：" + src + "）");
                return found;
            }
            if (!warnedNoName)
            {
                warnedNoName = true;
                Log("@：拿不到自己的玩家名，@功能不会生效。"
                  + "（大厅里你显示成什么名字，就把设置文件 [MultiPlayer] Handle 填成什么）");
            }
            return "";
        }

        /// <summary>兜底：从设置 ini 的 [MultiPlayer] Handle 读取自己的名字。
        /// 纯文本逐行解析，避免依赖 INI 库的具体 API。</summary>
        static string ReadHandleFromSettings()
        {
            string gamePath = SafeGetGamePath();
            if (gamePath.Length == 0) return "";

            string[] candidates = new string[] { "RA2MO.ini", "Settings.ini" };
            foreach (string n in candidates)
            {
                try
                {
                    string f = Path.Combine(gamePath, n);
                    if (!File.Exists(f)) continue;

                    bool inMp = false;
                    foreach (string raw in File.ReadAllLines(f))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == ';') continue;
                        if (line[0] == '[')
                        {
                            inMp = line.Equals("[MultiPlayer]", StringComparison.OrdinalIgnoreCase);
                            continue;
                        }
                        if (!inMp) continue;

                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        if (line.Substring(0, eq).Trim().Equals("Handle", StringComparison.OrdinalIgnoreCase))
                            return Clean(line.Substring(eq + 1));
                    }
                }
                catch { }
            }
            return "";
        }

        /// <summary>取 ClientCore.ProgramConstants.GamePath（游戏根目录）。</summary>
        static string SafeGetGamePath()
        {
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = a.GetType("ClientCore.ProgramConstants");
                    if (t == null) continue;
                    FieldInfo f = t.GetField("GamePath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (f != null) return Clean(f.GetValue(null) as string);
                }
            }
            catch { }
            return "";
        }

        // ------------------------------------------------------------------
        // @功能（局域网大厅聊天）
        //
        // 挂在大厅聊天渲染的路上：LANGameLobby::Player_HandleChatCommand 里，
        // 颜色算完之后插一次调用 —— 消息里含 "@我的名字" 就用高亮色 + 放提示音。
        // 房主自己的发言也会走这里（经过自连接绕回来），所以两边行为一致。
        // ------------------------------------------------------------------

        /// <summary>由 Player_HandleChatCommand 的补丁调用。
        /// 注意：参数/返回值故意用 object（装箱的 Color），避免 DLL 与 exe 之间
        /// 对 MonoGame 的 Color 类型产生跨程序集签名依赖 —— 那种依赖一旦版本对不齐，
        /// 会在第一次聊天（方法 JIT 时）直接抛 MissingMethodException 崩掉。</summary>
        public static object MentionColor(object original, object lobby, string text)
        {
            try
            {
                if (text == null) return original;
                AppendChatLog(text);
                string me = MyPlayerName();
                bool hasAt = text.IndexOf('@') >= 0;

                if (me.Length == 0)
                {
                    if (hasAt) Log("@：收到含 @ 的消息，但拿不到自己的名字 → 无法判断（消息：" + text + "）");
                    return original;
                }

                string why;
                if (!IsMentioned(text, lobby, me, out why))
                {
                    // 只在消息里真的有 @ 时记一条，避免刷屏
                    if (hasAt) Log("@：消息含 @ 但与我无关（我='" + me + "'，消息：" + text + "）");
                    return original;
                }

                Log("@：命中（" + why + "）！我='" + me + "'，消息：" + text + " → 高亮" + (mentionSoundEnabled ? " + 提示音" : "（提示音已关）"));
                if (mentionSoundEnabled) PlayMessageSound();
                // 用青色而不是金色：局域网默认色号 5 就是 Goldenrod，金色高亮跟它几乎一样，
                // 看上去就像"@没反应"。青色不在那 15 个色号里，一眼能认出来。
                return (object)new Microsoft.Xna.Framework.Color(0, 255, 255);
            }
            catch (Exception ex) { Log("mention error: " + ex.Message); }
            return original;
        }

        // ------------------------------------------------------------------
        // 聊天留档
        //
        // 这个客户端没有录像，聊天就是最直接的复盘材料：和 spawn.ini 快照放同一个目录。
        // 每条聊天先入队，StatusTick 每 2 秒落一次盘，避免每句话都碰磁盘。
        // ------------------------------------------------------------------

        static readonly List<string> chatLogQueue = new List<string>();
        static DateTime lastChatFlush = DateTime.MinValue;

        static void AppendChatLog(string text)
        {
            try
            {
                if (!chatLogEnabled || string.IsNullOrEmpty(text)) return;
                string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text.Trim();
                if (line.Length == 0) return;
                lock (chatLogQueue)
                {
                    if (chatLogQueue.Count >= 2000) return;   // 异常刷屏时别把内存撑爆
                    chatLogQueue.Add(line);
                }
            }
            catch { }
        }

        static void ChatLogTick()
        {
            if ((DateTime.Now - lastChatFlush).TotalSeconds < 2) return;
            lastChatFlush = DateTime.Now;

            string[] lines;
            lock (chatLogQueue)
            {
                if (chatLogQueue.Count == 0) return;
                lines = chatLogQueue.ToArray();
                chatLogQueue.Clear();
            }

            try
            {
                if (!chatLogEnabled) return;      // 关掉了就把积压丢掉
                string root = SafeGetGamePath();
                if (string.IsNullOrEmpty(root)) return;

                string dir = Path.Combine(root, "对局记录");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "聊天_" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
                File.AppendAllLines(file, lines, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("chatlog: 写入失败: " + ex.Message); }
        }

        /// <summary>
        /// 这条消息是不是在叫我。支持三种写法：
        ///   @我的名字              —— 一对一
        ///   @所有人 / @all         —— 所有人都提醒
        ///   @队伍N / @teamN        —— 只提醒队伍编号为 N 的人（N 是游戏里显示的 1~4）
        /// 聊天回调只给了消息文本、没有发送者，所以队伍只能按"接收者自己的队伍号"匹配。
        /// </summary>
        static bool IsMentioned(string text, object lobby, string me, out string why)
        {
            why = "";

            if (text.IndexOf("@所有人", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("@all", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                why = "@所有人";
                return true;
            }

            if (me.Length > 0 && text.IndexOf("@" + me, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                why = "@" + me;
                return true;
            }

            int team = MyTeamId(lobby);
            if (team >= 0 && team < 4)
            {
                int display = team + 1;
                if (text.IndexOf("@队伍" + display, StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("@team" + display, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    why = "@队伍" + display;
                    return true;
                }
            }

            return false;
        }

        /// <summary>自己在大厅里的队伍编号（0 起，拿不到返回 -1）。</summary>
        static int MyTeamId(object lobby)
        {
            try
            {
                IList players = GetMember(lobby, "Players") as IList;
                if (players == null) return -1;

                string me = Clean(MyPlayerName());
                if (me.Length == 0) return -1;

                foreach (object o in players)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null) continue;
                    if (!SameName(Clean(lp.Name), me)) continue;

                    object v = GetMember(lp, "TeamId");
                    return v == null ? -1 : Convert.ToInt32(v);
                }
            }
            catch { }
            return -1;
        }

        /// <summary>由 Player_HandleChatCommand 的补丁调用（插在 ChatMessage 构造之前）。
        /// 消息正文 @ 到我时，在本机显示的这份文本最前面加一个"[@你]"标记。
        /// 只改本机显示的字符串，发出去的内容一个字节都不动 —— 对端没装补丁也照样能收。
        ///
        /// 为什么光改颜色不够：局域网聊天默认色号是 5 = Goldenrod(218,165,32)，
        /// 和"金色高亮"肉眼几乎分不出来，看起来就像"@没反应"。</summary>
        public static string MentionText(string text, object lobby)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return text;
                if (text.StartsWith("[@", StringComparison.Ordinal)) return text;   // 已经加过，别重复

                string me = MyPlayerName();
                if (me.Length == 0) return text;

                string why;
                if (!IsMentioned(text, lobby, me, out why)) return text;

                Log("@：给消息正文加了 [@你] 标记（" + why + "）→ " + text);
                return "[@你] " + text;
            }
            catch { return text; }
        }

        // ------------------------------------------------------------------
        // @ 提示音（自己生成的，不用客户端音效、不依赖系统声音方案、无版权问题）
        //
        // 做法：内存里现生成一段 16bit/22kHz 单声道的"叮—咚"（880Hz 0.09s → 1318Hz 0.16s，
        //       头尾各 3ms 淡入淡出免爆音），包成 WAV 交给 System.Media.SoundPlayer 异步播。
        //
        // 为什么不用别的：
        //   · 客户端音效（sndMessageSound / message.wav）——用户明确要求不要用；
        //   · SystemSounds.*  ——跟随用户的声音方案，方案设成"无声"时就真的没声；
        //   · Console.Beep    ——同步阻塞，且很多机器上根本不出声。
        // 自己生成最可控：音量固定、时长固定、任何机器都一样。
        // ------------------------------------------------------------------

        static byte[] mentionWav;                    // 生成的 WAV（留静态引用，防止被 GC）
        static System.Media.SoundPlayer mentionPlayer;
        static DateTime lastMentionSound = DateTime.MinValue;

        static void PlayMessageSound()
        {
            try
            {
                if ((DateTime.Now - lastMentionSound).TotalMilliseconds < 250) return;   // 防刷屏
                lastMentionSound = DateTime.Now;

                if (mentionPlayer == null)
                {
                    mentionWav = BuildMentionWav();
                    mentionPlayer = new System.Media.SoundPlayer(new MemoryStream(mentionWav));
                    mentionPlayer.Load();
                    Log("提示音: 已生成自制提示音（" + mentionWav.Length + " 字节）");
                }
                mentionPlayer.Play();
            }
            catch (Exception ex) { Log("提示音出错: " + ex.Message); }
        }

        /// <summary>现搓一个提示音 WAV：880Hz 0.09s + 1318Hz 0.16s，头尾各 3ms 淡入淡出。</summary>
        static byte[] BuildMentionWav()
        {
            const int rate = 22050;
            const int amp = 9000;                    // 峰值，别太吵
            const double fade = 0.003;
            int n1 = (int)(rate * 0.09);
            int n2 = (int)(rate * 0.16);
            short[] pcm = new short[n1 + n2];

            Tone(pcm, 0, n1, 880.0, rate, amp, fade);
            Tone(pcm, n1, n2, 1318.5, rate, amp, fade);

            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ms);
                int dataBytes = pcm.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);                          // fmt 块长度
                w.Write((short)1);                    // PCM
                w.Write((short)1);                    // 单声道
                w.Write(rate);
                w.Write(rate * 2);                    // 字节率 = 采样率 × 声道 × 位深/8
                w.Write((short)2);                    // 块对齐
                w.Write((short)16);                   // 位深
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);
                foreach (short s in pcm) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>往 buf 的 [offset, offset+count) 写一段正弦音，头尾 fade 秒内做淡入淡出。</summary>
        static void Tone(short[] buf, int offset, int count, double freq, int rate, int amp, double fade)
        {
            int fadeN = (int)(fade * rate);
            for (int i = 0; i < count; i++)
            {
                double env = 1.0;
                if (fadeN > 0)
                {
                    if (i < fadeN) env = (double)i / fadeN;
                    int tail = count - 1 - i;
                    if (tail < fadeN) env = Math.Min(env, (double)tail / fadeN);
                }
                buf[offset + i] = (short)(Math.Sin(2.0 * Math.PI * freq * i / rate) * env * amp);
            }
        }

        // ------------------------------------------------------------------
        // 截图 / 控制帧（局域网大厅）
        //
        // 帧格式（和大厅既有消息共用那条 TCP，\u0002 收尾）：
        //     MO-CTRL \u0001 <kind> \u0001 <from> \u0001 <base64 payload>
        // 目前 kind = SHOT（截图）。
        //
        // 流程：谁按热键谁发 → 客机发给房主 → 房主本地显示 + 原样广播给所有人。
        //       帧里带着发送者名字，所以发送者自己收到时会跳过，不会重复弹。
        // ------------------------------------------------------------------

        public const string CtrlTag = "MO-CTRL";
        const string CtrlSep = "\u0001";

        /// <summary>热键（在大厅或游戏内都生效，只要前台窗口是客户端或 gamemd）。</summary>
        const int VK_F4 = 0x73, VK_F5 = 0x74, VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78;
        static bool clipKeyWasDown, statsKeyWasDown, soundKeyWasDown;
        static bool mentionSoundEnabled = true;      // F9 可切换

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        static bool KeyDown(int vk)
        {
            try { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
            catch { return false; }
        }

        static int selfPid;                      // 本进程 PID（查一次就够）
        static int gamePid;                      // gamemd.exe 的 PID（缓存，5 秒刷一次）
        static DateTime lastPidScan = DateTime.MinValue;

        /// <summary>前台窗口是不是"我们的"：客户端自己，或者游戏本体 gamemd。
        /// 加这个判断是为了避免在别的程序里按 F8 也把截图发到游戏房间。
        /// 注意：这里每帧都会被调用，所以绝不能每帧去 Process.GetProcessById（那会不停地开进程句柄）。</summary>
        static bool OursInFront()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                uint pid;
                GetWindowThreadProcessId(fg, out pid);
                if (pid == 0) return false;

                if (selfPid == 0)
                {
                    try { selfPid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
                }
                if (selfPid != 0 && pid == (uint)selfPid) return true;

                if ((DateTime.Now - lastPidScan).TotalSeconds > 15)
                {
                    // 扫描进程有开销（几十毫秒），放在每帧调用的路径上会把大厅卡出周期性顿挫。
                    // 现在只剩 Tab 补全用它，15 秒足够。
                    lastPidScan = DateTime.Now;
                    try
                    {
                        System.Diagnostics.Process[] ps = System.Diagnostics.Process.GetProcessesByName("gamemd");
                        gamePid = ps.Length > 0 ? ps[0].Id : 0;
                        foreach (System.Diagnostics.Process p in ps) { try { p.Dispose(); } catch { } }
                    }
                    catch { }
                }
                return gamePid != 0 && pid == (uint)gamePid;
            }
            catch { }
            return false;
        }

        public static bool IsCtrlMessage(string message)
        {
            return message != null && message.StartsWith(CtrlTag + CtrlSep, StringComparison.Ordinal);
        }

        static int probeCount;

        /// <summary>
        /// 诊断用：客户端每收到一条局域网消息都会先调这里（只记前 30 条）。
        /// 目的是看清"对方的帧到底长什么样" —— 特别是 MO-CTRL 后面的分隔符在不在。
        /// 排查完可以把这个调用从 IL 里去掉。
        /// </summary>
        public static void ProbeMessage(string message)
        {
            try
            {
                if (message == null || probeCount >= 30) return;
                probeCount++;

                int n = message.Length < 16 ? message.Length : 16;
                StringBuilder codes = new StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) codes.Append(',');
                    codes.Append((int)message[i]);
                }

                Log("probe: len=" + message.Length + " ctrl=" + IsCtrlMessage(message)
                    + " codes=[" + codes + "]" + (message.Length > 16 ? " …" : "")
                    + " text='" + (message.Length > 24 ? message.Substring(0, 24) : message) + "'");
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // 聊天框 Tab 补全（局域网大厅）
        //
        // 这套客户端本来就**没有** @ 补全：聊天框只挂了 EnterPressed，
        // Tab(9) 在 XNATextBox::Keyboard_OnKeyPressed 的 switch 里直接落到 ret，什么都不做。
        //
        // 这里不去改 Rampastring.XNAUI.dll（它是公共库，动它影响面太大），
        // 改成在 LANGameLobby::Update 每帧轮询 VK_TAB，并且要求
        // 「聊天框正被选中 + 光标处正在写 @名字」两个条件同时成立才动手，
        // 所以其它控件用 Tab 完全不受影响。
        // ------------------------------------------------------------------

        const int VK_TAB = 0x09;
        static bool tabKeyWasDown;

        static void TabTick(object lobby, bool front)
        {
            if (!tabCompleteEnabled) { tabKeyWasDown = false; return; }

            bool down = front && KeyDown(VK_TAB);
            if (down && !tabKeyWasDown) TryCompleteMention(lobby);
            tabKeyWasDown = down;
        }

        static void TryCompleteMention(object lobby)
        {
            object tb = GetMember(lobby, "tbChatInput");
            if (tb == null) return;

            // 只有聊天框处于"被选中"状态才响应（否则 Tab 是别的控件在用）
            object wm = GetMember(tb, "WindowManager");
            object sel = wm == null ? null : GetMember(wm, "SelectedControl");
            if (sel == null || !object.ReferenceEquals(sel, tb)) return;

            string text = GetMember(tb, "Text") as string;
            if (string.IsNullOrEmpty(text)) return;

            int at = text.LastIndexOf('@');
            if (at < 0) return;

            // @ 前面必须是行首或空白，避免把邮箱地址之类的东西当成 @
            if (at > 0 && !char.IsWhiteSpace(text[at - 1])) return;

            string partial = text.Substring(at + 1);
            if (partial.IndexOf(' ') >= 0) return;      // @ 后面已经有空格 → 名字打完了，不用补

            List<string> names = CollectNames(lobby, partial);
            if (names.Count == 0)
            {
                Log("@补全：没有匹配 '" + partial + "' 的玩家");
                return;
            }

            string pick = names[0];
            string newText = text.Substring(0, at + 1) + pick;

            SetMember(tb, "Text", newText);
            SetMember(tb, "InputPosition", newText.Length);
            SetMember(tb, "TextEndPosition", newText.Length);

            Log("@补全：'" + partial + "' → '" + pick + "'（候选：" + string.Join("/", names.ToArray()) + "）");
        }

        /// <summary>按前缀收集候选名字：大厅玩家表 + 自己（局域网里自己不一定在表里）。</summary>
        static List<string> CollectNames(object lobby, string prefix)
        {
            List<string> found = new List<string>();

            IList ps = GetMember(lobby, "Players") as IList;
            if (ps != null)
            {
                foreach (object p in ps)
                {
                    string n = Clean(GetMember(p, "Name") as string);
                    if (n.Length == 0) continue;
                    if (prefix.Length > 0 && n.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) != 0) continue;
                    if (!HasName(found, n)) found.Add(n);
                }
            }

            string me = MyPlayerName();
            if (me.Length > 0
                && (prefix.Length == 0 || me.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) == 0)
                && !HasName(found, me))
                found.Add(me);

            return found;
        }

        static bool HasName(List<string> list, string name)
        {
            foreach (string s in list) if (SameName(s, name)) return true;
            return false;
        }

        /// <summary>反射写字段或属性（含基类）。返回是否真的写成功（找不到成员时不再静默失败）。</summary>
        static bool SetMember(object o, string name, object value)
        {
            if (o == null) return false;
            Type t = o.GetType();
            while (t != null)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) { f.SetValue(o, value); return true; }
                PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanWrite) { p.SetValue(o, value, null); return true; }
                t = t.BaseType;
            }
            return false;
        }

        /// <summary>
        /// 「回到加新功能之前」总开关。
        /// false = 关闭 @功能、截图、语音、踢人、F4~F8 快捷键、中继版本自检（这些都需要 exe 侧对应的 IL 补丁配合）。
        /// 改回 true 即可恢复；注意 exe 侧也要用带对应模式的流水线重新打补丁。
        /// 中继本身的收发/转发不受此开关影响。
        /// </summary>
        internal static readonly bool FeaturesEnabled = false;

        // ==================================================================
        // 主菜单右下角：补丁版本 + HMOL 版本
        //
        // 由 MainMenu::Update 的 IL 钩子每帧调用。显示内容形如：
        //     Patch v0.1.6  |  HMOL v1.5.16
        //     Patch v0.1.6  |  HMOL not running      （不是从 HMOL 启动的）
        // 直接改写主菜单 [lblVersion]（原版把它放在屏幕外 2048,663），
        // 并按控件实际尺寸贴到右下角，所以不依赖 MainMenu.ini 里的坐标。
        // ==================================================================

        static object versionLabel;          // MainMenu 上的 lblVersion 控件
        static object versionLabelOwner;     // 它所属的 MainMenu（重建时重新取）
        static PropertyInfo versionLabelText; // 缓存的 Text 属性（每帧要读，别反复查类型链）
        static string versionLabelHmol;      // 缓存的 HMOL 版本（"" = 没有会话信息）
        static bool versionLabelHmolRead;
        static bool versionLabelPlaced;      // 是否已成功贴到右下角（窗口尺寸就绪前会重试）
        static bool versionLabelMissing;     // 只在第一次找不到控件时写日志，之后安静重试
        static bool positionLogged;          // 位置诊断只写一次日志
        static string versionLineCache;      // 版本行只拼一次（每帧比较用，避免反复分配字符串）

        // 主菜单精简：固定隐藏 CnCNet 线上入口与社交图标，下方按钮上移补位。
        // 这是本补丁的固定行为（局域网版不需要线上入口），不提供开关，也不涉及主题/美化。
        static object mainMenuLayoutOwner;
        static bool mainMenuShifted;
        static object cncnetButton;
        static bool cncnetLabelsHidden;
        static object extraMenuOwner;
        static bool extraMenuHidden;

        public static void MainMenuTick(object mainMenu)
        {
            if (mainMenu == null) return;

            LoadSettings();

            try { OptionsTick(mainMenu); }
            catch (Exception ex) { Log("options tick error: " + ex.Message); }

            // 隐藏 CnCNet 线上入口与社交图标（局域网版固定精简，不需要线上入口）
            try { ApplyMainMenuLayout(mainMenu); }
            catch (Exception ex) { Log("verlabel: 调整主菜单布局失败: " + ex.Message); }

            try { HideLeftoverLinks(mainMenu); }
            catch (Exception ex) { Log("verlabel: 清理残留链接失败: " + ex.Message); }

            // 顶部栏：禁用「CnCNet 大厅」小按钮、把状态文字改成「局域网大厅」
            try { ApplyTopBarLayout(mainMenu); }
            catch (Exception ex) { Log("verlabel: 调整顶部栏失败: " + ex.Message); }

            // 回主菜单：清掉大厅引用（导出的提示不要再往旧大厅里发）
            logLobbyCurrent = null;

            // 设置窗口「地图」页：清单有变化时重画
            try { MapTabTick(); } catch (Exception ex) { Log("clearmaps: 刷新列表失败: " + ex.Message); }

            try
            {
                if (versionLabel == null || !ReferenceEquals(versionLabelOwner, mainMenu))
                {
                    object label = GetMember(mainMenu, "lblVersion");
                    if (label == null)
                    {
                        // 理论上 Initialize 已经建好控件；拿不到就安静地下一帧再试，不要永久放弃
                        if (!versionLabelMissing)
                        {
                            versionLabelMissing = true;
                            Log("verlabel: 还没找到 MainMenu.lblVersion，稍后重试");
                        }
                        return;
                    }
                    versionLabelMissing = false;
                    versionLabelOwner = mainMenu;
                    versionLabel = label;
                    versionLabelText = FindProperty(label.GetType(), "Text");
                    versionLabelPlaced = false;
                    positionLogged = false;
                }

                string want = versionLineCache;
                if (want == null) want = versionLineCache = BuildVersionLine();
                string current = versionLabelText == null ? null : versionLabelText.GetValue(versionLabel, null) as string;
                if (!string.Equals(current, want, StringComparison.Ordinal))
                {
                    // 客户端在更新检查等场景会重写这个标签，所以每帧都对一次，不一致就改回来
                    SetMember(versionLabel, "Text", want);
                    versionLabelPlaced = false;
                    Log("verlabel: " + want);
                }

                // 第一次可能还没拿到窗口尺寸（Update 早于布局），没贴上就下一帧接着试
                if (!versionLabelPlaced) versionLabelPlaced = PlaceBottomRight(mainMenu, versionLabel);
            }
            catch (Exception ex)
            {
                // 单次异常不永久关闭功能（可能是某一帧控件状态异常），下一帧会继续尝试
                if (!versionLabelMissing)
                {
                    versionLabelMissing = true;
                    Log("verlabel error: " + ex.Message);
                }
            }
        }

        static PropertyInfo FindProperty(Type type, string name)
        {
            while (type != null)
            {
                PropertyInfo p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p;
                type = type.BaseType;
            }
            return null;
        }


        /// <summary>
        /// 隐藏主菜单的 CnCNet 线上大厅入口，并把下面的按钮整体上移 42 像素。
        /// 只在「按钮当前还是可见」时上移 —— 旧版安装器改过 MainMenu.ini（Visible=0 且已上移）
        /// 的情况会跳过，不会二次位移。
        /// </summary>
        static void ApplyMainMenuLayout(object mainMenu)
        {
            // 换了一个 MainMenu 实例（客户端重建主菜单）就重新按新实例判断，
            // 否则新控件的按钮停在原位、CnCNet 却被隐藏，会多出一个空位。
            if (!ReferenceEquals(mainMenuLayoutOwner, mainMenu))
            {
                mainMenuLayoutOwner = mainMenu;
                mainMenuShifted = false;
                cncnetButton = null;
                cncnetLabelsHidden = false;
            }

            if (cncnetButton == null)
            {
                cncnetButton = FindControl(mainMenu, "btnCnCNet");
                if (cncnetButton == null) return;   // 布局还没建好，下一帧再试
            }

            bool hidden = GetMember(cncnetButton, "Visible") is bool v && !v;

            if (!mainMenuShifted)
            {
                if (hidden)
                {
                    // 旧版安装器改过 MainMenu.ini（已经隐藏+上移），不要再动
                    mainMenuShifted = true;
                }
                else
                {
                    // 所有按钮都找到了才动：只移动一部分的话，下一帧会对已移动的再移一次
                    List<object> buttons = new List<object>(MenuButtons.Length);
                    for (int i = 0; i < MenuButtons.Length; i++)
                    {
                        object b = FindControl(mainMenu, MenuButtons[i]);
                        if (b == null) { buttons.Clear(); break; }
                        buttons.Add(b);
                    }

                    if (buttons.Count == MenuButtons.Length)
                    {
                        for (int i = 0; i < buttons.Count; i++)
                            SetMember(buttons[i], "Y", GetInt(buttons[i], "Y", 0) - MenuButtonShift);
                        mainMenuShifted = true;
                        Log("verlabel: 已隐藏 CnCNet 入口，下方按钮上移 " + MenuButtonShift + "px");
                    }
                }
            }

            if (!hidden) SetMember(cncnetButton, "Visible", false);

            if (!cncnetLabelsHidden)
            {
                object st = FindControl(mainMenu, "lblCnCNetStatus");
                object cnt = FindControl(mainMenu, "lblCnCNetPlayerCount");
                if (st == null && cnt == null) return;     // 还没建好，下一帧再试
                if (st != null) SetMember(st, "Visible", false);
                if (cnt != null) SetMember(cnt, "Visible", false);
                cncnetLabelsHidden = true;
            }

            // 主页的社交图标（推特/油管/脸书/Discord）和左下角更新状态一并关掉
            if (!ReferenceEquals(extraMenuOwner, mainMenu))
            {
                extraMenuOwner = mainMenu;
                extraMenuHidden = false;
            }

            if (!extraMenuHidden)
            {
                string[] extraNames = { "menu_fb", "menu_yt", "menu_tw", "menu_dc", "lblUpdateStatus", "btnCredits" };
                int found = 0;
                for (int i = 0; i < extraNames.Length; i++)
                {
                    object ctl = FindControl(mainMenu, extraNames[i]);
                    if (ctl == null) continue;
                    found++;
                    SetMember(ctl, "Visible", false);
                }
                if (found == extraNames.Length)
                {
                    extraMenuHidden = true;
                    Log("verlabel: 已关闭主页社交图标与更新状态 " + found + " 项");
                }
            }
        }

        // 「关于/制作人员」窗口里的 ModDB（官网）按钮：窗口是临时创建的，
        // 所以每两秒扫一遍 WindowManager.Controls，见到就关掉。
        static DateTime lastLinkSweep = DateTime.MinValue;
        static bool moddbHiddenLogged;

        static void HideLeftoverLinks(object mainMenu)
        {
            if ((DateTime.Now - lastLinkSweep).TotalSeconds < 2) return;
            lastLinkSweep = DateTime.Now;

            try
            {
                object wm = GetMember(mainMenu, "WindowManager");
                if (wm == null) return;
                IList controls = GetMember(wm, "Controls") as IList;
                if (controls == null) return;

                foreach (object ctl in controls)
                {
                    if (ctl == null) continue;

                    string[] linkNames =
                    {
                        "btnModDB", "btnForums", "btnGooglePlus",
                        "btnYouTube", "btnFacebook", "btnTwitter",
                        // 主菜单右上角那个 24x24 的「附加」小方块：点开只有一个「返回」，
                        // MO 里所有 Extras 按钮都是 Visible=0，留着纯占位，直接关掉
                        "btnExtras",
                    };

                    for (int i = 0; i < linkNames.Length; i++)
                    {
                        object b = FindControl(ctl, linkNames[i]);
                        if (b == null) continue;
                        if (GetMember(b, "Visible") is bool v && !v) continue;
                        SetMember(b, "Visible", false);
                        if (!moddbHiddenLogged)
                        {
                            moddbHiddenLogged = true;
                            Log("verlabel: 已关闭关于窗口里的官网/社交链接");
                        }
                    }
                }
            }
            catch { }
        }

        static readonly string[] MenuButtons =
            { "btnLan", "btnLoadGame", "btnOptions", "btnMapEditor", "btnStatistics", "btnExit" };

        const int MenuButtonShift = 42;


        // ==================================================================
        // 顶部栏（TopBar）：把 CnCNet 相关按钮彻底关掉，状态文字改成「局域网大厅」
        //
        // TopBar 里有这些按钮（都是 CnCNet 账号/线上大厅相关）：
        //   btnCnCNetLobby      「CnCNet 大厅 (F3)」—— 就是那个没被禁用的小按钮
        //   btnPrivateMessages  私信（F4）
        //   btnLogout           注销
        // 只隐藏它们而不动 btnMainButton，保证 F2 回主菜单仍然可用。
        // F3/F4 的键盘热键会先检查 AllowClick，所以必须一起把 AllowClick 置 false，
        // 只设 Visible 是拦不住热键的。
        // ==================================================================

        static object topBarOwner;
        static object topBar;
        static bool topBarApplied;
        static int topBarAttempts;
        static DateTime lastTopBarVerify = DateTime.MinValue;

        /// <summary>控件存在且 Visible/AllowClick 仍为真（需要重新关）。</summary>
        static bool IsControlShown(object root, string name)
        {
            object c = FindControl(root, name);
            if (c == null) return false;
            if (GetMember(c, "Visible") is bool v && v) return true;
            if (GetMember(c, "AllowClick") is bool a && a) return true;
            return false;
        }

        static void ApplyTopBarLayout(object mainMenu)
        {
            if (!ReferenceEquals(topBarOwner, mainMenu))
            {
                topBarOwner = mainMenu;
                topBar = null;
                topBarApplied = false;
            }

            if (topBar == null)
            {
                topBar = GetMember(mainMenu, "topBar");
                if (topBar == null) return;
            }

            if (!topBarApplied)
            {
                object b1 = FindControl(topBar, "btnCnCNetLobby");
                object b2 = FindControl(topBar, "btnPrivateMessages");
                object b3 = FindControl(topBar, "btnLogout");
                if (b1 == null || b2 == null || b3 == null)
                {
                    // 按钮还没建好就显示"已关闭"会让 F3/F4 继续生效，这里等下一帧重试
                    if (++topBarAttempts >= 120) topBarApplied = true;
                    return;
                }

                DisableButton(topBar, "btnCnCNetLobby");
                DisableButton(topBar, "btnPrivateMessages");
                DisableButton(topBar, "btnLogout");
                HideControl(topBar, "lblCnCNetStatus");
                HideControl(topBar, "lblCnCNetPlayerCount");

                topBarApplied = true;
                topBarAttempts = 0;
                Log("verlabel: 顶部栏已关闭 CnCNet 入口（大厅/私信/注销）");
            }
            else if ((DateTime.Now - lastTopBarVerify).TotalMilliseconds >= 1000)
            {
                // 顶部栏在切分辨率 / 返回主菜单时会重建控件，重建后那几个按钮又会显示出来。
                // 每秒复查一次：只有真的变回可见时才重新关（正常情况什么都不做）。
                lastTopBarVerify = DateTime.Now;
                if (IsControlShown(topBar, "btnCnCNetLobby")
                    || IsControlShown(topBar, "btnPrivateMessages")
                    || IsControlShown(topBar, "btnLogout")
                    || IsControlShown(topBar, "lblCnCNetStatus")
                    || IsControlShown(topBar, "lblCnCNetPlayerCount"))
                {
                    DisableButton(topBar, "btnCnCNetLobby");
                    DisableButton(topBar, "btnPrivateMessages");
                    DisableButton(topBar, "btnLogout");
                    HideControl(topBar, "lblCnCNetStatus");
                    HideControl(topBar, "lblCnCNetPlayerCount");
                    Log("verlabel: 顶部栏被重建，已重新关闭 CnCNet 入口");
                }
            }

            // 客户端在连接状态变化时会重写这个标签（默认「离线」），每帧对一次
            object status = GetMember(topBar, "lblConnectionStatus");
            if (status == null) return;
            if ((GetMember(status, "Text") as string) == LanLobbyStatusText) return;

            SetMember(status, "Text", LanLobbyStatusText);
            CallMethod(status, "CenterOnParent");
        }

        const string LanLobbyStatusText = "局域网大厅";

        static void DisableButton(object root, string name)
        {
            object button = FindControl(root, name);
            if (button == null) return;
            SetMember(button, "AllowClick", false);   // 拦热键（F3/F4 走 AllowClick）
            SetMember(button, "Visible", false);
        }

        static void CallMethod(object o, string name)
        {
            if (o == null) return;
            Type t = o.GetType();
            while (t != null)
            {
                MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (m != null) { m.Invoke(o, null); return; }
                t = t.BaseType;
            }
        }

        static void HideControl(object root, string name)
        {
            object control = FindControl(root, name);
            if (control == null) return;
            if (GetMember(control, "Visible") is bool v && !v) return;
            SetMember(control, "Visible", false);
        }

        /// <summary>先按字段名找（MainMenu 里大部分控件都是字段），找不到再按 Name 在子控件树里搜。</summary>
        static object FindControl(object root, string name)
        {
            object direct = GetMember(root, name);
            if (direct != null) return direct;
            return FindChildByName(root, name, 0);
        }

        static object FindChildByName(object parent, string name, int depth)
        {
            if (parent == null || depth > 4) return null;

            IList children = GetMember(parent, "Children") as IList;
            if (children == null) return null;

            foreach (object child in children)
            {
                if (child == null) continue;
                if (GetMember(child, "Name") is string childName
                    && string.Equals(childName, name, StringComparison.OrdinalIgnoreCase))
                    return child;

                object found = FindChildByName(child, name, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        static string BuildVersionLine()
        {
            if (!versionLabelHmolRead)
            {
                versionLabelHmolRead = true;
                try { versionLabelHmol = ReadHmolVersion(); }
                catch { versionLabelHmol = ""; }
            }

            string hmol = string.IsNullOrEmpty(versionLabelHmol)
                ? "HMOL not running"
                : "HMOL v" + versionLabelHmol;

            return "Patch v" + Version + "  |  " + hmol;
        }

        /// <summary>读 HMOL 启动前写的会话标记；不是这次启动留下的旧文件就算"没在跑"。</summary>
        static string ReadHmolVersion()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SessionStampFileName);
                if (!File.Exists(path)) return "";
                if (!SessionStampIsFresh(path)) return "";

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    string line = raw == null ? "" : raw.Trim();
                    if (line.StartsWith("HMOL=", StringComparison.OrdinalIgnoreCase))
                        return SanitizeToken(line.Substring(5));
                }
            }
            catch (Exception ex)
            {
                Log("verlabel: 读取 HMOL 版本失败: " + ex.Message);
            }
            return "";
        }

        /// <summary>
        /// 按控件实际宽高贴到右下角。贴上返回 true。
        /// 尺寸优先取 WindowManager.RenderResolutionX/Y —— 控件坐标用的是**渲染分辨率**，
        /// 不是 MainMenu.ini 里写的 Size（那是 1280x768，实际常见 1280x720，
        /// 直接用它会算出屏幕外的 y，看起来就像"版本号没显示"）。
        /// </summary>
        static bool PlaceBottomRight(object fallback, object label)
        {
            try
            {
                int w = 0, h = 0;

                object wm = GetMember(label, "WindowManager");
                if (wm != null)
                {
                    w = GetInt(wm, "RenderResolutionX", 0);
                    h = GetInt(wm, "RenderResolutionY", 0);
                }

                if (w <= 0 || h <= 0)
                {
                    object container = GetMember(label, "Parent") ?? fallback;
                    w = GetInt(container, "Width", 0);
                    h = GetInt(container, "Height", 0);
                }

                int lw = GetInt(label, "Width", 0);
                int lh = GetInt(label, "Height", 0);
                if (w <= 0 || h <= 0 || lw <= 0) return false;

                int x = w - lw - 10;
                int y = h - lh - 6;
                if (x < 0) x = 0;
                if (y < 0) y = 0;

                SetMember(label, "X", x);
                SetMember(label, "Y", y);

                // 读回确认真的设进去了（X/Y 是属性，若只读会写失败）
                int rx = GetInt(label, "X", -1);
                int ry = GetInt(label, "Y", -1);

                if (!positionLogged)
                {
                    positionLogged = true;
                    object parent = GetMember(label, "Parent");
                    string parentName = parent == null
                        ? "<null>"
                        : ((GetMember(parent, "Name") as string) ?? "<noname>");
                    Log("verlabel: 位置 x=" + rx + " y=" + ry
                        + "（控件 " + lw + "x" + lh + "，渲染分辨率 " + w + "x" + h
                        + "，parent=" + parentName + "，visible=" + GetMember(label, "Visible") + "）");
                }

                return rx == x && ry == y;
            }
            catch (Exception ex)
            {
                if (!positionLogged)
                {
                    positionLogged = true;
                    Log("verlabel: 定位失败: " + ex.Message);
                }
                return false;
            }
        }


        /// <summary>由 LANLobby::Update 每帧调用（保留入口，旧版 exe 已打入调用）。</summary>
        public static void WindowTick(object window, object gameTime)
        {
        }

        /// <summary>由 LANGameLobby::Update 的补丁每帧调用 —— 热键轮询 / 版本自检 / 语音。</summary>
        public static void Tick(object lobby, object gameTime)
        {
            LoadSettings();

            FlushUi();

            // 房主身份以大厅的 IsHost 为准，isHost 只是给网络线程用的缓存。
            // 两套判据不一致时，游戏帧会按一套走、控制帧按另一套走，排障日志会自相矛盾。
            try
            {
                object hv = GetMember(lobby, "IsHost");
                if (hv is bool)
                {
                    bool hostNow = (bool)hv;
                    if (hostNow != isHost)
                    {
                        Log("host: 按大厅状态校正 isHost " + isHost + " → " + hostNow);
                        isHost = hostNow;
                    }
                }
            }
            catch { }

            try { CleanupConnections(); } catch { }

            bool front = false;
            try { front = OursInFront(); } catch { }

            // Tab 补全：本轮新加的功能，不受 FeaturesEnabled 总开关影响
            // （那个开关管的是"回到加新功能之前"那批：截图/语音/F5/F6/版本自检）
            try { TabTick(lobby, front); }
            catch (Exception ex) { Log("tab tick error: " + ex.Message); }

            // 开赛前检查：本轮新加的功能，同样不受 FeaturesEnabled 总开关影响
            try { PreflightTick(lobby); }
            catch (Exception ex) { Log("preflight tick error: " + ex.Message); }

            // 延迟测量 / 大厅状态条 / 掉线提示：本轮新加，不受总开关影响
            try { FpsTick(); } catch { }
            try { PingTick(lobby); } catch { }
            try { StatusTick(lobby); }
            catch (Exception ex) { Log("status tick error: " + ex.Message); }

            // 地图同步：房主侧那个「同步地图」按钮的创建与显隐
            try { MapSyncTick(lobby); }
            catch (Exception ex) { Log("mapsync tick error: " + ex.Message); }

            // 引擎拓展检测（房间广播 + 状态条标记）
            try { ExtensionCheckTick(lobby); } catch { }

            // 底部「快捷语」按钮与面板
            try { QuickPanelTick(lobby); } catch { }

            // 客机进房自动勾上「准备」；有人重名时提示一次
            try { AutoReadyTick(lobby); } catch { }
            try { DupNameTick(lobby); } catch { }

            // 房间公告：房主给新进房的人私发一次
            try
            {
                IList pl = GetMember(lobby, "Players") as IList;
                MotdTick(lobby, pl, isHost);
            }
            catch { }
            // 状态栏右侧「日志」按钮 + 实时日志面板
            try { LogPanelTick(lobby); } catch { }

            // 进大厅后自动报一次版本（Patch + HMOL）
            try { VersionAnnounceTick(lobby); } catch { }

            // F5：大厅 + 中继体检报告（本轮重新放出来，方便排查）
            try
            {
                bool k = front && KeyDown(VK_F5);
                if (k && !statsKeyWasDown) ShareReport(lobby);
                statsKeyWasDown = k;
            }
            catch (Exception ex) { Log("体检报告出错: " + ex.Message); }

            // F9：开/关 @ 提示音
            try
            {
                bool k = front && KeyDown(VK_F9);
                if (k && !soundKeyWasDown)
                {
                    mentionSoundEnabled = !mentionSoundEnabled;
                    if (settingsSoundCheck != null) SetMember(settingsSoundCheck, "Checked", mentionSoundEnabled);
                    SaveSettings();
                    Notice(lobby, "@ 提示音已" + (mentionSoundEnabled ? "开启" : "关闭"), true);
                    Log("@ 提示音: " + (mentionSoundEnabled ? "开" : "关"));
                }
                soundKeyWasDown = k;
            }
            catch { }

            if (!FeaturesEnabled) return;

            // F6：把剪贴板内容当聊天发出去（分享 IP:端口 用）
            try
            {
                bool k = front && KeyDown(VK_F6);
                if (k && !clipKeyWasDown) ShareClipboard(lobby);
                clipKeyWasDown = k;
            }
            catch (Exception ex) { Log("分享剪贴板出错: " + ex.Message); }

            try { HelloTick(lobby); } catch { }

            try { VoiceTick(lobby, front); }
            catch (Exception ex) { Log("voice tick error: " + ex.Message); }
        }

        // ==================================================================
        // 实用三件套：分享地址(F6) / 体检报告(F5) / 中继版本自检
        // ==================================================================

        /// <summary>把一条文本当聊天发出去（走大厅自己的发送逻辑，客机会经房主广播）。</summary>
        static void SendChat(object lobby, string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return;
                // 控制字符会破坏大厅的 \u0001 / \u0002 分帧
                text = text.Replace('\u0001', ' ').Replace('\u0002', ' ')
                           .Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (text.Length > 700) text = text.Substring(0, 700) + "…";
                MethodInfo sm = FindMethod(lobby.GetType(), "SendChatMessage", 1);
                if (sm == null) { Log("找不到 SendChatMessage，消息发不出去"); return; }
                sm.Invoke(lobby, new object[] { text });
            }
            catch (Exception ex) { Log("SendChat 出错: " + ex.Message); }
        }

        /// <summary>F6：把剪贴板内容当聊天发出去 —— HMOL 复制出来的 IP:端口 直接就能发到房间里。</summary>
        static void ShareClipboard(object lobby)
        {
            string txt = "";
            try { txt = Clipboard.GetText(); } catch { }
            txt = (txt ?? "").Trim();
            if (txt.Length == 0) { Notice(lobby, "剪贴板是空的，没东西可发", false); return; }
            Log("clip: 把剪贴板内容（" + txt.Length + " 字符）当聊天发出去");
            SendChat(lobby, txt);
        }

        /// <summary>F5：大厅 + 中继的体检报告，直接发到聊天区（省得再去翻 MoLanRelay.log）。</summary>
        static void ShareReport(object lobby)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[体检] 中继 v").Append(Version);
            try
            {
                sb.Append(" 身份=").Append(IsHostOf(lobby) ? "房主" : "客机");
                IList players = GetMember(lobby, "Players") as IList;
                if (players != null)
                {
                    StringBuilder names = new StringBuilder();
                    foreach (object o in players)
                    {
                        LANPlayerInfo lp = o as LANPlayerInfo;
                        if (lp == null) continue;
                        string n = Clean(lp.Name);
                        if (n.Length == 0) continue;
                        if (names.Length > 0) names.Append('/');
                        names.Append(n);
                    }
                    sb.Append(" 玩家=").Append(names.Length == 0 ? "(无)" : names.ToString());
                }
            }
            catch { }
            double rtt = CurrentRttMs();
            sb.Append(" | 延迟=").Append(rtt > 0 ? ((int)Math.Round(rtt)) + "ms" : "未知");
            sb.Append(" | ").Append(StatsLine());
            Log("report: " + sb);
            SendChat(lobby, sb.ToString());
        }

        /// <summary>中继计数摘要（统计线程和 F5 报告共用一份，避免两处描述不一致）。</summary>
        static string StatsLine()
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
            return "中继:本机游戏发出的包=" + ti + " 已注入=" + to
                 + " 已发出帧=" + totalSent + " 已转发帧=" + totalFwd + " 已收到帧=" + totalRecv
                 + (totalSendFail > 0 ? " 发送失败=" + totalSendFail : "")
                 + (per.Length > 0 ? " | 分对端: " + per.ToString().TrimEnd() : "");
        }

        // ---- 中继版本自检 ----

        const string HelloKind = "HELLO";
        static DateTime lastHello = DateTime.MinValue;
        static readonly HashSet<string> warnedVersion = new HashSet<string>();

        // ---- 延迟测量（PING / PONG）----

        const string PingKind = "PING";
        const string PongKind = "PONG";
        static DateTime lastPingSent = DateTime.MinValue;
        static double lastRttMs;          // 最近一次测得的往返延迟
        static int currentFps;            // 客户端大厅的刷新率（每秒统计一次）
        static int fpsFrames;
        static DateTime fpsWindowStart = DateTime.MinValue;

        /// <summary>每 2 秒向对端发一次 PING；对端原样回 PONG，用来算往返延迟。</summary>
        static void PingTick(object lobby)
        {
            if (lobby == null) return;
            if ((DateTime.Now - lastPingSent).TotalSeconds < 2.0) return;
            lastPingSent = DateTime.Now;

            string payload = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(DateTime.Now.Ticks.ToString()));
            string frame = CtrlTag + CtrlSep + PingKind + CtrlSep + Clean(MyPlayerName()) + CtrlSep + payload;
            SendCtrlToPeers(lobby, frame);
        }

        /// <summary>房主发给每个已连接客机；客机只发给房主。</summary>
        static void SendCtrlToPeers(object lobby, string frame)
        {
            try
            {
                if (!IsHostOf(lobby))
                {
                    MethodInfo send = FindMethod(lobby.GetType(), "SendMessageToHost", 1);
                    if (send != null) send.Invoke(lobby, new object[] { frame });
                    return;
                }

                string me = Clean(MyPlayerName());
                IList players = GetMember(lobby, "Players") as IList;
                if (players == null) return;
                object[] arr = new object[players.Count];
                try { players.CopyTo(arr, 0); } catch { return; }
                foreach (object o in arr)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null || lp.TcpClient == null) continue;
                    if (SameName(Clean(lp.Name), me)) continue;
                    SendLocked(lp, frame);
                }
            }
            catch (Exception ex) { Log("ping: 发送失败: " + ex.Message); }
        }

        /// <summary>把一帧控制消息回给指定的人（客机只有房主一个目标）。</summary>
        static void SendCtrlTo(object lobby, string target, string frame)
        {
            try
            {
                if (IsHostOf(lobby))
                {
                    // 这里不能走 FindPlayer：它查的是静态字段 players，而那要等开局
                    // （WriteSpawnIni → Relay.Start）才有值。大厅阶段用它必然找不到人，
                    // 表现为「清单能发到客机、地图分片却一片都收不到」（WarnOnce 还只打一次日志）。
                    LANPlayerInfo lp = FindPlayerIn(lobby, target);
                    if (lp == null)
                    {
                        WarnOnce("ctrlplayer", target, "ctrl 发送失败：大厅玩家表里找不到 '" + target + "'");
                        return;
                    }
                    SendLocked(lp, frame);
                    return;
                }

                MethodInfo send = FindMethod(lobby.GetType(), "SendMessageToHost", 1);
                if (send != null) send.Invoke(lobby, new object[] { frame });
            }
            catch (Exception ex) { Log("ctrl: 回发失败: " + ex.Message); }
        }

        /// <summary>从大厅对象当前的玩家表里按名字找人（大厅阶段唯一可靠的查找方式）。</summary>
        static LANPlayerInfo FindPlayerIn(object lobby, string name)
        {
            try
            {
                IList list = GetMember(lobby, "Players") as IList;
                if (list == null) return null;

                object[] arr = new object[list.Count];
                try { list.CopyTo(arr, 0); } catch { return null; }

                foreach (object o in arr)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null) continue;
                    if (SameName(lp.Name, name) || SameName(CleanName(lp), name)) return lp;
                }
            }
            catch { }
            return null;
        }

        static void OnPong(string from, string b64)
        {
            try
            {
                long sentTicks = long.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                double ms = (DateTime.Now.Ticks - sentTicks) / 10000.0;
                if (ms < 0 || ms > 60000) return;

                // 指数平滑，避免数字一直跳
                lastRttMs = lastRttMs <= 0 ? ms : lastRttMs * 0.7 + ms * 0.3;

                Peer p = FindPeer(from);
                if (p != null) p.RttMs = lastRttMs;
            }
            catch { }
        }

        /// <summary>房主取"最慢的那个客机"的延迟，客机取自己到房主的延迟。</summary>
        static double CurrentRttMs()
        {
            double max = 0;
            lock (peers) { foreach (Peer p in peers) if (p.RttMs > max) max = p.RttMs; }
            return max > 0 ? max : lastRttMs;
        }

        static void FpsTick()
        {
            DateTime now = DateTime.Now;
            if (fpsWindowStart == DateTime.MinValue) { fpsWindowStart = now; fpsFrames = 0; return; }

            fpsFrames++;
            double seconds = (now - fpsWindowStart).TotalSeconds;
            if (seconds < 1.0) return;

            currentFps = (int)Math.Round(fpsFrames / seconds);
            fpsFrames = 0;
            fpsWindowStart = now;
        }

        // ==================================================================
        // 大厅状态条：聊天输入框上方的一行
        //     FPS 60   |   Ping 23ms   |   Patch vX.Y.Z   |   HMOL vX.Y.Z
        // 另外把延迟附到顶部栏的「局域网大厅」后面。每秒刷新一次。
        // ==================================================================

        static object statusLabel;
        static string lastStatusColor = "";
        static object statusLabelLobby;
        static DateTime lastStatusUpdate = DateTime.MinValue;

        static readonly Dictionary<string, bool> onlineState =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static object onlineLobby;
        static DateTime lastOnlineScan = DateTime.MinValue;

        // ==================================================================
        // 补丁自己的设置（Resources\MoLanPatch.ini）
        //   从设置窗口新增的「联机补丁」页里切换，写回这个文件。
        // ==================================================================

        const string SettingsFileName = "MoLanPatch.ini";

        // ==================================================================
        // 局域网 TCP 端口（默认 1233）
        //   原客户端把 1233 写死在三个地方，已被 IL 补丁（lanport 模式）换成调本方法：
        //     · LANGameLobby::ListenForClients  → new TcpListener(IPAddress.Any, 1233)  房主监听
        //     · LANGameLobby::SetUp             → Connect("127.0.0.1", 1233)            房主自连
        //     · LANLobby::LbGameList_DoubleLeftClick                                    客机双击加入
        //   端口来源（按顺序取第一个有效值）：
        //     1. Resources\HMOL.session.txt 里的 LANPORT=（HMOL 启动器写）
        //     2. Resources\MoLanPatch.ini   里的 LANPort=（手改也行）
        //     3. HMOL.session.txt 在游戏根也有一份，兜底再看一次
        //     4. 都没有 → 1233
        // ==================================================================

        static int lanPortCache;

        /// <summary>本机局域网用的 TCP 端口（1..65535）。由 IL 插桩在监听/自连/双击加入时调用。</summary>
        public static int LANPort()
        {
            if (lanPortCache > 0) return lanPortCache;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string source = "";
            int port = 0;

            port = ReadPortSetting(Path.Combine(baseDir, SessionStampFileName), "LANPORT");
            if (port > 0) source = "HMOL.session.txt";

            if (port <= 0)
            {
                port = ReadPortSetting(Path.Combine(baseDir, SettingsFileName), "LANPort");
                if (port > 0) source = SettingsFileName;
            }

            if (port <= 0)
            {
                try
                {
                    string parent = Path.GetDirectoryName(baseDir.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parent))
                    {
                        port = ReadPortSetting(Path.Combine(parent, SessionStampFileName), "LANPORT");
                        if (port > 0) source = "游戏根\\HMOL.session.txt";
                    }
                }
                catch { }
            }

            if (port < 1 || port > 65535)
            {
                if (port != 0) WarnOnce("lanport", "invalid", "局域网端口 " + port + " 不合法，已回退 1233（有效范围 1..65535）");
                port = 1233;
                source = "默认";
            }

            lanPortCache = port;
            Log("lanport: 局域网 TCP 端口 = " + port + "（" + source + "）");
            return port;
        }

        /// <summary>从 ini/session 文本里读一个整数端口；没有这个键或解析失败返回 0。</summary>
        static int ReadPortSetting(string path, string key)
        {
            try
            {
                if (!File.Exists(path)) return 0;

                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw == null ? "" : raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;

                    int value;
                    if (int.TryParse(line.Substring(eq + 1).Trim(), out value)) return value;
                }
            }
            catch { }
            return 0;
        }

        static bool settingsLoaded;
        static bool statusBarEnabled = true;
        static object settingsSoundCheck;
        static object settingsStatusCheck;

        // 设置页里的其余开关（默认全开）
        static bool tabCompleteEnabled = true;       // @ 前缀按 Tab 补全名字
        static bool offlineNoticeEnabled = true;     // 聊天区提示「XXX 掉线了」
        static bool announceVersionEnabled = true;   // 进房间自动报一次版本
        static bool preflightCheckEnabled = true;    // 开赛前 3 秒核对版本/已装包
        static bool showFpsEnabled = true;           // 状态条：FPS
        static bool showPingEnabled = true;          // 状态条：Ping
        static bool showVersionEnabled = true;       // 状态条：Patch / HMOL
        static bool topBarRttEnabled = true;         // 顶部「局域网大厅 · 23ms」
        // 同步地图时要不要连预览图（.png）一起发。默认不发：走樱花 Frp 时流量是按量算的，
        // 预览图动辄几百 KB~3 MB，纯粹是列表里的缩略图，不影响开局。
        // 需要的话在 Resources\MoLanPatch.ini 里写 SyncPreview=1。
        static bool syncPreviewEnabled;
        // v0.12.2 新增，默认全开；都能在设置里单独关掉
        static bool quickPanelEnabled = true;        // 大厅底部「快捷语」按钮与面板
        static bool extCheckEnabled = true;          // 引擎拓展检测（房间广播 + 状态条标记）
        static bool chatLogEnabled = true;           // 聊天记录留档到 对局记录\聊天_日期.txt
        // v0.12.3：娱乐 / 美化，同样都能在设置里关
        static bool miniGamesEnabled = true;         // /dice /coin /rps /8ball
        static bool motdEnabled = true;              // 房主 /motd 房间公告
        static bool statusColorEnabled = true;       // 状态条按状态变色

        static object optionsPatchedWindow;
        static int optionsPatchAttempts;

        static DateTime lastSettingsTry = DateTime.MinValue;

        static void LoadSettings()
        {
            if (settingsLoaded) return;

            // 读失败（文件被占用/编码异常）时不要永久锁定默认值：每 2 秒重试一次，
            // 否则用户改完设置看不到效果，也不知道为什么。
            if (lastSettingsTry != DateTime.MinValue && (DateTime.Now - lastSettingsTry).TotalSeconds < 2) return;
            lastSettingsTry = DateTime.Now;

            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                if (!File.Exists(path)) { settingsLoaded = true; return; }

                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw == null ? "" : raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    bool on = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);

                    if (string.Equals(key, "MentionSound", StringComparison.OrdinalIgnoreCase)) mentionSoundEnabled = on;
                    if (string.Equals(key, "AutoReady", StringComparison.OrdinalIgnoreCase)) autoReadyEnabled = on;
                    else if (string.Equals(key, "DupNameWarn", StringComparison.OrdinalIgnoreCase)) dupNameWarnEnabled = on;
                    else if (string.Equals(key, "StatusBar", StringComparison.OrdinalIgnoreCase)) statusBarEnabled = on;
                    else if (string.Equals(key, "TabComplete", StringComparison.OrdinalIgnoreCase)) tabCompleteEnabled = on;
                    else if (string.Equals(key, "OfflineNotice", StringComparison.OrdinalIgnoreCase)) offlineNoticeEnabled = on;
                    else if (string.Equals(key, "AnnounceVersion", StringComparison.OrdinalIgnoreCase)) announceVersionEnabled = on;
                    else if (string.Equals(key, "PreflightCheck", StringComparison.OrdinalIgnoreCase)) preflightCheckEnabled = on;
                    else if (string.Equals(key, "StatusFps", StringComparison.OrdinalIgnoreCase)) showFpsEnabled = on;
                    else if (string.Equals(key, "StatusPing", StringComparison.OrdinalIgnoreCase)) showPingEnabled = on;
                    else if (string.Equals(key, "StatusVersion", StringComparison.OrdinalIgnoreCase)) showVersionEnabled = on;
                    else if (string.Equals(key, "TopBarRtt", StringComparison.OrdinalIgnoreCase)) topBarRttEnabled = on;
                    else if (string.Equals(key, "SyncPreview", StringComparison.OrdinalIgnoreCase)) syncPreviewEnabled = on;
                    else if (string.Equals(key, "QuickPanel", StringComparison.OrdinalIgnoreCase)) quickPanelEnabled = on;
                    else if (string.Equals(key, "ExtCheck", StringComparison.OrdinalIgnoreCase)) extCheckEnabled = on;
                    else if (string.Equals(key, "ChatLog", StringComparison.OrdinalIgnoreCase)) chatLogEnabled = on;
                    else if (string.Equals(key, "MiniGames", StringComparison.OrdinalIgnoreCase)) miniGamesEnabled = on;
                    else if (string.Equals(key, "Motd", StringComparison.OrdinalIgnoreCase)) motdEnabled = on;
                    else if (string.Equals(key, "StatusColor", StringComparison.OrdinalIgnoreCase)) statusColorEnabled = on;
                }

                settingsLoaded = true;
                Log("settings: MentionSound=" + (mentionSoundEnabled ? "1" : "0")
                    + " StatusBar=" + (statusBarEnabled ? "1" : "0")
                    + " TabComplete=" + (tabCompleteEnabled ? "1" : "0")
                    + " OfflineNotice=" + (offlineNoticeEnabled ? "1" : "0")
                    + " AnnounceVersion=" + (announceVersionEnabled ? "1" : "0")
                    + " PreflightCheck=" + (preflightCheckEnabled ? "1" : "0")
                    + " StatusFps=" + (showFpsEnabled ? "1" : "0")
                    + " StatusPing=" + (showPingEnabled ? "1" : "0")
                    + " StatusVersion=" + (showVersionEnabled ? "1" : "0")
                    + " TopBarRtt=" + (topBarRttEnabled ? "1" : "0"));
            }
            catch (Exception ex) { Log("settings: 读取失败: " + ex.Message); }
        }

        static void SaveSettings()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                Dictionary<string, string> known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                known["MentionSound"] = mentionSoundEnabled ? "1" : "0";
                known["AutoReady"] = autoReadyEnabled ? "1" : "0";
                known["DupNameWarn"] = dupNameWarnEnabled ? "1" : "0";
                known["StatusBar"] = statusBarEnabled ? "1" : "0";
                known["TabComplete"] = tabCompleteEnabled ? "1" : "0";
                known["OfflineNotice"] = offlineNoticeEnabled ? "1" : "0";
                known["AnnounceVersion"] = announceVersionEnabled ? "1" : "0";
                known["PreflightCheck"] = preflightCheckEnabled ? "1" : "0";
                known["StatusFps"] = showFpsEnabled ? "1" : "0";
                known["StatusPing"] = showPingEnabled ? "1" : "0";
                known["StatusVersion"] = showVersionEnabled ? "1" : "0";
                known["TopBarRtt"] = topBarRttEnabled ? "1" : "0";
                known["SyncPreview"] = syncPreviewEnabled ? "1" : "0";
                known["QuickPanel"] = quickPanelEnabled ? "1" : "0";
                known["ExtCheck"] = extCheckEnabled ? "1" : "0";
                known["ChatLog"] = chatLogEnabled ? "1" : "0";
                known["MiniGames"] = miniGamesEnabled ? "1" : "0";
                known["Motd"] = motdEnabled ? "1" : "0";
                known["StatusColor"] = statusColorEnabled ? "1" : "0";

                // 读-改-写：只替换我们认识的键，用户手写的注释 / 其它键 / 行序都保留，
                // 免得每点一下开关就把整份 ini 覆盖成固定 10 行。
                List<string> lines = new List<string>();
                HashSet<string> written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (File.Exists(path))
                {
                    foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string line = raw == null ? "" : raw;
                        string t = line.Trim();
                        int eq = t.IndexOf('=');
                        if (eq > 0)
                        {
                            string key = t.Substring(0, eq).Trim();
                            string val;
                            if (known.TryGetValue(key, out val))
                            {
                                if (written.Add(key)) lines.Add(key + "=" + val);
                                continue;          // 旧值 / 重复键丢掉
                            }
                        }
                        lines.Add(line);
                    }
                }

                foreach (KeyValuePair<string, string> kv in known)
                    if (!written.Contains(kv.Key)) lines.Add(kv.Key + "=" + kv.Value);

                File.WriteAllText(path, string.Join("\r\n", lines.ToArray()) + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("settings: 保存失败: " + ex.Message); }
        }

        static void OnMentionSoundToggled(object sender, EventArgs e)
        {
            try
            {
                if (settingsSoundCheck == null) return;
                bool on = GetMember(settingsSoundCheck, "Checked") is bool b && b;
                if (on == mentionSoundEnabled) return;

                mentionSoundEnabled = on;
                SaveSettings();
                Log("@ 提示音: " + (on ? "开" : "关"));
            }
            catch { }
        }

        static void OnStatusBarToggled(object sender, EventArgs e)
        {
            try
            {
                if (settingsStatusCheck == null) return;
                bool on = GetMember(settingsStatusCheck, "Checked") is bool b && b;
                if (on == statusBarEnabled) return;

                statusBarEnabled = on;
                SaveSettings();
                Log("状态条: " + (on ? "显示" : "隐藏"));
            }
            catch { }
        }

        /// <summary>「联机补丁」页里其余开关共用这一个回调：按控件名改对应字段。</summary>
        static void OnOptionToggled(object sender, EventArgs e)
        {
            try
            {
                string name = GetMember(sender, "Name") as string;
                bool on = GetMember(sender, "Checked") is bool b && b;

                switch (name)
                {
                    case "chkMoTabComplete": tabCompleteEnabled = on; break;
                    case "chkMoOffline":     offlineNoticeEnabled = on; break;
                    case "chkMoAnnounce":    announceVersionEnabled = on; break;
                    case "chkMoPreflight":   preflightCheckEnabled = on; break;
                    case "chkMoFps":         showFpsEnabled = on; break;
                    case "chkMoPing":        showPingEnabled = on; break;
                    case "chkMoVersion":     showVersionEnabled = on; break;
                    case "chkMoTopBarRtt":   topBarRttEnabled = on; break;
                    case "chkMoAutoReady":   autoReadyEnabled = on; break;
                    case "chkMoDupName":     dupNameWarnEnabled = on; break;
                    case "chkMoQuickPanel":  quickPanelEnabled = on; break;
                    case "chkMoExtCheck":    extCheckEnabled = on; break;
                    case "chkMoChatLog":     chatLogEnabled = on; break;
                    case "chkMoMiniGames":   miniGamesEnabled = on; break;
                    case "chkMoMotd":        motdEnabled = on; break;
                    case "chkMoStatusColor": statusColorEnabled = on; break;
                    default: return;
                }

                SaveSettings();
                Log("settings: " + name + " = " + (on ? "开" : "关"));
            }
            catch { }
        }

        /// <summary>设置窗口第一次出现时改造一次：删 CnCNet/更新页、隐藏下载音乐包、加「联机补丁」页。</summary>
        static void OptionsTick(object mainMenu)
        {
            object win = GetMember(mainMenu, "optionsWindow");
            if (win == null || ReferenceEquals(optionsPatchedWindow, win)) return;

            // 只有确认改造成功才记下来：万一窗口还没建好（拿不到面板），下一帧再试。
            // 若一上来就置位，这个进程里设置页就永远是原样了。
            if (ApplyOptionsWindowPatch(win)) optionsPatchedWindow = win;
            else if (++optionsPatchAttempts >= 60)
            {
                optionsPatchedWindow = win;        // 试了 60 帧还不行就放弃，别每帧刷日志
                Log("options: 连续尝试失败，本进程不再改造设置窗口");
            }
        }

        /// <summary>改造设置窗口。返回 false 表示"现在还不具备条件"，可以下一帧重试。</summary>
        static bool ApplyOptionsWindowPatch(object win)
        {
            try
            {
                object tabControl = GetMember(win, "tabControl");
                Array panels = GetMember(win, "optionsPanels") as Array;
                int panelCount = panels == null ? 0 : panels.Length;

                // 按文本找标签，别写死索引：客户端自己会用 OptionsWindow.ini 的
                // RemoveTabIndex5 把「组件」标签先移除（此时 Tabs 只有 5 个）。
                int idxCnCNet = FindTabIndex(tabControl, "CnCNet");
                int idxUpdate = FindTabIndex(tabControl, "更新");
                int tabCount = TabCount(tabControl);

                if (optionsPatchAttempts == 0)
                    Log("options: Tabs=" + tabCount + " panels=" + panelCount
                        + " CnCNet=" + idxCnCNet + " 更新=" + idxUpdate);

                // 1) 「联机补丁」页不新建面板：XNAOptionsPanel 是 internal abstract，
                //    Activator.CreateInstance 会报「它是抽象类」。
                //    直接复用客户端已经建好的 CnCNet 页：把里面的控件全部隐藏，再放我们自己的两个勾选项。
                object page = (panels != null && idxCnCNet >= 0 && idxCnCNet < panels.Length)
                    ? panels.GetValue(idxCnCNet) : null;

                if (page == null)
                {
                    Log("options: 拿不到 CnCNet 页，下一帧再试");
                    return false;
                }

                // 保险：只有在确认拿到的是 CnCNet 页时才敢清空它的控件，
                // 否则（万一面板顺序和标签顺序对不上）会把别的页清成空白。
                string pageType = page.GetType().Name;
                Log("options: 联机补丁页取自 panels[" + idxCnCNet + "] = " + pageType);
                if (pageType.IndexOf("CnCNet", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    Log("options: 面板不是 CnCNet 页，放弃改造");
                    return false;
                }

                Log("options: CnCNet 页子控件 " + CountChildren(page) + " 个，全部隐藏");
                HideAllChildren(page);
                AddPatchCheckBoxes(win, page);

                // 2) 不再裁剪 optionsPanels —— 「更新」页要留下来改造成「地图」页。
                //    （组件页已被 ini 移除，所以数组下标和标签下标仍然一一对应）

                // 3) 复用 CnCNet 的标签槽位：直接改名成「联机补丁」
                //    （不用 AddTab —— 新标签的位置/贴图依赖内部计算，实测可能看不见）
                if (tabControl != null && idxCnCNet >= 0)
                {
                    IList tabs = tabControl == null ? null : GetMember(tabControl, "Tabs") as IList;
                    if (tabs != null && idxCnCNet < tabs.Count)
                    {
                        SetMember(tabs[idxCnCNet], "Text", "联机补丁");
                        Log("options: 已把 CnCNet 标签改名为「联机补丁」");
                    }
                }

                // 4) 「更新」页改造成「地图」页：里面的更新控件全部藏掉，换成已同步地图清单
                object updatePage = (panels != null && idxUpdate >= 0 && idxUpdate < panels.Length)
                    ? panels.GetValue(idxUpdate) : null;
                if (updatePage != null)
                {
                    IList tabs2 = tabControl == null ? null : GetMember(tabControl, "Tabs") as IList;
                    if (tabs2 != null && idxUpdate < tabs2.Count)
                    {
                        SetMember(tabs2[idxUpdate], "Text", "地图");
                        Log("options: 已把「更新」标签改名为「地图」");
                    }
                    HideAllChildren(updatePage);
                    BuildMapTab(win, updatePage);
                }
                else Log("options: 找不到「更新」页，做不了地图页");

                // 5) 隐藏「下载音乐包」按钮
                object soundtrack = FindControl(win, "btnSoundtrack");
                if (soundtrack != null)
                {
                    SetMember(soundtrack, "Visible", false);
                    Log("options: 已隐藏 下载音乐包 按钮");
                }

                // 6) 选中第 0 页，并确保面板状态一致
                if (tabControl != null) SetMember(tabControl, "SelectedTab", 0);
                if (panels != null && panels.Length > 0) CallMethod(panels.GetValue(0), "Enable");
                return true;
            }
            catch (Exception ex)
            {
                // 走到这里说明已经动过控件了，重试只会把勾选项加两遍；当作完成处理。
                Log("options: 改造设置窗口失败: " + ex.Message);
                return true;
            }
        }

        static int TabCount(object tabControl)
        {
            IList tabs = tabControl == null ? null : GetMember(tabControl, "Tabs") as IList;
            return tabs == null ? -1 : tabs.Count;
        }

        static int FindTabIndex(object tabControl, string text)
        {
            try
            {
                IList tabs = tabControl == null ? null : GetMember(tabControl, "Tabs") as IList;
                if (tabs == null) return -1;

                for (int i = 0; i < tabs.Count; i++)
                {
                    string t = GetMember(tabs[i], "Text") as string;
                    if (string.Equals(t, text, StringComparison.OrdinalIgnoreCase)) return i;
                }
            }
            catch { }
            return -1;
        }

        static IList ChildList(object parent)
        {
            return parent == null ? null : GetMember(parent, "Children") as IList;
        }

        static int CountChildren(object parent)
        {
            IList children = ChildList(parent);
            return children == null ? -1 : children.Count;
        }

        /// <summary>把 CnCNet 页里原有的控件全部藏起来，只留我们自己加的勾选项。</summary>
        static void HideAllChildren(object parent)
        {
            IList children = ChildList(parent);
            if (children == null) return;

            for (int i = 0; i < children.Count; i++)
            {
                object child = children[i];
                if (child == null) continue;
                try { SetMember(child, "Visible", false); }
                catch { }
            }
        }

        /// <summary>往（已清空的）CnCNet 页里放设置项。</summary>
        static void AddPatchCheckBoxes(object win, object page)
        {
            object wm = GetMember(win, "WindowManager");
            if (wm == null) { Log("options: 拿不到 WindowManager"); return; }

            Type checkType = FindType("ClientGUI.XNAClientCheckBox");
            if (checkType == null) { Log("options: 找不到 XNAClientCheckBox"); return; }

            MethodInfo addChild = FindMethod(page.GetType(), "AddChild", 1);
            if (addChild == null) { Log("options: 找不到 AddChild"); return; }

            // 两列排：面板只有 342 高，一列放 15 项会顶出底边。
            // 右列从 y=44 开始，给右上角那三个按钮让出位置。
            const int x1 = 16;
            const int x2 = 292;
            int y = 14;

            settingsSoundCheck = AddCheckBox(checkType, wm, addChild, page,
                "chkMoMentionSound", "@ 提示音（F9 也可切换）", x1, y, mentionSoundEnabled,
                "OnMentionSoundToggled");
            y += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoTabComplete", "@ 时按 Tab 补全玩家名", x1, y, tabCompleteEnabled,
                "OnOptionToggled");
            y += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoOffline", "有人掉线时在聊天区提示", x1, y, offlineNoticeEnabled,
                "OnOptionToggled");
            y += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoAnnounce", "进房间自动报一次版本", x1, y, announceVersionEnabled,
                "OnOptionToggled");
            y += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoPreflight", "开赛前 3 秒核对版本与已装包", x1, y, preflightCheckEnabled,
                "OnOptionToggled");
            y += 26;
            settingsStatusCheck = AddCheckBox(checkType, wm, addChild, page,
                "chkMoStatusBar", "在聊天框上方显示状态条", x1, y, statusBarEnabled,
                "OnStatusBarToggled");
            y += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoFps", "状态条：显示 FPS", x1, y, showFpsEnabled,
                "OnOptionToggled");
            y += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoPing", "状态条：显示 Ping", x1, y, showPingEnabled,
                "OnOptionToggled");
            y += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoMiniGames", "小游戏命令（/dice /coin /rps /8ball）", x1, y, miniGamesEnabled,
                "OnOptionToggled");
            y += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoMotd", "允许房主用 /motd 发房间公告", x1, y, motdEnabled,
                "OnOptionToggled");
            y += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoStatusColor", "状态条按状态变色", x1, y, statusColorEnabled,
                "OnOptionToggled");

            int y2 = 44;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoVersion", "状态条：显示版本号", x2, y2, showVersionEnabled,
                "OnOptionToggled");
            y2 += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoTopBarRtt", "顶部栏显示延迟", x2, y2, topBarRttEnabled,
                "OnOptionToggled");
            y2 += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoAutoReady", "客机进房自动准备", x2, y2, autoReadyEnabled,
                "OnOptionToggled");
            y2 += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoDupName", "同名玩家提醒", x2, y2, dupNameWarnEnabled,
                "OnOptionToggled");
            y2 += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoQuickPanel", "显示「快捷语」按钮", x2, y2, quickPanelEnabled,
                "OnOptionToggled");
            y2 += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoExtCheck", "引擎拓展检测与纯净提示", x2, y2, extCheckEnabled,
                "OnOptionToggled");
            y2 += 26;
            AddCheckBox(checkType, wm, addChild, page,
                "chkMoChatLog", "聊天留档到「对局记录」", x2, y2, chatLogEnabled,
                "OnOptionToggled");
            y2 += 26;

            // 右上角两个按钮：外观整套照抄设置窗口自己的「保存」按钮
            int pw = GetInt(page, "Width", 552);
            if (pw < 300) pw = 552;
            object refBtn = FindControl(win, "btnSave") ?? FindControl(win, "btnCancel");
            int bw = GetInt(refBtn, "Width", 92);
            int bh = GetInt(refBtn, "Height", 25);
            if (bw <= 0) bw = 92;
            if (bh <= 0) bh = 25;
            AddOptionButton(page, wm, refBtn, "btnMoOpenLog", "打开日志",
                pw - bw * 3 - 32, 10, bw, bh, "LogButtonClick");
            AddOptionButton(page, wm, refBtn, "btnMoExportDiag", "导出诊断",
                pw - bw * 2 - 24, 10, bw, bh, "LogExportClick");
            AddOptionButton(page, wm, refBtn, "btnMoOpenMatch", "对局记录",
                pw - bw - 16, 10, bw, bh, "OpenMatchFolderClick");


            Log("options: 联机补丁页已就绪（18 个开关 + 打开日志/导出诊断/对局记录）");
        }

        static void AddOptionButton(object parent, object wm, object refBtn, string name, string text,
                                    int x, int y, int w, int h, string handlerName)
        {
            try
            {
                Type btnType = FindType("ClientGUI.XNAClientButton");
                if (btnType == null) { Log("options: 找不到 XNAClientButton"); return; }
                MethodInfo addChild = FindMethod(parent.GetType(), "AddChild", 1);
                if (addChild == null) return;

                object btn = Activator.CreateInstance(btnType, new object[] { wm });
                SetMember(btn, "Name", name);
                SetMember(btn, "Text", text);
                CopyButtonLook(refBtn, btn);      // 贴图/字号/文字颜色照抄原版按钮
                SetMember(btn, "X", x);
                SetMember(btn, "Y", y);
                SetMember(btn, "Width", w);
                SetMember(btn, "Height", h);
                addChild.Invoke(parent, new object[] { btn });

                MethodInfo addClick = btnType.GetMethod("add_LeftClick",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo handler = typeof(Relay).GetMethod(handlerName,
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (addClick != null && handler != null)
                    addClick.Invoke(btn, new object[] { Delegate.CreateDelegate(typeof(EventHandler), handler) });
            }
            catch (Exception ex) { Log("options: 创建按钮失败(" + name + "): " + ex.Message); }
        }

        static object AddCheckBox(Type checkType, object wm, MethodInfo addChild, object parent,
            string name, string text, int x, int y, bool initial, string handlerName)
        {
            try
            {
                object chk = Activator.CreateInstance(checkType, new object[] { wm });
                SetMember(chk, "Name", name);
                addChild.Invoke(parent, new object[] { chk });

                SetMember(chk, "Text", text);
                SetMember(chk, "X", x);
                SetMember(chk, "Y", y);
                SetMember(chk, "Checked", initial);

                MethodInfo addChanged = checkType.GetMethod("add_CheckedChanged",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo handler = typeof(Relay).GetMethod(handlerName,
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (addChanged != null && handler != null)
                {
                    Delegate d = Delegate.CreateDelegate(typeof(EventHandler), handler);
                    addChanged.Invoke(chk, new object[] { d });
                }

                return chk;
            }
            catch (Exception ex)
            {
                Log("options: 创建复选框失败(" + name + "): " + ex.Message);
                return null;
            }
        }

        static void StatusTick(object lobby)
        {
            if (lobby == null) return;

            // 聊天留档：攒够 2 秒的聊天一次性写盘（被刷屏时也只写这一处）
            try { ChatLogTick(); } catch { }

            DateTime now = DateTime.Now;

            // 掉线提示（每 2 秒扫一次玩家表的连接状态）
            if ((now - lastOnlineScan).TotalSeconds >= 2.0)
            {
                lastOnlineScan = now;
                try { ScanOnlineState(lobby); } catch { }
            }

            if ((now - lastStatusUpdate).TotalSeconds < 1.0) return;
            lastStatusUpdate = now;

            // 设置里关掉「显示状态条」后：只保留顶部「局域网大厅」，不再画状态条
            if (!statusBarEnabled)
            {
                try
                {
                    if (statusLabel != null && GetMember(statusLabel, "Visible") is bool vis && vis)
                        SetMember(statusLabel, "Visible", false);
                }
                catch { }
                UpdateTopBarRtt();
                return;
            }

            string text = BuildStatusText();

            // 地图正在传输时，把进度接到状态条末尾（每帧都覆盖，传完自动消失）
            string syncText = MapSyncStatusText();
            if (syncText.Length > 0) text = text.Length == 0 ? syncText : text + "   |   " + syncText;

            // 三项状态都被关掉时 text 为空：别留一行空白，直接藏起来
            if (text.Length == 0)
            {
                try
                {
                    if (statusLabel != null && GetMember(statusLabel, "Visible") is bool emptyVis && emptyVis)
                        SetMember(statusLabel, "Visible", false);
                }
                catch { }
                UpdateTopBarRtt();
                return;
            }

            try
            {
                object label = EnsureStatusLabel(lobby);
                if (label != null)
                {
                    if (GetMember(label, "Visible") is bool v && !v) SetMember(label, "Visible", true);
                    if ((GetMember(label, "Text") as string) != text) SetMember(label, "Text", text);

                    // 配色：同步中=天蓝，非纯净=橙，其它=浅灰。
                    // 只在颜色真的变了才写属性，避免每帧触发重绘。
                    if (statusColorEnabled)
                    {
                        string want = syncText.Length > 0 ? "SkyBlue"
                                    : (extCheckEnabled && extShortResult.Length > 0 ? "Orange"
                                    : "LightGray");
                        if (want != lastStatusColor)
                        {
                            object c = MakeColor(want);
                            if (c != null)
                            {
                                SetMember(label, "TextColor", c);
                                lastStatusColor = want;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log("status: 更新状态条失败: " + ex.Message); }

            UpdateTopBarRtt();
        }

        /// <summary>顶部栏：「局域网大厅 · 23ms」；设置里关掉延迟就只显示「局域网大厅」。</summary>
        static void UpdateTopBarRtt()
        {
            try
            {
                if (topBar == null) return;

                object status = GetMember(topBar, "lblConnectionStatus");
                if (status == null) return;

                double rtt = (statusBarEnabled && topBarRttEnabled) ? CurrentRttMs() : 0;
                string top = LanLobbyStatusText
                             + (rtt > 0 ? "  ·  " + ((int)Math.Round(rtt)) + "ms" : "");
                if ((GetMember(status, "Text") as string) == top) return;

                SetMember(status, "Text", top);
                CallMethod(status, "CenterOnParent");
            }
            catch { }
        }

        static string BuildStatusText()
        {
            double rtt = CurrentRttMs();
            string ping = rtt > 0 ? ((int)Math.Round(rtt)) + "ms" : "--";

            if (!versionLabelHmolRead)
            {
                versionLabelHmolRead = true;
                try { versionLabelHmol = ReadHmolVersion(); } catch { versionLabelHmol = ""; }
            }
            string hmol = string.IsNullOrEmpty(versionLabelHmol) ? "--" : "v" + versionLabelHmol;

            // 三项可以在设置里各自关掉；全关掉就返回空串，由 StatusTick 把整行藏起来
            List<string> parts = new List<string>(4);
            if (showFpsEnabled) parts.Add("FPS " + currentFps);
            if (showPingEnabled) parts.Add("Ping " + ping);
            if (showVersionEnabled)
            {
                parts.Add("Patch v" + Version);
                parts.Add("HMOL " + hmol);
            }
            if (extCheckEnabled && extShortResult.Length > 0) parts.Add("非纯净 " + extShortResult);

            return parts.Count == 0 ? "" : string.Join("   |   ", parts.ToArray());
        }

        /// <summary>在大厅聊天输入框上方建一行标签（加到输入框所在的同一个父控件，坐标才对）。</summary>
        static object EnsureStatusLabel(object lobby)
        {
            if (statusLabel != null && ReferenceEquals(statusLabelLobby, lobby)) return statusLabel;
            statusLabel = null;
            statusLabelLobby = null;

            object input = GetMember(lobby, "tbChatInput");
            if (input == null) return null;   // 大厅还没建好，下一帧再试

            object wm = GetMember(input, "WindowManager");
            if (wm == null) return null;

            Type labelType = FindType("Rampastring.XNAUI.XNAControls.XNALabel");
            if (labelType == null) { Log("status: 找不到 XNALabel"); return null; }

            try
            {
                object label = Activator.CreateInstance(labelType, new object[] { wm });
                SetMember(label, "Name", "MoRelayStatus");

                object parent = GetMember(input, "Parent");
                if (parent == null) parent = lobby;

                MethodInfo add = FindMethod(parent.GetType(), "AddChild", 1);
                if (add == null) { Log("status: 找不到 AddChild"); return null; }
                add.Invoke(parent, new object[] { label });

                // 挂上之后立刻记下来：后面定位若失败，也只是位置不对，
                // 不能每帧再新建一个（否则标签会越积越多）。
                statusLabel = label;
                statusLabelLobby = lobby;

                object fontIndex = GetMember(input, "FontIndex");
                if (fontIndex != null) SetMember(label, "FontIndex", Convert.ToInt32(fontIndex));

                object color = MakeColor("LightGray");
                if (color != null) SetMember(label, "TextColor", color);

                SetMember(label, "Text", BuildStatusText());

                int ix = GetInt(input, "X", 0);
                int iy = GetInt(input, "Y", 0);
                int lh = GetInt(label, "Height", 0);
                if (lh <= 0) lh = 14;

                int ly = iy - lh - 2;

                // 聊天记录列表的底边原本一直顶到输入框上沿，状态条压上去就会被聊天文字盖住。
                // 先把列表高度收到状态条上方（只收一次，同一个大厅实例不会重复收），
                // 状态条就正好落在“列表”和“输入框”之间的空行里。
                object chatList = GetMember(lobby, "lbChatMessages");
                if (chatList != null)
                {
                    int listY = GetInt(chatList, "Y", -1);
                    int listH = GetInt(chatList, "Height", -1);
                    if (listY >= 0 && listH > 0)
                    {
                        int newH = ly - 1 - listY;
                        if (newH > 40 && newH < listH)
                        {
                            SetMember(chatList, "Height", newH);
                            Log("status: 聊天列表高度 " + listH + " → " + newH + "（给状态条腾一行）");
                        }
                    }
                }

                SetMember(label, "X", ix);
                SetMember(label, "Y", ly);

                Log("status: 已在聊天框上方创建状态条");
                return label;
            }
            catch (Exception ex)
            {
                Log("status: 创建状态条失败: " + ex.Message);
                statusLabel = null;
                statusLabelLobby = null;
                return null;
            }
        }

        static void ScanOnlineState(object lobby)
        {
            if (!ReferenceEquals(onlineLobby, lobby))
            {
                onlineLobby = lobby;
                onlineState.Clear();
            }

            IList players = GetMember(lobby, "Players") as IList;
            if (players == null) return;

            string me = Clean(MyPlayerName());
            foreach (object o in players)
            {
                LANPlayerInfo lp = o as LANPlayerInfo;
                if (lp == null) continue;

                string n = Clean(lp.Name);
                if (n.Length == 0 || SameName(n, me)) continue;

                bool online;
                try { online = lp.TcpClient != null && lp.TcpClient.Connected; }
                catch { online = false; }

                bool was;
                if (onlineState.TryGetValue(n, out was) && was && !online && offlineNoticeEnabled)
                {
                    Notice(lobby, n + " 掉线了", false);
                    Log("online: " + n + " 掉线");
                }
                onlineState[n] = online;
            }
        }

        // ---- 进大厅后自动播报一次版本（Patch + HMOL）----

        static bool versionAnnounceDone;
        static DateTime versionAnnounceAt = DateTime.MinValue;

        static void VersionAnnounceTick(object lobby)
        {
            if (lobby == null || !announceVersionEnabled) return;

            IList players = GetMember(lobby, "Players") as IList;
            bool multi = players != null && players.Count >= 2;

            if (!multi)
            {
                // 房间里只剩自己/没人：下次有人进来时重新报
                versionAnnounceDone = false;
                versionAnnounceAt = DateTime.MinValue;
                return;
            }

            if (versionAnnounceDone) return;

            DateTime now = DateTime.Now;
            if (versionAnnounceAt == DateTime.MinValue)
            {
                versionAnnounceAt = now.AddSeconds(2);   // 等聊天区就绪
                return;
            }
            if (now < versionAnnounceAt) return;

            versionAnnounceDone = true;

            if (!versionLabelHmolRead)
            {
                versionLabelHmolRead = true;
                try { versionLabelHmol = ReadHmolVersion(); } catch { versionLabelHmol = ""; }
            }
            string hmol = string.IsNullOrEmpty(versionLabelHmol) ? "HMOL 未知" : "HMOL v" + versionLabelHmol;

            SendChat(lobby, "[版本] Patch v" + Version + "  |  " + hmol);
            Log("version: 已在大厅播报本机版本");
        }

        /// <summary>周期性（15 秒）把自己的中继版本广播出去；帧很小，忽略不计。</summary>
        static void HelloTick(object lobby)
        {
            if (lobby == null) return;
            if ((DateTime.Now - lastHello).TotalSeconds < 15) return;
            IList players = GetMember(lobby, "Players") as IList;
            if (players == null || players.Count < 2) return;
            lastHello = DateTime.Now;

            string me = Clean(MyPlayerName());
            string frame = CtrlTag + CtrlSep + HelloKind + CtrlSep + me + CtrlSep
                         + Convert.ToBase64String(Encoding.UTF8.GetBytes(Version + "\n" + me));
            try
            {
                if (IsHostOf(lobby))
                {
                    foreach (object o in players)
                    {
                        LANPlayerInfo lp = o as LANPlayerInfo;
                        if (lp == null || lp.TcpClient == null) continue;
                        if (SameName(Clean(lp.Name), me)) continue;
                        SendLocked(lp, frame);
                    }
                }
                else
                {
                    MethodInfo sm = FindMethod(lobby.GetType(), "SendMessageToHost", 1);
                    if (sm != null) sm.Invoke(lobby, new object[] { frame });
                }
            }
            catch { }
        }

        /// <summary>收到别人的版本自检帧：不一致就红字 + 聊天区各来一条（同一台只提醒一次）。</summary>
        static void OnHello(object lobby, string from, string b64)
        {
            string ver = "";
            try
            {
                string payload = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                string[] p = payload.Split('\n');
                if (p.Length > 0) ver = p[0].Trim();
            }
            catch { return; }
            if (ver.Length == 0) return;

            if (ver == Version)
            {
                lock (warnedVersion) { warnedVersion.Remove(from); }   // 一致了就清掉，允许以后再报
                return;
            }

            bool first;
            lock (warnedVersion) { first = warnedVersion.Add(from + "|" + ver); }
            if (!first) return;

            string msg = "⚠ " + (from.Length == 0 ? "对方" : from) + " 的中继版本是 v" + ver + "，我的是 v" + Version
                       + " —— 版本不一致会导致联机卡住/不读条，请双方用同一份补丁包！";
            Log("hello: " + msg);

            // 这里是网络线程：不能直接往通知列表/聊天框写，投递给 UI 线程执行
            PostUi(delegate()
            {
                Notice(lobby, msg, false);
                SendChat(lobby, msg);
            });
        }

        // ---- 开赛前检查：HMOL 版本 + 当前实例已装包 ----

        const string PreflightRequestKind = "PREQ";
        const string PreflightInfoKind = "PMETA";
        const string PreflightUnknown = "?";
        const string SessionStampFileName = "HMOL.session.txt";

        /// <summary>房主等待对端回信的时长（需求就是 3 秒）。</summary>
        const double PreflightWaitSeconds = 3.0;

        static object preflightLobby;
        static DateTime preflightDeadline = DateTime.MinValue;
        static DateTime preflightLastTick = DateTime.MinValue;
        static bool preflightResuming;
        static bool launchHooked;      // exe 侧是否挂了 HostLaunchGame 钩子（挂了就不再用 StartGame 兜底）
        static bool preflightReAsked;  // 本轮是否已经补问过一次（防止无限等待）
        static int ctrlLogCount;       // 诊断用：控制帧日志只打前 60 条

        // 检查发现不一致时停下来等用户确认（是 = 继续开局 / 否 = 停止开局）
        static bool preflightDialogOpen;
        static object preflightDialogLobby;
        static object preflightDialogBox;
        static DateTime preflightDialogOpenedAt;

        static readonly Dictionary<string, PreflightInfo> preflightPeers =
            new Dictionary<string, PreflightInfo>(StringComparer.OrdinalIgnoreCase);

        sealed class PreflightInfo
        {
            public string Version = "";
            public List<string> Packages = new List<string>();
            public bool HasInfo;
            public bool Responded;
        }

        /// <summary>
        /// 由 LANGameLobby::HostLaunchGame 的补丁调用（房主点「开始游戏」→ 广播 LAUNCH 之前）。
        /// 返回 true = 先别广播。
        ///
        /// 为什么必须卡在这里而不是 StartGame：
        ///   局域网开局是「房主广播 LAUNCH → 各端（含房主自己）收到后各自 StartGame」。
        ///   以前把检查挂在 StartGame 上，LAUNCH 早就广播出去了，客机已经开游戏，
        ///   只有房主自己被卡住 —— 表现就是"房主提示不一致，客机却进游戏了"。
        /// </summary>
        public static bool BeforeHostLaunch(object lobby)
        {
            launchHooked = true;          // 说明 exe 侧确实挂了这个钩子（StartGame 就不再做兜底检查）

            try
            {
                if (lobby == null) return false;
                if (!preflightCheckEnabled) return false;   // 设置里关掉了开赛前检查：直接放行
                if (preflightResuming) return false;        // 我们自己审批后重新广播，别再拦
                if (preflightDialogOpen) return true;       // 已经在问用户，别重复广播
                if (!IsHostOf(lobby)) return false;         // 客机不发起 LAUNCH

                // 还在同一轮 3 秒等待里（连点两次）：继续按住
                if (preflightLobby != null && ReferenceEquals(preflightLobby, lobby)
                    && DateTime.Now < preflightDeadline) return true;

                if (ConnectedPeerCount(lobby) < 1) return false;

                lock (preflightPeers) preflightPeers.Clear();

                preflightLobby = lobby;
                preflightDeadline = DateTime.Now.AddSeconds(PreflightWaitSeconds);
                preflightLastTick = DateTime.Now;
                preflightReAsked = false;
                SendPreflightRequest(lobby);
                Notice(lobby, "正在核对双方 HMOL 版本与已安装包（3 秒）…", true);
                Log("preflight: 房主开始 3 秒检查（LAUNCH 广播已扣住）");
                return true;
            }
            catch (Exception ex)
            {
                Log("preflight: 启动检查失败，直接放行: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 由 MultiplayerGameLobby::StartGame 的补丁调用。
        /// 正常情况下只是一个"兜底闸门"：等待/确认框期间不放行。
        /// 只有在 exe 侧没挂上 HostLaunchGame 钩子（launchHooked=false）时才自己做整套 3 秒检查。
        /// </summary>
        public static bool BeforeStart(object lobby)
        {
            try
            {
                if (preflightResuming || lobby == null) return false;
                if (preflightDialogOpen) return true;
                if (preflightLobby != null && ReferenceEquals(preflightLobby, lobby)
                    && DateTime.Now < preflightDeadline) return true;

                // 客机：本地没有房主选中的地图就别开始。
                // 客户端 WriteSpawnIni 的第一句就是 Map.IsCoop()（上游没判空），
                // 缺图时开局 = 直接 KABOOOOOOM，比留在大厅难受得多。
                if (!IsHostOf(lobby) && !LocalHasSelectedMap(lobby))
                {
                    // 顺手告诉房主"我这边没这张图"，不然房主只看到客机没进来
                    try { SendCtrlTo(lobby, "", CtrlFrame(MapMissKind, "")); } catch { }

                    Notice(lobby, "本地没有这张地图，已阻止开始。请点「接收地图」让房主同步，或让房主换图", false);
                    Log("preflight: 客机缺图，已拦下 StartGame 并通知房主");
                    return true;
                }

                // 客机：本地有这张图，但和房主的版本不一样（同名不同内容）→ 一样开不了局
                if (!IsHostOf(lobby) && remoteMapSha.Length > 0)
                {
                    string localSha = LocalMapSha(lobby);
                    if (localSha.Length > 0
                        && !string.Equals(localSha, remoteMapSha, StringComparison.OrdinalIgnoreCase))
                    {
                        try { SendCtrlTo(lobby, "", CtrlFrame(MapMissKind, "")); } catch { }

                        Notice(lobby, "你这张地图的版本和房主不一致，已阻止开始。请点「接收地图」重新同步", false);
                        Log("preflight: 地图版本不一致 local=" + localSha.Substring(0, 12)
                            + " remote=" + remoteMapSha.Substring(0, 12));
                        return true;
                    }
                }

                if (launchHooked) return false;             // 已经由 HostLaunchGame 卡过，这里直接放行

                // ---- 兜底路径（老 exe / 钩子没挂上）----
                if (!preflightCheckEnabled) return false;
                if (!IsHostOf(lobby)) return false;
                if (ConnectedPeerCount(lobby) < 1) return false;

                lock (preflightPeers) preflightPeers.Clear();

                preflightLobby = lobby;
                preflightDeadline = DateTime.Now.AddSeconds(PreflightWaitSeconds);
                preflightLastTick = DateTime.Now;
                preflightReAsked = false;
                SendPreflightRequest(lobby);
                Notice(lobby, "正在核对双方 HMOL 版本与已安装包（3 秒）…", true);
                Log("preflight: 房主开始 3 秒检查（兜底：走的是 StartGame 钩子）");
                return true;
            }
            catch (Exception ex)
            {
                Log("preflight: 启动检查失败，直接开始: " + ex.Message);
                return false;
            }
        }

        /// <summary>当前大厅选中的地图，本地文件在不在（客机开局前的安全检查）。</summary>
        static bool LocalHasSelectedMap(object lobby)
        {
            try
            {
                object map = GetMember(lobby, "Map");
                if (map == null)
                {
                    object gm = GetMember(lobby, "GameModeMap");
                    map = gm == null ? null : GetMember(gm, "Map");
                }
                if (map == null) return false;

                string path = Convert.ToString(GetMember(map, "CompleteFilePath"));
                return !string.IsNullOrEmpty(path) && File.Exists(path);
            }
            catch
            {
                // 判断不出来就放行，别把正常开局挡住
                return true;
            }
        }

        /// <summary>
        /// 由 LANGameLobby::HandleClientMessage 的补丁在开头调用：这条玩家的连接是不是已经废了。
        ///
        /// 客户端收到"不认识的局域网命令"时会写日志：
        ///     Logger.Log("Unknown LAN command from " + lpInfo.ToString() + " : " + data);
        /// 而 LANPlayerInfo.ToString() 会去读 TcpClient.Client.RemoteEndPoint —— socket 已释放时
        /// 抛 ObjectDisposedException（"无法访问已释放的对象"），直接把客户端打崩（KABOOOOOOOM）。
        /// 客机断线重连后，旧连接残留的包就会踩到这个坑。
        /// </summary>
        public static bool PeerGone(object lpInfo)
        {
            try
            {
                LANPlayerInfo lp = lpInfo as LANPlayerInfo;
                if (lp == null) return false;

                TcpClient t = lp.TcpClient;
                if (t == null) return true;

                // 先看 TcpClient.Connected（它缓存的是"最近一次 IO 的结果"，socket 被释放后通常为 false）。
                try { if (!t.Connected) return true; }
                catch { }

                // Connected 有滞后性：socket 刚 Dispose 完它可能仍返回 true，
                // 随后客户端在"未知命令"日志里调 ToString() → RemoteEndPoint → 直接崩进程。
                // 所以再碰一次 RemoteEndPoint：只有 ObjectDisposedException 才算"连接已废"，
                // 其它异常（比如还没连上）不当作已死，避免把正常消息误丢掉。
                try
                {
                    Socket s = t.Client;
                    if (s == null) return true;
                    return (s.RemoteEndPoint as IPEndPoint) == null;
                }
                catch (ObjectDisposedException) { return true; }
                catch { return false; }
            }
            catch { return false; }
        }

        /// <summary>由 LANGameLobby::Update 的补丁每帧调用；时间到就汇总，再决定继续还是停下来问用户。</summary>
        static void PreflightTick(object lobby)
        {
            // 正在等用户点「是/否」：确认框还在就什么都不做
            if (preflightDialogOpen)
            {
                // XNAMessageBox.Hide() 只把自己从窗口移除、不改 Visible，所以光看 Visible 判断不了，
                // 再加一个 2 分钟超时兜底：用户走开了也不会把"开始游戏"永久卡住。
                bool alive = IsControlVisible(preflightDialogBox)
                             && (DateTime.Now - preflightDialogOpenedAt).TotalSeconds < 120;
                if (alive) return;

                preflightDialogOpen = false;
                preflightDialogBox = null;
                object waiting = preflightDialogLobby;
                preflightDialogLobby = null;
                if (waiting != null)
                {
                    Notice(waiting, "已取消开始游戏（确认框已关闭或超时）", false);
                    UnlockRoom(waiting);      // 别再让房间锁着，否则客人进不来
                }
                Log("preflight: 确认框已关闭/超时，视为取消");
                return;
            }

            object target = preflightLobby;
            if (target == null || !ReferenceEquals(target, lobby)) return;

            // 设置里关掉了开赛前检查（可能是 3 秒等待中被关掉）：丢掉这一轮，别再自己开局
            if (!preflightCheckEnabled)
            {
                preflightLobby = null;
                preflightDeadline = DateTime.MinValue;
                preflightLastTick = DateTime.MinValue;
                return;
            }

            DateTime now = DateTime.Now;

            // Update 每帧都会跑；中途断了 2 秒以上，说明窗口被隐藏/游戏被暂停（比如退出了大厅），
            // 这时绝不能"回来就自己开局"，直接取消这一轮，等用户重新点开始。
            // 阈值别设太小：一次 GC/写 spawn.ini/开音频设备就能卡 0.5 秒以上，
            // 以前用 0.5 秒会把"正常慢帧"误判成"离开大厅"，用户看到的就是"已取消检查"。
            if (preflightLastTick != DateTime.MinValue
                && (now - preflightLastTick).TotalSeconds > 2.0)
            {
                CancelPreflight(lobby, "等待期间大厅被中断");
                return;
            }

            preflightLastTick = now;

            // 到点了还有对端没回信：很可能只是丢包/对方刚进房，再补问一次（只补一次，最多多等 2 秒）。
            // 不补的话，一次丢包就会被说成"版本不一致"，两边明明装的一样却弹框。
            if (now >= preflightDeadline && !preflightReAsked)
            {
                List<string> missing = MissingPreflightPeers(target);
                if (missing.Count > 0)
                {
                    preflightReAsked = true;
                    preflightDeadline = now.AddSeconds(2.0);
                    SendPreflightRequest(target);
                    Log("preflight: 还有 " + missing.Count + " 人没回信（" + string.Join("/", missing.ToArray())
                        + "），补问一次，再等 2 秒");
                    return;
                }
            }

            if (now < preflightDeadline) return;

            preflightLobby = null;
            preflightDeadline = DateTime.MinValue;
            preflightLastTick = DateTime.MinValue;
            preflightReAsked = false;

            bool needsConfirm, passed;
            string dialogText;
            string summary = BuildPreflightSummary(target, out needsConfirm, out passed, out dialogText);

            if (summary.Length > 0)
            {
                Notice(target, summary, passed);
                SendChat(target, summary);
            }

            // 通过 / 技术上没法判断（拿不到玩家表）→ 照常开始
            if (!needsConfirm)
            {
                ResumeLaunch(target);
                return;
            }

            // 发现不一致、或完全没法核对（没从 HMOL 启动）→ 停下来问用户，绝不自己开局
            if (ShowPreflightConfirmDialog(target, dialogText))
            {
                Log("preflight: 需要用户确认，已暂停开始");
                return;
            }

            // 弹窗起不来时按"否"处理：宁可不开，也不要带着未知状态硬开
            Notice(target, "已停止开始游戏（无法弹出确认框）：" + summary, false);
            Log("preflight: 确认框不可用，已停止开始");
        }

        static bool IsControlVisible(object control)
        {
            if (control == null) return false;
            return GetMember(control, "Visible") is bool visible && visible;
        }

        /// <summary>
        /// 房主确认无误（或选择继续）后，继续之前被扣住的那次「开始游戏」。
        /// 走 HostLaunchGame（重新广播 LAUNCH，让所有人一起开局）；
        /// 拿不到这个方法才退回直接调 StartGame（老 exe）。
        /// </summary>
        static void ResumeLaunch(object lobby)
        {
            preflightResuming = true;
            try
            {
                MethodInfo launch = FindMethod(lobby.GetType(), "HostLaunchGame", 0);
                if (launch != null)
                {
                    launch.Invoke(lobby, null);
                    Log("preflight: 已放行，重新广播 LAUNCH");
                    return;
                }

                MethodInfo start = FindMethod(lobby.GetType(), "StartGame", 0);
                if (start == null)
                {
                    Log("preflight: 找不到 HostLaunchGame / StartGame，无法继续开始");
                    return;
                }

                start.Invoke(lobby, null);
                Log("preflight: 继续开始游戏（退回直接调 StartGame）");
            }
            catch (Exception ex)
            {
                Log("preflight: 继续开始失败: " + ex.Message);
            }
            finally
            {
                preflightResuming = false;
            }
        }

        /// <summary>
        /// 弹客户端的「是/否」对话框。客户端消息框的按钮回调是 Action&lt;XNAMessageBox&gt;，
        /// 中继不引用 ClientGUI 程序集，所以用表达式树现造一个同签名的委托。
        /// 返回 false 表示弹不出来（调用方按"否"处理）。
        /// </summary>
        static bool ShowPreflightConfirmDialog(object lobby, string dialogText)
        {
            try
            {
                object wm = GetMember(lobby, "WindowManager");
                if (wm == null) { Log("preflight: 拿不到 WindowManager，无法弹确认框"); return false; }

                Type boxType = FindType("ClientGUI.XNAMessageBox");
                if (boxType == null) { Log("preflight: 找不到 ClientGUI.XNAMessageBox"); return false; }

                // 不能用 GetMethod(name, flags)：有重载时会抛 AmbiguousMatchException，
                // 而且异常会一路落到 catch → 变成"点了开始却直接放弃检查"。
                MethodInfo showYesNo = null;
                foreach (MethodInfo mi in boxType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (mi.Name == "ShowYesNoDialog" && mi.GetParameters().Length == 3) { showYesNo = mi; break; }
                }
                if (showYesNo == null) { Log("preflight: 找不到 ShowYesNoDialog(3 参)"); return false; }

                // 消息框的正文标签只有一行、宽度固定，塞长了会被切掉（"显示不全"），
                // 所以这里只用一句短话；完整差异已经发到聊天区了。
                string text = string.IsNullOrEmpty(dialogText)
                    ? "检查未通过。仍然开始?"
                    : dialogText;

                object box = showYesNo.Invoke(null, new object[] { wm, "联机前检查", text });
                if (box == null) { Log("preflight: 弹窗创建失败"); return false; }

                bool okYes = SetMember(box, "YesClickedAction", MakeBoxAction(boxType, "PreflightDialogYes"));
                bool okNo = SetMember(box, "NoClickedAction", MakeBoxAction(boxType, "PreflightDialogNo"));
                if (!okYes || !okNo)
                {
                    // 按钮动作没绑上就别把框留着：否则用户点「是」不会重新开局，
                    // 下一帧还会被当成"用户取消了"，比不弹框更让人困惑。
                    Log("preflight: 按钮动作绑定失败，关掉确认框");
                    try { CallMethod(box, "Hide"); } catch { }
                    return false;
                }

                preflightDialogLobby = lobby;
                preflightDialogBox = box;
                preflightDialogOpen = true;
                preflightDialogOpenedAt = DateTime.Now;
                return true;
            }
            catch (Exception ex)
            {
                Log("preflight: 弹确认框失败: " + ex.Message);
                return false;
            }
        }

        static Delegate MakeBoxAction(Type boxType, string handlerName)
        {
            MethodInfo handler = typeof(Relay).GetMethod(handlerName,
                BindingFlags.Static | BindingFlags.NonPublic);
            ParameterExpression param = Expression.Parameter(boxType, "box");
            Expression body = Expression.Call(handler, Expression.Convert(param, typeof(object)));
            return Expression.Lambda(typeof(Action<>).MakeGenericType(boxType), body, param).Compile();
        }

        static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
            }

            // 程序集还没被加载时，按已知程序集名再试一次
            try
            {
                string asmName = null;
                if (fullName.StartsWith("DTAConfig.", StringComparison.Ordinal)) asmName = "DTAConfig";
                else if (fullName.StartsWith("ClientGUI.", StringComparison.Ordinal)) asmName = "ClientGUI";
                else if (fullName.StartsWith("ClientCore.", StringComparison.Ordinal)) asmName = "ClientCore";
                else if (fullName.StartsWith("Rampastring.XNAUI.", StringComparison.Ordinal)) asmName = "Rampastring.XNAUI";

                if (asmName != null) return Type.GetType(fullName + ", " + asmName, false);
            }
            catch { }

            return null;
        }

        static void PreflightDialogYes(object box)
        {
            object lobby = preflightDialogLobby;
            preflightDialogLobby = null;
            preflightDialogBox = null;
            preflightDialogOpen = false;
            if (lobby == null) return;

            Log("preflight: 用户选择「是」，继续开始");
            Notice(lobby, "已确认继续，开始游戏", true);
            ResumeLaunch(lobby);
        }

        static void PreflightDialogNo(object box)
        {
            object lobby = preflightDialogLobby;
            preflightDialogLobby = null;
            preflightDialogBox = null;
            preflightDialogOpen = false;

            Log("preflight: 用户选择「否」，停止开始");
            if (lobby != null)
            {
                Notice(lobby, "已停止开始游戏：请先对齐双方的 HMOL 版本 / 已安装包，再重新点击开始游戏", false);
                UnlockRoom(lobby);
            }
        }

        /// <summary>
        /// 「开始游戏」需要先锁定房间；中途停下来了就把锁解开 ——
        /// 否则房间一直锁着，客人「刚进去就自动退」（客户端日志是
        /// "Dropping client because the game room is locked."）。
        /// </summary>
        static void UnlockRoom(object lobby)
        {
            try
            {
                MethodInfo unlock = FindMethod(lobby.GetType(), "UnlockGame", 1);
                if (unlock == null) return;
                unlock.Invoke(lobby, new object[] { false });
                Notice(lobby, "已自动解锁房间，朋友现在可以重新加入了", true);
                Log("preflight: 已自动解锁房间");
            }
            catch (Exception ex) { Log("preflight: 解锁房间失败: " + ex.Message); }
        }

        /// <summary>放弃这一轮检查（大厅中途被关掉/隐藏时用），避免"回到大厅就自己开局"。</summary>
        static void CancelPreflight(object lobby, string reason)
        {
            preflightLobby = null;
            preflightDeadline = DateTime.MinValue;
            preflightLastTick = DateTime.MinValue;
            preflightReAsked = false;
            lock (preflightPeers) preflightPeers.Clear();

            Log("preflight: 已取消（" + reason + "）");
            Notice(lobby, "已取消联机前检查，请重新点击开始游戏", false);
        }

        /// <summary>还没回信的已连接对端（用于"到点补问一次"和汇总措辞）。</summary>
        static List<string> MissingPreflightPeers(object lobby)
        {
            List<string> missing = new List<string>();
            try
            {
                string me = Clean(MyPlayerName());
                IList players = GetMember(lobby, "Players") as IList;
                if (players == null) return missing;

                object[] arr = new object[players.Count];
                try { players.CopyTo(arr, 0); } catch { return missing; }

                foreach (object o in arr)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null || lp.TcpClient == null) continue;
                    string name = Clean(lp.Name);
                    if (name.Length == 0 || SameName(name, me)) continue;

                    PreflightInfo info = null;
                    lock (preflightPeers) preflightPeers.TryGetValue(name, out info);
                    if (info == null || !info.Responded) missing.Add(name);
                }
            }
            catch { }
            return missing;
        }

        /// <summary>房主向所有已连接的对端要一份检查信息。</summary>
        static void SendPreflightRequest(object lobby)
        {
            try
            {
                string me = Clean(MyPlayerName());
                string frame = CtrlTag + CtrlSep + PreflightRequestKind + CtrlSep + me + CtrlSep + "";
                IList players = GetMember(lobby, "Players") as IList;
                if (players == null) { Log("preflight: 拿不到玩家表，没法发检查请求"); return; }

                object[] arr = new object[players.Count];
                try { players.CopyTo(arr, 0); } catch { return; }
                int sent = 0, skipped = 0;
                foreach (object o in arr)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null) continue;
                    if (lp.TcpClient == null) { skipped++; continue; }
                    if (SameName(Clean(lp.Name), me)) continue;
                    SendLocked(lp, frame);
                    sent++;
                }

                Log("preflight: 已向 " + sent + " 个对端请求检查信息"
                    + (skipped > 0 ? "（跳过 " + skipped + " 个还没连上的）" : ""));
            }
            catch (Exception ex)
            {
                Log("preflight: 请求检查信息失败: " + ex.Message);
            }
        }

        /// <summary>把本机的 HMOL 版本 + 已装包清单发给房主（房主自己不回）。</summary>
        static void SendPreflightInfo(object lobby)
        {
            try
            {
                PreflightInfo mine = ReadLocalPreflightInfo();
                string payload = mine.HasInfo ? BuildPayload(mine) : PreflightUnknown;

                string me = Clean(MyPlayerName());
                string frame = CtrlTag + CtrlSep + PreflightInfoKind + CtrlSep + me + CtrlSep
                             + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));

                // 正常只有客机会收到 PREQ；房主收到就直接广播本机信息，不往回发。
                // 两条路都发一遍：
                //   · hostClient（中继自己的每连接写锁）—— 不会和客户端的写入互相截断
                //   · 客户端自带的 SendMessageToHost —— 兜底（hostClient 可能是上一局的旧连接）
                // 重复一条不影响：房主收到两次也只是把同一份信息存两次。
                int sentVia = 0;
                if (!IsHostOf(lobby))
                {
                    if (hostClient != null)
                    {
                        SendClientLocked(hostClient, hostEncoding, frame);
                        sentVia++;
                    }

                    MethodInfo send = FindMethod(lobby.GetType(), "SendMessageToHost", 1);
                    if (send != null)
                    {
                        send.Invoke(lobby, new object[] { frame });
                        sentVia++;
                    }

                    if (sentVia > 0)
                    {
                        Log("preflight: 已回复本机检查信息（from='" + me + "'，" + sentVia + " 条路径）");
                        return;
                    }
                    Log("preflight: 找不到任何可用的回信通道（hostClient / SendMessageToHost 都不可用）");
                    return;
                }

                // 房主走到这里（正常情况下不会）：直接广播本机信息，不往回发
                IList players = GetMember(lobby, "Players") as IList;
                if (players == null) return;
                foreach (object o in players)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null || lp.TcpClient == null) continue;
                    if (SameName(Clean(lp.Name), me)) continue;
                    SendLocked(lp, frame);
                }
            }
            catch (Exception ex)
            {
                Log("preflight: 发送本机检查信息失败: " + ex.Message);
            }
        }

        /// <summary>收到对端的检查信息。</summary>
        static void OnPreflight(object lobby, string from, string b64)
        {
            try
            {
                PreflightInfo info = new PreflightInfo();
                info.Responded = true;

                if (!string.IsNullOrEmpty(b64))
                {
                    string payload = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    string[] lines = payload.Split('\n');
                    if (lines.Length > 0) info.Version = SanitizeToken(lines[0]);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string package = SanitizeToken(lines[i]);
                        if (package.Length > 0) info.Packages.Add(package);
                    }
                }

                info.HasInfo = info.Version.Length > 0 &&
                               !string.Equals(info.Version, PreflightUnknown, StringComparison.Ordinal);

                string who = from.Length == 0 ? "?" : from;
                lock (preflightPeers) preflightPeers[who] = info;

                Log("preflight: 收到 " + who + " 的检查信息：HMOL "
                    + (info.HasInfo ? "v" + info.Version : "未检测到")
                    + "，包 " + info.Packages.Count + " 个");
            }
            catch (Exception ex)
            {
                Log("preflight: 解析对端检查信息失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 汇总双方差异。
        /// <paramref name="needsConfirm"/> = true：需要弹「是/否」确认框
        ///   （发现不一致，或本机没有 HMOL 信息、根本没法核对）；
        /// <paramref name="passed"/> = true：核对通过。
        /// 两者都为 false：技术上没法判断（拿不到玩家表），直接放行。
        /// </summary>
        static string BuildPreflightSummary(object lobby, out bool needsConfirm, out bool passed, out string dialogText)
        {
            needsConfirm = false;
            passed = false;
            dialogText = "";

            PreflightInfo mine = ReadLocalPreflightInfo();
            if (!mine.HasInfo)
            {
                // 完全没法核对 —— 不替用户做主，交给确认框
                needsConfirm = true;
                dialogText = "本机没有 HMOL 会话信息，无法核对。仍然开始?";
                return "未检测到 HMOL 会话信息（不是从 HMOL 启动），无法核对 HMOL 版本与已安装包";
            }

            string me = Clean(MyPlayerName());
            List<string> issues = new List<string>();
            int checkedCount = 0;

            IList players = GetMember(lobby, "Players") as IList;
            if (players == null) return "";   // 拿不到玩家表：技术异常，别因此拦人

            foreach (object o in players)
            {
                LANPlayerInfo lp = o as LANPlayerInfo;
                if (lp == null || lp.TcpClient == null) continue;

                string name = Clean(lp.Name);
                if (name.Length == 0 || SameName(name, me)) continue;

                PreflightInfo theirs = null;
                lock (preflightPeers) preflightPeers.TryGetValue(name, out theirs);

                if (theirs == null || !theirs.Responded)
                {
                    // 注意措辞：没回信 ≠ 版本不一样。以前一律写成"对方可能未装新补丁"，
                    // 在"两边其实装的一样、只是丢了个包"时会让人以为是版本问题。
                    // 另一个常见原因：只换了 MoLanRelay.dll、没换三个 exe —— 老 exe 里没有
                    // MO-CTRL 的识别代码，请求根本不会被处理，自然也回不了信。
                    issues.Add("没收到 " + name + " 的检查信息（丢包，或对方的 exe 还是旧的 —— 必须把 Resources 里的 3 个 exe 和 MoLanRelay.dll 一起换）");
                    continue;
                }

                if (!theirs.HasInfo)
                {
                    issues.Add(name + " 不是从 HMOL 启动（未检测到 HMOL 会话信息）");
                    continue;
                }

                checkedCount++;

                if (!string.Equals(theirs.Version, mine.Version, StringComparison.OrdinalIgnoreCase))
                    issues.Add(name + " 的 HMOL 版本是 v" + theirs.Version + "，我的是 v" + mine.Version);

                List<string> missing = Diff(mine.Packages, theirs.Packages);
                List<string> extra = Diff(theirs.Packages, mine.Packages);
                if (missing.Count > 0 || extra.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(name).Append(" 的已装包与我不一致");
                    if (missing.Count > 0) sb.Append("；缺少 ").Append(JoinLimited(missing));
                    if (extra.Count > 0) sb.Append("；多出 ").Append(JoinLimited(extra));
                    issues.Add(sb.ToString());
                }
            }

            if (issues.Count == 0)
            {
                passed = true;
                return "检查通过：双方 HMOL 版本与已安装包一致（已核对 " + checkedCount + " 人）";
            }

            // 客户端消息框只有一行、宽度固定，长文本会被直接切掉（"字体显示不全"）。
            // 所以弹窗只放一句短话，完整差异走聊天区（那里能换行、能滚动）。
            needsConfirm = true;
            dialogText = "检查到 " + issues.Count + " 项不一致，详见聊天区。仍然开始?";
            StringBuilder all = new StringBuilder("联机前检查发现不一致：");
            all.Append(string.Join("；", issues.ToArray()));
            if (all.Length > 700) all.Length = 700;
            return all.ToString();
        }

        /// <summary>
        /// 会话标记是 HMOL 在启动游戏前写的，所以它的修改时间应该就在本进程启动前后。
        /// 如果比本进程启动还早很多，说明是上一次 HMOL 留下的旧文件（比如这次是直接双击客户端），
        /// 按"没有 HMOL 会话信息"处理，免得拿旧数据骗过检查。
        /// </summary>
        static bool SessionStampIsFresh(string path)
        {
            try
            {
                DateTime written = File.GetLastWriteTime(path);
                DateTime started = Process.GetCurrentProcess().StartTime;
                return written >= started.AddMinutes(-10);
            }
            catch
            {
                return true;   // 拿不到启动时间就不拦，宁可不报也别误报
            }
        }

        static PreflightInfo ReadLocalPreflightInfo()
        {
            PreflightInfo info = new PreflightInfo();

            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SessionStampFileName);
                if (!File.Exists(path))
                {
                    Log("preflight: 没有找到 " + path);
                    return info;
                }

                if (!SessionStampIsFresh(path))
                {
                    Log("preflight: " + path + " 是上一次启动留下的旧文件，忽略");
                    return info;
                }

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    string line = raw == null ? "" : raw.Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("HMOL=", StringComparison.OrdinalIgnoreCase))
                        info.Version = SanitizeToken(line.Substring(5));
                    else if (line.StartsWith("PACKAGE=", StringComparison.OrdinalIgnoreCase))
                    {
                        string package = SanitizeToken(line.Substring(8));
                        if (package.Length > 0) info.Packages.Add(package);
                    }
                }

                info.HasInfo = info.Version.Length > 0 &&
                               !string.Equals(info.Version, PreflightUnknown, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                Log("preflight: 读取 HMOL 会话信息失败: " + ex.Message);
            }

            return info;
        }

        static string BuildPayload(PreflightInfo info)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(info.Version);
            foreach (string package in info.Packages) sb.Append('\n').Append(package);
            return sb.ToString();
        }

        static int ConnectedPeerCount(object lobby)
        {
            try
            {
                string me = Clean(MyPlayerName());
                int count = 0;
                IList players = GetMember(lobby, "Players") as IList;
                if (players == null) return 0;

                foreach (object o in players)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null || lp.TcpClient == null) continue;
                    if (SameName(Clean(lp.Name), me)) continue;
                    count++;
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        static List<string> Diff(List<string> left, List<string> right)
        {
            List<string> result = new List<string>();
            foreach (string a in left)
            {
                bool found = false;
                foreach (string b in right)
                {
                    if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) result.Add(a);
            }
            return result;
        }

        static string JoinLimited(List<string> items)
        {
            StringBuilder sb = new StringBuilder();
            int limit = items.Count < 6 ? items.Count : 6;
            for (int i = 0; i < limit; i++)
            {
                if (sb.Length > 0) sb.Append('、');
                sb.Append(items[i]);
            }
            if (items.Count > limit) sb.Append(" 等 ").Append(items.Count).Append(" 个");
            return sb.ToString();
        }

        static string SanitizeToken(string value)
        {
            if (value == null) return "";
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                if (ch == '\u0001' || ch == '\u0002' || ch == '\r' || ch == '\n') continue;
                sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        // ---- 游戏文件一致性 ----

        /// <summary>由 LANGameLobby::HandleFileHashCommand 的补丁调用（房主侧）。</summary>
        public static void FileHashResult(object lobby, string sender, string theirHash)
        {
            try
            {
                string who = Clean(sender);
                if (SameName(who, MyPlayerName())) return;          // 自己那份不用报

                string mine = Clean(GetMember(lobby, "localFileHash") as string);
                string theirs = Clean(theirHash);

                // 本机还没算出哈希（字段名变了/还没算完）：只记日志，别误报"文件不一致"
                if (mine.Length == 0 || theirs.Length == 0)
                {
                    Log("filehash: 跳过（我=" + Short(mine) + "，他=" + Short(theirs) + "）");
                    return;
                }

                bool same = string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase);
                if (same) { Log("filehash: " + who + " 游戏文件校验通过"); return; }   // 通过就不刷屏

                string msg = "⚠ 游戏文件不一致：" + (who.Length == 0 ? "对方" : who) + " 的文件和你的不一样"
                           + "（他 " + Short(theirs) + " / 你 " + Short(mine) + "）"
                           + " —— 联机很可能卡住或崩溃，请核对双方 MO 版本与补丁是否完全一致！";
                Log("filehash: " + msg);
                PostUi(delegate()
                {
                    Notice(lobby, msg, false);
                    SendChat(lobby, msg);
                });
            }
            catch (Exception ex) { Log("filehash 检查出错: " + ex.Message); }
        }

        static string Short(string h)
        {
            if (string.IsNullOrEmpty(h)) return "空";
            return h.Length <= 8 ? h : h.Substring(0, 8);
        }

        /// <summary>弹一条本地通知（拿不到颜色就只写日志，绝不因为提示失败而中断功能）。</summary>
        static void Notice(object lobby, string text, bool good)
        {
            try
            {
                Log("notice: " + text);
                MethodInfo am = FindMethod(lobby.GetType(), "AddNotice", 2);
                if (am == null) return;
                object color = MakeColor(good ? "LimeGreen" : "Red");
                if (color == null) return;
                am.Invoke(lobby, new object[] { text, color });
            }
            catch { }
        }

        /// <summary>由 HandleClientMessage / HandleMessageFromServer 的补丁调用。</summary>
        public static void OnCtrl(object lobby, string message)
        {
            try
            {
                string[] parts = message.Split('\u0001');
                if (parts.Length < 4) return;
                string kind = parts[1];
                string from = Clean(parts[2]);

                // 负载 = 第 4 段及之后的全部内容。负载内部本身也可能含 \u0001（地图同步的字段分隔就是它），
                // 所以不能再"从某处往后找下一个 \u0001"——那会把负载的第一段切掉。
                string b64 = parts.Length > 3 ? string.Join("\u0001", parts, 3, parts.Length - 3) : "";

                bool mine = from.Length > 0 && SameName(from, MyPlayerName());

                // 诊断用：前 60 条控制帧记下"收到了什么、来自谁"。
                // 排查"房主收不到回信"时，这两边的日志一对比就知道是没收到还是名字对不上。
                if (ctrlLogCount < 60)
                {
                    ctrlLogCount++;
                    Log("ctrl: 收到 " + kind + " from='" + from + "'（mine=" + (mine ? "1" : "0") + "）");
                }

                // 延迟测量：收到 PING 原样回 PONG，收到 PONG 就算往返时间。不转发。
                if (kind == PingKind)
                {
                    if (!mine)
                    {
                        string pong = CtrlTag + CtrlSep + PongKind + CtrlSep
                                    + Clean(MyPlayerName()) + CtrlSep + b64;
                        SendCtrlTo(lobby, from, pong);
                    }
                    return;
                }
                if (kind == PongKind)
                {
                    if (!mine) OnPong(from, b64);
                    return;
                }

                if (kind == "SHOT")
                {
                    // 截图显示准备改成"聊天区内联"（像聊天软件那样），还没做。
                    // 这里刻意只记日志、绝不弹窗口 —— 弹窗会抢走客户端焦点，
                    // 客户端一失去激活就停止绘制（自定义鼠标也不画了），看起来就是"卡死+没鼠标"。
                    if (!mine)
                        Log("shot: 收到 " + (from.Length == 0 ? "?" : from) + " 的截图（" + b64.Length + " 字符），当前版本不显示");
                }
                else if (kind == "VOICE")
                {
                    // 语音整体处于停用状态（FeaturesEnabled=false）：对端发来的语音帧直接丢掉，
                    // 绝不能走到 QueueVoicePlayback —— 那会打开麦克风/扬声器并分配非托管内存。
                    if (FeaturesEnabled && !mine) QueueVoicePlayback(b64);
                }
                else if (kind == HelloKind)
                {
                    if (!mine) OnHello(lobby, from, b64);
                }
                else if (kind == PreflightRequestKind)
                {
                    // 房主向客机要信息；客机收到就回，不再往下转发，免得人人互问
                    if (!mine && !IsHostOf(lobby)) SendPreflightInfo(lobby);
                    return;
                }
                else if (kind == PreflightInfoKind)
                {
                    if (!mine) OnPreflight(lobby, from, b64);
                }
                else if (kind == MapHelloKind)
                {
                    // 房主发来的「这张图要哪些文件」清单
                    if (!mine && !IsHostOf(lobby)) OnMapHello(lobby, from, b64);
                    return;
                }
                else if (kind == MapAskKind)
                {
                    // 客机点「接收地图」→ 请房主把当前选中地图的清单发过来
                    if (!mine && IsHostOf(lobby)) OnMapAsk(lobby, from);
                    return;
                }
                else if (kind == MapMissKind)
                {
                    // 客机缺图、StartGame 被拦下 → 告诉房主"有人没这张图，点同步"
                    if (!mine && IsHostOf(lobby)) OnMapMiss(lobby, from);
                    return;
                }
                else if (kind == MapHaveQKind)
                {
                    // 房主问"你有哪些图"→ 客机只回，不再转发
                    if (!mine && !IsHostOf(lobby)) OnMapHaveQ(lobby);
                    return;
                }
                else if (kind == MapHaveKind)
                {
                    // 客机回报的地图清单
                    if (!mine && IsHostOf(lobby)) OnMapHave(lobby, from, b64);
                    return;
                }
                else if (kind == MapReqKind)
                {
                    // 客机点「接收」后回头要文件
                    if (!mine && IsHostOf(lobby)) OnMapReq(lobby, from, b64);
                    return;
                }
                else if (kind == MapDataKind)
                {
                    if (!mine && !IsHostOf(lobby)) OnMapData(lobby, from, b64);
                    return;
                }
                else if (kind == MapEndKind)
                {
                    if (!mine && !IsHostOf(lobby)) OnMapEnd(lobby, from, b64);
                    return;
                }
                else if (kind == MapHashKind)
                {
                    // 房主广播"我当前选中的地图 + 它的 SHA-256"，客机开局前用来核对版本
                    if (!mine && !IsHostOf(lobby)) OnMapHash(b64);
                    return;
                }
                else if (kind == PmKind)
                {
                    // 私聊：负载 = 目标玩家名 \u0001 正文。房主负责转交，别人谁都看不到。
                    if (!mine)
                    {
                        string[] pp = (b64 ?? "").Split(new char[] { MapField }, 2);
                        string target = pp.Length > 1 ? pp[0].Trim() : "";
                        string body = pp.Length > 1 ? pp[1] : b64;

                        if (IsHostOf(lobby) && target.Length > 0 && !SameName(target, MyPlayerName()))
                            SendCtrlTo(lobby, target, CtrlFrame(PmKind, target + MapField + body));
                        else
                            OnPrivateMessage(lobby, from, body);
                    }
                    return;
                }
                else if (kind == DenyKind)
                {
                    // 房主拒绝加入：把原因显示出来，别让玩家以为是自己掉线
                    if (!mine) OnDeny(lobby, b64);
                    return;
                }
                else return;

                // 房主负责转发给"其他人"（不回发给发送者，语音帧没必要浪费带宽）。
                // mine 必须直接丢掉：房主自己也在这张玩家表里（回环连接），
                // 转发就会发给自己 → 再进一次 OnCtrl → 再转发 → 死循环刷屏。
                if (!mine && IsHostOf(lobby)) ForwardToOthers(lobby, from, message);
            }
            catch (Exception ex) { Log("ctrl error: " + ex.Message); }
        }

        /// <summary>房主：把控制帧发给除 from 之外的所有人。</summary>
        static void ForwardToOthers(object lobby, string from, string message)
        {
            try
            {
                IList players = GetMember(lobby, "Players") as IList;
                if (players == null) return;
                object[] arr = new object[players.Count];
                try { players.CopyTo(arr, 0); } catch { return; }   // 收包线程：不直接遍历活 List
                foreach (object o in arr)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null) continue;
                    string n = Clean(lp.Name);
                    if (n.Length == 0) continue;
                    if (from.Length > 0 && SameName(n, from)) continue;   // 不回发给发送者
                    if (SameName(n, MyPlayerName())) continue;            // 也不发给自己（回环连接会再触发一次）
                    if (lp.TcpClient == null) continue;                   // 没连接的跳过
                    SendLocked(lp, message);
                }
            }
            catch (Exception ex) { Log("ctrl 转发失败: " + ex.Message); }
        }

        static bool IsHostOf(object lobby)
        {
            try
            {
                object v = GetMember(lobby, "IsHost");
                if (v is bool) return (bool)v;
            }
            catch { }
            return false;
        }

        /// <summary>截当前客户端窗口 → 缩小 → 发出去。</summary>
        static void CaptureAndSend(object lobby)
        {
            byte[] png = CaptureWindow();
            if (png == null || png.Length == 0) { Log("shot: 没截到图"); return; }

            string me = MyPlayerName();
            string frame = CtrlTag + CtrlSep + "SHOT" + CtrlSep + Clean(me) + CtrlSep + Convert.ToBase64String(png);
            Log("shot: 截图 " + png.Length + " 字节（发送者 " + (me.Length == 0 ? "?" : me) + "）");

            try
            {
                if (IsHostOf(lobby))
                {
                    // 广播给所有人（自己那份由 OnCtrl 的 mine 判断跳过）
                    MethodInfo bm = FindMethod(lobby.GetType(), "BroadcastMessage", 1);
                    if (bm != null) bm.Invoke(lobby, new object[] { frame });
                }
                else
                {
                    MethodInfo sm = FindMethod(lobby.GetType(), "SendMessageToHost", 1);
                    if (sm != null) sm.Invoke(lobby, new object[] { frame });
                }
            }
            catch (Exception ex) { Log("shot: 发送失败: " + ex.Message); }
        }

        // ==================================================================
        // 语音聊天（按住 F7 说话，仅局域网大厅）
        //
        // 刻意不用 winmm 的回调（CALLBACK_NULL + 只轮询）：
        //   · 回调要往 native 传托管委托，写错就是访问违例 —— try/catch 兜不住，直接崩客户端
        //   · 轮询版本只在 Tick（UI 线程）里读 WAVEHDR 的 dwFlags，逻辑完全可预期
        //
        // 格式：8kHz / 16bit / 单声道 PCM，每帧 100ms（1600 字节，base64 后约 2.1KB）
        //       —— 局域网带宽压力很小（约 16KB/s）。要省流量以后可以换 μ-law。
        // ==================================================================

        const int VoiceRate = 8000;
        const int VoiceFrameMs = 100;
        const int VoiceFrameBytes = VoiceRate * 2 * VoiceFrameMs / 1000;   // 1600
        const int MicBufCount = 4;
        const int SpkBufCount = 8;

        const string PttKeyName = "F7";
        static bool pttWasDown;

        static bool voiceInitTried;
        // 这三个由后台初始化线程写、UI 线程读 —— 用 volatile 保证"数组准备好了"能被看见
        static volatile bool voiceOk, voiceBroken, micFailed;
        static IntPtr hWaveIn = IntPtr.Zero, hWaveOut = IntPtr.Zero;
        static IntPtr[] micHdrs, spkHdrs;
        static IntPtr[] micBufs, spkBufs;
        static bool[] spkInUse;
        static readonly Queue<byte[]> voicePlayQueue = new Queue<byte[]>();

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct WAVEFORMATEX
        {
            public short wFormatTag, nChannels;
            public int nSamplesPerSec, nAvgBytesPerSec;
            public short nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public int dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        const int WAVE_MAPPER = -1;
        const int CALLBACK_NULL = 0;
        const int WAVE_FORMAT_PCM = 1;
        const int WHDR_DONE = 1;

        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int cb);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int cb);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInStop(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int cb);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int cb);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        static WAVEFORMATEX VoiceFormat()
        {
            WAVEFORMATEX f = new WAVEFORMATEX();
            f.wFormatTag = WAVE_FORMAT_PCM;
            f.nChannels = 1;
            f.nSamplesPerSec = VoiceRate;
            f.nAvgBytesPerSec = VoiceRate * 2;
            f.nBlockAlign = 2;
            f.wBitsPerSample = 16;
            f.cbSize = 0;
            return f;
        }

        /// <summary>第一次需要用到语音时才初始化，并且放到后台线程 ——
        /// 打开音频设备可能要上百毫秒，绝不能卡在客户端的 UI 线程上（那看起来就是"卡死"）。
        /// 初始化没完成之前，本帧直接跳过语音。</summary>
        static bool EnsureVoice()
        {
            if (voiceOk) return true;
            if (voiceBroken || voiceInitTried) return false;
            voiceInitTried = true;
            Thread t = new Thread(delegate()
            {
                try { InitVoice(); }
                catch (Exception ex) { FailVoice(ex.Message); }
            });
            t.IsBackground = true;
            t.Name = "MoRelay-VoiceInit";
            t.Start();
            return false;
        }

        /// <summary>真正打开采集/播放设备。麦克风和扬声器分开对待：没麦克风也能听别人说话。</summary>
        static void InitVoice()
        {
            // 语音整体停用（FeaturesEnabled=false）：连设备都不要打开，
            // 免得在"关了的功能"上分配非托管内存、开着麦克风。
            if (!FeaturesEnabled) { voiceBroken = true; return; }

            try
            {
                WAVEFORMATEX fmt = VoiceFormat();
                int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));

                // ---- 麦克风（拿不到就只做"能听不能说"）----
                IntPtr hin;
                int r = waveInOpen(out hin, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                if (r != 0)
                {
                    micFailed = true;
                    Log("voice: 麦克风打不开 code=" + r + " —— 只能听别人说，自己说不了（不影响其他功能）");
                }
                else
                {
                    hWaveIn = hin;
                    micHdrs = new IntPtr[MicBufCount];
                    micBufs = new IntPtr[MicBufCount];
                    for (int i = 0; i < MicBufCount; i++)
                    {
                        micBufs[i] = Marshal.AllocHGlobal(VoiceFrameBytes);
                        micHdrs[i] = Marshal.AllocHGlobal(hdrSize);
                        WAVEHDR wh = new WAVEHDR();
                        wh.lpData = micBufs[i];
                        wh.dwBufferLength = VoiceFrameBytes;
                        Marshal.StructureToPtr(wh, micHdrs[i], false);
                        waveInPrepareHeader(hWaveIn, micHdrs[i], hdrSize);
                        waveInAddBuffer(hWaveIn, micHdrs[i], hdrSize);
                    }
                    waveInStart(hWaveIn);
                }

                // ---- 扬声器（这个打不开才真的停用）----
                IntPtr hout;
                r = waveOutOpen(out hout, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
                if (r != 0) { FailVoice("扬声器打不开 code=" + r); return; }
                hWaveOut = hout;

                spkHdrs = new IntPtr[SpkBufCount];
                spkBufs = new IntPtr[SpkBufCount];
                spkInUse = new bool[SpkBufCount];
                for (int i = 0; i < SpkBufCount; i++)
                {
                    spkBufs[i] = Marshal.AllocHGlobal(VoiceFrameBytes);
                    spkHdrs[i] = Marshal.AllocHGlobal(hdrSize);
                    WAVEHDR wh = new WAVEHDR();
                    wh.lpData = spkBufs[i];
                    wh.dwBufferLength = VoiceFrameBytes;
                    Marshal.StructureToPtr(wh, spkHdrs[i], false);
                    waveOutPrepareHeader(hWaveOut, spkHdrs[i], hdrSize);
                }

                voiceOk = true;
                Log("voice: 语音已就绪（8kHz/16bit 单声道；按住 " + PttKeyName + " 说话）"
                    + (micFailed ? "  ※麦克风不可用，仅可收听" : ""));
            }
            catch (Exception ex) { FailVoice(ex.Message); }
        }

        static void FailVoice(string why)
        {
            voiceBroken = true;
            Log("voice: 已停用（" + why + "）");
        }

        /// <summary>每帧调用：PTT 状态 + 收麦克风 + 填充播放缓冲。front = 前台是我们的窗口/游戏。</summary>
        static void VoiceTick(object lobby, bool front)
        {
            bool ptt = front && KeyDown(VK_F7);

            if (ptt && !EnsureVoice()) { pttWasDown = ptt; return; }

            if (voiceOk)
            {
                if (ptt && !pttWasDown)
                    Log(micFailed ? "voice: 按下了说话键，但这台机器没有可用麦克风"
                                  : "voice: 开始说话（" + PttKeyName + " 按住中）");
                if (!micFailed) { try { PumpMic(lobby, ptt); } catch (Exception ex) { FailVoice("采集出错: " + ex.Message); } }
                try { PumpSpeaker(); } catch (Exception ex) { FailVoice("播放出错: " + ex.Message); }
            }
            pttWasDown = ptt;
        }

        /// <summary>轮询麦克风缓冲：填满的取出来（PTT 时发出去，否则丢弃），然后还回去。</summary>
        static void PumpMic(object lobby, bool ptt)
        {
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            for (int i = 0; i < MicBufCount; i++)
            {
                WAVEHDR wh = (WAVEHDR)Marshal.PtrToStructure(micHdrs[i], typeof(WAVEHDR));
                if ((wh.dwFlags & WHDR_DONE) == 0) continue;

                int n = wh.dwBytesRecorded;
                if (ptt && n > 0)
                {
                    byte[] pcm = new byte[n];
                    Marshal.Copy(micBufs[i], pcm, 0, n);
                    SendVoiceFrame(lobby, pcm);
                }

                // 复位并重新入队
                wh.dwFlags = 0;
                wh.dwBytesRecorded = 0;
                wh.dwBufferLength = VoiceFrameBytes;
                Marshal.StructureToPtr(wh, micHdrs[i], false);
                waveInAddBuffer(hWaveIn, micHdrs[i], hdrSize);
            }
        }

        static void SendVoiceFrame(object lobby, byte[] pcm)
        {
            string me = MyPlayerName();
            string frame = CtrlTag + CtrlSep + "VOICE" + CtrlSep + Clean(me) + CtrlSep + Convert.ToBase64String(pcm);
            try
            {
                if (IsHostOf(lobby))
                {
                    IList players = GetMember(lobby, "Players") as IList;
                    if (players == null) return;
                    foreach (object o in players)
                    {
                        LANPlayerInfo lp = o as LANPlayerInfo;
                        if (lp == null || lp.TcpClient == null) continue;
                        if (SameName(Clean(lp.Name), me)) continue;      // 不用发给自己
                        SendLocked(lp, frame);
                    }
                }
                else
                {
                    MethodInfo sm = FindMethod(lobby.GetType(), "SendMessageToHost", 1);
                    if (sm != null) sm.Invoke(lobby, new object[] { frame });
                }
            }
            catch (Exception ex) { Log("voice: 发送失败: " + ex.Message); }
        }

        /// <summary>解码收到的语音帧 → 塞进播放队列（丢太久没播的，避免延迟越积越大）。</summary>
        static void QueueVoicePlayback(string b64)
        {
            try
            {
                if (!EnsureVoice()) return;
                byte[] pcm;
                try { pcm = Convert.FromBase64String(b64); } catch { return; }
                if (pcm.Length == 0) return;
                lock (voicePlayQueue)
                {
                    if (voicePlayQueue.Count > SpkBufCount) voicePlayQueue.Dequeue();   // 丢最旧的
                    voicePlayQueue.Enqueue(pcm);
                }
            }
            catch (Exception ex) { Log("voice: 收包失败: " + ex.Message); }
        }

        /// <summary>把播放队列喂给空闲的输出缓冲。</summary>
        static void PumpSpeaker()
        {
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            for (int i = 0; i < SpkBufCount; i++)
            {
                WAVEHDR wh = (WAVEHDR)Marshal.PtrToStructure(spkHdrs[i], typeof(WAVEHDR));
                bool free = spkInUse[i] ? ((wh.dwFlags & WHDR_DONE) != 0) : true;
                if (!free) continue;

                byte[] pcm = null;
                lock (voicePlayQueue) { if (voicePlayQueue.Count > 0) pcm = voicePlayQueue.Dequeue(); }
                if (pcm == null) continue;

                int n = Math.Min(pcm.Length, VoiceFrameBytes);
                Marshal.Copy(pcm, 0, spkBufs[i], n);

                wh.lpData = spkBufs[i];
                wh.dwBufferLength = n;
                wh.dwFlags = 0;
                wh.dwLoops = 0;
                Marshal.StructureToPtr(wh, spkHdrs[i], false);
                waveOutWrite(hWaveOut, spkHdrs[i], hdrSize);
                spkInUse[i] = true;
            }
        }

        // ---------- 截图用到的 Win32 ----------

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        /// <summary>截当前前台窗口（前台是我们的窗口或游戏时就截它，否则截客户端主窗口），
        /// 缩到宽 640 以内再编码成 PNG。（目前没有调用方 —— 等"聊天区内联显示"做完再接上）</summary>
        static byte[] CaptureWindow()
        {
            IntPtr h = IntPtr.Zero;
            // 前台是"我们的"（客户端自己或 gamemd）→ 截前台窗口：游戏里按 F8 截到的就是游戏画面
            if (OursInFront()) { try { h = GetForegroundWindow(); } catch { } }
            if (h == IntPtr.Zero) { try { h = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; } catch { } }
            if (h == IntPtr.Zero) { try { h = GetForegroundWindow(); } catch { } }
            if (h == IntPtr.Zero) return null;

            RECT r;
            if (!GetWindowRect(h, out r)) return null;
            int w = r.Right - r.Left, hh = r.Bottom - r.Top;
            if (w <= 0 || hh <= 0 || w > 20000 || hh > 20000) return null;

            using (Bitmap full = new Bitmap(w, hh))
            {
                using (Graphics g = Graphics.FromImage(full))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, hh));

                int tw = Math.Min(w, 640);
                int th = Math.Max(1, (int)(hh * (tw / (double)w)));
                using (Bitmap small = new Bitmap(full, new Size(tw, th)))
                using (MemoryStream ms = new MemoryStream())
                {
                    small.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
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
                return SameName(n, self);
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
                    if (n.Length == 0 || SameName(n, self)) continue;
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

        /// <summary>热路径用：前缀（"MO-RELAY\u0001名字\u0001"）预先算好，每包只拼一次 Base64。</summary>
        static string BuildFrameWithPrefix(string prefix, byte[] data)
        {
            return prefix + Convert.ToBase64String(data);
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
            // 玩家表可能变了（名字也可能改），清洗缓存重来
            lock (cleanNames) { cleanNames.Clear(); }

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
                    p.TagPrefix = Tag + "\u0001" + name + "\u0001";
                    p.Game = new IPEndPoint(IPAddress.Loopback, GamePort);
                    try
                    {
                        p.Sock = new UdpClient(new IPEndPoint(IPAddress.Loopback, BasePort + portNo));
                        // 游戏是突发发送（一帧内好几个包），默认 8KB 的 UDP 缓冲很容易溢出丢包；
                        // 丢包在游戏里表现就是卡顿/延迟尖峰。
                        try { p.Sock.Client.ReceiveBufferSize = 256 * 1024; } catch { }
                        try { p.Sock.Client.SendBufferSize = 64 * 1024; } catch { }
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
                    lock (peers) { foreach (Peer p in peers) { ti += p.PacketsIn; to += p.PacketsOut; } }
                    if (ti != lastIn || to != lastOut || totalSent != lastSent || totalRecv != lastRecv || totalSendFail != lastFail)
                    {
                        Log("stats: " + StatsLine());
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
            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);   // 复用，别每包 new
            while (running && peer.Alive)
            {
                try
                {
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
                        frame = BuildFrameWithPrefix(selfTagPrefix, data);
                        totalSent++;
                        SendToPlayer(peer.Name, frame);
                    }
                    else
                    {
                        // 客机：发给房主，tag = 目标 = 该对端（请房主转发）
                        frame = BuildFrameWithPrefix(peer.TagPrefix, data);
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
            IList list = PlayerList();          // 快照：网络线程不能直接遍历客户端的 List
            foreach (object o in list)
            {
                LANPlayerInfo lp = o as LANPlayerInfo;
                if (lp == null) continue;
                // 先比原值（绝大多数情况一次命中，不加锁、不分配），再退回清洗后的名字
                if (SameName(lp.Name, name) || SameName(CleanName(lp), name)) return lp;
            }
            return null;
        }

        // 玩家名的清洗结果按对象缓存：FindPlayer 在"每个游戏包"上都会被调用，
        // 以前每包对每个玩家做两次 Replace + Trim，高频包时会堆出大量垃圾字符串。
        static readonly Dictionary<object, string> cleanNames = new Dictionary<object, string>();

        static string CleanName(LANPlayerInfo lp)
        {
            if (lp == null) return "";
            lock (cleanNames)
            {
                string cached;
                if (cleanNames.TryGetValue(lp, out cached)) return cached;
                cached = Clean(lp.Name);
                cleanNames[lp] = cached;
                return cached;
            }
        }

        static LANPlayerInfo HostPlayer()
        {
            IList list = PlayerList();          // 快照：UDP 线程不能直接遍历客户端的 List
            // 注意：玩家表里"自己"排在第一个（两边都是），所以绝不能拿 players[0] 当房主。
            // 客机只与房主有一条 TCP 连接（隧道那条），因此"不是自己、且有 TcpClient 的对端"就是房主。
            foreach (object o in list)
            {
                LANPlayerInfo lp = o as LANPlayerInfo;
                if (lp == null) continue;
                if (SameName(lp.Name, selfName) || SameName(CleanName(lp), selfName)) continue;
                if (lp.TcpClient == null) continue;
                return lp;
            }
            // 兜底：第一个不是自己的
            foreach (object o in list)
            {
                LANPlayerInfo lp = o as LANPlayerInfo;
                if (lp == null) continue;
                if (SameName(Clean(lp.Name), selfName)) continue;
                return lp;
            }
            return null;
        }

        /// <summary>
        /// 这条 TCP 连接要承载游戏 UDP 帧，必须先把套接字调好：
        ///
        /// · <c>NoDelay = true</c>：关掉 Nagle。客户端默认没设，而 Nagle 会把小包攒着
        ///   等 ACK 再发（叠加延迟 ACK 可达 200ms 以上）。游戏数据是每帧几十字节的小包，
        ///   这是"延迟特别高"最主要的来源。
        /// · 加大收发缓冲：接收端一时忙（正在渲染）时不至于把发送端顶住。
        /// · SendTimeout：万一对端真的不收，写操作超时丢包，而不是把本机的中继线程卡死
        ///   （卡死会让 UDP 接收缓冲溢出，表现就是整局越来越卡）。
        ///
        /// 同一个连接只调一次。
        /// </summary>
        static void TuneSocket(TcpClient client)
        {
            if (client == null) return;
            lock (tunedClients)
            {
                if (tunedClients.Contains(client)) return;
                tunedClients.Add(client);
            }

            try { client.NoDelay = true; } catch { }
            try { client.SendBufferSize = 128 * 1024; } catch { }
            try { client.ReceiveBufferSize = 128 * 1024; } catch { }
            try { client.SendTimeout = 1000; } catch { }

            Log("socket tuned: NoDelay=1, send/recv buffer=128K, sendTimeout=1000ms");
        }

        static readonly HashSet<TcpClient> tunedClients = new HashSet<TcpClient>();

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
            TuneSocket(client);
            string ep = "?";
            bool ok = false;
            try { ok = client.Connected; ep = client.Client.RemoteEndPoint.ToString(); } catch { }
            Log("register host client: " + (ok ? "connected" : "not-connected") + " / " + ep);
        }

        /// <summary>对"通往房主的那条连接"的唯一写入口（与 SendLocked 走同一套每连接锁）。</summary>
        public static void SendClientLocked(TcpClient client, Encoding encoding, string message)
        {
            if (client == null || message == null) { totalSendFail++; return; }
            TuneSocket(client);
            try
            {
                if (!client.Connected) { totalSendFail++; return; }
                SendMessageCore(client, encoding, message);
            }
            catch (Exception ex) { totalSendFail++; Log("send-to-host failed: " + ex.Message); }
        }

        static DateTime lastConnSweep = DateTime.MinValue;

        static bool ConnAlive(TcpClient c)
        {
            try { return c != null && c.Connected; } catch { return false; }
        }

        /// <summary>
        /// 清理已断开连接留下的状态（写锁表 / 调优标记 / 发送队列 + 它的线程）。
        /// 以前这三张表只增不减：每进一次房间就新建 TcpClient，旧连接连同它的发送线程
        /// 会一直被引用着，重连几十次后内存和线程都白白占着。
        /// </summary>
        static void CleanupConnections()
        {
            if ((DateTime.Now - lastConnSweep).TotalSeconds < 5) return;
            lastConnSweep = DateTime.Now;

            List<TcpClient> dead = new List<TcpClient>();
            lock (sendQueues)
            {
                foreach (KeyValuePair<TcpClient, SendQueue> kv in sendQueues)
                    if (!ConnAlive(kv.Key)) dead.Add(kv.Key);
            }
            lock (sendLocksGate)
            {
                foreach (TcpClient c in sendLocks.Keys) if (!ConnAlive(c)) dead.Add(c);
            }
            lock (tunedClients)
            {
                foreach (TcpClient c in tunedClients) if (!ConnAlive(c)) dead.Add(c);
            }

            if (dead.Count == 0) return;

            int n = 0;
            for (int i = 0; i < dead.Count; i++)
            {
                TcpClient c = dead[i];
                if (ReferenceEquals(c, hostClient)) continue;    // 当前仍在用的房主连接别动

                lock (sendQueues)
                {
                    SendQueue q;
                    if (sendQueues.TryGetValue(c, out q))
                    {
                        q.Alive = false;
                        lock (q.Gate) Monitor.Pulse(q.Gate);     // 叫醒发送线程，让它自己退出
                        sendQueues.Remove(c);
                    }
                }
                lock (sendLocksGate) sendLocks.Remove(c);
                lock (tunedClients) tunedClients.Remove(c);
                n++;
            }
            if (n > 0) Log("cleanup: 清理了 " + n + " 条已断开连接的残留状态");
        }

        /// <summary>同一条连接的写锁（不同连接互不阻塞）。</summary>
        static object LockFor(TcpClient client)
        {
            lock (sendLocksGate)
            {
                object gate;
                if (!sendLocks.TryGetValue(client, out gate))
                {
                    gate = new object();
                    sendLocks[client] = gate;
                }
                return gate;
            }
        }

        /// <summary>
        /// 把一条大厅消息写进流里（结尾加 \u0002，客户端按它分帧）。
        /// 用每线程复用缓冲，省掉每包一次字符串拼接 + 一次 byte[] 分配 ——
        /// 游戏数据每秒几十到几百包，这些分配会变成 Gen0 GC 抖动。
        /// </summary>
        static void WriteMessage(TcpClient client, Encoding encoding, string message)
        {
            int need = encoding.GetByteCount(message) + 1;
            byte[] buffer = txBuffer;
            if (buffer == null || buffer.Length < need)
            {
                buffer = new byte[need < 4096 ? 4096 : need];
                txBuffer = buffer;
            }

            int n = encoding.GetBytes(message, 0, message.Length, buffer, 0);
            buffer[n] = (byte)'\u0002';

            lock (LockFor(client))
            {
                NetworkStream ns = client.GetStream();
                ns.Write(buffer, 0, n + 1);
            }
        }

        // ==================================================================
        // 游戏帧的发送队列（每条连接一个）
        //
        // 为什么必须异步：房主转发时如果直接同步写，只要有一个客机的 TCP
        // 窗口卡住（网络抖动/它那边卡了），写操作就会阻塞 —— 而它是在
        // "收包线程"里执行的，于是这个线程也不再收包，连带其他玩家一起卡。
        // 典型表现就是"一个人网络差，全房延迟爆炸"。
        //
        // 现在游戏帧只入队，由该连接自己的线程去写：慢的只积压自己那一条，
        // 超过上限就丢最旧的帧（游戏数据本来就可以丢，丢总比整房卡住好）。
        // 大厅/控制帧仍走同步写，保证聊天、进出房间这些语义不变。
        // ==================================================================

        class SendQueue
        {
            public readonly TcpClient Client;
            public readonly Encoding Encoding;
            public readonly Queue<string> Items = new Queue<string>();
            public readonly object Gate = new object();
            public volatile bool Alive = true;
            public Thread Worker;
            public int Dropped;

            public SendQueue(TcpClient client, Encoding encoding)
            {
                Client = client;
                Encoding = encoding;
            }
        }

        static readonly Dictionary<TcpClient, SendQueue> sendQueues = new Dictionary<TcpClient, SendQueue>();
        static readonly string RelayFramePrefix = Tag + "\u0001";
        const int SendQueueLimit = 256;       // 约 1 秒的游戏包量；超了丢最旧的，保证实时性

        static void SendMessageCore(TcpClient client, Encoding encoding, string message)
        {
            Encoding enc = encoding == null ? Encoding.UTF8 : encoding;

            // 游戏帧异步、可丢；其余（聊天/控制/进出房间）保持同步，语义不变
            if (message.StartsWith(RelayFramePrefix, StringComparison.Ordinal))
                EnqueueGameFrame(client, enc, message);
            else
                WriteMessage(client, enc, message);
        }

        static void EnqueueGameFrame(TcpClient client, Encoding encoding, string message)
        {
            SendQueue q;
            lock (sendQueues)
            {
                if (!sendQueues.TryGetValue(client, out q))
                {
                    q = new SendQueue(client, encoding);
                    q.Worker = new Thread(delegate() { SendLoop(q); });
                    q.Worker.IsBackground = true;
                    q.Worker.Name = "MoRelayTx";
                    sendQueues[client] = q;
                    q.Worker.Start();
                }
            }

            if (!q.Alive) { totalSendFail++; return; }

            lock (q.Gate)
            {
                if (q.Items.Count >= SendQueueLimit)
                {
                    q.Items.Dequeue();
                    q.Dropped++;
                    totalSendFail++;      // 计入统计：能看出是不是有人网络差导致积压
                }
                q.Items.Enqueue(message);
                Monitor.Pulse(q.Gate);
            }
        }

        static void SendLoop(SendQueue q)
        {
            List<string> batch = new List<string>(64);

            while (q.Alive)
            {
                batch.Clear();
                lock (q.Gate)
                {
                    while (q.Items.Count == 0 && q.Alive) Monitor.Wait(q.Gate, 1000);
                    if (!q.Alive) return;
                    while (q.Items.Count > 0 && batch.Count < 64) batch.Add(q.Items.Dequeue());
                }

                if (batch.Count == 0) continue;

                try { WriteMessages(q.Client, q.Encoding, batch); }
                catch (Exception ex)
                {
                    totalSendFail++;
                    WarnOnce("tx", ex.Message, "发送线程停止（连接可能已断）: " + ex.Message);
                    q.Alive = false;
                    return;
                }
            }
        }

        /// <summary>
        /// 一次 Write 写多条消息：减少系统调用次数，也让内核把它们合成更少的 TCP 段
        /// （段越少，公网隧道上丢一个包要重传的代价越小）。
        /// 每条消息后面照样补 \u0002，客户端按它分帧，行为不变。
        /// </summary>
        static void WriteMessages(TcpClient client, Encoding encoding, List<string> messages)
        {
            int need = 0;
            for (int i = 0; i < messages.Count; i++) need += encoding.GetByteCount(messages[i]) + 1;

            byte[] buffer = txBuffer;
            if (buffer == null || buffer.Length < need)
            {
                buffer = new byte[need < 8192 ? 8192 : need];
                txBuffer = buffer;
            }

            int pos = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                string m = messages[i];
                int n = encoding.GetBytes(m, 0, m.Length, buffer, pos);
                pos += n;
                buffer[pos++] = (byte)'\u0002';
            }

            lock (LockFor(client))
            {
                NetworkStream ns = client.GetStream();
                ns.Write(buffer, 0, pos);
            }
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
                TcpClient tcp = lp.TcpClient;
                if (tcp == null)
                {
                    // 房主侧理论上不会走到这；出现就说明这个玩家的连接还没建立/已断开
                    WarnOnce("nullconn", Clean(lp.Name) + "",
                        "发送失败：玩家 '" + Clean(lp.Name) + "' 的 TcpClient 为空（连接未建立或已断开）");
                    totalSendFail++;
                    return;
                }

                TuneSocket(tcp);
                bool queued = message.StartsWith(RelayFramePrefix, StringComparison.Ordinal);
                SendMessageCore(tcp, Encoding.UTF8, message);

                // 游戏帧是异步入队的（还可能被丢弃），此时说"刚发过"是谎报；
                // 只有真正同步写出去的大厅消息才重置这个时间戳。
                if (!queued) lp.TimeSinceLastSentMessage = TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                totalSendFail++;
                WarnOnce("sendex", Clean(lp.Name) + "", "send to '" + Clean(lp.Name) + "' failed: " + ex.Message);
            }
        }

        // ==================================================================
        // 地图同步（房主点按钮 → 缺图的客机确认后接收）
        //
        // 为什么不做成「一选图就自动推」：
        //   · 一张图在磁盘上不一定是单个 .map —— 同目录同名的 .ini（客户端读它取
        //     [Basic]/[ForcedOptions]/[Preview]）和预览图都得一起给；
        //   · 也不是所有图都在 Maps\Custom，任务图/合并进 INI 的图在别的目录底下。
        //   自动推会一进房间就往公网隧道上灌几十 MB，所以改成：
        //     - 房主选好图，点「同步地图」按钮，才把这张图需要的文件清单发出去；
        //     - 客机只有在本地确实缺文件（大小或 SHA-256 不同）时才弹窗问；
        //     - 玩家点「接收」才开始传，每个文件写完都校验 SHA-256。
        //   文件一律按「相对游戏根目录」的路径收发，所以落在哪个目录都能对上。
        // ==================================================================

        const string MapHelloKind = "MAPHELLO";
        const string MapReqKind = "MAPREQ";
        const string MapDataKind = "MAPDATA";
        const string MapEndKind = "MAPEND";
        const string MapAskKind = "MAPASK";   // 客机点「接收地图」→ 请房主发现清单
        const string MapHaveQKind = "MAPHAVEQ";  // 房主问客机：你本地有哪些图
        const string MapHaveKind = "MAPHAVE";    // 客机回报清单（deflate + Base91）
        const string MapMissKind = "MAPMISS"; // 客机缺图拦下开局 → 提醒房主点「同步地图」
        const string MapHashKind = "MAPHASH"; // 房主广播当前地图的 SHA-256（开局前核对版本）
        const string PmKind = "PM";       // 私聊（只发给指定玩家，房主负责转交）
        // 客户端的大厅分帧用 '\u0002'（HandleServerCommunication 里 IndexOf((char)2)），
        // 所以这里的字段分隔**绝不能**用 \u0002 —— 否则整帧会被客户端切成碎片，
        // 每片都因为段数不够被 OnCtrl 丢掉（表现为"按钮点了但双方都没反应"）。
        // 用 \u0001 是安全的：客户端不按它切，而 OnCtrl 是把第 4 段之后整体当负载。
        const char MapField = '\u0001';      // 负载内部字段分隔
        // 提速：每帧 32 KB（base64 后约 43 KB），不再人为 sleep。
        // 客户端的接收缓冲已经被 rxbuf 补丁调到 64 KB，一条消息基本一次 Read 就能读完；
        // 发送端靠 TCP 自身的流控（写满就阻塞在 Write 上），不需要我们再限速。
        // 之前的 512 B + 6 ms 只有 ~33 KB/s，这也是地图同步慢的主要原因。
        const int MapChunkBytes = 32768;
        static int MapChunkDelayMs = 0;                // 0 = 不限速，整条 TCP 灌满为止
        const long MapPackMaxBytes = 48L * 1024 * 1024;   // 超过这个大就不压了（省内存）

        class MapFile
        {
            public string Rel;      // 相对游戏根目录，'/' 分隔
            public long Size;
            public string Sha;      // SHA-256 大写十六进制
            public long PackedSize; // deflate 后的大小；0 = 不压缩，按原样发
            public byte[] Packed;   // 压好的字节（只在房主这一单里缓存，传完即弃）
            /// <summary>
            /// 这个文件所属地图在 MentalOmegaMaps.ini 里的注册段（只有该图的 .map 这个文件带，
            /// 同一张图的 .ini/预览图留空）。多图批量同步时每个文件各带各的。
            /// </summary>
            public string IniSection = "";
        }

        class MapJob
        {
            public string Id;
            public string Name;
            public long Total;
            public List<MapFile> Files = new List<MapFile>();
            /// <summary>
            /// 房主 INI\MentalOmegaMaps.ini 里这张图的注册段（含 [节名] 与各行）。
            /// MapsMO\ 下的图不是靠扫目录登记的，缺这段客机就算有文件也不会出现在地图列表里。
            /// </summary>
            public string IniSection = "";
            /// <summary>客机：这次要收哪些文件（序号）。</summary>
            public List<int> Req;
            /// <summary>客机：每个文件从哪个字节继续（断点续传）。</summary>
            public Dictionary<int, long> Resume;
        }

        static MapJob hostMapJob;                                              // 房主：当前这一单
        static readonly Dictionary<string, MapJob> clientMapJobs =
            new Dictionary<string, MapJob>(StringComparer.OrdinalIgnoreCase);   // 客机：已接收的任务
        static MapJob pendingMapJob;
        static object pendingMapLobby;
        static List<int> pendingMapMissing;
        static object mapSyncButton;
        static object mapSyncButtonLobby;
        static object lastMapRefreshLobby;       // 已热刷新过的房间（同一张图只刷一次）
        static string lastMapRefreshRel = "";
        static DateTime lastMapButtonCheck = DateTime.MinValue;

        static string CtrlFrame(string kind, string payload)
        {
            return CtrlTag + CtrlSep + kind + CtrlSep + Clean(MyPlayerName()) + CtrlSep + payload;
        }

        // ---------------- 地图版本一致性（房主广播当前地图的 SHA-256） ----------------
        //
        // 光有"文件存在"不够：同一个名字的图，两边版本不同一样开不了局（读条就崩）。
        // 房主每次选图变化就把 rel + SHA-256 广播一次，客机在开局前拿本地算的比对。

        static string announcedMapRel = "";
        static string pendingHashRel = "", pendingHashSha = "";
        static bool hashThreadRunning;
        static string remoteMapRel = "", remoteMapSha = "";

        static string CurrentMapRel(object lobby)
        {
            try
            {
                object map = GetMember(lobby, "Map");
                if (map == null)
                {
                    object gm = GetMember(lobby, "GameModeMap");
                    map = gm == null ? null : GetMember(gm, "Map");
                }
                if (map == null) return "";

                string full = Convert.ToString(GetMember(map, "CompleteFilePath"));
                if (string.IsNullOrEmpty(full)) return "";

                string root = SafeGetGamePath();
                return RelativeToRoot(full, root) ?? "";
            }
            catch { return ""; }
        }

        static string LocalMapSha(object lobby)
        {
            try
            {
                string rel = CurrentMapRel(lobby);
                if (rel.Length == 0) return "";
                string root = SafeGetGamePath();
                return Sha256Of(Path.Combine(root, rel.Replace('/', '\\')));
            }
            catch { return ""; }
        }

        static void MapHashTick(object lobby)
        {
            try
            {
                if (!IsHostOf(lobby)) return;
                string rel = CurrentMapRel(lobby);
                if (rel.Length == 0) return;

                if (!string.Equals(rel, announcedMapRel, StringComparison.OrdinalIgnoreCase) && !hashThreadRunning)
                {
                    hashThreadRunning = true;
                    string root = SafeGetGamePath();
                    string relCopy = rel;
                    Thread t = new Thread(delegate()
                    {
                        string sha = "";
                        try { sha = Sha256Of(Path.Combine(root, relCopy.Replace('/', '\\'))); } catch { }
                        if (sha.Length > 0) { pendingHashRel = relCopy; pendingHashSha = sha; }
                        hashThreadRunning = false;
                    });
                    t.IsBackground = true;
                    t.Name = "MoMapHash";
                    t.Start();
                }

                if (pendingHashRel.Length > 0
                    && !string.Equals(pendingHashRel, announcedMapRel, StringComparison.OrdinalIgnoreCase))
                {
                    announcedMapRel = pendingHashRel;
                    SendCtrlToPeers(lobby, CtrlFrame(MapHashKind,
                        pendingHashRel + MapField + pendingHashSha));
                    Log("mapsync: 已广播地图版本 " + pendingHashRel + " sha="
                        + pendingHashSha.Substring(0, Math.Min(12, pendingHashSha.Length)));
                }
            }
            catch { }
        }

        static void OnMapHash(string payload)
        {
            try
            {
                string[] p = payload.Split(MapField);
                if (p.Length < 2) return;
                remoteMapRel = p[0];
                remoteMapSha = p[1];
                Log("mapsync: 房主地图版本 " + remoteMapRel + " sha="
                    + remoteMapSha.Substring(0, Math.Min(12, remoteMapSha.Length)));
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // 引擎拓展检测（借鉴 MO_ExperienceEnhancements 的 PvpNotice 思路）
        //
        // 那类插件（MOExperience / 自动装车 / Phobos / 透明迷雾）只装在自己机器上，
        // 对方在客户端里完全看不出来，PVP 时等于隐形加成。我们在**大厅房间**里
        // 各自扫一遍自己的游戏根目录，命中就广播一条 —— 比它每 30 秒在游戏内
        // 刷一遍温和得多，而且在开局前就能看到。
        // 只做提示，不阻止开局；没装的人不会发任何消息。
        // ------------------------------------------------------------------

        static object extCheckLobby;
        static volatile string extCheckResult;        // null=还没扫完；""=干净；其它=命中的组件（全名）
        static volatile string extShortResult = "";   // 状态条用的短名，如 "MOExp、迷雾"
        static bool extCheckStarted;
        static bool extCheckAnnounced;
        static int extCheckAnnouncedCount;

        static void ExtensionCheckTick(object lobby)
        {
            if (lobby == null || !extCheckEnabled) return;

            // 换房间（或重进大厅）就重新扫：同一次进程里文件不会变，但房间对象会换
            if (!ReferenceEquals(extCheckLobby, lobby))
            {
                extCheckLobby = lobby;
                extCheckResult = null;
                extShortResult = "";
                extCheckStarted = false;
                extCheckAnnounced = false;
                extCheckAnnouncedCount = 0;
            }

            // 扫描本身不设人数门槛：一个人的时候状态条也能显示「非纯净」
            if (!extCheckStarted)
            {
                extCheckStarted = true;
                Thread t = new Thread(delegate()
                {
                    try { DetectEngineExtensions(); }
                    catch (Exception ex) { extCheckResult = ""; Log("extcheck: 扫描失败: " + ex.Message); }
                });
                t.IsBackground = true;
                t.Start();
                return;
            }

            // 还在扫 / 本机是纯净的：什么都不发
            if (extCheckResult == null || extCheckResult.Length == 0) return;

            // 房间广播只在有人时做；新人进来会补报一次
            int count = 0;
            try
            {
                IList players = GetMember(lobby, "Players") as IList;
                count = players == null ? 0 : players.Count;
            }
            catch { }
            if (count < 2)
            {
                extCheckAnnounced = false;
                extCheckAnnouncedCount = 0;
                return;
            }

            if (extCheckAnnounced && extCheckAnnouncedCount >= count) return;
            extCheckAnnounced = true;
            extCheckAnnouncedCount = count;

            Log("extcheck: 广播 " + extCheckResult);
            BroadcastChat(lobby, "【纯净提示】" + MyPlayerName() + " 的本机装了：" + extCheckResult + "（联机对战请自行判断公平性）");
        }

        /// <summary>扫游戏根目录。只列会改变游戏行为/视野的组件；
        /// ddraw.dll（cnc-ddraw）只是渲染包装，MO 自带，不算。</summary>
        static void DetectEngineExtensions()
        {
            string root = SafeGetGamePath();
            if (string.IsNullOrEmpty(root))
            {
                try { root = Path.GetDirectoryName(typeof(Relay).Assembly.Location); }
                catch { }
            }
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                extCheckResult = "";
                extShortResult = "";
                return;
            }

            string[,] items =
            {
                { "MOExperience.dll", "MOExperience 功能拓展", "MOExp" },
                { "AutoLoad_nolog.dll", "自动装车拓展", "装车" },
                { "AutoLoad.dll", "自动装车拓展", "装车" },
                { "Phobos.dll", "Phobos 引擎拓展", "Phobos" },
                { "shroud.shp", "透明迷雾替换", "迷雾" },
            };

            List<string> full = new List<string>();
            List<string> shortNames = new List<string>();
            for (int i = 0; i < items.GetLength(0); i++)
            {
                try
                {
                    if (!File.Exists(Path.Combine(root, items[i, 0]))) continue;
                    if (!full.Contains(items[i, 1])) full.Add(items[i, 1]);
                    if (!shortNames.Contains(items[i, 2])) shortNames.Add(items[i, 2]);
                }
                catch { }
            }

            extCheckResult = full.Count == 0 ? "" : string.Join("、", full.ToArray());
            extShortResult = shortNames.Count == 0 ? "" : string.Join("、", shortNames.ToArray());
            Log("extcheck: 根目录=" + root + " 命中=" + full.Count);
        }

        // ------------------------------------------------------------------
        // 快捷语面板
        //
        // 底部按钮行 x=380 起是空的（锁定房间/自动准备右边，地图预览底边到 y=670，
        // 变道/退出在 x=862 以后），按钮放这里不会压住任何原版控件。
        // 面板本身照抄地图选择框的做法：XNAPanel + XNAMultiColumnListBox，
        // 背景贴图借聊天框的（自建 DarkeningPanel 在这个客户端里画不出来）。
        // ------------------------------------------------------------------

        static object quickButton, quickButtonLobby;
        static DateTime lastQuickBtnCheck = DateTime.MinValue;
        static object quickPanel, quickList, quickGuiLobby;
        static readonly List<string> quickListTexts = new List<string>();

        static void QuickPanelTick(object lobby)
        {
            if (lobby == null) return;
            try
            {
                // 换了大厅实例：旧控件跟着旧窗口一起没了，引用清空重建
                if (!ReferenceEquals(quickButtonLobby, lobby))
                {
                    quickButton = null;
                    quickButtonLobby = lobby;
                    quickPanel = null;
                    quickList = null;
                    quickGuiLobby = lobby;
                    quickListTexts.Clear();
                }

                if (!quickPanelEnabled)
                {
                    if (quickButton != null) SetMember(quickButton, "Visible", false);
                    if (quickPanel != null) SetMember(quickPanel, "Visible", false);
                    return;
                }

                if (quickButton != null)
                {
                    SetMember(quickButton, "Visible", true);
                    return;
                }

                if ((DateTime.Now - lastQuickBtnCheck).TotalMilliseconds < 500) return;
                lastQuickBtnCheck = DateTime.Now;
                EnsureQuickButton(lobby);
            }
            catch (Exception ex) { Log("quick: tick 失败: " + ex.Message); }
        }

        static void EnsureQuickButton(object lobby)
        {
            object wm = GetMember(lobby, "WindowManager");
            Type btnType = FindType("ClientGUI.XNAClientButton");
            if (wm == null || btnType == null) return;

            try
            {
                object btn = Activator.CreateInstance(btnType, new object[] { wm });
                SetMember(btn, "Name", "btnMoQuickText");
                SetMember(btn, "Text", "快捷语");
                CopyButtonLook(GetMember(lobby, "btnLockGame") ?? GetMember(lobby, "btnLaunchGame"), btn);
                SetMember(btn, "X", 380);
                SetMember(btn, "Y", 678);
                SetMember(btn, "Width", 80);
                SetMember(btn, "Height", 23);
                AddChildTo(lobby, btn);

                MethodInfo addClick = btnType.GetMethod("add_LeftClick",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo handler = typeof(Relay).GetMethod("QuickButtonClick",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (addClick != null && handler != null)
                    addClick.Invoke(btn, new object[] { Delegate.CreateDelegate(typeof(EventHandler), handler) });

                quickButton = btn;
                Log("quick: 已创建「快捷语」按钮 (380,678)");
            }
            catch (Exception ex) { Log("quick: 创建按钮失败: " + ex.Message); }
        }

        static void QuickButtonClick(object sender, EventArgs e)
        {
            try
            {
                object lobby = quickButtonLobby;
                if (lobby == null) return;
                if (quickPanel == null || !ReferenceEquals(quickGuiLobby, lobby))
                {
                    quickPanel = null;
                    quickList = null;
                    quickGuiLobby = lobby;
                    BuildQuickTextGui(lobby);
                }
                if (quickPanel == null) { Log("quick: 面板没建出来"); return; }

                RefreshQuickTextGui();
                SetMember(quickPanel, "Visible", true);
            }
            catch (Exception ex) { Log("quick: 打开面板失败: " + ex.Message); }
        }

        static void CloseQuickTextGui()
        {
            try { if (quickPanel != null) SetMember(quickPanel, "Visible", false); } catch { }
        }

        static void BuildQuickTextGui(object lobby)
        {
            try
            {
                object wm = GetMember(lobby, "WindowManager");
                Type panelType = FindType("Rampastring.XNAUI.XNAControls.XNAPanel");
                Type listType = FindType("Rampastring.XNAUI.XNAControls.XNAMultiColumnListBox");
                Type lblType = FindType("Rampastring.XNAUI.XNAControls.XNALabel")
                            ?? FindType("ClientGUI.XNALabel");
                Type btnType = FindType("ClientGUI.XNAClientButton");
                if (wm == null || panelType == null || listType == null || btnType == null)
                {
                    Log("quick: 面板缺少控件类型");
                    return;
                }

                int W = 560, H = 320;
                int lw = GetInt(lobby, "Width", 1024), lh = GetInt(lobby, "Height", 720);
                int X = (lw - W) / 2, Y = (lh - H) / 2;
                if (X < 0) X = 0;
                if (Y < 0) Y = 0;

                object panel = Activator.CreateInstance(panelType, new object[] { wm });
                SetMember(panel, "Name", "panMoQuickText");
                SetMember(panel, "X", X);
                SetMember(panel, "Y", Y);
                SetMember(panel, "Width", W);
                SetMember(panel, "Height", H);

                object chat = GetMember(lobby, "lbChatMessages");
                if (chat != null)
                {
                    object bg = GetMember(chat, "BackgroundTexture");
                    if (bg != null) SetMember(panel, "BackgroundTexture", bg);
                    object dm = GetMember(chat, "DrawMode");
                    if (dm != null) SetMember(panel, "DrawMode", dm);
                }
                AddChildTo(lobby, panel);

                if (lblType != null)
                {
                    object title = Activator.CreateInstance(lblType, new object[] { wm });
                    SetMember(title, "Text", "快捷语");
                    SetMember(title, "X", 14);
                    SetMember(title, "Y", 10);
                    AddChildTo(panel, title);

                    object hint = Activator.CreateInstance(lblType, new object[] { wm });
                    SetMember(hint, "Text", "选中一条后点「发送」；来源见下方（MOExperience.ini 存在时会自动合并）");
                    SetMember(hint, "X", 14);
                    SetMember(hint, "Y", 34);
                    AddChildTo(panel, hint);
                }

                object list = Activator.CreateInstance(listType, new object[] { wm });
                SetMember(list, "Name", "lbMoQuickText");
                SetMember(list, "X", 14);
                SetMember(list, "Y", 58);
                SetMember(list, "Width", W - 28);
                SetMember(list, "Height", H - 58 - 42);
                AddChildTo(panel, list);

                MethodInfo addCol = listType.GetMethod("AddColumn", new Type[] { typeof(string), typeof(int) });
                if (addCol != null)
                {
                    addCol.Invoke(list, new object[] { "序号", 50 });
                    addCol.Invoke(list, new object[] { "内容", W - 28 - 50 - 12 });
                }
                quickList = list;

                object refBtn = GetMember(lobby, "btnLockGame") ?? GetMember(lobby, "btnLaunchGame");
                int by = H - 34;
                AddOptionButton(panel, wm, refBtn, "btnMoQuickSend", "发送", 14, by, 90, 23, "QuickGuiSend");
                AddOptionButton(panel, wm, refBtn, "btnMoQuickClose", "关闭", 110, by, 90, 23, "QuickGuiClose");

                SetMember(panel, "Visible", false);
                quickPanel = panel;
                Log("quick: 面板已建好");
            }
            catch (Exception ex)
            {
                Log("quick: 建面板失败: " + ex.Message);
                quickPanel = null;
            }
        }

        static void RefreshQuickTextGui()
        {
            try
            {
                if (quickList == null) return;

                MethodInfo clear = FindMethod(quickList.GetType(), "ClearItems", 0);
                if (clear != null) clear.Invoke(quickList, null);
                quickListTexts.Clear();

                MethodInfo addItem = quickList.GetType().GetMethod("AddItem",
                    new Type[] { typeof(string[]), typeof(bool) });
                if (addItem == null) { Log("quick: 列表找不到 AddItem"); return; }

                string[] texts = QuickTexts();
                for (int i = 0; i < texts.Length; i++)
                {
                    addItem.Invoke(quickList, new object[] { new string[] { (i + 1).ToString(), texts[i] }, true });
                    quickListTexts.Add(texts[i]);
                }
            }
            catch (Exception ex) { Log("quick: 刷新列表失败: " + ex.Message); }
        }

        static void QuickGuiSend(object sender, EventArgs e)
        {
            try
            {
                object lobby = quickButtonLobby;
                if (lobby == null) return;

                int idx = GetInt(quickList, "SelectedIndex", -1);
                if (idx < 0 || idx >= quickListTexts.Count)
                {
                    Notice(lobby, "先在列表里选一条快捷语", false);
                    return;
                }

                string text = quickListTexts[idx];
                CloseQuickTextGui();
                BroadcastChat(lobby, text);
            }
            catch (Exception ex) { Log("quick: 发送失败: " + ex.Message); }
        }

        static void QuickGuiClose(object sender, EventArgs e) { CloseQuickTextGui(); }

        static void MapSyncTick(object lobby)
        {
            if (lobby == null) return;

            // 房主：当前地图变了就把它的 SHA-256 广播出去（客机开局前拿它核对版本）
            try { MapHashTick(lobby); } catch { }

            // 房主与客机都有这个按钮：房主「同步地图」/「取消同步」，客机「接收地图」
            if (mapSyncButton != null && ReferenceEquals(mapSyncButtonLobby, lobby))
            {
                try
                {
                    bool host = IsHostOf(lobby);

                    // 两边都要有这个按钮：房主用「同步地图」主动推，客机用「接收地图」请房主发。
                    // 位置已经和「锁定房间」对齐，客机页同样有这一排按钮，所以不会突兀。
                    SetMember(mapSyncButton, "Visible", true);

                    string label = mapSyncTxActive ? "取消同步"
                                 : mapSyncRxActive ? "接收中…"
                                 : (host ? "推送地图" : "接收地图");
                    if ((GetMember(mapSyncButton, "Text") as string) != label)
                        SetMember(mapSyncButton, "Text", label);
                    SetMember(mapSyncButton, "Enabled", !mapSyncRxActive);

                    // 布局可能比按钮晚算好，每帧校正一次位置（内部只在变化时才写）
                    PositionMapSyncButton(lobby);
                }
                catch { }
                return;
            }

            if ((DateTime.Now - lastMapButtonCheck).TotalMilliseconds < 500) return;
            lastMapButtonCheck = DateTime.Now;
            EnsureMapSyncButton(lobby);
        }

        static void EnsureMapSyncButton(object lobby)
        {
            // 这版客户端的字段名是 lbMapList（XNAMultiColumnListBox）；
            // 新版上游改成了 lbGameModeMapList，两个都试一下，换客户端版本不至于失效。
            object mapList = GetMember(lobby, "lbMapList");
            if (mapList == null) mapList = GetMember(lobby, "lbGameModeMapList");
            if (mapList == null) return;      // 大厅还没建好，下一轮再试
            object wm = GetMember(lobby, "WindowManager");
            if (wm == null) return;

            Type btnType = FindType("ClientGUI.XNAClientButton");
            if (btnType == null) { Log("mapsync: 找不到 XNAClientButton"); return; }

            try
            {
                object btn = Activator.CreateInstance(btnType, new object[] { wm });
                SetMember(btn, "Name", "btnMoMapSync");
                SetMember(btn, "Text", "推送地图");

                // 先给个保守的初始位置，真正的位置由 PositionMapSyncButton 每帧校正
                // （创建这一刻大厅的布局可能还没算出来，Width/Right 都是 0）
                bool hostNow = IsHostOf(lobby);
                object anchor = hostNow
                    ? (GetMember(lobby, "btnLockGame") ?? GetMember(lobby, "btnLaunchGame"))
                    : (GetMember(lobby, "btnLaunchGame") ?? GetMember(lobby, "btnLockGame"));
                CopyButtonLook(anchor, btn);      // 和旁边那个原版按钮同一套贴图/字体

                SetMember(btn, "X", GetInt(mapList, "X", 12));
                SetMember(btn, "Y", GetInt(mapList, "Y", 0) + GetInt(mapList, "Height", 0) + 4);
                SetMember(btn, "Width", SyncButtonWidth);
                SetMember(btn, "Height", 23);

                object parent = GetMember(anchor, "Parent") ?? GetMember(mapList, "Parent") ?? lobby;
                MethodInfo add = FindMethod(parent.GetType(), "AddChild", 1);
                if (add == null) { Log("mapsync: 找不到 AddChild"); return; }
                add.Invoke(parent, new object[] { btn });

                MethodInfo addClick = btnType.GetMethod("add_LeftClick",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo handler = typeof(Relay).GetMethod("MapSyncButtonClick",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (addClick != null && handler != null)
                {
                    Delegate d = Delegate.CreateDelegate(typeof(EventHandler), handler);
                    addClick.Invoke(btn, new object[] { d });
                }

                mapSyncButton = btn;
                mapSyncButtonLobby = lobby;
                try { SetMember(btn, "Visible", true); } catch { }
                PositionMapSyncButton(lobby);
                Log("mapsync: 已在大厅加「同步地图 / 接收地图」按钮（底部按钮行，host=" + hostNow + "）");
            }
            catch (Exception ex)
            {
                Log("mapsync: 创建按钮失败: " + ex.Message);
                mapSyncButton = null;
                mapSyncButtonLobby = null;
            }
        }

        /// <summary>同步按钮的宽度（比原版按钮窄，塞进底部按钮行的空档）。</summary>
        const int SyncButtonWidth = 88;

        /// <summary>
        /// 底部按钮行里，紧跟在「开始游戏」右边的那个控件：
        ///   房主是「锁定房间」(btnLockGame)
        ///   客机是「自动准备」勾选框(chkAutoReady，同样是 x≈224)
        /// 两个角色是同一个槽位，所以同步按钮都要塞进它们和「开始游戏」之间的空档。
        /// </summary>
        static object BottomRowNeighbor(object lobby)
        {
            object autoChk = GetMember(lobby, "chkAutoReady");
            object lockBtn = GetMember(lobby, "btnLockGame");

            if (IsShown(autoChk)) return autoChk;
            if (IsShown(lockBtn)) return lockBtn;
            return IsHostOf(lobby) ? (lockBtn ?? autoChk) : (autoChk ?? lockBtn);
        }

        static bool IsShown(object o)
        {
            if (o == null) return false;
            object v = GetMember(o, "Visible");
            return v is bool && (bool)v;
        }

        /// <summary>
        /// 每帧校正「同步地图 / 接收地图」的位置。房主和客机底部按钮行不一样：
        ///   房主：开始游戏 | 空档 | 锁定房间 | … | 退出游戏
        ///   客机：开始游戏 | 空档 | 自动准备 | … | 退出游戏
        /// 两边的空档都只有 ~79px，所以按钮宽度必须跟着空档算，
        /// 不能照抄 133px 的原版按钮（那就是之前压住「开始游戏」的原因）。
        /// </summary>
        static void PositionMapSyncButton(object lobby)
        {
            if (mapSyncButton == null || lobby == null) return;
            try
            {
                bool host = IsHostOf(lobby);
                object launch = GetMember(lobby, "btnLaunchGame");
                object neighbor = BottomRowNeighbor(lobby);
                if (launch == null || neighbor == null) return;

                int launchX = GetInt(launch, "X", -1);
                int launchRight = launchX < 0 ? 0 : launchX + GetInt(launch, "Width", 0);

                int nx = GetInt(neighbor, "X", -1);
                int ny = GetInt(neighbor, "Y", -1);
                int ah = GetInt(launch, "Height", 0);
                if (nx < 0 || ny <= 0) return;        // 布局还没算好，下一帧再说
                if (ah <= 0) ah = 23;

                int gap = (launchRight > 0 && nx > launchRight) ? nx - launchRight : 0;

                int w = SyncButtonWidth;
                if (gap > 0)
                {
                    w = gap - 10;                     // 两边各留 5px
                    if (w > SyncButtonWidth) w = SyncButtonWidth;
                    if (w < 40) w = 40;
                }

                int x = nx - w - 5;
                int y = ny;

                // 极端情况（空档比按钮还窄）：贴住「开始游戏」右侧，绝不让两边重叠
                if (launchRight > 0 && x < launchRight + 4)
                {
                    x = launchRight + 4;
                    w = nx - x - 4;
                    if (w < 32) w = 32;
                }

                if (x < 0) x = 0;
                if (y < 0) y = 0;

                bool changed = GetInt(mapSyncButton, "X", -1) != x
                            || GetInt(mapSyncButton, "Y", -1) != y
                            || GetInt(mapSyncButton, "Width", -1) != w
                            || GetInt(mapSyncButton, "Height", -1) != ah;
                if (!changed) return;

                SetMember(mapSyncButton, "AdaptiveText", true);   // 窄的时候文字自动缩，不溢出
                SetMember(mapSyncButton, "X", x);
                SetMember(mapSyncButton, "Y", y);
                SetMember(mapSyncButton, "Width", w);
                SetMember(mapSyncButton, "Height", ah);
                Log("mapsync: 位置 host=" + host + " x=" + x + " y=" + y + " w=" + w
                    + "（开始游戏右边缘=" + launchRight + "，右侧邻居 x=" + nx + " 空档=" + gap + "）");
            }
            catch { }
        }

        static void MapSyncButtonClick(object sender, EventArgs e)
        {
            try
            {
                object lobby = mapSyncButtonLobby;
                if (lobby == null) return;

                if (mapSyncTxActive) { CancelMapSync(lobby); return; }
                if (IsHostOf(lobby)) OpenMapSyncGui(lobby);   // 房主：弹出选择框（单张 / 多张）
                else RequestMapSync(lobby);                    // 客机：直接要当前图
            }
            catch (Exception ex) { Log("mapsync: 点按钮出错: " + ex.Message); }
        }

        // ==================================================================
        // 大厅内「地图同步」选择框（房主）
        //   点「同步地图」弹出：可以只推当前地图，也可以选中多张一起推。
        //   控件直接挂在大厅上、贴图从聊天框/底部按钮借，不用自建面板——
        //   之前那套 DarkeningPanel + ChatListBox 在这个客户端里画不出来（全透明）。
        // ==================================================================

        static object mapGuiPanel, mapGuiList, mapGuiHint;
        static object mapGuiLobby;
        // 过滤档位：0 = 只列对方缺的（默认）；1 = 只看第三方图；2 = 全部（含官方）
        static int mapGuiFilter = 0;
        static readonly string[] mapGuiFilterText = { "只缺图", "只看第三方", "查看全部" };

        // 客机上报的"我有哪些图"（键 = 相对路径|大小）。只有收到过清单，"只缺图"才有意义。
        static readonly HashSet<string> peerHaveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool peerHaveReceived;
        static object peerHaveLobby;

        static string lastScanRoot = "";
        static readonly HashSet<string> mapGuiPicked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly List<string> mapGuiRels = new List<string>();

        static void AddChildTo(object parent, object child)
        {
            if (parent == null || child == null) return;
            MethodInfo add = FindMethod(parent.GetType(), "AddChild", 1);
            if (add != null) add.Invoke(parent, new object[] { child });
        }

        static void OpenMapSyncGui(object lobby)
        {
            try
            {
                if (lobby == null) return;
                if (!ReferenceEquals(mapGuiLobby, lobby))
                {
                    mapGuiPanel = null; mapGuiList = null; mapGuiHint = null; mapGuiLobby = lobby;
                    mapGuiPicked.Clear();
                }
                if (mapGuiPanel == null) BuildMapSyncGui(lobby);
                if (mapGuiPanel == null) { Log("mapsync: 建不出选择框，直接推当前图"); StartMapSync(lobby); return; }

                // 先问一遍"客机有哪些图"，这样列表能只列对方缺的（省得误推、白花流量）
                if (!ReferenceEquals(peerHaveLobby, lobby) || !peerHaveReceived)
                {
                    peerHaveLobby = lobby;
                    peerHaveKeys.Clear();
                    peerHaveReceived = false;
                    SendCtrlToPeers(lobby, CtrlFrame(MapHaveQKind, ""));
                    Log("mapsync: 已向客机请求本地地图清单");
                }

                RefreshMapGui();
                SetMember(mapGuiPanel, "Visible", true);
                Log("mapsync: 已打开地图同步选择框（可选 " + mapGuiRels.Count + " 张）");
            }
            catch (Exception ex) { Log("mapsync: 打开选择框失败: " + ex.Message); }
        }

        static void CloseMapSyncGui()
        {
            try { if (mapGuiPanel != null) SetMember(mapGuiPanel, "Visible", false); } catch { }
        }

        static void BuildMapSyncGui(object lobby)
        {
            try
            {
                object wm = GetMember(lobby, "WindowManager");
                Type panelType = FindType("Rampastring.XNAUI.XNAControls.XNAPanel");
                Type listType = FindType("Rampastring.XNAUI.XNAControls.XNAMultiColumnListBox");
                Type lblType = FindType("Rampastring.XNAUI.XNAControls.XNALabel")
                            ?? FindType("ClientGUI.XNALabel");
                Type btnType = FindType("ClientGUI.XNAClientButton");
                if (wm == null || panelType == null || listType == null || btnType == null)
                {
                    Log("mapsync: 选择框缺少控件类型");
                    return;
                }

                int W = 640, H = 400;
                int lw = GetInt(lobby, "Width", 1024), lh = GetInt(lobby, "Height", 720);
                int X = (lw - W) / 2, Y = (lh - H) / 2;
                if (X < 0) X = 0;
                if (Y < 0) Y = 0;

                object panel = Activator.CreateInstance(panelType, new object[] { wm });
                SetMember(panel, "Name", "panMoMapGui");
                SetMember(panel, "X", X);
                SetMember(panel, "Y", Y);
                SetMember(panel, "Width", W);
                SetMember(panel, "Height", H);

                // 背景贴图借用聊天框的：保证有实底，不会整块透明
                object chat = GetMember(lobby, "lbChatMessages");
                if (chat != null)
                {
                    object bg = GetMember(chat, "BackgroundTexture");
                    if (bg != null) SetMember(panel, "BackgroundTexture", bg);
                    object dm = GetMember(chat, "DrawMode");
                    if (dm != null) SetMember(panel, "DrawMode", dm);
                }
                AddChildTo(lobby, panel);

                if (lblType != null)
                {
                    object title = Activator.CreateInstance(lblType, new object[] { wm });
                    SetMember(title, "Text", "地图同步");
                    SetMember(title, "X", 14);
                    SetMember(title, "Y", 10);
                    AddChildTo(panel, title);

                    object hint = Activator.CreateInstance(lblType, new object[] { wm });
                    SetMember(hint, "Text", "选一行后点「加入选中」可以攒多张；也可以直接「推送当前地图」");
                    SetMember(hint, "X", 14);
                    SetMember(hint, "Y", 34);
                    AddChildTo(panel, hint);
                    mapGuiHint = hint;
                }

                object list = Activator.CreateInstance(listType, new object[] { wm });
                SetMember(list, "Name", "lbMoMapGui");
                SetMember(list, "X", 14);
                SetMember(list, "Y", 58);
                SetMember(list, "Width", W - 28);
                SetMember(list, "Height", H - 58 - 42);
                AddChildTo(panel, list);

                MethodInfo addCol = listType.GetMethod("AddColumn", new Type[] { typeof(string), typeof(int) });
                if (addCol != null)
                {
                    addCol.Invoke(list, new object[] { "选", 34 });
                    addCol.Invoke(list, new object[] { "地图", 230 });
                    addCol.Invoke(list, new object[] { "大小", 80 });
                    addCol.Invoke(list, new object[] { "位置", W - 28 - 34 - 230 - 80 - 12 });
                }
                mapGuiList = list;

                // 按钮外观照抄大厅底部那个「锁定房间」
                object refBtn = GetMember(lobby, "btnLockGame") ?? GetMember(lobby, "btnLaunchGame");
                int by = H - 34;
                AddOptionButton(panel, wm, refBtn, "btnMoGuiPushCur", "推送当前地图", 14, by, 116, 23, "MapGuiPushCurrent");
                AddOptionButton(panel, wm, refBtn, "btnMoGuiAdd", "加入选中", 138, by, 84, 23, "MapGuiAddPicked");
                AddOptionButton(panel, wm, refBtn, "btnMoGuiRemove", "移出选中", 228, by, 84, 23, "MapGuiRemovePicked");
                AddOptionButton(panel, wm, refBtn, "btnMoGuiPushSel", "推送已选", 318, by, 96, 23, "MapGuiPushPicked");
                AddOptionButton(panel, wm, refBtn, "btnMoGuiClose", "关闭", 420, by, 72, 23, "MapGuiClose");
                AddOptionButton(panel, wm, refBtn, "btnMoGuiFilter", mapGuiFilterText[mapGuiFilter], 500, by, 110, 23, "MapGuiToggleFilter");

                SetMember(panel, "Visible", false);
                mapGuiPanel = panel;
                Log("mapsync: 选择框已建好");
            }
            catch (Exception ex)
            {
                Log("mapsync: 建选择框失败: " + ex.Message);
                mapGuiPanel = null;
            }
        }

        static void RefreshMapGui()
        {
            try
            {
                if (mapGuiList == null) return;

                MethodInfo clear = FindMethod(mapGuiList.GetType(), "ClearItems", 0);
                if (clear != null) clear.Invoke(mapGuiList, null);
                mapGuiRels.Clear();

                string root = MapLibraryRoot(SafeGetGamePath());
                // 0=只缺图 / 1=只看第三方 都排除官方图；2=全部
                List<string> lib = ScanMapLibrary(root, mapGuiFilter != 2);

                MethodInfo addItem = mapGuiList.GetType().GetMethod("AddItem", new Type[] { typeof(string[]), typeof(bool) });
                if (addItem == null) { Log("mapsync: 选择框找不到 AddItem"); return; }

                bool missingOnly = (mapGuiFilter == 0 && peerHaveReceived);

                foreach (string rel in lib)
                {
                    string full = Path.Combine(root, rel.Replace('/', '\\'));
                    long size = 0;
                    try { FileInfo fi = new FileInfo(full); if (fi.Exists) size = fi.Length; } catch { }

                    // "只缺图"：客机已经有同名同大小的，就不列出来
                    if (missingOnly && peerHaveKeys.Contains(rel + "|" + size)) continue;

                    string name = Path.GetFileNameWithoutExtension(rel);
                    string dir = Path.GetDirectoryName(rel) ?? "";
                    string mark = mapGuiPicked.Contains(rel) ? "√" : "";
                    addItem.Invoke(mapGuiList, new object[] { new string[] { mark, name, FormatSize(size), dir }, true });
                    mapGuiRels.Add(rel);
                }

                if (mapGuiRels.Count == 0)
                {
                    string why;
                    if (missingOnly) why = "（对方已有全部第三方地图，没什么要推的）";
                    else why = "（没找到地图文件，根=" + lastScanRoot + "）";
                    addItem.Invoke(mapGuiList, new object[] { new string[] { "", why, "", "" }, true });
                }

                if (mapGuiHint != null)
                {
                    string filterName = mapGuiFilterText[mapGuiFilter];
                    if (mapGuiFilter == 0 && !peerHaveReceived)
                        filterName = "只缺图（还没收到对方清单，等同只看第三方）";

                    SetMember(mapGuiHint, "Text", mapGuiRels.Count == 0
                        ? "列表为空：" + filterName + "；已选 " + mapGuiPicked.Count + " 张"
                        : "已选 " + mapGuiPicked.Count + " 张，列表 " + mapGuiRels.Count + " 张（" + filterName + "）");
                }
            }
            catch (Exception ex) { Log("mapsync: 刷新选择框失败: " + ex.Message); }
        }

        /// <summary>客机收到"你有哪些图"：把自己第三方地图的 路径+大小 压缩后回给房主。</summary>
        static void OnMapHaveQ(object lobby)
        {
            try
            {
                string root = MapLibraryRoot(SafeGetGamePath());
                List<string> lib = ScanMapLibrary(root, true);   // 官方那几百张不发了，没必要
                StringBuilder sb = new StringBuilder(lib.Count * 48);
                foreach (string rel in lib)
                {
                    long size = 0;
                    try
                    {
                        FileInfo fi = new FileInfo(Path.Combine(root, rel.Replace('/', '\\')));
                        if (fi.Exists) size = fi.Length;
                    }
                    catch { }
                    sb.Append(rel).Append('|').Append(size).Append('\n');
                }

                byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());
                byte[] packed = Deflate(raw);
                string payload = packed.Length + ":" + raw.Length + ":" + ToBase91(packed, 0, packed.Length);
                SendCtrlTo(lobby, "", CtrlFrame(MapHaveKind, payload));
                Log("mapsync: 已上报本地地图清单 " + lib.Count + " 张（" + payload.Length + " 字符）");
            }
            catch (Exception ex) { Log("mapsync: 上报地图清单失败: " + ex.Message); }
        }

        /// <summary>房主收到客机的地图清单：存起来，"只缺图"过滤用它。</summary>
        static void OnMapHave(object lobby, string from, string payload)
        {
            try
            {
                string[] head = (payload ?? "").Split(new char[] { ':' }, 3);
                if (head.Length < 3) { Log("mapsync: 地图清单格式不对"); return; }

                int packedLen = int.Parse(head[0]);
                int rawLen = int.Parse(head[1]);
                byte[] packed = FromBase91(head[2], packedLen);
                string text = Encoding.UTF8.GetString(Inflate(packed, rawLen));

                peerHaveKeys.Clear();
                foreach (string line in text.Split('\n'))
                {
                    string s = line.Trim();
                    if (s.Length == 0) continue;
                    peerHaveKeys.Add(s);
                }
                peerHaveReceived = true;

                Log("mapsync: 收到 '" + from + "' 的地图清单 " + peerHaveKeys.Count + " 张");
                if (mapGuiPanel != null) RefreshMapGui();
            }
            catch (Exception ex) { Log("mapsync: 解析地图清单失败: " + ex.Message); }
        }

        static string MapGuiSelectedRel()
        {
            try
            {
                if (mapGuiList == null) return null;
                int idx = GetInt(mapGuiList, "SelectedIndex", -1);
                if (idx < 0 || idx >= mapGuiRels.Count) return null;
                return mapGuiRels[idx];
            }
            catch { return null; }
        }

        static void MapGuiPushCurrent(object sender, EventArgs e)
        {
            object lobby = mapGuiLobby;
            if (lobby == null) return;
            CloseMapSyncGui();
            StartMapSync(lobby);
        }

        static void MapGuiAddPicked(object sender, EventArgs e)
        {
            string rel = MapGuiSelectedRel();
            if (rel == null) { Log("mapsync: 选择框没有选中行"); return; }
            mapGuiPicked.Add(rel);
            RefreshMapGui();
        }

        static void MapGuiRemovePicked(object sender, EventArgs e)
        {
            string rel = MapGuiSelectedRel();
            if (rel == null) return;
            mapGuiPicked.Remove(rel);
            RefreshMapGui();
        }

        static void MapGuiPushPicked(object sender, EventArgs e)
        {
            object lobby = mapGuiLobby;
            if (lobby == null) return;

            string root = MapLibraryRoot(SafeGetGamePath());
            List<MapTarget> targets = new List<MapTarget>();
            foreach (string rel in mapGuiPicked)
            {
                string full = Path.Combine(root, rel.Replace('/', '\\'));
                if (!File.Exists(full)) continue;
                targets.Add(new MapTarget { Full = full, Name = Path.GetFileNameWithoutExtension(rel) });
            }
            if (targets.Count == 0)
            {
                Notice(lobby, "还没选地图：选一行点「加入选中」，或直接点「推送当前地图」", false);
                return;
            }
            CloseMapSyncGui();
            Log("mapsync: 从选择框推送 " + targets.Count + " 张");
            StartMapSyncTargets(lobby, targets);
        }

        static void MapGuiClose(object sender, EventArgs e) { CloseMapSyncGui(); }

        /// <summary>在「只缺图 → 只看第三方 → 查看全部」三档之间循环。</summary>
        static void MapGuiToggleFilter(object sender, EventArgs e)
        {
            mapGuiFilter = (mapGuiFilter + 1) % mapGuiFilterText.Length;
            try
            {
                object btn = FindControl(mapGuiPanel, "btnMoGuiFilter");
                if (btn != null) SetMember(btn, "Text", mapGuiFilterText[mapGuiFilter]);
            }
            catch { }
            RefreshMapGui();
        }

        /// <summary>房主收到客机的缺图报告：直接在聊天区提醒去点「同步地图」。</summary>
        static void OnMapMiss(object lobby, string from)
        {
            string who = string.IsNullOrEmpty(from) ? "有玩家" : from;
            Notice(lobby, who + " 没有当前这张地图，已阻止开局。请点「同步地图」把图发过去", false);
            Log("mapsync: " + who + " 报告缺图，已提示房主同步");
        }

        /// <summary>客机点「接收地图」：只发一个请求，清单仍由房主那边按自己选中的地图发。</summary>
        static void RequestMapSync(object lobby)
        {
            if (lobby == null) return;
            SendCtrlTo(lobby, "", CtrlFrame(MapAskKind, ""));
            Notice(lobby, "已请房主发送当前选中的地图，等对方把清单发过来…", true);
            Log("mapsync: 已向房主请求同步地图");
        }

        /// <summary>房主收到客机的同步请求：把自己选中的那张图的清单发出去。</summary>
        static void OnMapAsk(object lobby, string from)
        {
            string who = string.IsNullOrEmpty(from) ? "客机" : from;
            Log("mapsync: " + who + " 请求同步地图");

            if (mapSyncTxActive)
            {
                Notice(lobby, who + " 想同步地图，但当前正在传输，等这次传完再点一次", false);
                return;
            }

            Notice(lobby, who + " 请求同步地图，正在发送清单…", true);
            StartMapSync(lobby);
        }

        static void StartMapSync(object lobby)
        {
            if (lobby == null) return;
            if (!IsHostOf(lobby)) { Notice(lobby, "只有房主能同步地图", false); return; }

            // 选中地图：GameLobbyBase 上有 protected Map Map => GameModeMap?.Map
            object map = GetMember(lobby, "Map");
            if (map == null)
            {
                object gm = GetMember(lobby, "GameModeMap");
                map = gm == null ? null : GetMember(gm, "Map");
            }
            if (map == null) { Notice(lobby, "先在地图列表里选一张图，再点「同步地图」", false); return; }

            string root = SafeGetGamePath();
            if (string.IsNullOrEmpty(root)) { Notice(lobby, "拿不到游戏目录，无法同步", false); return; }

            string name = Convert.ToString(GetMember(map, "Name"));
            string complete = Convert.ToString(GetMember(map, "CompleteFilePath"));
            if (string.IsNullOrEmpty(complete) || !File.Exists(complete))
            {
                Notice(lobby, "这张图没有找到可发送的文件", false);
                return;
            }

            List<MapTarget> targets = new List<MapTarget>(1);
            targets.Add(new MapTarget
            {
                Full = complete,
                Name = string.IsNullOrEmpty(name) ? "这张地图" : name,
                Map = map
            });
            StartMapSyncTargets(lobby, targets);
        }

        /// <summary>一次要同步的一张图（单图同步 / 设置页多选批量同步都走这里）。</summary>
        class MapTarget
        {
            public string Full;    // 地图文件绝对路径
            public string Name;    // 显示名
            public object Map;     // 可选：客户端的 Map 对象（用来取 PreviewPath）
        }

        /// <summary>按一组地图生成一单任务并广播清单（多图时共用一条连接、一份清单）。</summary>
        static void StartMapSyncTargets(object lobby, List<MapTarget> targets)
        {
            if (lobby == null) return;
            if (!IsHostOf(lobby)) { Notice(lobby, "只有房主能同步地图", false); return; }

            string root = SafeGetGamePath();
            if (string.IsNullOrEmpty(root)) { Notice(lobby, "拿不到游戏目录，无法同步", false); return; }
            if (targets == null || targets.Count == 0) { Notice(lobby, "先选一张图，再点「同步地图」", false); return; }

            List<MapFile> files = new List<MapFile>();
            int mapCount = 0;
            string firstName = "这张地图";

            foreach (MapTarget t in targets)
            {
                List<MapFile> one = CollectTargetFiles(root, t);
                if (one.Count == 0) { Log("mapsync: 跳过没有文件的图 " + t.Name); continue; }
                if (mapCount == 0) firstName = t.Name;
                mapCount++;

                foreach (MapFile f in one)
                {
                    bool dup = false;
                    foreach (MapFile have in files)
                        if (string.Equals(have.Rel, f.Rel, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) files.Add(f);
                }
            }
            if (files.Count == 0) { Notice(lobby, "没有找到可发送的文件", false); return; }

            // 先压一遍：地图文件里有大量重复/零填充，deflate 常能压到一半以下
            // （实测 4.4 MB 的图压到 219 KB，2.0 MB 的压到 1.2 MB）。
            long txTotal = 0;
            foreach (MapFile f in files)
            {
                try
                {
                    string full = Path.Combine(root, f.Rel.Replace('/', '\\'));
                    if (!File.Exists(full) || f.Size > MapPackMaxBytes) { txTotal += f.Size; continue; }

                    byte[] raw = File.ReadAllBytes(full);
                    byte[] packed = Deflate(raw);
                    if (packed.Length < raw.Length - raw.Length / 12)     // 至少省 8% 才用
                    {
                        f.Packed = packed;
                        f.PackedSize = packed.Length;
                    }
                    txTotal += f.PackedSize > 0 ? f.PackedSize : f.Size;
                }
                catch (Exception ex)
                {
                    Log("mapsync: 压缩失败 " + f.Rel + ": " + ex.Message);
                    txTotal += f.Size;
                }
            }

            MapJob job = new MapJob();
            job.Id = DateTime.Now.Ticks.ToString("x");
            job.Name = mapCount <= 1 ? firstName : (mapCount + " 张地图");
            job.Files = files;
            // 兼容 v1 的全局段字段：取第一张图的
            foreach (MapFile f in files) if (!string.IsNullOrEmpty(f.IniSection)) { job.IniSection = f.IniSection; break; }

            long total = 0;
            foreach (MapFile f in files) total += f.Size;      // 原始大小（弹窗里给人看的）
            job.Total = total;
            hostMapJob = job;

            mapSyncCancelRequested = false;
            mapSyncTxActive = false;
            mapSyncTxBytes = 0;
            mapSyncTxTotal = txTotal;                          // 进度按"实际要发多少字节"算

            // v2 清单：每个文件自带自己的注册段（多图批量同步时各带各的）
            StringBuilder sb = new StringBuilder();
            sb.Append("v2").Append(MapField).Append(job.Id).Append(MapField)
              .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(job.Name))).Append(MapField)
              .Append(total).Append(MapField).Append(files.Count);
            foreach (MapFile f in files)
            {
                sb.Append(MapField)
                  .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(f.Rel)))
                  .Append('\t').Append(f.Size).Append('\t').Append(f.Sha)
                  .Append('\t').Append(f.PackedSize)                       // 0 = 未压缩
                  .Append('\t').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(f.IniSection ?? "")));
            }

            SendCtrlToPeers(lobby, CtrlFrame(MapHelloKind, sb.ToString()));
            Notice(lobby, "已把「" + job.Name + "」的清单发给客机（" + files.Count + " 个文件，"
                + FormatSize(total) + "，走隧道约 " + FormatSize(EstTraffic(txTotal)) + " 流量）", true);
            Log("mapsync: 发出清单 id=" + job.Id + " 图=" + mapCount + " 文件=" + files.Count
                + " 原始=" + total + " B 压缩后=" + txTotal + " B");
        }

        /// <summary>收集一张图的文件：地图本体 + 同目录同名的附属文件（+ 可选预览图）。</summary>
        static List<MapFile> CollectTargetFiles(string root, MapTarget t)
        {
            List<MapFile> result = new List<MapFile>();
            try
            {
                if (t == null || string.IsNullOrEmpty(t.Full) || !File.Exists(t.Full)) return result;

                AddMapFile(result, t.Full, root);

                string dir = Path.GetDirectoryName(t.Full);
                string stem = Path.GetFileNameWithoutExtension(t.Full);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (string f in Directory.GetFiles(dir))
                    {
                        if (!string.Equals(Path.GetFileNameWithoutExtension(f), stem,
                                StringComparison.OrdinalIgnoreCase)) continue;
                        AddMapFile(result, f, root);
                    }
                }

                if (t.Map != null)
                {
                    string preview = Convert.ToString(GetMember(t.Map, "PreviewPath"));
                    if (!string.IsNullOrEmpty(preview))
                    {
                        string p = Path.IsPathRooted(preview) ? preview : Path.Combine(root, preview);
                        AddMapFile(result, p, root);
                    }
                }

                // 这张图的注册段挂到它的 .map 条目上（客机缺这段的话文件对了列表里也看不到）
                string mapRel = RelativeToRoot(t.Full, root);
                if (!string.IsNullOrEmpty(mapRel))
                {
                    string sec = ReadMapIniSection(root, mapRel);
                    foreach (MapFile f in result)
                        if (string.Equals(f.Rel, mapRel, StringComparison.OrdinalIgnoreCase)) f.IniSection = sec;
                }
            }
            catch (Exception ex) { Log("mapsync: 收集文件失败: " + ex.Message); }
            return result;
        }

        /// <summary>
        /// 收集「这一张图」需要的全部文件：
        ///   1) 地图文件本身（CompleteFilePath，可能在 Maps\Custom 也可能在别的目录）；
        ///   2) 同目录同名的其它文件（.ini / 预览图等）；
        ///   3) Map.PreviewPath 指向的文件（如果存在且在游戏目录内）。
        /// </summary>
        static List<MapFile> CollectMapFiles(object map, string root)
        {
            List<MapFile> result = new List<MapFile>();

            try
            {
                string complete = Convert.ToString(GetMember(map, "CompleteFilePath"));
                if (string.IsNullOrEmpty(complete) || !File.Exists(complete))
                {
                    Log("mapsync: 地图文件不存在: " + (complete == null ? "(null)" : complete));
                    return result;
                }

                AddMapFile(result, complete, root);

                string dir = Path.GetDirectoryName(complete);
                string stem = Path.GetFileNameWithoutExtension(complete);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (string f in Directory.GetFiles(dir))
                    {
                        if (!string.Equals(Path.GetFileNameWithoutExtension(f), stem,
                                StringComparison.OrdinalIgnoreCase)) continue;
                        AddMapFile(result, f, root);
                    }
                }

                string preview = Convert.ToString(GetMember(map, "PreviewPath"));
                if (!string.IsNullOrEmpty(preview))
                {
                    string p = Path.IsPathRooted(preview) ? preview : Path.Combine(root, preview);
                    AddMapFile(result, p, root);
                }
            }
            catch (Exception ex) { Log("mapsync: 收集文件失败: " + ex.Message); }

            return result;
        }

        static void AddMapFile(List<MapFile> list, string full, string root)
        {
            try
            {
                if (string.IsNullOrEmpty(full) || !File.Exists(full)) return;

                string rel = RelativeToRoot(full, root);
                if (rel == null) return;
                foreach (MapFile f in list) if (string.Equals(f.Rel, rel, StringComparison.OrdinalIgnoreCase)) return;

                // 省流量：预览图（同名的 .png/.jpg 等）默认不发。
                // 它只是地图列表里的缩略图，缺了不影响开局；要发就 SyncPreview=1。
                if (!syncPreviewEnabled && IsPreviewImage(rel))
                {
                    Log("mapsync: 跳过预览图 " + rel + "（省流量；需要的话在 MoLanPatch.ini 里设 SyncPreview=1）");
                    return;
                }

                MapFile mf = new MapFile();
                mf.Rel = rel;
                mf.Size = new FileInfo(full).Length;
                mf.Sha = Sha256Of(full);
                list.Add(mf);
            }
            catch (Exception ex) { Log("mapsync: 读文件失败 " + full + ": " + ex.Message); }
        }

        static bool IsPreviewImage(string rel)
        {
            string ext = (Path.GetExtension(rel) ?? "").ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif";
        }

        /// <summary>把绝对路径转成「相对游戏根目录、'/' 分隔」；不在游戏目录内则返回 null。</summary>
        static string RelativeToRoot(string full, string root)
        {
            try
            {
                string f = Path.GetFullPath(full);
                string r = Path.GetFullPath(root);
                if (!r.EndsWith("\\")) r += "\\";
                if (!f.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return null;
                return f.Substring(r.Length).Replace('\\', '/');
            }
            catch { return null; }
        }

        // ---------------- MentalOmegaMaps.ini 的注册段 ----------------
        //
        // MapsMO\ 底下的图不是靠"扫描目录"登记的，而是靠 INI\MentalOmegaMaps.ini 里的
        // 一个段（段名就是相对路径去掉扩展名，如 [MapsMO\Defend\GuardPresident]）。
        // 客机缺图时这段通常也没有，所以只把 .map/.png 传过去，地图列表里仍然看不到。

        /// <summary>地图对应的 ini 段名，如 "MapsMO\Defend\GuardPresident"。</summary>
        static string MapIniSectionName(string rel)
        {
            int dot = rel.LastIndexOf('.');
            string stem = dot > 0 ? rel.Substring(0, dot) : rel;
            return stem.Replace('/', '\\');
        }

        /// <summary>房主：从自己的 INI\MentalOmegaMaps.ini 里取出这张图的注册段（含段名行）。</summary>
        static string ReadMapIniSection(string root, string rel)
        {
            try
            {
                string ini = Path.Combine(root, "INI\\MentalOmegaMaps.ini");
                if (!File.Exists(ini)) return "";

                string wanted = "[" + MapIniSectionName(rel) + "]";
                StringBuilder sb = new StringBuilder();
                bool inSection = false;

                foreach (string raw in File.ReadAllLines(ini, Encoding.UTF8))
                {
                    string line = raw == null ? "" : raw.Trim();
                    if (line.StartsWith("["))
                    {
                        if (inSection) break;                       // 到下一个段就收工
                        if (string.Equals(line, wanted, StringComparison.OrdinalIgnoreCase))
                        {
                            inSection = true;
                            sb.AppendLine(line);
                            continue;
                        }
                    }
                    if (inSection) sb.AppendLine(raw);
                }

                return inSection ? sb.ToString() : "";
            }
            catch (Exception ex)
            {
                Log("mapsync: 读注册段失败: " + ex.Message);
                return "";
            }
        }

        /// <summary>客机：本地 ini 里没有这段就追加进去（已有则不动，免得覆盖别的地图信息）。</summary>
        static bool ApplyMapIniSection(string root, string rel, string sectionText)
        {
            try
            {
                string ini = Path.Combine(root, "INI\\MentalOmegaMaps.ini");
                if (!File.Exists(ini)) { Log("mapsync: 本地没有 INI\\MentalOmegaMaps.ini，无法注册地图"); return false; }

                string wanted = "[" + MapIniSectionName(rel) + "]";
                string sectionName = MapIniSectionName(rel);
                string text = File.ReadAllText(ini, Encoding.UTF8);
                bool changed = false;

                // (1) 地图自己的段：[MapsMO\Defend\Xxx]
                bool hasSection = false;
                foreach (string line in text.Split('\n'))
                {
                    if (string.Equals(line.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) { hasSection = true; break; }
                }

                if (!hasSection && !string.IsNullOrEmpty(sectionText))
                {
                    string tail = text.EndsWith("\n") ? "" : "\r\n";
                    File.AppendAllText(ini, tail + sectionText.TrimEnd() + "\r\n", new UTF8Encoding(false));
                    Log("mapsync: 已把 " + wanted + " 注册段写入 INI");
                    text = File.ReadAllText(ini, Encoding.UTF8);
                    changed = true;
                }
                else if (hasSection)
                {
                    Log("mapsync: 本地 INI 已有 " + wanted + "，不重复写入");
                }

                // (2) [MultiMaps] 里的登记行（LoadMultiMaps 只遍历这里的键，光有上面的段没用）
                if (AddMultiMapsEntry(ini, sectionName))
                {
                    Log("mapsync: 已在 [MultiMaps] 里登记 " + sectionName);
                    changed = true;
                }
                else if (!HasMultiMapsEntry(ini, sectionName))
                {
                    Log("mapsync: [MultiMaps] 段不存在或登记失败，地图可能仍然不显示");
                }

                return changed;
            }
            catch (Exception ex)
            {
                Log("mapsync: 写注册段失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>[MultiMaps] 里是否已经有这条地图。</summary>
        static bool HasMultiMapsEntry(string ini, string sectionName)
        {
            try
            {
                foreach (string raw in File.ReadAllLines(ini, Encoding.UTF8))
                {
                    string line = raw == null ? "" : raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (string.Equals(line.Substring(eq + 1).Trim(), sectionName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>往 [MultiMaps] 段末尾插一行「序号 = 地图段名」；已有则返回 false。</summary>
        static bool AddMultiMapsEntry(string ini, string sectionName)
        {
            try
            {
                List<string> lines = new List<string>(File.ReadAllLines(ini, Encoding.UTF8));
                int start = -1, end = -1;

                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i] == null ? "" : lines[i].Trim();
                    if (!t.StartsWith("[")) continue;
                    if (start < 0)
                    {
                        if (string.Equals(t, "[MultiMaps]", StringComparison.OrdinalIgnoreCase)) start = i;
                    }
                    else { end = i; break; }
                }

                if (start < 0) return false;
                if (end < 0) end = lines.Count;

                int maxIdx = -1;
                for (int i = start + 1; i < end; i++)
                {
                    string t = lines[i] == null ? "" : lines[i].Trim();
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;

                    string val = t.Substring(eq + 1).Trim();
                    if (string.Equals(val, sectionName, StringComparison.OrdinalIgnoreCase)) return false;   // 已有

                    int idx;
                    if (int.TryParse(t.Substring(0, eq).Trim(), out idx) && idx > maxIdx) maxIdx = idx;
                }

                lines.Insert(end, (maxIdx + 1) + "\t=\t" + sectionName);
                File.WriteAllLines(ini, lines, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Log("mapsync: 写 [MultiMaps] 失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 同步完成后把这张图注册进大厅并刷新地图列表，不必重启客户端。
        /// 用 LoadMultiMaps 重读 ini（这样 Description/Briefing/预览尺寸等字段才完整），
        /// 但它内部不判重，所以调用后必须按 CompleteFilePath 去重，否则列表里每张图都会翻倍。
        /// </summary>
        static bool RefreshMapListAfterSync(object lobby, string root, MapJob job, string rel)
        {
            try
            {
                // 同一房间里同一张图只刷一次：客机反复点「接收地图」时不必反复重建列表
                if (ReferenceEquals(lastMapRefreshLobby, lobby)
                    && string.Equals(lastMapRefreshRel, rel, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                object loader = GetMember(lobby, "MapLoader");
                if (loader == null) loader = GetMember(lobby, "mapLoader");
                if (loader == null) { Log("mapsync: 找不到 MapLoader，跳过热刷新（重启客户端后可见）"); return false; }

                string relName = MapIniSectionName(rel);       // MapsMO\Defend\GuardPresident
                string full = Path.Combine(root, rel.Replace('/', '\\'));
                if (!File.Exists(full)) { Log("mapsync: 热刷新时地图文件不存在: " + full); return false; }

                // 不用 LoadCustomMap：它只解析 .map 本身，ini 里的 Description / Briefing /
                // PreviewSize / 任务文本全都拿不到（表现就是"能进游戏但没字幕"）。
                // 改成重新跑一遍 LoadMultiMaps，让它按 ini 注册段建出完整的 Map 对象。
                Type iniType = FindType("Rampastring.Tools.IniFile");
                if (iniType == null) { Log("mapsync: 找不到 IniFile 类型，跳过热刷新"); return false; }

                string iniPath = Path.Combine(root, "INI\\MentalOmegaMaps.ini");
                if (!File.Exists(iniPath)) { Log("mapsync: 找不到 " + iniPath); return false; }

                MethodInfo loadMulti = FindMethod(loader.GetType(), "LoadMultiMaps", 1);
                if (loadMulti == null) { Log("mapsync: 找不到 LoadMultiMaps，跳过热刷新"); return false; }

                object ini = Activator.CreateInstance(iniType, new object[] { iniPath });
                loadMulti.Invoke(loader, new object[] { ini });

                // LoadMultiMaps 会把整份 ini 重新注册一遍（它内部不判重），
                // 所以下面按 CompleteFilePath 去重：保留先出现的（原有对象），删掉后来加进来的副本。
                IList gmList = GetMember(loader, "GameModes") as IList;
                if (gmList == null) { Log("mapsync: 取不到 GameModes，跳过热刷新"); return false; }

                foreach (object gm in gmList)
                {
                    if (gm == null) continue;
                    IList maps = GetMember(gm, "Maps") as IList;
                    if (maps == null) continue;

                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = maps.Count - 1; i >= 0; i--)
                    {
                        object m = maps[i];
                        string key = Convert.ToString(GetMember(m, "CompleteFilePath"));
                        if (string.IsNullOrEmpty(key)) key = Convert.ToString(GetMember(m, "Name"));
                        if (string.IsNullOrEmpty(key)) continue;
                        if (!seen.Add(key)) maps.RemoveAt(i);
                    }
                }

                // 在注册表里找到刚加进来的这张图（按相对路径匹配），设成客机当前选图。
                // 不设的话 Map 仍是 null，开局会被 WriteSpawnIni 的 Map.IsCoop() 打崩。
                object target = null;
                object firstMode = null;
                string wantPath = Path.GetFullPath(full);
                foreach (object gm in gmList)
                {
                    if (gm == null) continue;
                    IList maps = GetMember(gm, "Maps") as IList;
                    if (maps == null) continue;
                    foreach (object m in maps)
                    {
                        string mp = Convert.ToString(GetMember(m, "CompleteFilePath"));
                        if (string.IsNullOrEmpty(mp)) continue;
                        if (!string.Equals(Path.GetFullPath(mp), wantPath, StringComparison.OrdinalIgnoreCase)) continue;
                        target = m;
                        firstMode = gm;
                        break;
                    }
                    if (target != null) break;
                }

                if (target == null)
                {
                    Log("mapsync: ini 里没找到 " + relName + " 的注册项，跳过热刷新");
                    return false;
                }

                // 设 Map/GameMode 期间临时关掉"游戏选项变更广播"，免得客机反向把房主的选图改掉
                SetMember(lobby, "disableGameOptionUpdateBroadcast", true);
                try
                {
                    SetMember(lobby, "GameMode", firstMode);
                    SetMember(lobby, "Map", target);
                }
                finally
                {
                    SetMember(lobby, "disableGameOptionUpdateBroadcast", false);
                }

                // 重建地图列表（真正显示新图的地方）
                MethodInfo listMaps = FindMethod(lobby.GetType(), "ListMaps", 0);
                if (listMaps != null)
                {
                    try { listMaps.Invoke(lobby, null); }
                    catch (Exception ex) { Log("mapsync: ListMaps 失败: " + ex.Message); }
                }

                // 再刷一次模式下拉框
                MethodInfo refresh = FindMethod(lobby.GetType(), "RefreshMapSelectionUI", 0);
                if (refresh != null)
                {
                    try { refresh.Invoke(lobby, null); }
                    catch (Exception ex) { Log("mapsync: 刷新地图列表失败: " + ex.Message); }
                }

                Log("mapsync: 已热刷新地图列表（" + relName + "），无需重启客户端");
                lastMapRefreshLobby = lobby;
                lastMapRefreshRel = rel;
                return true;
            }
            catch (Exception ex)
            {
                Log("mapsync: 热刷新失败（重启客户端后可见）: " + ex.Message);
                return false;
            }
        }
        static string Sha256Of(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(fs));
            }
        }

        static string Sha256Of(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(data));
            }
        }

        static string ToHex(byte[] hash)
        {
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        // ---------------- deflate：地图文件压缩/解压 ----------------
        // 地图文件里大量重复/零填充，deflate 很划算（实测 4.4 MB → 219 KB、2.0 MB → 1.2 MB）。
        // 只在真能省 8% 以上时才压；.png 这类已经压过的会原样发。

        static byte[] Deflate(byte[] data)
        {
            using (MemoryStream outMs = new MemoryStream())
            {
                using (DeflateStream ds = new DeflateStream(outMs, CompressionMode.Compress, true))
                    ds.Write(data, 0, data.Length);
                return outMs.ToArray();
            }
        }

        static byte[] Inflate(byte[] data, int expected)
        {
            using (MemoryStream inMs = new MemoryStream(data))
            using (DeflateStream ds = new DeflateStream(inMs, CompressionMode.Decompress))
            using (MemoryStream outMs = new MemoryStream(expected > 0 ? expected : 1 << 20))
            {
                byte[] buf = new byte[64 * 1024];
                int n;
                while ((n = ds.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n);
                return outMs.ToArray();
            }
        }

        // ---------------- Ascii85：比 Base64 少约 6% 流量 ----------------
        // 樱花 Frp 是按流量算的：Base64 每 3 字节变 4 个字符（+33%），
        // Ascii85 每 4 字节变 5 个字符（+25%），传输同样的数据少 6~7% 的字节。
        // 字符范围是 '!'(33) ~ 'u'(117)，全是可打印 ASCII，不含 \u0001/\u0002，安全。

        static string ToAscii85(byte[] data, int offset, int count)
        {
            StringBuilder sb = new StringBuilder((count / 4 + 1) * 5);
            int end = offset + count;
            char[] c = new char[5];

            for (int i = offset; i < end; i += 4)
            {
                int n = Math.Min(4, end - i);
                uint v = 0;
                for (int k = 0; k < 4; k++) v = (v << 8) | (uint)(k < n ? data[i + k] : 0);
                for (int k = 4; k >= 0; k--) { c[k] = (char)(v % 85 + 33); v /= 85; }
                sb.Append(c, 0, n + 1);
            }
            return sb.ToString();
        }

        // ---------------- Base91：比 Base64 省 10% 左右、比 Ascii85 再省 1.6% ----------------
        // 樱花 Frp 按流量计费，能省就省：Base64 膨胀 33%、Ascii85 25%、Base91 约 23%。
        // 字母表里没有 \u0001/\u0002，不会破坏大厅的文本分帧。

        const string B91Alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!#$%&()*+,./:;<=>?@[]^_`{|}~\"";

        static readonly byte[] B91Rev = BuildB91Rev();

        static byte[] BuildB91Rev()
        {
            byte[] rev = new byte[128];
            for (int i = 0; i < rev.Length; i++) rev[i] = 0xFF;
            for (int i = 0; i < B91Alphabet.Length; i++) rev[B91Alphabet[i]] = (byte)i;
            return rev;
        }

        static string ToBase91(byte[] data, int offset, int count)
        {
            StringBuilder sb = new StringBuilder((count * 23 / 20) + 4);
            int b = 0, n = 0;
            int end = offset + count;

            for (int i = offset; i < end; i++)
            {
                b |= data[i] << n;
                n += 8;
                if (n > 13)
                {
                    int v = b & 8191;
                    if (v > 88) { b >>= 13; n -= 13; }
                    else { v = b & 16383; b >>= 14; n -= 14; }
                    sb.Append(B91Alphabet[v % 91]).Append(B91Alphabet[v / 91]);
                }
            }
            if (n > 0) sb.Append(B91Alphabet[b % 91]);
            if (n > 7 || b > 90) sb.Append(B91Alphabet[b / 91]);

            return sb.ToString();
        }

        static byte[] FromBase91(string s, int expectedLen)
        {
            byte[] outBuf = new byte[expectedLen];
            int oi = 0;
            int b = 0, n = 0, v = -1;

            foreach (char ch in s)
            {
                int c = ch < 128 ? B91Rev[ch] : 0xFF;
                if (c == 0xFF) throw new FormatException("Base91 非法字符: " + (int)ch);

                if (v < 0) { v = c; }
                else
                {
                    v += c * 91;
                    b |= v << n;
                    n += (v & 8191) > 88 ? 13 : 14;
                    while (n > 7)
                    {
                        if (oi < outBuf.Length) outBuf[oi++] = (byte)(b & 0xFF);
                        b >>= 8;
                        n -= 8;
                    }
                    v = -1;
                }
            }
            if (v > -1 && oi < outBuf.Length) outBuf[oi++] = (byte)((b | (v << n)) & 0xFF);
            return outBuf;
        }

        static byte[] FromAscii85(string s, int expectedLen)
        {
            byte[] outBuf = new byte[expectedLen];
            byte[] tmp = new byte[4];
            int oi = 0, i = 0;

            while (i < s.Length && oi < expectedLen)
            {
                int n = Math.Min(5, s.Length - i);
                ulong v = 0;          // 用 ulong 累加：85^5 超过 uint 上限，避免溢出回绕
                for (int k = 0; k < 5; k++)
                {
                    int c = (k < n ? s[i + k] - 33 : 84);
                    if (c < 0 || c > 84) throw new FormatException("Ascii85 字符越界: " + c);
                    v = v * 85 + (ulong)c;
                }
                for (int k = 3; k >= 0; k--) { tmp[k] = (byte)(v & 0xFF); v >>= 8; }

                int emit = Math.Min(n - 1, expectedLen - oi);
                for (int k = 0; k < emit; k++) outBuf[oi++] = tmp[k];
                i += n;
            }
            return outBuf;
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        }

        /// <summary>
        /// 估算这次传输会消耗多少"隧道流量"：数据已经 deflate 过，再乘 Ascii85 的 1.25 倍，
        /// 加上每帧几十字节的协议头。樱花 Frp 按流量计费，推送前给用户一个数好判断。
        /// </summary>
        static long EstTraffic(long packedBytes)
        {
            if (packedBytes <= 0) return 0;
            return (long)(packedBytes * 1.23) + packedBytes / 200;
        }

        // ---------------- 客机侧 ----------------

        static void OnMapHello(object lobby, string from, string payload)
        {
            try
            {
                string[] parts = payload.Split(MapField);
                if (parts.Length < 5) return;

                bool v2 = string.Equals(parts[0], "v2", StringComparison.OrdinalIgnoreCase);

                MapJob job = new MapJob();
                job.Id = parts[1];
                job.Name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                job.Total = long.Parse(parts[3]);
                int count = int.Parse(parts[4]);

                for (int i = 0; i < count && 5 + i < parts.Length; i++)
                {
                    string[] rec = parts[5 + i].Split('\t');
                    if (rec.Length < 3) continue;

                    MapFile f = new MapFile();
                    f.Rel = Encoding.UTF8.GetString(Convert.FromBase64String(rec[0]));
                    f.Size = long.Parse(rec[1]);
                    f.Sha = rec[2];
                    if (rec.Length >= 4) long.TryParse(rec[3], out f.PackedSize);   // 0 或空 = 未压缩
                    if (v2 && rec.Length >= 5 && rec[4].Length > 0)
                    {
                        try { f.IniSection = Encoding.UTF8.GetString(Convert.FromBase64String(rec[4])); }
                        catch { f.IniSection = ""; }
                    }
                    job.Files.Add(f);
                }

                // v1 的清单：注册段单独放在最后一段，挂到第一张图的 .map 上
                if (!v2 && parts.Length > 5 + count)
                {
                    try { job.IniSection = Encoding.UTF8.GetString(Convert.FromBase64String(parts[5 + count])); }
                    catch { job.IniSection = ""; }
                    if (job.Files.Count > 0) job.Files[0].IniSection = job.IniSection;
                }

                string root = SafeGetGamePath();
                List<int> missing = new List<int>();
                Dictionary<int, long> resume = new Dictionary<int, long>();
                long need = 0;

                for (int i = 0; i < job.Files.Count; i++)
                {
                    if (MapFileMatches(root, job.Files[i])) continue;
                    missing.Add(i);

                    long total = job.Files[i].PackedSize > 0 ? job.Files[i].PackedSize : job.Files[i].Size;
                    long have = PartLength(root, job.Files[i]);

                    // 断点续传：上次没收完的那部分留着，这次只补剩下的字节
                    if (have > 0 && have < total)
                    {
                        resume[i] = have;
                        need += total - have;
                        Log("mapsync: 断点续传 " + job.Files[i].Rel + " 已有 " + have + "/" + total + " B");
                    }
                    else
                    {
                        if (have >= total && have > 0) DeletePart(root, job.Files[i]);   // 旧的半成品不可信，重来
                        need += total;
                    }
                }

                if (missing.Count == 0)
                {
                    // 文件都在，但注册段可能缺（缺图的机器往往两样都缺）：
                    // 补写 ini 后直接热刷新，用户不用重启也能在列表里看到。
                    bool iniAdded0 = ApplySectionsFromJob(root, job);
                    string firstRel0 = FirstMapRel(job);
                    bool refreshed0 = firstRel0.Length > 0 && RefreshMapListAfterSync(lobby, root, job, firstRel0);

                    string msg0 = "地图「" + job.Name + "」与房主一致，不需要同步";
                    if (refreshed0) msg0 += "（地图列表已刷新）";
                    else if (iniAdded0) msg0 += "（注册信息已补写，重启客户端后可见）";

                    PostUi(delegate { Notice(lobby, msg0, true); });
                    Log("mapsync: 本地已齐全，忽略清单 id=" + job.Id + "（iniAdded=" + iniAdded0 + " refreshed=" + refreshed0 + "）");
                    return;
                }

                job.Req = missing;
                job.Resume = resume;

                pendingMapJob = job;
                pendingMapLobby = lobby;
                pendingMapMissing = missing;

                MapJob mj = job;
                List<int> ms = missing;
                long nb = need;
                PostUi(delegate { ShowMapSyncDialog(lobby, mj, ms, nb); });
                Log("mapsync: 收到清单「" + job.Name + "」，缺 " + missing.Count + " 个文件 / " + need + " B");
            }
            catch (Exception ex) { Log("mapsync: 处理清单失败: " + ex.Message); }
        }

        // ---------------- 断点续传：未收完的分片存在 Resources\MoMapPart\<sha>.part ----------------

        static string PartDir(string root)
        {
            return Path.Combine(root, "Resources\\MoMapPart");
        }

        static string PartPath(string root, MapFile f)
        {
            string sha = (f.Sha ?? "").ToUpperInvariant();
            if (sha.Length > 32) sha = sha.Substring(0, 32);
            return Path.Combine(PartDir(root), sha + ".part");
        }

        static long PartLength(string root, MapFile f)
        {
            try
            {
                string p = PartPath(root, f);
                return File.Exists(p) ? new FileInfo(p).Length : 0;
            }
            catch { return 0; }
        }

        static void DeletePart(string root, MapFile f)
        {
            try { string p = PartPath(root, f); if (File.Exists(p)) File.Delete(p); } catch { }
        }

        /// <summary>把清单里每个文件自带的注册段写进 INI（多图批量时会有多段）。</summary>
        static bool ApplySectionsFromJob(string root, MapJob job)
        {
            bool any = false;
            foreach (MapFile f in job.Files)
            {
                if (string.IsNullOrEmpty(f.IniSection)) continue;
                if (ApplyMapIniSection(root, f.Rel, f.IniSection)) any = true;
            }
            return any;
        }

        static string FirstMapRel(MapJob job)
        {
            foreach (MapFile f in job.Files)
            {
                string ext = (Path.GetExtension(f.Rel) ?? "").ToLowerInvariant();
                if (ext == ".map" || ext == ".mpr" || ext == ".yrm") return f.Rel;
            }
            return job.Files.Count > 0 ? job.Files[0].Rel : "";
        }

        static bool MapFileMatches(string root, MapFile f)
        {
            try
            {
                string full = Path.Combine(root, f.Rel.Replace('/', '\\'));
                if (!File.Exists(full)) return false;
                if (new FileInfo(full).Length != f.Size) return false;
                return string.Equals(Sha256Of(full), f.Sha, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static void ShowMapSyncDialog(object lobby, MapJob job, List<int> missing, long need)
        {
            try
            {
                object wm = GetMember(lobby, "WindowManager");
                if (wm == null) { Log("mapsync: 拿不到 WindowManager"); return; }

                Type boxType = FindType("ClientGUI.XNAMessageBox");
                if (boxType == null) { Log("mapsync: 找不到 XNAMessageBox"); return; }

                MethodInfo showYesNo = null;
                foreach (MethodInfo mi in boxType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (mi.Name == "ShowYesNoDialog" && mi.GetParameters().Length == 3) { showYesNo = mi; break; }
                }
                if (showYesNo == null) { Log("mapsync: 找不到 ShowYesNoDialog"); return; }

                // 弹窗正文只有一行，必须短。大小按"原始大小"报，
                // 压缩后更小就顺带写一句，免得用户以为图变小了。
                long raw = 0;
                foreach (int i in missing) if (i >= 0 && i < job.Files.Count) raw += job.Files[i].Size;
                string text = "同步「" + job.Name + "」？" + missing.Count + " 个文件 / " + FormatSize(raw)
                    + (need > 0 ? "（走隧道约 " + FormatSize(EstTraffic(need)) + " 流量）" : "");
                object box = showYesNo.Invoke(null, new object[] { wm, "地图同步", text });
                if (box == null) { Log("mapsync: 弹窗创建失败"); return; }

                SetMember(box, "YesClickedAction", MakeBoxAction(boxType, "MapSyncDialogYes"));
                SetMember(box, "NoClickedAction", MakeBoxAction(boxType, "MapSyncDialogNo"));
            }
            catch (Exception ex) { Log("mapsync: 弹窗失败: " + ex.Message); }
        }

        static void MapSyncDialogYes(object box)
        {
            MapJob job = pendingMapJob;
            object lobby = pendingMapLobby;
            List<int> missing = pendingMapMissing;
            pendingMapJob = null;
            pendingMapLobby = null;
            pendingMapMissing = null;

            if (job == null || lobby == null || missing == null) return;

            clientMapJobs[job.Id] = job;

            long need = 0;
            foreach (int i in missing)
            {
                long total = job.Files[i].PackedSize > 0 ? job.Files[i].PackedSize : job.Files[i].Size;
                long have = 0;
                if (job.Resume != null) job.Resume.TryGetValue(i, out have);
                need += total - have;
            }
            mapSyncRxActive = true;
            mapSyncRxBytes = 0;
            mapSyncRxTotal = need;
            mapSyncRxStartedAt = DateTime.Now;

            // 请求格式：id | idx:偏移, idx:偏移, ...（没有偏移就是 0，也就是从头收）
            StringBuilder sb = new StringBuilder();
            sb.Append(job.Id).Append(MapField);
            for (int i = 0; i < missing.Count; i++)
            {
                if (i > 0) sb.Append(',');
                int idx = missing[i];
                long off = 0;
                if (job.Resume != null) job.Resume.TryGetValue(idx, out off);
                sb.Append(idx);
                if (off > 0) sb.Append(':').Append(off);
            }

            SendCtrlTo(lobby, "", CtrlFrame(MapReqKind, sb.ToString()));
            Log("mapsync: 用户已接收，向房主请求 " + missing.Count + " 个文件（含断点续传）");
        }

        static void MapSyncDialogNo(object box)
        {
            pendingMapJob = null;
            pendingMapLobby = null;
            pendingMapMissing = null;
            Log("mapsync: 用户拒绝接收");
        }

        static void OnMapData(object lobby, string from, string payload)
        {
            try
            {
                string[] p = payload.Split(MapField);
                if (p.Length < 5) return;

                string id = p[0];
                int idx = int.Parse(p[1]);
                int off = int.Parse(p[2]);
                int len = int.Parse(p[3]);

                MapJob job;
                if (!clientMapJobs.TryGetValue(id, out job)) return;
                if (idx < 0 || idx >= job.Files.Count) return;

                MapFile f = job.Files[idx];
                byte[] bytes = FromBase91(p[4], len);

                // 直接落到 Resources\MoMapPart\<sha>.part：断线/取消后这部分不用重传
                string root = SafeGetGamePath();
                string part = PartPath(root, f);
                string dir = Path.GetDirectoryName(part);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using (FileStream fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                {
                    if (fs.Length != off)
                    {
                        if (fs.Length > off) return;      // 重发的旧分片，忽略
                        Log("mapsync: 分片偏移不符（磁盘 " + fs.Length + "，收到 " + off + "），丢弃");
                        return;
                    }
                    if (off == 0)
                        Log("mapsync: 开始接收 " + f.Rel + "（" + f.Size + " B"
                            + (f.PackedSize > 0 ? "，压缩后 " + f.PackedSize + " B" : "") + "）");

                    fs.Seek(off, SeekOrigin.Begin);
                    fs.Write(bytes, 0, bytes.Length);
                }
                mapSyncRxBytes += bytes.Length;
            }
            catch (Exception ex) { Log("mapsync: 接收分片失败: " + ex.Message); }
        }

        static void OnMapEnd(object lobby, string from, string payload)
        {
            try
            {
                string[] p = payload.Split(MapField);
                if (p.Length < 1) return;
                string id = p[0];

                MapJob job;
                if (!clientMapJobs.TryGetValue(id, out job)) return;

                mapSyncRxActive = false;

                string root = SafeGetGamePath();
                int ok = 0, bad = 0;

                List<int> req = job.Req;
                if (req == null)
                {
                    req = new List<int>();
                    for (int i = 0; i < job.Files.Count; i++) req.Add(i);
                }

                foreach (int i in req)
                {
                    if (i < 0 || i >= job.Files.Count) continue;
                    MapFile f = job.Files[i];

                    long total = f.PackedSize > 0 ? f.PackedSize : f.Size;
                    long have = PartLength(root, f);
                    if (have != total)
                    {
                        // 不完整：留着，下次点「接收地图」自动接着收
                        bad++;
                        Log("mapsync: 文件不完整 " + f.Rel + "（" + have + "/" + total + " B，已保留待续传）");
                        continue;
                    }

                    byte[] data;
                    try { data = File.ReadAllBytes(PartPath(root, f)); }
                    catch (Exception ex)
                    {
                        bad++;
                        Log("mapsync: 读临时文件失败 " + f.Rel + ": " + ex.Message);
                        continue;
                    }

                    if (f.PackedSize > 0)
                    {
                        try { data = Inflate(data, (int)f.Size); }
                        catch (Exception ex)
                        {
                            bad++;
                            DeletePart(root, f);
                            Log("mapsync: 解压失败 " + f.Rel + ": " + ex.Message);
                            continue;
                        }
                    }

                    if (data.Length != f.Size ||
                        !string.Equals(Sha256Of(data), f.Sha, StringComparison.OrdinalIgnoreCase))
                    {
                        bad++;
                        DeletePart(root, f);      // 内容不对，别留着污染下次续传
                        Log("mapsync: 校验失败 " + f.Rel + "（长度 " + data.Length + "/" + f.Size + "）");
                        continue;
                    }

                    try
                    {
                        WriteMapFile(root, f.Rel, data);
                        DeletePart(root, f);
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        bad++;
                        Log("mapsync: 写入失败 " + f.Rel + ": " + ex.Message);
                    }
                }

                clientMapJobs.Remove(id);

                string name = job.Name;
                string firstRel = FirstMapRel(job);

                // 注册段（多图时逐个写）+ 热刷新：文件写对了但 ini 里没登记，地图列表照样看不到
                bool iniAdded = ApplySectionsFromJob(root, job);
                bool refreshed = bad == 0 && firstRel.Length > 0
                                 && RefreshMapListAfterSync(lobby, root, job, firstRel);

                string msg;
                if (bad == 0)
                {
                    msg = "地图「" + name + "」已同步（" + ok + " 个文件）"
                        + (refreshed ? "，地图列表已刷新"
                                     : (iniAdded ? "，注册信息已补写，重启客户端后可见" : "，重启客户端后可见"));
                }
                else
                {
                    msg = "「" + name + "」有 " + bad + " 个文件没传完（已保留，下次接着收），详见 MoLanRelay.log";
                }
                PostUi(delegate { Notice(lobby, msg, bad == 0); });
                double rxSecs = mapSyncRxStartedAt == DateTime.MinValue
                    ? 0 : (DateTime.Now - mapSyncRxStartedAt).TotalSeconds;
                double rxKbps = rxSecs > 0.001 ? (mapSyncRxBytes / 1024.0 / rxSecs) : 0;
                mapSyncRxStartedAt = DateTime.MinValue;
                Log("mapsync: 接收完成 ok=" + ok + " bad=" + bad + " root=" + root
                    + " 用时=" + rxSecs.ToString("0.0") + "s 速度=" + rxKbps.ToString("0") + " KB/s");
            }
            catch (Exception ex) { Log("mapsync: 收尾失败: " + ex.Message); }
        }

        static void WriteMapFile(string root, string rel, byte[] data)
        {
            string clean = (rel ?? "").Replace('/', '\\').TrimStart('\\');
            if (clean.Length == 0 || clean.Contains("..")) throw new IOException("路径不合法: " + rel);

            string full = Path.GetFullPath(Path.Combine(root, clean));
            string rootFull = Path.GetFullPath(root);
            if (!rootFull.EndsWith("\\")) rootFull += "\\";
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new IOException("路径越界: " + rel);

            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = full + ".mosync-part";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(full)) File.Delete(full);
            File.Move(tmp, full);

            RecordSyncedMap(root, clean);
            Log("mapsync: 已写入 " + rel + "（" + data.Length + " B）");
        }

        // ---------------- 已同步地图的清单与清理 ----------------
        //
        // 只记录"本机通过地图同步接收到的文件"。清理时只删这些，
        // 用户自己放进去的地图一概不碰 —— 这是这个功能的安全边界。

        static readonly object syncedMapLock = new object();

        static string SyncedMapListPath(string root)
        {
            return Path.Combine(root, "Resources\\MoLanSyncedMaps.txt");
        }

        static void RecordSyncedMap(string root, string rel)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(rel)) return;
                string file = SyncedMapListPath(root);
                lock (syncedMapLock)
                {
                    if (File.Exists(file))
                    {
                        foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                            if (string.Equals((raw ?? "").Trim(), rel, StringComparison.OrdinalIgnoreCase)) return;
                    }
                    else
                    {
                        try { Directory.CreateDirectory(Path.GetDirectoryName(file)); } catch { }
                        File.WriteAllText(file, "", new UTF8Encoding(false));
                    }
                    File.AppendAllText(file, rel + "\r\n", new UTF8Encoding(false));
                }
            }
            catch (Exception ex) { Log("clearmaps: 记录清单失败: " + ex.Message); }
        }

        static List<string> LoadSyncedMapList(string root)
        {
            List<string> list = new List<string>();
            try
            {
                string file = SyncedMapListPath(root);
                if (!File.Exists(file)) return list;
                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string s = (raw ?? "").Replace('/', '\\').Trim().TrimStart('\\');
                    if (s.Length == 0 || s.Contains("..")) continue;

                    bool dup = false;
                    foreach (string have in list)
                        if (string.Equals(have, s, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) list.Add(s);
                }
            }
            catch (Exception ex) { Log("clearmaps: 读清单失败: " + ex.Message); }
            return list;
        }

        /// <summary>从 INI\MentalOmegaMaps.ini 里删掉这张图的注册段和 [MultiMaps] 登记行。</summary>
        static bool RemoveMapIniSection(string root, string rel)
        {
            try
            {
                string ini = Path.Combine(root, "INI\\MentalOmegaMaps.ini");
                if (!File.Exists(ini)) return false;

                string sectionName = MapIniSectionName(rel);
                string wanted = "[" + sectionName + "]";
                List<string> lines = new List<string>(File.ReadAllLines(ini, Encoding.UTF8));
                bool changed = false;

                // (1) 删段：从段名行删到下一个 '[' 之前
                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i] == null ? "" : lines[i].Trim();
                    if (!string.Equals(t, wanted, StringComparison.OrdinalIgnoreCase)) continue;

                    int end = i + 1;
                    while (end < lines.Count)
                    {
                        string n = lines[end] == null ? "" : lines[end].Trim();
                        if (n.StartsWith("[")) break;
                        end++;
                    }
                    lines.RemoveRange(i, end - i);
                    changed = true;
                    break;
                }

                // (2) 删 [MultiMaps] 里的登记行
                int start = -1, stop = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i] == null ? "" : lines[i].Trim();
                    if (!t.StartsWith("[")) continue;
                    if (start < 0)
                    {
                        if (string.Equals(t, "[MultiMaps]", StringComparison.OrdinalIgnoreCase)) start = i;
                    }
                    else { stop = i; break; }
                }
                if (start >= 0)
                {
                    if (stop < 0) stop = lines.Count;
                    for (int i = stop - 1; i > start; i--)
                    {
                        string t = lines[i] == null ? "" : lines[i].Trim();
                        int eq = t.IndexOf('=');
                        if (eq <= 0) continue;
                        if (string.Equals(t.Substring(eq + 1).Trim(), sectionName, StringComparison.OrdinalIgnoreCase))
                        {
                            lines.RemoveAt(i);
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    File.WriteAllLines(ini, lines, new UTF8Encoding(false));
                    Log("clearmaps: 已从 INI 里移除 " + wanted);
                }
                return changed;
            }
            catch (Exception ex)
            {
                Log("clearmaps: 清理注册段失败(" + rel + "): " + ex.Message);
                return false;
            }
        }

        /// <summary>设置页「清理已同步地图」按钮：先弹确认框，确认后才真删。</summary>
        static void AskCleanSyncedMaps(object owner)
        {
            try
            {
                string root = SafeGetGamePath();
                List<string> rels = LoadSyncedMapList(root);
                if (rels.Count == 0)
                {
                    ShowMessageBox(topBar, "清理已同步地图", "还没有通过地图同步接收过地图。");
                    return;
                }

                object wm = GetMember(owner, "WindowManager");
                Type boxType = FindType("ClientGUI.XNAMessageBox");
                if (wm == null || boxType == null) { Log("clearmaps: 弹不出确认框"); return; }

                MethodInfo showYesNo = null;
                foreach (MethodInfo mi in boxType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (mi.Name == "ShowYesNoDialog" && mi.GetParameters().Length == 3) { showYesNo = mi; break; }
                }
                if (showYesNo == null) { Log("clearmaps: 找不到 ShowYesNoDialog"); return; }

                string text = "本机通过同步接收过 " + rels.Count + " 个地图文件，全部删除?";
                object box = showYesNo.Invoke(null, new object[] { wm, "清理已同步地图", text });
                if (box == null) { Log("clearmaps: 确认框创建失败"); return; }

                bool okYes = SetMember(box, "YesClickedAction", MakeBoxAction(boxType, "CleanMapsYes"));
                bool okNo = SetMember(box, "NoClickedAction", MakeBoxAction(boxType, "CleanMapsNo"));
                if (!okYes || !okNo)
                {
                    Log("clearmaps: 按钮动作绑定失败，关掉确认框");
                    try { CallMethod(box, "Hide"); } catch { }
                }
            }
            catch (Exception ex) { Log("clearmaps: 失败: " + ex.Message); }
        }

        static void CleanMapsYes(object box)
        {
            try { CallMethod(box, "Hide"); } catch { }
            CleanSyncedMaps();
        }

        static void CleanMapsNo(object box)
        {
            try { CallMethod(box, "Hide"); } catch { }
            Log("clearmaps: 用户取消");
        }

        static void CleanSyncedMaps()
        {
            try
            {
                string root = SafeGetGamePath();
                List<string> rels = LoadSyncedMapList(root);
                if (rels.Count == 0) return;

                int files = 0, failed = 0, maps = 0;

                foreach (string rel in rels)
                {
                    string ext = Path.GetExtension(rel) ?? "";
                    if (ext.Equals(".map", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".mpr", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".yrm", StringComparison.OrdinalIgnoreCase))
                        maps++;

                    if (DeleteSyncedFile(root, rel)) files++;
                    else failed++;
                }

                try { File.Delete(SyncedMapListPath(root)); } catch { }

                Log("clearmaps: 删除 " + files + " 个文件 / " + maps + " 张地图，失败 " + failed);
                ShowMessageBox(topBar, "清理已同步地图",
                    "已删除 " + files + " 个文件（" + maps + " 张地图）"
                    + (failed > 0 ? "，" + failed + " 个删除失败（可能被占用）" : "")
                    + "。\n\n地图列表要重启客户端后才会更新。");
            }
            catch (Exception ex) { Log("clearmaps: 清理失败: " + ex.Message); }
        }

        /// <summary>删一个同步来的文件（连同 .mosync-part、INI 注册段、空目录）。</summary>
        static bool DeleteSyncedFile(string root, string rel)
        {
            try
            {
                string full = Path.Combine(root, rel);
                bool ok = true;

                try { if (File.Exists(full + ".mosync-part")) File.Delete(full + ".mosync-part"); } catch { }
                if (File.Exists(full))
                {
                    try { File.Delete(full); }
                    catch (Exception ex) { ok = false; Log("clearmaps: 删除失败 " + rel + ": " + ex.Message); }
                }

                string ext = Path.GetExtension(rel) ?? "";
                if (ext.Equals(".map", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".mpr", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".yrm", StringComparison.OrdinalIgnoreCase))
                    RemoveMapIniSection(root, rel);

                // 顺手删掉因为同步而建出来的空目录（只删空目录，删不掉就留着）
                string dir = Path.GetDirectoryName(full);
                while (!string.IsNullOrEmpty(dir) && dir.Length > root.Length
                       && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(dir).Length > 0) break;
                        Directory.Delete(dir);
                    }
                    catch { break; }
                    dir = Path.GetDirectoryName(dir);
                }
                return ok;
            }
            catch (Exception ex)
            {
                Log("clearmaps: 删除失败 " + rel + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>从清单里去掉一行的相对路径。</summary>
        static void RemoveSyncedMapEntry(string root, string rel)
        {
            try
            {
                string file = SyncedMapListPath(root);
                if (!File.Exists(file)) return;

                List<string> keep = new List<string>();
                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    string s = (raw ?? "").Trim();
                    if (s.Length == 0) continue;
                    if (string.Equals(s, rel, StringComparison.OrdinalIgnoreCase)) continue;
                    keep.Add(s);
                }
                File.WriteAllLines(file, keep.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("clearmaps: 更新清单失败: " + ex.Message); }
        }

        // ---------------- 设置窗口「地图」页：已同步地图清单 ----------------

        static object mapTabPanel;
        static object mapTabList;
        static readonly List<string> mapTabRels = new List<string>();
        static DateTime lastMapTabRefresh = DateTime.MinValue;

        static void BuildMapTab(object win, object panel)
        {
            try
            {
                object wm = GetMember(win, "WindowManager");
                if (wm == null) { Log("clearmaps: 拿不到 WindowManager"); return; }
                MethodInfo addChild = FindMethod(panel.GetType(), "AddChild", 1);
                if (addChild == null) return;

                int pw = GetInt(panel, "Width", 552);
                int ph = GetInt(panel, "Height", 342);
                if (pw < 300) pw = 552;
                if (ph < 200) ph = 342;

                // 说明文字
                Type lblType = FindType("Rampastring.XNAUI.XNAControls.XNALabel");
                if (lblType != null)
                {
                    object lbl = Activator.CreateInstance(lblType, new object[] { wm });
                    SetMember(lbl, "Name", "lblMoMapTabHint");
                    SetMember(lbl, "Text", "本机通过「同步地图」接收到的文件（只删这些，自己的地图不受影响）");
                    SetMember(lbl, "X", 16);
                    SetMember(lbl, "Y", 12);
                    addChild.Invoke(panel, new object[] { lbl });
                }

                // 清单
                Type listType = FindType("Rampastring.XNAUI.XNAControls.XNAMultiColumnListBox");
                if (listType == null) { Log("clearmaps: 找不到 XNAMultiColumnListBox"); return; }

                object list = Activator.CreateInstance(listType, new object[] { wm });
                SetMember(list, "Name", "lbMoMapTab");
                SetMember(list, "X", 16);
                SetMember(list, "Y", 40);
                SetMember(list, "Width", pw - 32);
                SetMember(list, "Height", ph - 40 - 44);
                addChild.Invoke(panel, new object[] { list });

                MethodInfo addCol = listType.GetMethod("AddColumn", new Type[] { typeof(string), typeof(int) });
                if (addCol != null)
                {
                    int lw = pw - 32;
                    addCol.Invoke(list, new object[] { "文件", lw * 42 / 100 });
                    addCol.Invoke(list, new object[] { "大小", lw * 16 / 100 });
                    addCol.Invoke(list, new object[] { "位置", lw * 42 / 100 });
                }
                mapTabList = list;

                // 按钮：外观照抄设置窗口自己的「保存」按钮，位置按它的实际尺寸排
                object refBtn = FindControl(win, "btnSave") ?? FindControl(win, "btnCancel");
                int bw = GetInt(refBtn, "Width", 92);
                int bh = GetInt(refBtn, "Height", 25);
                if (bw <= 0) bw = 92;
                if (bh <= 0) bh = 25;
                int by = ph - bh - 10;
                int bx = 16;
                AddOptionButton(panel, wm, refBtn, "btnMoMapOpen", "打开目录", bx, by, bw, bh, "OnMapTabOpenFolder"); bx += bw + 8;
                AddOptionButton(panel, wm, refBtn, "btnMoMapPreview", "看预览", bx, by, bw, bh, "OnMapTabPreview"); bx += bw + 8;
                AddOptionButton(panel, wm, refBtn, "btnMoMapDeleteSel", "删除选中", bx, by, bw, bh, "OnMapTabDeleteSel"); bx += bw + 8;
                AddOptionButton(panel, wm, refBtn, "btnMoMapDeleteAll", "全部删除", bx, by, bw, bh, "OnMapTabDeleteAll"); bx += bw + 8;
                AddOptionButton(panel, wm, refBtn, "btnMoMapRefresh", "刷新", bx, by, bw, bh, "OnMapTabRefresh");

                mapTabPanel = panel;
                RefreshMapTab(true);
                Log("options: 「地图」页已就绪");
            }
            catch (Exception ex) { Log("clearmaps: 建地图页失败: " + ex.Message); }
        }

        static void RefreshMapTab(bool force)
        {
            try
            {
                if (mapTabList == null) return;
                if (!force && (DateTime.Now - lastMapTabRefresh).TotalMilliseconds < 700) return;
                lastMapTabRefresh = DateTime.Now;

                string root = SafeGetGamePath();
                List<string> rels = LoadSyncedMapList(root);

                MethodInfo clear = FindMethod(mapTabList.GetType(), "ClearItems", 0);
                if (clear != null) clear.Invoke(mapTabList, null);
                mapTabRels.Clear();

                MethodInfo addItem = mapTabList.GetType().GetMethod("AddItem", new Type[] { typeof(string[]), typeof(bool) });
                if (addItem == null) { Log("clearmaps: 找不到 AddItem(string[])"); return; }

                foreach (string rel in rels)
                {
                    string name = Path.GetFileName(rel);
                    string dir = Path.GetDirectoryName(rel) ?? "";
                    long size = 0;
                    try
                    {
                        FileInfo fi = new FileInfo(Path.Combine(root, rel.Replace('/', '\\')));
                        if (fi.Exists) size = fi.Length;
                    }
                    catch { }
                    addItem.Invoke(mapTabList, new object[] { new string[] { name, FormatSize(size), dir }, true });
                    mapTabRels.Add(rel);
                }
                if (rels.Count == 0)
                    addItem.Invoke(mapTabList, new object[] { new string[] { "（还没有同步接收过地图）", "", "" }, true });
            }
            catch (Exception ex) { Log("clearmaps: 刷新列表失败: " + ex.Message); }
        }

        /// <summary>设置窗口开着「地图」页时每帧调一次（内部有 700ms 节流）。</summary>
        static void MapTabTick()
        {
            try
            {
                if (mapTabPanel == null || mapTabList == null) return;
                object vis = GetMember(mapTabPanel, "Visible");
                if (!(vis is bool) || !(bool)vis) return;
                RefreshMapTab(false);
            }
            catch { }
        }

        static void OnMapTabDeleteSel(object sender, EventArgs e) { DeleteSelectedSyncedMap(); }

        static void OnMapTabDeleteAll(object sender, EventArgs e)
        {
            AskCleanSyncedMaps(optionsPatchedWindow ?? topBar);
            lastMapTabRefresh = DateTime.MinValue;      // 让下一帧重画
        }

        static void OnMapTabRefresh(object sender, EventArgs e) { RefreshMapTab(true); }

        static string MapTabSelectedFull()
        {
            if (mapTabList == null) return null;
            int idx = GetInt(mapTabList, "SelectedIndex", -1);
            if (idx < 0 || idx >= mapTabRels.Count) return null;
            string root = SafeGetGamePath();
            if (string.IsNullOrEmpty(root)) return null;
            string full = Path.Combine(root, mapTabRels[idx].Replace('/', '\\'));
            return File.Exists(full) ? full : null;
        }

        /// <summary>在资源管理器里选中这一行对应的文件。</summary>
        static void OnMapTabOpenFolder(object sender, EventArgs e)
        {
            try
            {
                string full = MapTabSelectedFull();
                if (full == null) { Log("clearmaps: 打开目录时没有选中有效文件"); return; }
                Process.Start("explorer.exe", "/select,\"" + full + "\"");
            }
            catch (Exception ex) { Log("clearmaps: 打开目录失败: " + ex.Message); }
        }

        /// <summary>用系统默认看图工具打开同名预览图（没有就提示）。</summary>
        static void OnMapTabPreview(object sender, EventArgs e)
        {
            try
            {
                string full = MapTabSelectedFull();
                if (full == null) { Log("clearmaps: 看预览时没有选中有效文件"); return; }

                string png = Path.ChangeExtension(full, ".png");
                if (!File.Exists(png))
                {
                    Log("clearmaps: 没有预览图 " + Path.GetFileName(png));
                    return;
                }
                Process.Start(png);
            }
            catch (Exception ex) { Log("clearmaps: 看预览失败: " + ex.Message); }
        }

        static void DeleteSelectedSyncedMap()
        {
            try
            {
                if (mapTabList == null) return;

                int idx = GetInt(mapTabList, "SelectedIndex", -1);
                if (idx < 0 || idx >= mapTabRels.Count)
                {
                    Log("clearmaps: 没有选中任何一行");
                    return;
                }

                string rel = mapTabRels[idx];
                string root = SafeGetGamePath();
                DeleteSyncedFile(root, rel);
                RemoveSyncedMapEntry(root, rel);
                Log("clearmaps: 已删除选中 " + rel);
                RefreshMapTab(true);
            }
            catch (Exception ex) { Log("clearmaps: 删除选中失败: " + ex.Message); }
        }

        // ---------------- 房主侧发送 ----------------

        static void OnMapReq(object lobby, string from, string payload)
        {
            try
            {
                string[] p = payload.Split(MapField);
                if (p.Length < 2) return;

                MapJob job = hostMapJob;
                if (job == null || !string.Equals(job.Id, p[0], StringComparison.OrdinalIgnoreCase))
                {
                    Log("mapsync: 收到请求但这一单已失效（id=" + p[0] + "）");
                    return;
                }

                List<int> idx = new List<int>();
                Dictionary<int, long> offs = new Dictionary<int, long>();
                foreach (string raw in p[1].Split(','))
                {
                    string s = (raw ?? "").Trim();
                    if (s.Length == 0) continue;

                    int colon = s.IndexOf(':');
                    int v; long off = 0;
                    if (colon > 0)
                    {
                        if (!int.TryParse(s.Substring(0, colon), out v)) continue;
                        long.TryParse(s.Substring(colon + 1), out off);
                    }
                    else if (!int.TryParse(s, out v)) continue;

                    if (v < 0 || v >= job.Files.Count) continue;
                    idx.Add(v);
                    if (off > 0) offs[v] = off;
                }
                if (idx.Count == 0) return;

                Log("mapsync: " + from + " 请求 " + idx.Count + " 个文件，开始发送"
                    + (offs.Count > 0 ? "（" + offs.Count + " 个断点续传）" : ""));
                SendMapTo(lobby, from, job, idx, offs);
            }
            catch (Exception ex) { Log("mapsync: 处理请求失败: " + ex.Message); }
        }

        static void SendMapTo(object lobby, string target, MapJob job, List<int> idx,
                              Dictionary<int, long> offs)
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    string root = SafeGetGamePath();
                    int done = 0;
                    Stopwatch sw = Stopwatch.StartNew();   // 用来算实际速度（KB/s），排查慢的时候看日志

                    mapSyncCancelRequested = false;
                    mapSyncTxActive = true;
                    mapSyncTxBytes = 0;
                    mapSyncTxTotal = 0;
                    foreach (int i in idx)
                    {
                        long t = job.Files[i].PackedSize > 0 ? job.Files[i].PackedSize : job.Files[i].Size;
                        long have = 0;
                        if (offs != null) offs.TryGetValue(i, out have);
                        mapSyncTxTotal += Math.Max(0, t - have);
                    }

                    foreach (int i in idx)
                    {
                        if (mapSyncCancelRequested) { Log("mapsync: 已取消，停止发送"); break; }

                        MapFile f = job.Files[i];
                        string full = Path.Combine(root, f.Rel.Replace('/', '\\'));
                        if (!File.Exists(full))
                        {
                            Log("mapsync: 源文件不见了 " + f.Rel);
                            continue;
                        }

                        // 有压好的就用压好的；这一单没缓存（比如换了图）就现压一次
                        byte[] data;
                        if (f.PackedSize > 0 && f.Packed != null) data = f.Packed;
                        else if (f.PackedSize > 0) data = Deflate(File.ReadAllBytes(full));
                        else data = File.ReadAllBytes(full);

                        // 断点续传：客机说已经收到 off 字节了，从这里接着发
                        int off = 0;
                        long startOff = 0;
                        if (offs != null) offs.TryGetValue(i, out startOff);
                        if (startOff > 0)
                        {
                            if (startOff >= data.Length) off = data.Length;
                            else { off = (int)startOff; Log("mapsync: 从 " + off + " B 处续传 " + f.Rel); }
                        }

                        while (off < data.Length)
                        {
                            if (mapSyncCancelRequested) break;

                            int n = data.Length - off;
                            if (n > MapChunkBytes) n = MapChunkBytes;

                            string chunk = ToBase91(data, off, n);
                            // 多带一个 len，客机按它精确还原（Ascii85 末尾组是变长的）
                            string payload = job.Id + MapField + i + MapField + off + MapField + n
                                             + MapField + chunk;
                            SendCtrlTo(lobby, target, CtrlFrame(MapDataKind, payload));

                            off += n;
                            mapSyncTxBytes += n;
                            if (MapChunkDelayMs > 0) Thread.Sleep(MapChunkDelayMs);
                        }
                        if (mapSyncCancelRequested) break;

                        done++;
                    }

                    mapSyncTxActive = false;
                    SendCtrlTo(lobby, target, CtrlFrame(MapEndKind, job.Id + MapField + done));

                    double secs = sw.Elapsed.TotalSeconds;
                    double kbps = secs > 0.001 ? (mapSyncTxBytes / 1024.0 / secs) : 0;

                    string msg = mapSyncCancelRequested
                        ? "地图同步已取消（已发 " + done + " 个）"
                        : "地图文件已发送给 " + target + "（" + done + " 个）";
                    PostUi(delegate { Notice(lobby, msg, true); });
                    Log("mapsync: 发送结束 " + done + "/" + idx.Count + " cancel=" + mapSyncCancelRequested
                        + " 用时=" + secs.ToString("0.0") + "s 速度=" + kbps.ToString("0") + " KB/s"
                        + " 分片=" + MapChunkBytes + "B");
                }
                catch (Exception ex) { Log("mapsync: 发送失败: " + ex.Message); }
            });

            t.IsBackground = true;
            t.Name = "MoMapSyncTx";
            t.Start();
        }

        // ==================================================================
        // 聊天命令、诊断包、自动准备、同名提醒、同步进度
        // ==================================================================

        static bool autoReadyEnabled = true;
        static bool dupNameWarnEnabled = true;
        static object autoReadyLobby;
        static string lastNameSignature;
        static volatile bool mapSyncTxActive;
        static volatile bool mapSyncRxActive;
        static volatile bool mapSyncCancelRequested;
        static long mapSyncTxBytes;
        static long mapSyncTxTotal;
        static long mapSyncRxBytes;
        static long mapSyncRxTotal;
        static DateTime mapSyncRxStartedAt = DateTime.MinValue;

        /// <summary>由 LANGameLobby::SendChatMessage 的补丁调用（大厅里自己敲的每一句都先过这里）。
        /// 返回 true 表示这条消息已被当成命令处理，不要再发出去。</summary>
        public static bool TryChatCommand(object lobby, string message)
        {
            if (message == null) return false;
            string cmd = message.Trim();
            if (cmd.Length == 0 || cmd[0] != '/') return false;

            try
            {
                if (string.Equals(cmd, "/diag", StringComparison.OrdinalIgnoreCase))
                {
                    ExportDiagAsync(lobby);
                    return true;
                }
                if (string.Equals(cmd, "/sync", StringComparison.OrdinalIgnoreCase))
                {
                    StartMapSync(lobby);
                    return true;
                }
                if (string.Equals(cmd, "/maps", StringComparison.OrdinalIgnoreCase))
                {
                    ListMapsForChat(lobby);
                    return true;
                }
                if (cmd.StartsWith("/push", StringComparison.OrdinalIgnoreCase))
                {
                    string arg = cmd.Substring(5).Trim();
                    PushMapsByIndex(lobby, arg);
                    return true;
                }
                if (string.Equals(cmd, "/help", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cmd, "/?", StringComparison.OrdinalIgnoreCase))
                {
                    Notice(lobby, "命令：/diag 诊断包　/sync 同步当前地图　/maps 列地图库　/push 1,3,5 批量推送", true);
                    Notice(lobby, "　　　/w 玩家 内容 私聊　/r 内容 回复私聊　/me 动作 表情动作　/room 复制房间信息", true);
                    Notice(lobby, "　　　/q 预设快捷语（/q 序号 发送）　/motd 房间公告（房主）", true);
                    Notice(lobby, "小游戏：/dice [面数] 骰子　/coin 硬币　/rps 石头|剪刀|布　/8ball 问题", true);
                    Notice(lobby, "快捷语：/gg /gl /hf /afk /brb /wait /1 /2 /3", true);
                    return true;
                }

                // ---------------- 私聊 ----------------
                if (cmd.StartsWith("/w ", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = cmd.Substring(3).Trim();
                    int sp = rest.IndexOf(' ');
                    if (sp <= 0) { Notice(lobby, "用法：/w 玩家名 内容", false); return true; }
                    SendPrivate(lobby, rest.Substring(0, sp).Trim(), rest.Substring(sp + 1).Trim());
                    return true;
                }
                if (cmd.StartsWith("/r ", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(lastPmFrom)) { Notice(lobby, "还没有人私聊你，没有可回复的对象", false); return true; }
                    SendPrivate(lobby, lastPmFrom, cmd.Substring(3).Trim());
                    return true;
                }

                // ---------------- 表情动作 ----------------
                if (cmd.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
                {
                    string act = cmd.Substring(4).Trim();
                    if (act.Length == 0) { Notice(lobby, "用法：/me 动作", false); return true; }
                    BroadcastChat(lobby, "* " + MyPlayerName() + " " + act);
                    return true;
                }

                // ---------------- 房间信息 ----------------
                if (string.Equals(cmd, "/room", StringComparison.OrdinalIgnoreCase))
                {
                    Notice(lobby, RoomInfoText(lobby), true);
                    return true;
                }

                // ---------------- 小游戏 ----------------
                string low = cmd.ToLowerInvariant();
                if (miniGamesEnabled)
                {
                    if (low == "/coin") { MiniCoin(lobby); return true; }
                    if (low == "/dice" || low.StartsWith("/dice ")) { MiniDice(lobby, cmd); return true; }
                    if (low.StartsWith("/rps ")) { MiniRps(lobby, cmd); return true; }
                    if (low.StartsWith("/8ball ")) { Mini8Ball(lobby, cmd); return true; }
                }

                // ---------------- 房间公告 ----------------
                if (low == "/motd" || low.StartsWith("/motd ")) { MotdCommand(lobby, cmd); return true; }

                // ---------------- 预设快捷语 ----------------
                if (low == "/q") { ListQuickTexts(lobby); return true; }
                if (low.StartsWith("/q "))
                {
                    SendQuickText(lobby, cmd.Substring(3).Trim());
                    return true;
                }

                // ---------------- 快捷语 ----------------
                for (int i = 0; i < QuickPhrases.Length; i += 2)
                {
                    if (low == QuickPhrases[i]) { BroadcastChat(lobby, QuickPhrases[i + 1]); return true; }
                }
            }
            catch (Exception ex) { Log("chatcmd: " + ex.Message); }

            return false;
        }

        // ---------------- 聊天增强：私聊 / 表情动作 / 快捷语 / 房间信息 ----------------

        static string lastPmFrom = "";   // 最后一个私聊我的人，/r 用它

        static readonly string[] QuickPhrases =
        {
            "/gg",   "GG（打得好）",
            "/gl",   "GL HF（好运）",
            "/hf",   "HF（玩得开心）",
            "/afk",  "AFK（我离开一下）",
            "/brb",  "马上回来",
            "/wait", "等我一下",
            "/1",    "上",
            "/2",    "撤",
            "/3",    "这里需要支援",
        };

        // ---------------- 预设快捷语（与 MOExperience 的 QuickText 互通） ----------------
        //
        // MOExperience 把预设文本存在 MOExperience.ini 的 [MsgText] QuickText 里，用 \n 分隔。
        // 我们在客户端大厅做同一件事：内置几条 MO 常用语，如果游戏目录里有那个 ini，
        // 就把它的预设合并进来 —— 装了拓展的玩家不用记两套快捷语。
        // （只读配置文本，不含对方任何代码。）

        static string[] quickTextCache;
        static DateTime quickTextCacheAt = DateTime.MinValue;
        static string quickTextSource = "内置";

        static string[] QuickTexts()
        {
            if (quickTextCache != null && (DateTime.Now - quickTextCacheAt).TotalSeconds < 60) return quickTextCache;

            List<string> list = new List<string>
            {
                "我是萌新，大佬带我",
                "禁空，不禁英雄/大碟子/机场/运输机",
                "GG（打得好）",
                "等我一下",
                "这里需要支援",
            };

            string src = "内置";
            try
            {
                string root = SafeGetGamePath();
                string ini = root.Length == 0 ? "" : Path.Combine(root, "MOExperience.ini");
                if (ini.Length > 0 && File.Exists(ini))
                {
                    string val = IniSectionValue(ReadTextSmart(ini), "MsgText", "QuickText");
                    if (val.Length > 0)
                    {
                        src = "内置 + MOExperience.ini";
                        foreach (string piece in val.Replace("\\n", "\n").Split('\n'))
                        {
                            string one = piece.Trim();
                            if (one.Length == 0 || list.Contains(one)) continue;
                            list.Add(one);
                        }
                    }
                }
            }
            catch (Exception ex) { Log("quicktext: 读取 MOExperience.ini 失败: " + ex.Message); }

            quickTextSource = src;
            quickTextCache = list.ToArray();
            quickTextCacheAt = DateTime.Now;
            return quickTextCache;
        }

        static void ListQuickTexts(object lobby)
        {
            try
            {
                string[] texts = QuickTexts();
                Notice(lobby, "预设快捷语（/q 序号 发送，来源：" + quickTextSource + "）", true);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < texts.Length; i++)
                {
                    string item = (i + 1) + "." + texts[i];
                    if (sb.Length > 0 && sb.Length + item.Length > 80)
                    {
                        Notice(lobby, sb.ToString(), true);
                        sb.Length = 0;
                    }
                    sb.Append(item).Append("  ");
                }
                if (sb.Length > 0) Notice(lobby, sb.ToString(), true);
            }
            catch (Exception ex) { Log("quicktext: 列表失败: " + ex.Message); }
        }

        static void SendQuickText(object lobby, string arg)
        {
            int n;
            if (!int.TryParse(arg, out n))
            {
                Notice(lobby, "用法：/q 查看列表，/q 序号 发送", false);
                return;
            }
            string[] texts = QuickTexts();
            if (n < 1 || n > texts.Length)
            {
                Notice(lobby, "没有第 " + n + " 条（共 " + texts.Length + " 条），/q 查看列表", false);
                return;
            }
            BroadcastChat(lobby, texts[n - 1]);
        }

        /// <summary>按字节读文本：有 BOM 认 BOM，没有就按严格 UTF-8 试，失败再按系统 ANSI（中文 GBK）。</summary>
        static string ReadTextSmart(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch { return Encoding.Default.GetString(b); }
        }

        /// <summary>取指定节下某个键的值（跳过行尾 ';' 注释）。</summary>
        static string IniSectionValue(string text, string section, string key)
        {
            bool inSection = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[')
                {
                    inSection = string.Equals(line.Trim('[', ']').Trim(), section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                string v = line.Substring(eq + 1);
                int semi = v.IndexOf(';');
                if (semi >= 0) v = v.Substring(0, semi);
                return v.Trim();
            }
            return "";
        }

        static void OnPrivateMessage(object lobby, string from, string body)
        {
            try
            {
                string who = string.IsNullOrEmpty(from) ? "有人" : from;
                lastPmFrom = who;
                Notice(lobby, who + "（私聊）：" + body, true);
                Log("pm: 收到 " + who + " 的私聊");
            }
            catch (Exception ex) { Log("pm: 显示失败: " + ex.Message); }
        }

        static void SendPrivate(object lobby, string to, string body)
        {
            SendPrivate(lobby, to, body, true);
        }

        /// <summary>quiet=false 时不打本地回显（房间公告自动下发用，免得房主聊天区被刷）。</summary>
        static void SendPrivate(object lobby, string to, string body, bool quiet)
        {
            try
            {
                if (lobby == null) return;
                to = Clean(to);
                if (to.Length == 0) { Notice(lobby, "私聊目标为空", false); return; }
                if (SameName(to, MyPlayerName())) { Notice(lobby, "不能私聊自己", false); return; }

                string payload = to + MapField + body;
                if (IsHostOf(lobby))
                {
                    if (FindPlayerIn(lobby, to) == null)
                    {
                        Notice(lobby, "找不到玩家「" + to + "」（名字要完全一致）", false);
                        return;
                    }
                    SendCtrlTo(lobby, to, CtrlFrame(PmKind, payload));
                }
                else SendCtrlTo(lobby, "", CtrlFrame(PmKind, payload));

                if (quiet) Notice(lobby, "→ " + to + "（私聊）：" + body, true);
            }
            catch (Exception ex) { Log("pm: 发送失败: " + ex.Message); }
        }

        /// <summary>给所有人发一条聊天消息（复用客户端的 GLCHAT 广播）。</summary>
        static void BroadcastChat(object lobby, string text)
        {
            try
            {
                if (lobby == null) return;
                MethodInfo bm = FindMethod(lobby.GetType(), "BroadcastMessage", 1);
                if (bm != null && bm.GetParameters()[0].ParameterType == typeof(string))
                    bm.Invoke(lobby, new object[] { "GLCHAT System\u00010\u0001" + text });
                Notice(lobby, text, false);
            }
            catch (Exception ex) { Log("chat: 广播失败: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // 小游戏
        //
        // 纯聊天命令，不碰任何协议之外的东西，结果用 BroadcastChat 发出去，
        // 房间里装没装补丁的人都看得到。
        // ------------------------------------------------------------------

        static readonly Random rng = new Random(Environment.TickCount);

        static int RandRange(int min, int maxInclusive)
        {
            lock (rng) { return rng.Next(min, maxInclusive + 1); }
        }

        static void MiniCoin(object lobby)
        {
            try
            {
                string side = RandRange(0, 1) == 0 ? "正面" : "反面";
                BroadcastChat(lobby, "【硬币】" + MyPlayerName() + " 抛出：" + side + "！");
            }
            catch (Exception ex) { Log("game: 硬币失败: " + ex.Message); }
        }

        static void MiniDice(object lobby, string cmd)
        {
            try
            {
                // /dice 或 /dice 20（面数）
                int faces = 100;
                string arg = cmd.Length > 5 ? cmd.Substring(5).Trim() : "";
                if (arg.Length > 0)
                {
                    int n;
                    if (!int.TryParse(arg, out n) || n < 2 || n > 10000)
                    {
                        Notice(lobby, "用法：/dice 或 /dice 面数（2~10000）", false);
                        return;
                    }
                    faces = n;
                }
                int v = RandRange(1, faces);
                BroadcastChat(lobby, "【骰子】" + MyPlayerName() + " 掷出 1d" + faces + "：" + v);
            }
            catch (Exception ex) { Log("game: 骰子失败: " + ex.Message); }
        }

        static void MiniRps(object lobby, string cmd)
        {
            try
            {
                string pick = cmd.Length > 5 ? cmd.Substring(5).Trim() : "";
                // 石头 剪刀 布 / 也认 R S P
                int mine = -1;
                if (pick == "石头" || pick == "剪刀" || pick == "布")
                    mine = pick == "石头" ? 0 : (pick == "剪刀" ? 1 : 2);
                else if (pick.Equals("r", StringComparison.OrdinalIgnoreCase)) mine = 0;
                else if (pick.Equals("s", StringComparison.OrdinalIgnoreCase)) mine = 1;
                else if (pick.Equals("p", StringComparison.OrdinalIgnoreCase)) mine = 2;

                if (mine < 0)
                {
                    Notice(lobby, "用法：/rps 石头|剪刀|布（也可以 r|s|p）", false);
                    return;
                }

                string[] names = { "石头", "剪刀", "布" };
                int other = RandRange(0, 2);
                string result;
                if (mine == other) result = "平局";
                else if ((mine + 1) % 3 == other) result = "你（" + MyPlayerName() + "）赢";
                else result = "房间赢";

                BroadcastChat(lobby, "【猜拳】" + MyPlayerName() + " 出 " + names[mine]
                    + "，房间出 " + names[other] + " → " + result);
            }
            catch (Exception ex) { Log("game: 猜拳失败: " + ex.Message); }
        }

        static readonly string[] EightBallAnswers =
        {
            "肯定是的", "看起来不错", "八成会成", "可以试试", "先别急着定",
            "再想想吧", "不太好说", "别指望了", "基本没戏", "完全不行",
        };

        static void Mini8Ball(object lobby, string cmd)
        {
            try
            {
                string q = cmd.Length > 7 ? cmd.Substring(7).Trim() : "";
                if (q.Length == 0)
                {
                    Notice(lobby, "用法：/8ball 你的问题", false);
                    return;
                }
                string ans = EightBallAnswers[RandRange(0, EightBallAnswers.Length - 1)];
                BroadcastChat(lobby, "【问答】" + MyPlayerName() + " 问：" + q + " → " + ans);
            }
            catch (Exception ex) { Log("game: 问答失败: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // 房间公告（MOTD）
        //
        // 房主用 /motd 设置一段文字，存在内存里；有人进房时房主自动私发给他。
        // 不走广播，免得每次有人进来所有人的聊天区都被刷一遍。
        // ------------------------------------------------------------------

        static string roomMotd = "";
        static readonly HashSet<string> motdSentTo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void MotdCommand(object lobby, string cmd)
        {
            try
            {
                if (!motdEnabled)
                {
                    Notice(lobby, "房间公告已在设置里关闭", false);
                    return;
                }

                string arg = cmd.Length > 5 ? cmd.Substring(5).Trim() : "";

                if (!IsHostOf(lobby))
                {
                    // 客机：查看当前公告
                    if (roomMotd.Length == 0) Notice(lobby, "房主还没设置房间公告", true);
                    else Notice(lobby, "【房间公告】" + roomMotd, true);
                    return;
                }

                if (arg.Length == 0)
                {
                    Notice(lobby, roomMotd.Length == 0
                        ? "用法：/motd 内容（设为空则清除）"
                        : "当前公告：" + roomMotd + "　（/motd - 清除）", true);
                    return;
                }

                if (arg == "-" || arg == "清除")
                {
                    roomMotd = "";
                    motdSentTo.Clear();
                    Notice(lobby, "房间公告已清除", true);
                    Log("motd: 已清除");
                    return;
                }

                if (arg.Length > 200) arg = arg.Substring(0, 200);
                roomMotd = arg;
                motdSentTo.Clear();
                Notice(lobby, "房间公告已设置：" + arg, true);
                Log("motd: 已设置");
            }
            catch (Exception ex) { Log("motd: 失败: " + ex.Message); }
        }

        /// <summary>有人进房时把公告私发给他一次（只有房主做）。</summary>
        static void MotdTick(object lobby, IList players, bool host)
        {
            try
            {
                if (!motdEnabled || !host || roomMotd.Length == 0 || players == null) return;

                foreach (object o in players)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null) continue;
                    string name = Clean(lp.Name);
                    if (name.Length == 0) continue;
                    if (SameName(name, MyPlayerName())) continue;
                    if (!motdSentTo.Add(name)) continue;

                    SendPrivate(lobby, name, "【房间公告】" + roomMotd, false);
                    Log("motd: 已发给 " + name);
                }
            }
            catch (Exception ex) { Log("motd: tick 失败: " + ex.Message); }
        }

        /// <summary>把当前房间的要点拼成一行，方便复制给朋友。</summary>
        static string RoomInfoText(object lobby)
        {
            try
            {
                string map = "";
                object m = GetMember(lobby, "Map");
                if (m == null)
                {
                    object gm = GetMember(lobby, "GameModeMap");
                    if (gm != null) m = GetMember(gm, "Map");
                }
                if (m != null) map = Convert.ToString(GetMember(m, "Name"));

                object gm2 = GetMember(lobby, "GameMode");
                string mode = gm2 == null ? "" : Convert.ToString(gm2);

                IList list = GetMember(lobby, "Players") as IList;
                StringBuilder sb = new StringBuilder();
                if (list != null)
                {
                    object[] arr = new object[list.Count];
                    try { list.CopyTo(arr, 0); } catch { arr = new object[0]; }
                    foreach (object o in arr)
                    {
                        LANPlayerInfo lp = o as LANPlayerInfo;
                        if (lp == null) continue;
                        if (sb.Length > 0) sb.Append("、");
                        sb.Append(Clean(lp.Name));
                    }
                }

                return "房间：" + (mode.Length > 0 ? mode + " / " : "") + (map.Length > 0 ? map : "未选图")
                     + "　玩家（" + (list == null ? 0 : list.Count) + "）：" + sb.ToString();
            }
            catch (Exception ex) { return "拿不到房间信息: " + ex.Message; }
        }

        // ---------------- 多图批量同步（聊天命令 /maps + /push） ----------------

        /// <summary>
        /// 扫描本机地图库。自定义图不一定在 Maps\Custom —— 很多人直接丢进 MapsMO\Defend，
        /// 也可能在根目录下别的文件夹，所以默认把根目录下所有「不像系统目录」的都扫一遍；
        /// 只有明确选「只 Maps\Custom」时才缩到那一个目录。
        /// </summary>
        static List<string> ScanMapLibrary(string root, bool customOnly)
        {
            List<string> list = new List<string>();
            try
            {
                // 兜底：如果传进来的不是「含 MapsMO / Maps 的那一层」，往上找一级
                root = MapLibraryRoot(root);
                if (string.IsNullOrEmpty(root))
                {
                    Log("mapsync: 扫描失败：拿不到游戏根目录");
                    return list;
                }

                lastScanRoot = root;
                string[] roots;
                {
                    // customOnly = 默认模式：扫所有地图目录，但隐藏 MO 自带的 MapsMO
                    List<string> ds = new List<string>();
                    foreach (string d in Directory.GetDirectories(root))
                    {
                        string nm = Path.GetFileName(d);
                        if (nm.Length == 0 || nm.StartsWith("_")) continue;
                        if (string.Equals(nm, "Client", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(nm, "Resources", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(nm, "Saved Games", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(nm, "Screenshots", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(nm, "debug", StringComparison.OrdinalIgnoreCase)) continue;

                        ds.Add(nm);
                    }
                    roots = ds.ToArray();
                }

                foreach (string r in roots)
                {
                    string dir = Path.Combine(root, r);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".map" && ext != ".mpr" && ext != ".yrm") continue;

                        string rel = RelativeToRoot(f, root);
                        if (string.IsNullOrEmpty(rel)) continue;
                        if (customOnly && IsOfficialMap(rel)) continue;   // 默认只看第三方图

                        bool dup = false;
                        foreach (string have in list)
                            if (string.Equals(have, rel, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                        if (!dup) list.Add(rel);
                    }
                }
                // 兜底：按目录扫一个都没找到时，直接整棵树递归找（跳过 Resources 等大目录）。
                // 地图可能被塞在任何奇怪的位置，宁慢一点也别让列表空着。
                if (list.Count == 0 && !customOnly)
                {
                    Log("mapsync: 目录扫描 0 命中，改用整树兜底扫描 root=" + root);
                    CollectMapsRecursive(root, root, list, 0);
                    list.Sort(StringComparer.OrdinalIgnoreCase);
                }

                list.Sort(StringComparer.OrdinalIgnoreCase);
                Log("mapsync: 扫描地图库 root=" + root + " customOnly=" + customOnly
                    + " 命中=" + list.Count);
            }
            catch (Exception ex) { Log("mapsync: 扫描地图库失败: " + ex.Message); }
            return list;
        }

        /// <summary>兜底：整棵树递归找地图（跳过 Resources / 存档 / 截图 / _ 备份目录）。</summary>
        static void CollectMapsRecursive(string root, string dir, List<string> list, int depth)
        {
            if (depth > 6 || list.Count > 3000) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".map" && ext != ".mpr" && ext != ".yrm") continue;

                    string rel = RelativeToRoot(f, root);
                    if (string.IsNullOrEmpty(rel)) continue;

                    bool dup = false;
                    foreach (string have in list)
                        if (string.Equals(have, rel, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) list.Add(rel);
                }

                foreach (string d in Directory.GetDirectories(dir))
                {
                    string nm = Path.GetFileName(d);
                    if (nm.Length == 0 || nm.StartsWith("_")) continue;
                    if (string.Equals(nm, "Resources", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(nm, "Saved Games", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(nm, "Screenshots", StringComparison.OrdinalIgnoreCase)) continue;
                    CollectMapsRecursive(root, d, list, depth + 1);
                }
            }
            catch { }
        }

        /// <summary>
        /// 判断一张图是不是 MO 官方图。用目录区分：官方那三套在 MapsMO\Standard /
        /// MapsMO\Challenge / MapsMO\Cooperative；玩家自己加到 MapsMO\Defend 或其它目录的算第三方。
        /// </summary>
        static bool IsOfficialMap(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return false;
            string r = rel.Replace('/', '\\');
            return r.StartsWith("MapsMO\\Standard\\", StringComparison.OrdinalIgnoreCase)
                || r.StartsWith("MapsMO\\Challenge\\", StringComparison.OrdinalIgnoreCase)
                || r.StartsWith("MapsMO\\Cooperative\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 地图库根目录：正常就是游戏根（下面有 MapsMO 或 Maps）。
        /// 万一拿到的路径是 Resources 之类，就往上一级再试，避免列表永远空。
        /// </summary>
        static string MapLibraryRoot(string root)
        {
            try
            {
                string cur = (root ?? "").TrimEnd('\\');
                for (int i = 0; i < 3 && cur.Length > 0; i++)
                {
                    if (Directory.Exists(Path.Combine(cur, "MapsMO"))
                        || Directory.Exists(Path.Combine(cur, "Maps")))
                        return cur;

                    string parent = Path.GetDirectoryName(cur);
                    if (string.IsNullOrEmpty(parent) || parent == cur) break;
                    cur = parent;
                }
            }
            catch { }
            return root;
        }

        static void ListMapsForChat(object lobby)
        {
            string root = SafeGetGamePath();
            List<string> lib = ScanMapLibrary(root, false);
            if (lib.Count == 0) { Notice(lobby, "没找到地图文件（MapsMO / Maps 目录下）", false); return; }

            Notice(lobby, "地图库共 " + lib.Count + " 张；用 /push 序号 批量推送，例如 /push 1,3,5", true);
            int show = Math.Min(lib.Count, 30);
            for (int i = 0; i < show; i++)
            {
                string rel = lib[i];
                string name = Path.GetFileNameWithoutExtension(rel);
                string dir = Path.GetDirectoryName(rel) ?? "";
                Notice(lobby, (i + 1) + ". " + name + "    " + dir, true);
            }
            if (lib.Count > show) Notice(lobby, "……只显示前 " + show + " 条（用 /push 序号 仍可推任意一张）", true);
        }

        static void PushMapsByIndex(object lobby, string arg)
        {
            try
            {
                if (lobby == null) return;
                if (!IsHostOf(lobby)) { Notice(lobby, "只有房主能推送地图", false); return; }
                if (string.IsNullOrEmpty(arg)) { Notice(lobby, "用法：/push 1,3,5（先敲 /maps 看序号）", false); return; }

                string root = MapLibraryRoot(SafeGetGamePath());
                List<string> lib = ScanMapLibrary(root, false);
                if (lib.Count == 0) { Notice(lobby, "没找到地图文件", false); return; }

                List<MapTarget> targets = new List<MapTarget>();
                foreach (string raw in arg.Split(new char[] { ',', ' ', '，' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int v;
                    if (!int.TryParse(raw, out v)) continue;
                    if (v < 1 || v > lib.Count) { Log("mapsync: 序号越界 " + v); continue; }

                    string rel = lib[v - 1];
                    targets.Add(new MapTarget
                    {
                        Full = Path.Combine(root, rel.Replace('/', '\\')),
                        Name = Path.GetFileNameWithoutExtension(rel)
                    });
                }
                if (targets.Count == 0) { Notice(lobby, "序号都不对，先敲 /maps 看列表", false); return; }

                Log("mapsync: /push 选中 " + targets.Count + " 张图");
                StartMapSyncTargets(lobby, targets);
            }
            catch (Exception ex) { Log("mapsync: /push 失败: " + ex.Message); }
        }

        // ---------------- 房主拒绝加入：给客机一句人话 ----------------

        const string DenyKind = "DENY";

        /// <summary>由 LANLobby 的 ListenForClients 补丁调用：拒绝之前先把原因写回去。
        /// 以前是直接 Close，客机那边只表现为「刚进去就自动退」，完全不知道为什么。</summary>
        public static void DenyIncoming(object clientObj, int reason)
        {
            try
            {
                TcpClient tc = clientObj as TcpClient;
                if (tc == null) return;

                string text = reason == 2
                    ? "房间人数已满，房主没有接受你加入"
                    : "房主锁了房间，等解锁后再进";

                byte[] data = Encoding.UTF8.GetBytes(
                    CtrlTag + CtrlSep + DenyKind + CtrlSep + "host" + CtrlSep + text + "\u0002");
                tc.GetStream().Write(data, 0, data.Length);
                tc.GetStream().Flush();
                Thread.Sleep(200);        // 留一点时间让对方把这一帧读完再断
                Log("deny: 已回绝一个连接（" + reason + "）");
            }
            catch { }
        }

        static void OnDeny(object lobby, string reason)
        {
            string text = string.IsNullOrEmpty(reason) ? "房主没有接受你加入这个房间" : reason;
            Log("deny: 收到 -> " + text);
            PostUi(delegate { ShowMessageBox(lobby, "无法加入房间", text); });
        }

        static void ShowMessageBox(object lobby, string caption, string text)
        {
            try
            {
                object wm = lobby == null ? null : GetMember(lobby, "WindowManager");
                Type boxType = FindType("ClientGUI.XNAMessageBox");
                if (wm == null || boxType == null) return;

                MethodInfo show = null;
                foreach (MethodInfo mi in boxType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (mi.Name == "Show" && mi.GetParameters().Length == 3) { show = mi; break; }
                }
                if (show != null) show.Invoke(null, new object[] { wm, caption, text });
            }
            catch (Exception ex) { Log("msgbox: " + ex.Message); }
        }

        // ---------------- /diag：把日志和版本信息打成一个 zip ----------------

        // ==================================================================
        // 客户端内「日志」
        //
        // 入口只有设置窗口：「联机补丁」页右上角的「打开日志」「导出诊断」。
        //   「打开日志」→ 用记事本打开游戏根目录的 MoLanRelay.log
        //   「导出诊断」→ 和 /diag 一样，把补丁日志 + 客户端日志 + 崩溃日志
        //                 + spawn.ini + MoLanPatch.ini + 文件哈希打成桌面 zip
        // 大厅顶部状态栏不再放这两个按钮（那里的按钮不好找）。
        // ==================================================================
        static object logLobbyCurrent;

        /// <summary>把参考按钮的外观（贴图/字号/文字颜色/尺寸）整套复制过来。</summary>
        static void CopyButtonLook(object from, object to)
        {
            if (from == null || to == null) return;
            string[] names = new string[]
            {
                "IdleTexture", "HoverTexture", "FontIndex",
                "TextColorIdle", "TextColorHover", "TextColorDisabled",
                "TextXPosition", "TextYPosition", "AlphaRate",
                "IdleTextureAlpha", "HoverTextureAlpha"
            };
            foreach (string n in names)
            {
                try
                {
                    object v = GetMember(from, n);
                    if (v != null) SetMember(to, n, v);
                }
                catch { }
            }
            try
            {
                int w = GetInt(from, "Width", 0), h = GetInt(from, "Height", 0);
                if (w > 0) SetMember(to, "Width", w);
                if (h > 0) SetMember(to, "Height", h);
            }
            catch { }
        }

        static void LogButtonClick(object sender, EventArgs e) { OpenLogWindow(); }

        static void LogExportClick(object sender, EventArgs e)
        {
            try
            {
                ExportDiagAsync(logLobbyCurrent);
                if (logLobbyCurrent == null)
                    ShowMessageBox(topBar, "导出日志", "正在收集诊断信息，完成后会在桌面生成 MO诊断-时间.zip");
            }
            catch (Exception ex) { Log("logpanel: 导出失败: " + ex.Message); }
        }

        /// <summary>打开「对局记录」目录（没有就建一个空目录，免得用户以为按钮坏了）。</summary>
        static void OpenMatchFolderClick(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(logPath)) ResolveFallbackLogPath();
                string root = string.IsNullOrEmpty(logPath) ? SafeGetGamePath() : Path.GetDirectoryName(logPath);
                if (string.IsNullOrEmpty(root)) return;

                string dir = Path.Combine(root, "对局记录");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
                Log("match: 已打开对局记录目录");
            }
            catch (Exception ex) { Log("match: 打开对局记录目录失败: " + ex.Message); }
        }

        /// <summary>打开跟随文件增长的实时日志窗口；PowerShell 不可用时退回记事本。</summary>
        static void OpenLogWindow()
        {
            string path = null;
            try
            {
                if (string.IsNullOrEmpty(logPath)) ResolveFallbackLogPath();
                path = logPath;
            }
            catch { }

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Log("logpanel: 还没有日志文件: " + (path ?? "?"));
                if (logLobbyCurrent != null) Notice(logLobbyCurrent, "还没有日志文件", false);
                else ShowMessageBox(topBar, "日志", "还没有日志文件。\n进过一次局域网大厅后就会有。");
                return;
            }

            try
            {
                // 记事本：最朴素也最可靠（之前用 PowerShell 跟随窗口，实测在游戏里起不来）
                Process.Start("notepad.exe", "\"" + path + "\"");
                Log("logpanel: 已用记事本打开日志");
            }
            catch (Exception ex) { Log("logpanel: 打开日志失败: " + ex.Message); }
        }

        /// <summary>大厅里每帧：记住当前大厅（导出/提示要用）。</summary>
        static void LogPanelTick(object lobby)
        {
            if (lobby == null) return;
            logLobbyCurrent = lobby;
        }

        static void ExportDiagAsync(object lobby)
        {
            Notice(lobby, "正在收集诊断信息…", true);

            Thread t = new Thread(delegate()
            {
                string zipPath = null;
                try
                {
                    string root = SafeGetGamePath();
                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    if (string.IsNullOrEmpty(desktop)) desktop = root;
                    zipPath = Path.Combine(desktop, "MO诊断-" + stamp + ".zip");

                    List<string> files = new List<string>();
                    AddIfExists(files, Path.Combine(root, "MoLanRelay.log"));
                    AddIfExists(files, Path.Combine(root, "spawn.ini"));
                    AddIfExists(files, Path.Combine(root, "Resources\\MoLanPatch.ini"));
                    AddIfExists(files, Path.Combine(root, "Resources\\lanip.txt"));
                    AddIfExists(files, Path.Combine(root, "Resources\\client.log"));
                    AddIfExists(files, Path.Combine(root, "Resources\\ClientDefinitions.ini"));

                    try
                    {
                        string[] crashes = Directory.GetFiles(root, "ClientCrashLog_*.txt");
                        Array.Sort(crashes);
                        int from = crashes.Length > 3 ? crashes.Length - 3 : 0;
                        for (int i = from; i < crashes.Length; i++) files.Add(crashes[i]);
                    }
                    catch { }

                    string info = BuildDiagInfo(root);
                    string infoPath = Path.Combine(Path.GetTempPath(), "mo-diag-info.txt");
                    File.WriteAllText(infoPath, info, Encoding.UTF8);
                    files.Add(infoPath);

                    WriteStoreZip(zipPath, files, root);
                    try { File.Delete(infoPath); } catch { }

                    string msg = "诊断包已导出：" + zipPath;
                    PostUi(delegate { Notice(lobby, msg, true); });
                    Log("diag: " + zipPath + "（" + files.Count + " 个文件）");
                }
                catch (Exception ex)
                {
                    string msg = "诊断包导出失败：" + ex.Message;
                    PostUi(delegate { Notice(lobby, msg, false); });
                    Log("diag: 失败: " + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Name = "MoDiag";
            t.Start();
        }

        static void AddIfExists(List<string> list, string path)
        {
            try { if (File.Exists(path)) list.Add(path); } catch { }
        }

        static string BuildDiagInfo(string root)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("MO 局域网补丁 诊断信息");
            sb.AppendLine("时间      : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("中继版本  : " + Version);
            sb.AppendLine("游戏目录  : " + root);
            sb.AppendLine("用户名    : " + MyPlayerName());
            sb.AppendLine("系统      : " + Environment.OSVersion.VersionString);
            sb.AppendLine("运行时    : " + Environment.Version);
            sb.AppendLine("64位系统  : " + Environment.Is64BitOperatingSystem);
            sb.AppendLine("CPU       : " + Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"));
            sb.AppendLine("CPU核数   : " + Environment.ProcessorCount);
            sb.AppendLine();
            sb.AppendLine("关键文件哈希：");
            string[] rels = new string[]
            {
                "Resources\\clientdx.exe",
                "Resources\\clientogl.exe",
                "Resources\\clientxna.exe",
                "Resources\\MoLanRelay.dll",
                "Resources\\Binaries\\MoLanRelay.dll",
                "Resources\\Binaries\\Windows\\MonoGame.Framework.dll",
                "Resources\\Binaries\\Windows\\Rampastring.XNAUI.dll",
            };
            foreach (string rel in rels)
            {
                string full = Path.Combine(root, rel);
                string line;
                try
                {
                    if (File.Exists(full))
                        line = rel + "  " + new FileInfo(full).Length + "  " + Sha256Of(full).Substring(0, 32);
                    else
                        line = rel + "  （不存在）";
                }
                catch (Exception ex) { line = rel + "  读取失败: " + ex.Message; }
                sb.AppendLine("  " + line);
            }
            return sb.ToString();
        }

        /// <summary>写一个「只存储、不压缩」的 zip（.NET 4.0 没有 ZipFile，自己拼最稳）。</summary>
        static void WriteStoreZip(string zipPath, List<string> files, string root)
        {
            using (FileStream fs = File.Create(zipPath))
            {
                List<string> names = new List<string>();
                List<uint> crcs = new List<uint>();
                List<uint> sizes = new List<uint>();
                List<uint> offsets = new List<uint>();

                foreach (string f in files)
                {
                    byte[] data = File.ReadAllBytes(f);
                    string name = RelativeToRoot(f, root);
                    if (string.IsNullOrEmpty(name)) name = "外部\\" + Path.GetFileName(f);
                    name = name.Replace('\\', '/');

                    offsets.Add((uint)fs.Position);
                    names.Add(name);
                    crcs.Add(Crc32(data));
                    sizes.Add((uint)data.Length);

                    WriteZipLocal(fs, name, data, crcs[crcs.Count - 1]);
                }

                uint cdStart = (uint)fs.Position;
                for (int i = 0; i < names.Count; i++)
                    WriteZipCentral(fs, names[i], crcs[i], sizes[i], offsets[i]);
                uint cdSize = (uint)fs.Position - cdStart;
                WriteZipEnd(fs, (ushort)names.Count, cdSize, cdStart);
            }
        }

        static void WriteZipLocal(Stream s, string name, byte[] data, uint crc)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            BinaryWriter w = new BinaryWriter(s);
            w.Write(0x04034b50);
            w.Write((ushort)20);        // version needed
            w.Write((ushort)0x0800);    // UTF-8 文件名
            w.Write((ushort)0);         // store
            w.Write(DosTime());
            w.Write(DosDate());
            w.Write(crc);
            w.Write((uint)data.Length);
            w.Write((uint)data.Length);
            w.Write((ushort)nameBytes.Length);
            w.Write((ushort)0);
            w.Write(nameBytes);
            w.Write(data);
            w.Flush();
        }

        static void WriteZipCentral(Stream s, string name, uint crc, uint size, uint offset)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            BinaryWriter w = new BinaryWriter(s);
            w.Write(0x02014b50);
            w.Write((ushort)20);
            w.Write((ushort)20);
            w.Write((ushort)0x0800);
            w.Write((ushort)0);
            w.Write(DosTime());
            w.Write(DosDate());
            w.Write(crc);
            w.Write(size);
            w.Write(size);
            w.Write((ushort)nameBytes.Length);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write(0u);
            w.Write(offset);
            w.Write(nameBytes);
            w.Flush();
        }

        static void WriteZipEnd(Stream s, ushort count, uint cdSize, uint cdStart)
        {
            BinaryWriter w = new BinaryWriter(s);
            w.Write(0x06054b50);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write(count);
            w.Write(count);
            w.Write(cdSize);
            w.Write(cdStart);
            w.Write((ushort)0);
            w.Flush();
        }

        static ushort DosTime()
        {
            DateTime n = DateTime.Now;
            return (ushort)((n.Hour << 11) | (n.Minute << 5) | (n.Second / 2));
        }

        static ushort DosDate()
        {
            DateTime n = DateTime.Now;
            if (n.Year < 1980) return (ushort)(1 << 5 | 1);
            return (ushort)(((n.Year - 1980) << 9) | (n.Month << 5) | n.Day);
        }

        static uint[] crcTable;

        static uint Crc32(byte[] data)
        {
            if (crcTable == null)
            {
                uint[] t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[i] = c;
                }
                crcTable = t;
            }

            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        // ---------------- 自动准备 / 同名提醒 ----------------

        static void AutoReadyTick(object lobby)
        {
            if (!autoReadyEnabled || lobby == null) return;
            if (IsHostOf(lobby)) return;
            if (autoReadyLobby != null && ReferenceEquals(autoReadyLobby, lobby)) return;

            object chk = GetMember(lobby, "chkAutoReady");
            if (chk == null) return;

            // 没有地图（房主还没选图 / 选了本地没有的图）就别替玩家勾「准备」：
            // 勾了也开不了局，只会让人以为可以开始。等有了图下一帧再勾。
            if (!LocalHasSelectedMap(lobby))
            {
                if (!loggedAutoReadyNoMap)
                {
                    loggedAutoReadyNoMap = true;
                    Log("autoready: 本地还没有可用地图，先不自动准备");
                }
                return;
            }
            loggedAutoReadyNoMap = false;

            autoReadyLobby = lobby;
            try
            {
                if (GetMember(chk, "Checked") is bool on && on) return;
                SetMember(chk, "Checked", true);
                Log("autoready: 已勾上自动准备");
            }
            catch (Exception ex) { Log("autoready: " + ex.Message); }
        }

        static bool loggedAutoReadyNoMap;

        static void DupNameTick(object lobby)
        {
            if (!dupNameWarnEnabled || lobby == null) return;

            try
            {
                IList players = GetMember(lobby, "Players") as IList;
                if (players == null || players.Count < 2) return;

                List<string> names = new List<string>();
                foreach (object p in players)
                {
                    string n = Clean(Convert.ToString(GetMember(p, "Name")));
                    if (n.Length > 0) names.Add(n);
                }
                names.Sort(StringComparer.OrdinalIgnoreCase);

                StringBuilder sig = new StringBuilder();
                string dup = null;
                for (int i = 1; i < names.Count; i++)
                {
                    if (!string.Equals(names[i], names[i - 1], StringComparison.OrdinalIgnoreCase)) continue;
                    dup = names[i];
                    sig.Append(dup).Append('|');
                }
                if (dup == null) return;
                if (string.Equals(sig.ToString(), lastNameSignature, StringComparison.Ordinal)) return;

                lastNameSignature = sig.ToString();
                Notice(lobby, "有同名玩家「" + dup + "」：@ 和踢人会认错人，建议改名", false);
                Log("dupname: " + dup);
            }
            catch { }
        }

        // ---------------- 地图同步：进度 / 取消 ----------------

        static string MapSyncStatusText()
        {
            try
            {
                if (mapSyncTxActive)
                {
                    int pct = mapSyncTxTotal <= 0 ? 0 : (int)(mapSyncTxBytes * 100 / mapSyncTxTotal);
                    return "地图同步 发送 " + pct + "%";
                }
                if (mapSyncRxActive)
                {
                    int pct = mapSyncRxTotal <= 0 ? 0 : (int)(mapSyncRxBytes * 100 / mapSyncRxTotal);
                    return "地图同步 接收 " + pct + "%";
                }
            }
            catch { }
            return "";
        }

        static void CancelMapSync(object lobby)
        {
            mapSyncCancelRequested = true;
            mapSyncTxActive = false;
            mapSyncRxActive = false;
            Notice(lobby, "已取消地图同步", false);
            Log("mapsync: 用户取消");
        }

        // ------------------------------------------------------------------
        // 日志（出问题时能查）
        // ------------------------------------------------------------------

        static readonly object logLock = new object();
        // logPath 还没设置时（SetUp 阶段）先把日志攒着，等 Relay.Start 拿到路径后一起落盘。
        // 否则 "register host client" 这行永远看不到，就无法判断客机有没有拿到房主连接。
        static readonly List<string> pendingLog = new List<string>();

        /// <summary>logPath 还没设置时，用游戏根目录兜底一个日志路径。</summary>
        static void ResolveFallbackLogPath()
        {
            if (!string.IsNullOrEmpty(logPath)) return;
            string gp = SafeGetGamePath();
            if (gp.Length == 0) return;
            logPath = Path.Combine(gp, "MoLanRelay.log");
            Log("== 大厅阶段日志（尚未开局；路径由 GamePath 兜底）==");
        }

        static void Log(string line)
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss.fff") + " [MoLanRelay] " + line;

            // 大厅阶段 Relay.Start 还没跑（logPath 为空），但诊断最需要这时的日志
            // （@功能、踢人都在大厅里发生）。用 GamePath 兜底算一个路径，直接写盘。
            if (string.IsNullOrEmpty(logPath))
            {
                try { ResolveFallbackLogPath(); } catch { }
            }

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
