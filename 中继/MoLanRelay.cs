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
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
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

                // 关连接 —— 后面所有善后都是现成机制
                try { lp.TcpClient.Close(); }
                catch (Exception ex) { Log("kick: 关闭 '" + name + "' 的连接失败: " + ex.Message); }
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
                    PropertyInfo p = t.GetProperty("PLAYERNAME", BindingFlags.Public | BindingFlags.Static);
                    if (p != null) found = Clean(p.GetValue(null, null) as string);
                }
            }
            catch { }

            if (found.Length > 0) { cachedMyName = found; return found; }
            if (!warnedNoName) { warnedNoName = true; Log("拿不到自己的玩家名 —— 语音/截图里【跳过自己那份】的判断可能失效"); }
            return "";
        }

        // ------------------------------------------------------------------
        // @功能（局域网大厅聊天）
        //
        // 挂在大厅聊天渲染的路上：LANGameLobby::Player_HandleChatCommand 里，
        // 颜色算完之后插一次调用 —— 消息里含 "@我的名字" 就用高亮色 + 放提示音。
        // 房主自己的发言也会走这里（经过自连接绕回来），所以两边行为一致。
        // ------------------------------------------------------------------

        static bool loggedMention;

        /// <summary>由 Player_HandleChatCommand 的补丁调用。
        /// 注意：参数/返回值故意用 object（装箱的 Color），避免 DLL 与 exe 之间
        /// 对 MonoGame 的 Color 类型产生跨程序集签名依赖 —— 那种依赖一旦版本对不齐，
        /// 会在第一次聊天（方法 JIT 时）直接抛 MissingMethodException 崩掉。</summary>
        public static object MentionColor(object original, object lobby, string text)
        {
            try
            {
                if (text == null) return original;
                string me = MyPlayerName();
                if (me.Length == 0) return original;
                if (text.IndexOf("@" + me, StringComparison.OrdinalIgnoreCase) < 0) return original;

                if (!loggedMention)
                {
                    loggedMention = true;
                    Log("@：收到 @ 我的消息（'@" + me + "'），已高亮并放提示音");
                }
                PlayMessageSound(lobby);
                return (object)new Microsoft.Xna.Framework.Color(255, 226, 90);   // 金色
            }
            catch (Exception ex) { Log("mention error: " + ex.Message); }
            return original;
        }

        /// <summary>尽力而为：用大厅自带的"新消息"音效（MultiplayerGameLobby::sndMessageSound）。</summary>
        static void PlayMessageSound(object lobby)
        {
            try
            {
                object snd = GetMember(lobby, "sndMessageSound");
                if (snd == null) return;
                MethodInfo pm = snd.GetType().GetMethod("Play", Type.EmptyTypes);
                if (pm != null) pm.Invoke(snd, null);
            }
            catch { }
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
        const int VK_F4 = 0x73, VK_F5 = 0x74, VK_F6 = 0x75, VK_F7 = 0x76, VK_F8 = 0x77;

        static bool clipKeyWasDown, statsKeyWasDown;

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

                if ((DateTime.Now - lastPidScan).TotalSeconds > 5)
                {
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

        /// <summary>由 LANGameLobby::Update 的补丁每帧调用 —— 热键轮询 / 版本自检 / 语音。</summary>
        public static void Tick(object lobby, object gameTime)
        {
            bool front = false;
            try { front = OursInFront(); } catch { }

            // F6：把剪贴板内容当聊天发出去（分享 IP:端口 用）
            try
            {
                bool k = front && KeyDown(VK_F6);
                if (k && !clipKeyWasDown) ShareClipboard(lobby);
                clipKeyWasDown = k;
            }
            catch (Exception ex) { Log("分享剪贴板出错: " + ex.Message); }

            // F5：把"大厅 + 中继"体检报告发到聊天区
            try
            {
                bool k = front && KeyDown(VK_F5);
                if (k && !statsKeyWasDown) ShareReport(lobby);
                statsKeyWasDown = k;
            }
            catch (Exception ex) { Log("体检报告出错: " + ex.Message); }

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
            Notice(lobby, msg, false);
            SendChat(lobby, msg);
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
                bool same = mine.Length > 0 && theirs.Length > 0 &&
                            string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase);
                if (same) { Log("filehash: " + who + " 游戏文件校验通过"); return; }   // 通过就不刷屏

                string msg = "⚠ 游戏文件不一致：" + (who.Length == 0 ? "对方" : who) + " 的文件和你的不一样"
                           + "（他 " + Short(theirs) + " / 你 " + Short(mine) + "）"
                           + " —— 联机很可能卡住或崩溃，请核对双方 MO 版本与补丁是否完全一致！";
                Log("filehash: " + msg);
                Notice(lobby, msg, false);
                SendChat(lobby, msg);
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

                // base64 从第 4 段开始（base64 不含 \u0001，这里再切一次保险）
                int idx = message.IndexOf('\u0001', CtrlTag.Length + CtrlSep.Length + kind.Length + 1 + parts[2].Length + 1);
                string b64 = idx >= 0 ? message.Substring(idx + 1) : parts[3];

                bool mine = from.Length > 0 && SameName(from, MyPlayerName());

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
                    if (!mine) QueueVoicePlayback(b64);
                }
                else if (kind == HelloKind)
                {
                    if (!mine) OnHello(lobby, from, b64);
                }
                else return;

                // 房主负责转发给"其他人"（不回发给发送者，语音帧没必要浪费带宽）
                if (IsHostOf(lobby)) ForwardToOthers(lobby, from, message);
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
                foreach (object o in players)
                {
                    LANPlayerInfo lp = o as LANPlayerInfo;
                    if (lp == null) continue;
                    string n = Clean(lp.Name);
                    if (n.Length == 0) continue;
                    if (from.Length > 0 && SameName(n, from)) continue;   // 不回发给发送者
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
