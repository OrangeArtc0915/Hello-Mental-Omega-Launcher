using System;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

/// <summary>
/// 用 dnlib 给 MonoGame.Framework.dll 的 WinFormsGameForm::WndProc 增加 WM_IME_CHAR(0x286) 处理。
/// 用法: dnlibpatch &lt;in.dll&gt; &lt;out.dll&gt; [roundtrip|patch]
///   roundtrip = 只读入再写出，不做任何修改（用于检验工具链是否会破坏 IL）
///   patch     = 插入 WM_IME_CHAR 处理
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: dnlibpatch <in.dll> <out.dll> [roundtrip|patch]"); return 2; }
        string mode = args.Length > 2 ? args[2] : "patch";

        var mod = ModuleDefMD.Load(args[0]);
        Console.WriteLine("模块: " + mod.Name + "  (runtime " + mod.RuntimeVersion + ")");

        if (mode == "xna")
        {
            var xt = mod.GetTypes().FirstOrDefault(t => t.Name == "KeyboardEventInput");
            if (xt == null) { Console.WriteLine("SKIP: KeyboardEventInput 未找到"); return 0; }
            var hm = xt.Methods.FirstOrDefault(m => m.Name == "HookProc");
            if (hm == null || !hm.HasBody) { Console.WriteLine("SKIP: HookProc 未找到"); return 0; }
            var xins = hm.Body.Instructions;

            // 先移除旧的插入（若有）：ldarg.1 ; ldc.i4 646 ; beq *（Cecil 版留下的）
            for (int i = 1; i < xins.Count - 1; i++)
            {
                if (xins[i].OpCode.Code == dnlib.DotNet.Emit.Code.Ldc_I4 && xins[i].GetLdcI4Value() == 646 &&
                    xins[i - 1].OpCode.Code == dnlib.DotNet.Emit.Code.Ldarg_1 &&
                    (xins[i + 1].OpCode.Code == dnlib.DotNet.Emit.Code.Beq || xins[i + 1].OpCode.Code == dnlib.DotNet.Emit.Code.Beq_S))
                {
                    Console.WriteLine("    发现旧插入，先移除");
                    xins.RemoveAt(i + 1); xins.RemoveAt(i); xins.RemoveAt(i - 1);
                    break;
                }
            }

            Instruction wmCharTarget = null, fallBr = null;
            for (int i = 1; i < xins.Count; i++)
            {
                if (xins[i - 1].OpCode.Code == dnlib.DotNet.Emit.Code.Ldc_I4 && xins[i - 1].GetLdcI4Value() == 258 &&
                    (xins[i].OpCode.Code == dnlib.DotNet.Emit.Code.Beq || xins[i].OpCode.Code == dnlib.DotNet.Emit.Code.Beq_S))
                    wmCharTarget = (Instruction)xins[i].Operand;
            }
            for (int i = 1; i < xins.Count - 1; i++)
            {
                if (xins[i - 1].OpCode.Code == dnlib.DotNet.Emit.Code.Ldc_I4 && xins[i - 1].GetLdcI4Value() == 641 &&
                    (xins[i].OpCode.Code == dnlib.DotNet.Emit.Code.Beq || xins[i].OpCode.Code == dnlib.DotNet.Emit.Code.Beq_S))
                {
                    for (int k = i + 1; k < xins.Count && k <= i + 4; k++)
                    {
                        if (xins[k].OpCode.Code == dnlib.DotNet.Emit.Code.Br || xins[k].OpCode.Code == dnlib.DotNet.Emit.Code.Br_S) { fallBr = xins[k]; break; }
                    }
                    if (fallBr != null) break;
                }
            }
            if (wmCharTarget == null) { Console.WriteLine("SKIP: 未找到 WM_CHAR 分支"); return 0; }
            if (fallBr == null) { Console.WriteLine("SKIP: 未找到默认分支"); return 0; }
            int bi = xins.IndexOf(fallBr);
            xins.Insert(bi, Instruction.Create(OpCodes.Ldarg_1));
            xins.Insert(bi + 1, Instruction.CreateLdcI4(646));
            xins.Insert(bi + 2, Instruction.Create(OpCodes.Beq, wmCharTarget));
            hm.Body.SimplifyBranches();
            hm.Body.KeepOldMaxStack = true;   // 原方法结构较复杂，保留原 maxstack（插入的代码峰值不超过原值）
            Console.WriteLine("XNAUI: 已插入 3 条指令（WM_IME_CHAR -> WM_CHAR 处理）");
            var xwOpts = new dnlib.DotNet.Writer.ModuleWriterOptions(mod);
            xwOpts.MetadataOptions.Flags |= dnlib.DotNet.Writer.MetadataFlags.KeepOldMaxStack;
            mod.Write(args[1], xwOpts);
            Console.WriteLine("OK -> " + args[1]);
            return 0;
        }

        if (mode == "ime" || mode == "imediag")
        {
            var type = mod.GetTypes().FirstOrDefault(t => t.Name == "WinFormsGameForm");
            if (type == null) { Console.WriteLine("SKIP: WinFormsGameForm"); return 0; }
            var method = type.Methods.FirstOrDefault(m => m.Name == "WndProc");
            if (method == null || !method.HasBody) { Console.WriteLine("SKIP: WndProc"); return 0; }
            var ins = method.Body.Instructions;

            IMethod getMsg = null;
            foreach (var i in ins)
            {
                var im = i.Operand as IMethod;
                if (im != null && im.Name == "get_Msg") { getMsg = im; break; }
            }
            if (getMsg == null) { Console.WriteLine("SKIP: Message::get_Msg"); return 0; }

            var getHwnd = new MemberRefUser(mod, "get_HWnd", MethodSig.CreateInstance(mod.CorLibTypes.IntPtr), getMsg.DeclaringType);
            // 注意：不能用 IntPtr::ToInt64/ToInt32 这类值类型实例方法 —— IL 里 this 必须是托管指针，
            // 直接传 IntPtr 的值会被当成地址解引用，运行时报 AccessViolationException。
            // 用静态的 op_Equality 和静态字段 Zero 代替。
            var intPtrRef = mod.CorLibTypes.IntPtr.ToTypeDefOrRef();
            var intPtrZero = new MemberRefUser(mod, "Zero", new FieldSig(mod.CorLibTypes.IntPtr), intPtrRef);
            var opEquality = new MemberRefUser(mod, "op_Equality",
                MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr), intPtrRef);

            var imm32 = new ModuleRefUser(mod, "imm32.dll");
            var user32m = new ModuleRefUser(mod, "user32.dll");
            MethodDefUser MakePInvoke(ModuleRef mref, string name, MethodSig sig)
            {
                var md = new MethodDefUser(name, sig,
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl | MethodAttributes.HideBySig)
                { ImplAttributes = MethodImplAttributes.PreserveSig };
                md.ImplMap = new ImplMapUser(mref, name, PInvokeAttributes.CallConvWinapi);
                return md;
            }
            var immGetContext = MakePInvoke(imm32, "ImmGetContext", MethodSig.CreateStatic(mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr));
            var immCreateContext = MakePInvoke(imm32, "ImmCreateContext", MethodSig.CreateStatic(mod.CorLibTypes.IntPtr));
            var immAssociateContext = MakePInvoke(imm32, "ImmAssociateContext", MethodSig.CreateStatic(mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr));
            var immSetOpenStatus = MakePInvoke(imm32, "ImmSetOpenStatus", MethodSig.CreateStatic(mod.CorLibTypes.Int32, mod.CorLibTypes.IntPtr, mod.CorLibTypes.Int32));
            var immSetConversionStatus = MakePInvoke(imm32, "ImmSetConversionStatus", MethodSig.CreateStatic(mod.CorLibTypes.Int32, mod.CorLibTypes.IntPtr, mod.CorLibTypes.Int32, mod.CorLibTypes.Int32));
            var getCursorPos = MakePInvoke(user32m, "GetCursorPos", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.IntPtr));
            var getClientRect = MakePInvoke(user32m, "GetClientRect", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr));
            var screenToClient = MakePInvoke(user32m, "ScreenToClient", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr));
            var createCaret = MakePInvoke(user32m, "CreateCaret", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr, mod.CorLibTypes.Int32, mod.CorLibTypes.Int32));
            var setCaretPos = MakePInvoke(user32m, "SetCaretPos", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.Int32, mod.CorLibTypes.Int32));
            var showCaret = MakePInvoke(user32m, "ShowCaret", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.IntPtr));
            var immSetCompositionWindow = MakePInvoke(imm32, "ImmSetCompositionWindow", MethodSig.CreateStatic(mod.CorLibTypes.Int32, mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr));
            var immSetCandidateWindow = MakePInvoke(imm32, "ImmSetCandidateWindow", MethodSig.CreateStatic(mod.CorLibTypes.Int32, mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr));
            foreach (var m in new[] { immGetContext, immCreateContext, immAssociateContext, immSetOpenStatus, immSetConversionStatus, getCursorPos, getClientRect, screenToClient, createCaret, setCaretPos, showCaret, immSetCompositionWindow, immSetCandidateWindow })
                type.Methods.Add(m);

            // 静态字段代替局部变量：dnlib 不会给新加的 Local 分配索引，写出来就是非法 IL。
            var fBuf = new FieldDefUser("_moImeBuf", new FieldSig(mod.CorLibTypes.IntPtr), FieldAttributes.Private | FieldAttributes.Static);
            var fX = new FieldDefUser("_moImeX", new FieldSig(mod.CorLibTypes.Int32), FieldAttributes.Private | FieldAttributes.Static);
            var fY = new FieldDefUser("_moImeY", new FieldSig(mod.CorLibTypes.Int32), FieldAttributes.Private | FieldAttributes.Static);
            type.Fields.Add(fBuf);
            type.Fields.Add(fX);
            type.Fields.Add(fY);

            var marshalRef = new TypeRefUser(mod, "System.Runtime.InteropServices", "Marshal", mod.CorLibTypes.AssemblyRef);
            var allocHGlobal = new MemberRefUser(mod, "AllocHGlobal", MethodSig.CreateStatic(mod.CorLibTypes.IntPtr, mod.CorLibTypes.Int32), marshalRef);
            var readInt32 = new MemberRefUser(mod, "ReadInt32", MethodSig.CreateStatic(mod.CorLibTypes.Int32, mod.CorLibTypes.IntPtr, mod.CorLibTypes.Int32), marshalRef);
            var writeInt32 = new MemberRefUser(mod, "WriteInt32", MethodSig.CreateStatic(mod.CorLibTypes.Void, mod.CorLibTypes.IntPtr, mod.CorLibTypes.Int32, mod.CorLibTypes.Int32), marshalRef);

            var L1 = Instruction.Create(OpCodes.Nop);
            var L3 = Instruction.Create(OpCodes.Nop);

            var block = new[]
            {
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getMsg),
                Instruction.CreateLdcI4(0x281),
                Instruction.Create(OpCodes.Beq, L1),
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getMsg),
                Instruction.CreateLdcI4(0x0006),
                Instruction.Create(OpCodes.Beq, L1),
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getMsg),
                Instruction.CreateLdcI4(0x0007),
                Instruction.Create(OpCodes.Bne_Un, L3),

                L1,
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getHwnd),
                Instruction.Create(OpCodes.Call, immGetContext),
                Instruction.Create(OpCodes.Ldsfld, intPtrZero),
                Instruction.Create(OpCodes.Call, opEquality),
                Instruction.Create(OpCodes.Brfalse, L3),

                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getHwnd),
                Instruction.Create(OpCodes.Call, immCreateContext),
                Instruction.Create(OpCodes.Call, immAssociateContext),
                Instruction.Create(OpCodes.Pop),

                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getHwnd),
                Instruction.Create(OpCodes.Call, immGetContext),
                Instruction.Create(OpCodes.Ldc_I4_1),
                Instruction.Create(OpCodes.Call, immSetOpenStatus),
                Instruction.Create(OpCodes.Pop),

                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getHwnd),
                Instruction.Create(OpCodes.Call, immGetContext),
                Instruction.Create(OpCodes.Ldc_I4_1),
                Instruction.Create(OpCodes.Ldc_I4_0),
                Instruction.Create(OpCodes.Call, immSetConversionStatus),
                Instruction.Create(OpCodes.Pop),

                L3,
            };

            // ── 记录"最后一次在窗口内左键点击的位置"（点打字栏就是点它），并把 caret 摆过去 ──
            // 坐标一律用客户区坐标：光标矩形(caret)是 TSF 输入法唯一认的输入位置来源。
            var LskipClick = Instruction.Create(OpCodes.Nop);
            var LhaveClick = Instruction.Create(OpCodes.Nop);
            var clickBlock = new[]
            {
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getMsg),
                Instruction.CreateLdcI4(0x0201),
                Instruction.Create(OpCodes.Bne_Un, LskipClick),

                Instruction.Create(OpCodes.Ldsfld, fBuf),
                Instruction.Create(OpCodes.Ldsfld, intPtrZero),
                Instruction.Create(OpCodes.Call, opEquality),
                Instruction.Create(OpCodes.Brfalse, LhaveClick),
                Instruction.CreateLdcI4(32),
                Instruction.Create(OpCodes.Call, allocHGlobal),
                Instruction.Create(OpCodes.Stsfld, fBuf),
                LhaveClick,

                // GetCursorPos(buf) → 屏幕坐标；再 ScreenToClient → 客户区坐标
                Instruction.Create(OpCodes.Ldsfld, fBuf),
                Instruction.Create(OpCodes.Call, getCursorPos),
                Instruction.Create(OpCodes.Pop),
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getHwnd),
                Instruction.Create(OpCodes.Ldsfld, fBuf),
                Instruction.Create(OpCodes.Call, screenToClient),
                Instruction.Create(OpCodes.Pop),

                Instruction.Create(OpCodes.Ldsfld, fBuf),
                Instruction.Create(OpCodes.Ldc_I4_0),
                Instruction.Create(OpCodes.Call, readInt32),
                Instruction.Create(OpCodes.Stsfld, fX),

                Instruction.Create(OpCodes.Ldsfld, fBuf),
                Instruction.Create(OpCodes.Ldc_I4_4),
                Instruction.Create(OpCodes.Call, readInt32),
                Instruction.CreateLdcI4(26),
                Instruction.Create(OpCodes.Sub),
                Instruction.Create(OpCodes.Stsfld, fY),

                // CreateCaret(hwnd, 0, 1, 1) + SetCaretPos(x,y) + ShowCaret(hwnd)
                Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, getHwnd),
                Instruction.Create(OpCodes.Ldsfld, intPtrZero),
                Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Ldc_I4_1),
                Instruction.Create(OpCodes.Call, createCaret), Instruction.Create(OpCodes.Pop),

                Instruction.Create(OpCodes.Ldsfld, fX), Instruction.Create(OpCodes.Ldsfld, fY),
                Instruction.Create(OpCodes.Call, setCaretPos), Instruction.Create(OpCodes.Pop),

                Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, getHwnd),
                Instruction.Create(OpCodes.Call, showCaret), Instruction.Create(OpCodes.Pop),

                LskipClick,
            };

            // ── 开始输入时：确保 caret 有效（TSF 靠它定位候选框），并同步合成窗位置 ──
            var Lskip = Instruction.Create(OpCodes.Nop);
            var Lhave = Instruction.Create(OpCodes.Nop);
            var LhaveAnchor = Instruction.Create(OpCodes.Nop);
            var posBlock = new[]
            {
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getMsg),
                Instruction.CreateLdcI4(0x10D),
                Instruction.Create(OpCodes.Bne_Un, Lskip),

                // if (_moImeBuf == IntPtr.Zero) _moImeBuf = Marshal.AllocHGlobal(32);
                Instruction.Create(OpCodes.Ldsfld, fBuf),
                Instruction.Create(OpCodes.Ldsfld, intPtrZero),
                Instruction.Create(OpCodes.Call, opEquality),
                Instruction.Create(OpCodes.Brfalse, Lhave),
                Instruction.CreateLdcI4(32),
                Instruction.Create(OpCodes.Call, allocHGlobal),
                Instruction.Create(OpCodes.Stsfld, fBuf),
                Lhave,

                // 有锚点（记录过点击）就跳过；否则退回客户区左下角附近（聊天栏一般在那儿）
                Instruction.Create(OpCodes.Ldsfld, fX),
                Instruction.Create(OpCodes.Ldsfld, fY),
                Instruction.Create(OpCodes.Or),
                Instruction.Create(OpCodes.Brtrue, LhaveAnchor),

                // GetClientRect(hwnd, buf) → buf[12] = 客户区高度
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getHwnd),
                Instruction.Create(OpCodes.Ldsfld, fBuf),
                Instruction.Create(OpCodes.Call, getClientRect),
                Instruction.Create(OpCodes.Pop),
                Instruction.CreateLdcI4(24),
                Instruction.Create(OpCodes.Stsfld, fX),
                Instruction.Create(OpCodes.Ldsfld, fBuf), Instruction.CreateLdcI4(12), Instruction.Create(OpCodes.Call, readInt32),
                Instruction.CreateLdcI4(40), Instruction.Create(OpCodes.Sub),
                Instruction.Create(OpCodes.Stsfld, fY),

                LhaveAnchor,

                // CreateCaret(hwnd, 0, 1, 1) + SetCaretPos(x,y) + ShowCaret(hwnd)
                Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, getHwnd),
                Instruction.Create(OpCodes.Ldsfld, intPtrZero),
                Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Ldc_I4_1),
                Instruction.Create(OpCodes.Call, createCaret), Instruction.Create(OpCodes.Pop),

                Instruction.Create(OpCodes.Ldsfld, fX), Instruction.Create(OpCodes.Ldsfld, fY),
                Instruction.Create(OpCodes.Call, setCaretPos), Instruction.Create(OpCodes.Pop),

                Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, getHwnd),
                Instruction.Create(OpCodes.Call, showCaret), Instruction.Create(OpCodes.Pop),

                // COMPOSITIONFORM { dwStyle = CFS_POINT|CFS_FORCE_POSITION, ptCurrentPos = (x,y) } —— 客户区坐标
                Instruction.Create(OpCodes.Ldsfld, fBuf), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.CreateLdcI4(0x22), Instruction.Create(OpCodes.Call, writeInt32),
                Instruction.Create(OpCodes.Ldsfld, fBuf), Instruction.Create(OpCodes.Ldc_I4_4), Instruction.Create(OpCodes.Ldsfld, fX), Instruction.Create(OpCodes.Call, writeInt32),
                Instruction.Create(OpCodes.Ldsfld, fBuf), Instruction.Create(OpCodes.Ldc_I4_8), Instruction.Create(OpCodes.Ldsfld, fY), Instruction.Create(OpCodes.Call, writeInt32),
                Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, getHwnd), Instruction.Create(OpCodes.Call, immGetContext),
                Instruction.Create(OpCodes.Ldsfld, fBuf), Instruction.Create(OpCodes.Call, immSetCompositionWindow), Instruction.Create(OpCodes.Pop),

                Lskip,
            };

            var all = System.Linq.Enumerable.Concat(System.Linq.Enumerable.Concat(clickBlock, posBlock), block).ToArray();
            for (int k = 0; k < all.Length; k++) ins.Insert(k, all[k]);

            if (mode == "imediag")
            {
                foreach (var bi in all)
                {
                    int pops = 0, pushes = 0;
                    bi.CalculateStackUsage(false, out pushes, out pops);
                    Console.WriteLine(string.Format("  {0,-14} {1,-28} pops={2} pushes={3}", bi.OpCode.Name,
                        bi.Operand == null ? "" : bi.Operand.ToString(), pops, pushes));
                }
                return 0;
            }

            if (method.Body.MaxStack < 8) method.Body.MaxStack = 8;
            method.Body.SimplifyBranches();
            Console.WriteLine("  WndProc: 已插入 IME 上下文关联 + 点击锚点记录 + 候选窗定位，共 " + all.Length + " 条指令");

            // ── Fix A：WinFormsGameWindow.OnKeyPress 里置 e.Handled = true ──
            // 不置 true 的话，WinForms 的 WmImeChar 在 OnKeyPress 之后还会调用 DefWndProc，
            // 系统会再补一条 WM_CHAR，同一个字被投递两次（"你好" 变 "你好你好"）。
            var winType = mod.GetTypes().FirstOrDefault(t => t.Name == "WinFormsGameWindow");
            var onKeyPress = winType == null ? null : winType.Methods.FirstOrDefault(m => m.Name == "OnKeyPress");
            if (onKeyPress != null && onKeyPress.HasBody)
            {
                ITypeDefOrRef kpeRef = null;
                foreach (var i in onKeyPress.Body.Instructions)
                {
                    var im = i.Operand as IMethod;
                    if (im != null && im.Name == "get_KeyChar" && im.DeclaringType != null) { kpeRef = im.DeclaringType; break; }
                }
                if (kpeRef != null)
                {
                    var setHandled = new MemberRefUser(mod, "set_Handled",
                        MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.Boolean), kpeRef);
                    onKeyPress.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldarg_2));
                    onKeyPress.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Ldc_I4_1));
                    onKeyPress.Body.Instructions.Insert(2, Instruction.Create(OpCodes.Callvirt, setHandled));
                    if (onKeyPress.Body.MaxStack < 8) onKeyPress.Body.MaxStack = 8;
                    Console.WriteLine("  OnKeyPress: 已加入 e.Handled = true");
                }
                else Console.WriteLine("  OnKeyPress: 未找到 KeyPressEventArgs 引用，跳过");
            }
            else Console.WriteLine("  OnKeyPress: 未找到，跳过");

            mod.Write(args[1]);
            Console.WriteLine("OK -> " + args[1]);
            return 0;
        }

        // ──────────────────────────────────────────────────────────────
        // lanip：让局域网大厅支持 “/ip 地址:端口” 直连（配合内网穿透 / 虚拟局域网）
        //   1) TbChatInput_EnterPressed：以 /ip 开头的聊天内容不再当聊天发，
        //      而是解析出地址端口，借 HandleNetworkMessage 把一条“假游戏”注入列表，
        //      再选中它并调用原有的双击处理，直接发起加入。
        //   2) LbGameList_DoubleLeftClick：TCP 端口从写死的 1233 改成 EndPoint.Port
        //      （普通局域网条目端口是 1232 → 仍走 1233；直连条目则用用户给的端口）。
        // ──────────────────────────────────────────────────────────────
        if (mode == "lanip")
        {
            var lobby = mod.GetTypes().FirstOrDefault(t => t.Name == "LANLobby");
            if (lobby == null) { Console.WriteLine("SKIP: LANLobby 未找到"); return 0; }
            var chat = lobby.Methods.FirstOrDefault(m => m.Name == "TbChatInput_EnterPressed");
            var dbl = lobby.Methods.FirstOrDefault(m => m.Name == "LbGameList_DoubleLeftClick");
            if (chat == null || !chat.HasBody || dbl == null || !dbl.HasBody) { Console.WriteLine("SKIP: 目标方法缺失"); return 0; }
            if (lobby.Fields.Any(f => f.Name == "_moDcAddr")) { Console.WriteLine("SKIP: 这个补丁已经打过了"); return 0; }

            var cins = chat.Body.Instructions;
            var dins = dbl.Body.Instructions;

            // ---- 复用二进制里已有的引用 ----
            IMethod getText = null, setText = null, addMsg = null, getEndPoint = null, toUpper = null;
            IMethod objToString = null, concat = null, tcpCtor = null, listGetCount = null, getSelectedIndex = null, isNullOrEmpty = null;
            IField tbChat = null, lbChat = null, lbGame = null, itemsField = null, localGameField = null, stringEmpty = null, gameVersionField = null;
            foreach (var i in cins)
            {
                var im = i.Operand as IMethod; var fi = i.Operand as IField;
                if (im != null)
                {
                    if (im.Name == "get_Text" && getText == null) getText = im;
                    if (im.Name == "set_Text" && setText == null) setText = im;
                    if (im.Name == "IsNullOrEmpty" && isNullOrEmpty == null) isNullOrEmpty = im;
                }
                if (fi != null)
                {
                    if (fi.Name == "tbChatInput") tbChat = fi;
                    if (fi.Name == "lbChatMessages") lbChat = fi;
                    if (fi.Name == "lbGameList") lbGame = fi;
                    if (fi.Name == "Empty") stringEmpty = fi;
                }
            }
            foreach (var i in dins)
            {
                var im = i.Operand as IMethod; var fi = i.Operand as IField;
                if (im != null)
                {
                    if (im.Name == "AddMessage" && addMsg == null) addMsg = im;
                    if (im.Name == "get_EndPoint" && getEndPoint == null) getEndPoint = im;
                    if (im.Name == "ToUpper" && toUpper == null) toUpper = im;
                    if (im.Name == "Concat" && concat == null) concat = im;
                    if (im.Name == "ToString" && im.DeclaringType != null && im.DeclaringType.FullName == "System.Object" && objToString == null) objToString = im;
                    if (im.Name == ".ctor" && im.DeclaringType != null && im.DeclaringType.Name == "TcpClient") tcpCtor = im;
                    if (im.Name == "get_Count" && listGetCount == null) listGetCount = im;
                    if (im.Name == "get_SelectedIndex" && getSelectedIndex == null) getSelectedIndex = im;
                }
                if (fi != null)
                {
                    if (fi.Name == "Items") itemsField = fi;
                    if (fi.Name == "localGame") localGameField = fi;
                    if (fi.Name == "GAME_VERSION") gameVersionField = fi;
                    if (fi.Name == "tbChatInput") tbChat = fi;
                    if (fi.Name == "lbChatMessages") lbChat = fi;
                    if (fi.Name == "lbGameList") lbGame = fi;
                    if (fi.Name == "Empty") stringEmpty = fi;
                }
            }
            // ---- 局域网大厅 Initialize() 里的控件创建/事件挂载引用（做 UI 入口要用） ----
            var initM = lobby.Methods.FirstOrDefault(m => m.Name == "Initialize");
            IMethod getWm = null, setClientRect = null, setName = null, addLeftClick = null, addChild = null, getHeight = null, getRight = null, getX = null, evtCtor = null, rectCtor = null;
            IField btnJoinGameF = null, btnNewGameF = null, btnMainMenuF = null;
            if (initM != null && initM.HasBody)
            {
                foreach (var i in initM.Body.Instructions)
                {
                    var im2 = i.Operand as IMethod; var fi2 = i.Operand as IField;
                    if (im2 != null)
                    {
                        if (im2.Name == "get_WindowManager" && getWm == null) getWm = im2;
                        if (im2.Name == "set_ClientRectangle" && setClientRect == null) setClientRect = im2;
                        if (im2.Name == "set_Name" && setName == null) setName = im2;
                        if (im2.Name == "add_LeftClick" && addLeftClick == null) addLeftClick = im2;
                        if (im2.Name == "AddChild" && addChild == null) addChild = im2;
                        if (im2.Name == "get_Height" && getHeight == null) getHeight = im2;
                        if (im2.Name == "get_Right" && getRight == null) getRight = im2;
                        if (im2.Name == "get_X" && getX == null) getX = im2;
                        if (im2.Name == ".ctor" && im2.DeclaringType != null && im2.DeclaringType.Name == "EventHandler" && evtCtor == null) evtCtor = im2;
                        if (im2.Name == ".ctor" && im2.DeclaringType != null && im2.DeclaringType.Name == "Rectangle" && rectCtor == null) rectCtor = im2;
                    }
                    if (fi2 != null)
                    {
                        if (fi2.Name == "btnJoinGame") btnJoinGameF = fi2;
                        if (fi2.Name == "btnNewGame") btnNewGameF = fi2;
                        if (fi2.Name == "btnMainMenu") btnMainMenuF = fi2;
                    }
                }
            }
            if (getWm == null || setClientRect == null || setName == null || addLeftClick == null || addChild == null ||
                getHeight == null || getRight == null || getX == null || evtCtor == null || rectCtor == null || btnJoinGameF == null || btnNewGameF == null || btnMainMenuF == null)
            {
                Console.WriteLine("SKIP: Initialize 里的控件引用没找齐（getWm=" + (getWm != null) + " rect=" + (rectCtor != null) +
                    " evt=" + (evtCtor != null) + " addChild=" + (addChild != null) + " btnJoin=" + (btnJoinGameF != null) + "）");
                return 0;
            }

            if (getText == null || setText == null || addMsg == null || getEndPoint == null || toUpper == null ||
                objToString == null || concat == null || tcpCtor == null || listGetCount == null || getSelectedIndex == null ||
                tbChat == null || lbChat == null || lbGame == null || itemsField == null || localGameField == null ||
                stringEmpty == null || gameVersionField == null)
            {
                Console.WriteLine("SKIP: 有引用没找到（getText=" + (getText != null) + " tbChat=" + (tbChat != null) +
                    " lbGame=" + (lbGame != null) + " items=" + (itemsField != null) + " localGame=" + (localGameField != null) +
                    " gv=" + (gameVersionField != null) + " tcp=" + (tcpCtor != null) + " concat=" + (concat != null) + "）");
                return 0;
            }

            var handleNet = lobby.Methods.FirstOrDefault(m => m.Name == "HandleNetworkMessage");
            if (handleNet == null) { Console.WriteLine("SKIP: HandleNetworkMessage 未找到"); return 0; }
            var dblSelf = dbl;

            // ---- 类型引用 ----
            var ipEpSig = getEndPoint.MethodSig.RetType;                 // System.Net.IPEndPoint
            var ipEpType = ipEpSig.ToTypeDefOrRef();
            var systemRef = ((TypeRef)ipEpType).ResolutionScope;
            var ipAddrType = new TypeRefUser(mod, "System.Net", "IPAddress", systemRef);
            var ipAddrSig = ipAddrType.ToTypeSig();
            var eventArgsSig = chat.MethodSig.Params[1];                 // System.EventArgs
            var eventArgsType = eventArgsSig.ToTypeDefOrRef();

            // ---- 新增 MemberRef ----
            var strType = mod.CorLibTypes.String.ToTypeDefOrRef();
            var startsWith = new MemberRefUser(mod, "StartsWith", MethodSig.CreateInstance(mod.CorLibTypes.Boolean, mod.CorLibTypes.String), strType);
            var substr1 = new MemberRefUser(mod, "Substring", MethodSig.CreateInstance(mod.CorLibTypes.String, mod.CorLibTypes.Int32), strType);
            var substr2 = new MemberRefUser(mod, "Substring", MethodSig.CreateInstance(mod.CorLibTypes.String, mod.CorLibTypes.Int32, mod.CorLibTypes.Int32), strType);
            var lastIdx = new MemberRefUser(mod, "LastIndexOf", MethodSig.CreateInstance(mod.CorLibTypes.Int32, mod.CorLibTypes.Char), strType);
            var trimM = new MemberRefUser(mod, "Trim", MethodSig.CreateInstance(mod.CorLibTypes.String), strType);
            var strJoin = new MemberRefUser(mod, "Join", MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String, new SZArraySig(mod.CorLibTypes.String)), strType);
            var replaceCC = new MemberRefUser(mod, "Replace", MethodSig.CreateInstance(mod.CorLibTypes.String, mod.CorLibTypes.Char, mod.CorLibTypes.Char), strType);
            var lastIdxAny = new MemberRefUser(mod, "LastIndexOfAny", MethodSig.CreateInstance(mod.CorLibTypes.Int32, new SZArraySig(mod.CorLibTypes.Char)), strType);
            var strEquals = new MemberRefUser(mod, "Equals", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.String, mod.CorLibTypes.String), strType);
            var intTryParse = new MemberRefUser(mod, "TryParse", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.String, new ByRefSig(mod.CorLibTypes.Int32)), mod.CorLibTypes.Int32.ToTypeDefOrRef());
            var ipTryParse = new MemberRefUser(mod, "TryParse", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.String, new ByRefSig(ipAddrSig)), ipAddrType);
            var ipEpCtor = new MemberRefUser(mod, ".ctor", MethodSig.CreateInstance(mod.CorLibTypes.Void, ipAddrSig, mod.CorLibTypes.Int32), ipEpType);
            var ipEpGetPort = new MemberRefUser(mod, "get_Port", MethodSig.CreateInstance(mod.CorLibTypes.Int32), ipEpType);
            var eventArgsEmpty = new MemberRefUser(mod, "Empty", new FieldSig(eventArgsSig), eventArgsType);
            var setSelectedIndex = new MemberRefUser(mod, "set_SelectedIndex", MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.Int32), getSelectedIndex.DeclaringType);
            // 注意：不能用二进制里已有的 Concat —— 那可能是三参数重载，按两参数用会让栈错位。
            var concat2 = new MemberRefUser(mod, "Concat", MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.String), strType);

            // ---- 新增静态字段（当临时变量用：dnlib 不会给新 Local 分配索引） ----
            var fAddr = new FieldDefUser("_moDcAddr", new FieldSig(mod.CorLibTypes.String), FieldAttributes.Private | FieldAttributes.Static);
            var fHost = new FieldDefUser("_moDcHost", new FieldSig(mod.CorLibTypes.String), FieldAttributes.Private | FieldAttributes.Static);
            var fPortStr = new FieldDefUser("_moDcPortStr", new FieldSig(mod.CorLibTypes.String), FieldAttributes.Private | FieldAttributes.Static);
            var fJoined = new FieldDefUser("_moDcJoined", new FieldSig(mod.CorLibTypes.String), FieldAttributes.Private | FieldAttributes.Static);
            var fIdx = new FieldDefUser("_moDcIdx", new FieldSig(mod.CorLibTypes.Int32), FieldAttributes.Private | FieldAttributes.Static);
            var fPort = new FieldDefUser("_moDcPort", new FieldSig(mod.CorLibTypes.Int32), FieldAttributes.Private | FieldAttributes.Static);
            var fIp = new FieldDefUser("_moDcIp", new FieldSig(ipAddrSig), FieldAttributes.Private | FieldAttributes.Static);
            var fEp = new FieldDefUser("_moDcEp", new FieldSig(ipEpSig), FieldAttributes.Private | FieldAttributes.Static);
            foreach (var f in new[] { fAddr, fHost, fPortStr, fJoined, fIdx, fPort, fIp, fEp }) lobby.Fields.Add(f);

            // ================= 新 UI：地址输入框 + 「直连」按钮（取代 /ip 聊天命令） =================
            var tbChatType = tbChat.FieldSig.Type.ToTypeDefOrRef();      // XNAChatTextBox
            var btnClsType = btnNewGameF.FieldSig.Type.ToTypeDefOrRef(); // ClientGUI.XNAClientButton
            // XNASuggestionTextBox 属于 Rampastring.XNAUI，必须用 XNAUI 自己的程序集引用，
            // 不能借用 XNAChatTextBox（那是 ClientGUI 的类型，运行时会 TypeLoadException）。
            IResolutionScope xnaUiRef = null;
            foreach (var tr in mod.GetTypeRefs())
            {
                var ns = (string)tr.Namespace;
                if (ns != null && ns.StartsWith("Rampastring.XNAUI") && tr.ResolutionScope is AssemblyRef)
                { xnaUiRef = tr.ResolutionScope; break; }
            }
            if (xnaUiRef == null) { Console.WriteLine("  SKIP: 找不到 Rampastring.XNAUI 的程序集引用"); return 0; }
            var sugType = new TypeRefUser(mod, "Rampastring.XNAUI.XNAControls", "XNASuggestionTextBox", xnaUiRef);
            var wmSig = getWm.MethodSig.RetType;
            var evtSig = evtCtor.DeclaringType.ToTypeSig();
            var sugCtor = new MemberRefUser(mod, ".ctor", MethodSig.CreateInstance(mod.CorLibTypes.Void, wmSig), sugType);
            var sugSetSuggestion = new MemberRefUser(mod, "set_Suggestion", MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.String), sugType);
            var sugSetFontIndex = new MemberRefUser(mod, "set_FontIndex", MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.Int32), sugType);
            var tbGetFontIndex = new MemberRefUser(mod, "get_FontIndex", MethodSig.CreateInstance(mod.CorLibTypes.Int32), tbChatType);
            var tbAddEnter = new MemberRefUser(mod, "add_EnterPressed", MethodSig.CreateInstance(mod.CorLibTypes.Void, evtSig), sugType);
            var btnCtor = new MemberRefUser(mod, ".ctor", MethodSig.CreateInstance(mod.CorLibTypes.Void, wmSig), btnClsType);

            var fTbIp = new FieldDefUser("tbDirectIP", new FieldSig(sugType.ToTypeSig()), FieldAttributes.Private);
            var fBtnDc = new FieldDefUser("btnDirectConnect", new FieldSig(btnClsType.ToTypeSig()), FieldAttributes.Private);
            lobby.Fields.Add(fTbIp); lobby.Fields.Add(fBtnDc);

            // ---- 插进 TbChatInput_EnterPressed（它没有 try 块；BtnJoinGame_LeftClick 里有 try，
            //      往那里插会形成"从外部跳进 try 区域"的非法 IL）----
            //      守卫：只有「地址框非空」时才走直连，否则落到原有聊天逻辑。
            IMethod dcHandler = chat;
            var Lbad = Instruction.Create(OpCodes.Nop);
            var LnoSpace = Instruction.Create(OpCodes.Nop);
            var LnoColon = Instruction.Create(OpCodes.Nop);
            var Lparsed = Instruction.Create(OpCodes.Nop);
            var blk = new System.Collections.Generic.List<Instruction>();
            Action<OpCode> o = c => blk.Add(Instruction.Create(c));
            Action<int> i4 = v => blk.Add(Instruction.CreateLdcI4(v));

            // addr = tbDirectIP.Text.Trim()；空就直接提示
            o(OpCodes.Ldarg_0); blk.Add(Instruction.Create(OpCodes.Ldfld, fTbIp)); blk.Add(Instruction.Create(OpCodes.Callvirt, getText));
            blk.Add(Instruction.Create(OpCodes.Callvirt, trimM)); blk.Add(Instruction.Create(OpCodes.Stsfld, fAddr));
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fAddr)); blk.Add(Instruction.Create(OpCodes.Call, isNullOrEmpty));
            blk.Add(Instruction.Create(OpCodes.Brtrue, Lbad));
            // addr = addr.Replace('：', ':')   —— 兼容中文冒号（粘贴来源可能是中文界面）
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fAddr)); i4(65306); i4(58);
            blk.Add(Instruction.Create(OpCodes.Callvirt, replaceCC)); blk.Add(Instruction.Create(OpCodes.Stsfld, fAddr));
            // addr = 最后一个空白分隔片段（这样 "服务器: 1.2.3.4:1234" 这类粘贴也能认出地址）
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fAddr));
            i4(4); blk.Add(Instruction.Create(OpCodes.Newarr, mod.CorLibTypes.Char.ToTypeDefOrRef()));
            Action<int, int> cch = (ix, v) => { o(OpCodes.Dup); i4(ix); i4(v); o(OpCodes.Stelem_I2); };
            cch(0, 32); cch(1, 9); cch(2, 13); cch(3, 10);
            blk.Add(Instruction.Create(OpCodes.Callvirt, lastIdxAny)); blk.Add(Instruction.Create(OpCodes.Stsfld, fIdx));
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fIdx)); i4(0); blk.Add(Instruction.Create(OpCodes.Blt, LnoSpace));
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fAddr)); blk.Add(Instruction.Create(OpCodes.Ldsfld, fIdx)); i4(1); o(OpCodes.Add);
            blk.Add(Instruction.Create(OpCodes.Callvirt, substr1)); blk.Add(Instruction.Create(OpCodes.Stsfld, fAddr));
            blk.Add(LnoSpace);
            // idx = addr.LastIndexOf(':')
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fAddr)); i4(58); blk.Add(Instruction.Create(OpCodes.Callvirt, lastIdx));
            blk.Add(Instruction.Create(OpCodes.Stsfld, fIdx));
            // 没有冒号 → 当作只有 IP，端口默认 1233
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fIdx)); i4(0); blk.Add(Instruction.Create(OpCodes.Blt, LnoColon));
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fIdx)); i4(0); blk.Add(Instruction.Create(OpCodes.Ble, Lbad));
            // host = addr.Substring(0, idx)
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fAddr)); i4(0); blk.Add(Instruction.Create(OpCodes.Ldsfld, fIdx));
            blk.Add(Instruction.Create(OpCodes.Callvirt, substr2)); blk.Add(Instruction.Create(OpCodes.Stsfld, fHost));
            // portStr = addr.Substring(idx + 1)
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fAddr)); blk.Add(Instruction.Create(OpCodes.Ldsfld, fIdx)); i4(1); o(OpCodes.Add);
            blk.Add(Instruction.Create(OpCodes.Callvirt, substr1)); blk.Add(Instruction.Create(OpCodes.Stsfld, fPortStr));
            blk.Add(Instruction.Create(OpCodes.Br, Lparsed));
            blk.Add(LnoColon);
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fAddr)); blk.Add(Instruction.Create(OpCodes.Stsfld, fHost));
            blk.Add(Instruction.Create(OpCodes.Ldstr, "1233")); blk.Add(Instruction.Create(OpCodes.Stsfld, fPortStr));
            blk.Add(Lparsed);
            // int.TryParse(portStr, out _moDcPort) + 端口范围
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fPortStr)); blk.Add(Instruction.Create(OpCodes.Ldsflda, fPort));
            blk.Add(Instruction.Create(OpCodes.Call, intTryParse)); blk.Add(Instruction.Create(OpCodes.Brfalse, Lbad));
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fPort)); i4(1); blk.Add(Instruction.Create(OpCodes.Blt, Lbad));
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fPort)); i4(65535); blk.Add(Instruction.Create(OpCodes.Bgt, Lbad));
            // IPAddress.TryParse(host, out _moDcIp)
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fHost)); blk.Add(Instruction.Create(OpCodes.Ldsflda, fIp));
            blk.Add(Instruction.Create(OpCodes.Call, ipTryParse)); blk.Add(Instruction.Create(OpCodes.Brfalse, Lbad));
            // _moDcEp = new IPEndPoint(ip, port)
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fIp)); blk.Add(Instruction.Create(OpCodes.Ldsfld, fPort));
            blk.Add(Instruction.Create(OpCodes.Newobj, ipEpCtor)); blk.Add(Instruction.Create(OpCodes.Stsfld, fEp));
            // 先 ALIVE（让 HandleNetworkMessage 认识这个端点），再 GAME（注入列表条目）
            o(OpCodes.Ldarg_0); blk.Add(Instruction.Create(OpCodes.Ldstr, "ALIVE -1\u0001Host")); blk.Add(Instruction.Create(OpCodes.Ldsfld, fEp));
            blk.Add(Instruction.Create(OpCodes.Call, handleNet));
            // 注意：这里只压 \u0001 + 数组，不能再多压 ldarg.0/"GAME "（否则整块结尾会多出栈元素）
            blk.Add(Instruction.Create(OpCodes.Ldstr, "\u0001"));
            i4(9); blk.Add(Instruction.Create(OpCodes.Newarr, strType));
            Action<int, string> elem = (idx, s) => { o(OpCodes.Dup); i4(idx); blk.Add(Instruction.Create(OpCodes.Ldstr, s)); o(OpCodes.Stelem_Ref); };
            elem(0, "RL5");
            o(OpCodes.Dup); i4(1); blk.Add(Instruction.Create(OpCodes.Ldsfld, gameVersionField)); o(OpCodes.Stelem_Ref);
            o(OpCodes.Dup); i4(2); o(OpCodes.Ldarg_0); blk.Add(Instruction.Create(OpCodes.Ldfld, localGameField)); blk.Add(Instruction.Create(OpCodes.Callvirt, toUpper)); o(OpCodes.Stelem_Ref);
            elem(3, "-"); elem(4, "-"); elem(5, "0"); elem(6, "Host"); elem(7, "0"); elem(8, "0");
            blk.Add(Instruction.Create(OpCodes.Call, strJoin));
            blk.Add(Instruction.Create(OpCodes.Stsfld, fJoined));
            o(OpCodes.Ldarg_0); blk.Add(Instruction.Create(OpCodes.Ldstr, "GAME ")); blk.Add(Instruction.Create(OpCodes.Ldsfld, fJoined));
            blk.Add(Instruction.Create(OpCodes.Call, concat2));
            blk.Add(Instruction.Create(OpCodes.Ldsfld, fEp));
            blk.Add(Instruction.Create(OpCodes.Call, handleNet));
            // 清空输入框
            o(OpCodes.Ldarg_0); blk.Add(Instruction.Create(OpCodes.Ldfld, fTbIp)); blk.Add(Instruction.Create(OpCodes.Ldsfld, stringEmpty)); blk.Add(Instruction.Create(OpCodes.Callvirt, setText));
            // lbGameList.SelectedIndex = lbGameList.Items.Count - 1
            o(OpCodes.Ldarg_0); blk.Add(Instruction.Create(OpCodes.Ldfld, lbGame)); o(OpCodes.Dup);
            blk.Add(Instruction.Create(OpCodes.Ldfld, itemsField)); blk.Add(Instruction.Create(OpCodes.Callvirt, listGetCount)); i4(1); o(OpCodes.Sub);
            blk.Add(Instruction.Create(OpCodes.Callvirt, setSelectedIndex));
            // 直接发起加入（事件处理器签名 (object, EventArgs)）
            o(OpCodes.Ldarg_0); o(OpCodes.Ldnull); blk.Add(Instruction.Create(OpCodes.Ldsfld, eventArgsEmpty));
            blk.Add(Instruction.Create(OpCodes.Call, dblSelf));
            o(OpCodes.Ret);

            // Lbad：格式不对 → 聊天区提示
            blk.Add(Lbad);
            o(OpCodes.Ldarg_0); blk.Add(Instruction.Create(OpCodes.Ldfld, lbChat));
            blk.Add(Instruction.Create(OpCodes.Ldstr, "请输入 地址:端口 后点「直连」，例如 192.168.1.5:1233（只支持 IP，不支持域名）"));
            blk.Add(Instruction.Create(OpCodes.Callvirt, addMsg));
            o(OpCodes.Ldarg_0); blk.Add(Instruction.Create(OpCodes.Ldfld, fTbIp)); blk.Add(Instruction.Create(OpCodes.Ldsfld, stringEmpty)); blk.Add(Instruction.Create(OpCodes.Callvirt, setText));
            o(OpCodes.Ret);

            // ---- 调试：打印 depth-0 边界，并支持 MO_LIMIT 截断（二分定位非法指令用） ----
            {
                int dcur = 0;
                var zeros = new System.Collections.Generic.List<int>();
                for (int k = 0; k < blk.Count; k++)
                {
                    if (dcur == 0) zeros.Add(k);
                    int pops2, pushes2; blk[k].CalculateStackUsage(false, out pushes2, out pops2);
                    dcur = dcur - pops2 + pushes2;
                }
                if (dcur == 0) zeros.Add(blk.Count);
                Console.WriteLine("  [调试] depth-0 边界: " + string.Join(",", zeros));
                int lim = 0;
                if (int.TryParse(Environment.GetEnvironmentVariable("MO_LIMIT"), out lim) && lim > 0 && lim < blk.Count)
                {
                    var cut = blk.GetRange(0, lim);
                    cut.Add(Lbad);
                    cut.Add(Instruction.Create(OpCodes.Ret));
                    blk = cut;
                    Console.WriteLine("  [调试] MO_LIMIT=" + lim + " → 截断为 " + blk.Count + " 条");
                }
            }

            // 用 sender 区分是哪个输入框触发：只有地址框才走直连；其它（聊天框）完全走原逻辑。
            // XNAUI 触发 EnterPressed 时传的 sender 就是那个文本框本身（已从 XNATextBox 的 IL 确认）。
            const string SUGGEST = "输入 地址:端口 后回车直连";
            var Lchat = Instruction.Create(OpCodes.Nop);
            var all = new System.Collections.Generic.List<Instruction>();
            all.Add(Instruction.Create(OpCodes.Ldarg_1));                    // sender
            all.Add(Instruction.Create(OpCodes.Ldarg_0));
            all.Add(Instruction.Create(OpCodes.Ldfld, fTbIp));               // this.tbDirectIP
            all.Add(Instruction.Create(OpCodes.Ceq));
            all.Add(Instruction.Create(OpCodes.Brfalse, Lchat));
            foreach (var ins2 in blk) all.Add(ins2);
            all.Add(Lchat);

            var jins = chat.Body.Instructions;
            for (int k = 0; k < all.Count; k++) jins.Insert(k, all[k]);
            chat.Body.MaxStack = 16;
            // chat.Body.SimplifyBranches();  // 不调用：跳转距离可能超过短跳转范围
            Console.WriteLine("  TbChatInput_EnterPressed: 已加入直连入口，共 " + all.Count + " 条指令");

            // ---- 把两个控件挂进 Initialize() ----
            var ublk = new System.Collections.Generic.List<Instruction>();
            Action<OpCode> u = c => ublk.Add(Instruction.Create(c));
            Action<int> u4 = v => ublk.Add(Instruction.CreateLdcI4(v));

            // tbDirectIP = new XNASuggestionTextBox(WindowManager)
            u(OpCodes.Ldarg_0); u(OpCodes.Ldarg_0); ublk.Add(Instruction.Create(OpCodes.Call, getWm));
            ublk.Add(Instruction.Create(OpCodes.Newobj, sugCtor)); ublk.Add(Instruction.Create(OpCodes.Stfld, fTbIp));
            u(OpCodes.Ldarg_0); ublk.Add(Instruction.Create(OpCodes.Ldfld, fTbIp)); ublk.Add(Instruction.Create(OpCodes.Ldstr, "tbDirectIP"));
            ublk.Add(Instruction.Create(OpCodes.Callvirt, setName));
            // 字体：跟底部按钮保持一致（聊天框不是 XNATextBox 子孙，取它的字体/配色会 MissingMethod）
            var btnTypeSig = btnNewGameF.FieldSig.Type;
            var btnGetFontIndex = new MemberRefUser(mod, "get_FontIndex", MethodSig.CreateInstance(mod.CorLibTypes.Int32), btnTypeSig.ToTypeDefOrRef());
            u(OpCodes.Ldarg_0); ublk.Add(Instruction.Create(OpCodes.Ldfld, fTbIp));
            u(OpCodes.Ldarg_0); ublk.Add(Instruction.Create(OpCodes.Ldfld, btnNewGameF)); ublk.Add(Instruction.Create(OpCodes.Callvirt, btnGetFontIndex));
            ublk.Add(Instruction.Create(OpCodes.Callvirt, sugSetFontIndex));
            // 输入框：靠右对齐，紧贴「返回主菜单」左侧（272 = 12 间距 + 260 输入框，已去掉旁边的按钮）
            u(OpCodes.Ldarg_0); ublk.Add(Instruction.Create(OpCodes.Ldfld, fTbIp));
            u(OpCodes.Ldarg_0); ublk.Add(Instruction.Create(OpCodes.Ldfld, btnMainMenuF)); ublk.Add(Instruction.Create(OpCodes.Callvirt, getX)); u4(272); u(OpCodes.Sub);
            u(OpCodes.Ldarg_0); ublk.Add(Instruction.Create(OpCodes.Call, getHeight)); u4(35); u(OpCodes.Sub);
            u4(260); u4(23); ublk.Add(Instruction.Create(OpCodes.Newobj, rectCtor));
            ublk.Add(Instruction.Create(OpCodes.Callvirt, setClientRect));
            // Suggestion
            u(OpCodes.Ldarg_0); ublk.Add(Instruction.Create(OpCodes.Ldfld, fTbIp));
            ublk.Add(Instruction.Create(OpCodes.Ldstr, SUGGEST)); ublk.Add(Instruction.Create(OpCodes.Callvirt, sugSetSuggestion));
            // EnterPressed += DirectConnect_LeftClick
            u(OpCodes.Ldarg_0); ublk.Add(Instruction.Create(OpCodes.Ldfld, fTbIp));
            u(OpCodes.Ldarg_0); ublk.Add(Instruction.Create(OpCodes.Ldftn, dcHandler)); ublk.Add(Instruction.Create(OpCodes.Newobj, evtCtor));
            ublk.Add(Instruction.Create(OpCodes.Callvirt, tbAddEnter));
            // AddChild（只挂输入框；不再放单独的按钮，避免和底部按钮排抢视觉）
            u(OpCodes.Ldarg_0); u(OpCodes.Ldarg_0); ublk.Add(Instruction.Create(OpCodes.Ldfld, fTbIp)); ublk.Add(Instruction.Create(OpCodes.Call, addChild));

            var iins = initM.Body.Instructions;
            int anchor = -1;
            for (int k = 0; k < iins.Count; k++)
            {
                var im3 = iins[k].Operand as IMethod;
                if (iins[k].OpCode.Code == dnlib.DotNet.Emit.Code.Newobj && im3 != null && im3.DeclaringType != null &&
                    im3.DeclaringType.Name == "LANGameCreationWindow") { anchor = k; break; }
            }
            if (anchor < 0)
                for (int k = iins.Count - 1; k >= 0; k--)
                {
                    var im3 = iins[k].Operand as IMethod;
                    if (im3 != null && im3.Name == "AddChild") { anchor = k + 1; break; }
                }
            if (anchor < 0) { Console.WriteLine("  SKIP: Initialize 里找不到插入点"); }
            else
            {
                for (int k = 0; k < ublk.Count; k++) iins.Insert(anchor + k, ublk[k]);
                if (initM.Body.MaxStack < 16) initM.Body.MaxStack = 16;
                initM.Body.SimplifyBranches();
                Console.WriteLine("  LANLobby.Initialize: 已加入「地址输入框 + 直连按钮」，共 " + ublk.Count + " 条指令");
            }

            // ---- 改 TCP 端口：优先用 EndPoint.Port（普通局域网条目端口是 1232 → 仍用 1233） ----
            int ti = -1;
            for (int k = 0; k < dins.Count; k++)
            {
                var im = dins[k].Operand as IMethod;
                if (dins[k].OpCode.Code == dnlib.DotNet.Emit.Code.Newobj && im != null && im.Name == ".ctor" &&
                    im.DeclaringType != null && im.DeclaringType.Name == "TcpClient") { ti = k; break; }
            }
            if (ti < 1) { Console.WriteLine("  SKIP: 没找到 TcpClient 构造点"); }
            else
            {
                Instruction portConst = dins[ti - 1];
                if (portConst.OpCode.Code != dnlib.DotNet.Emit.Code.Ldc_I4 || portConst.GetLdcI4Value() != 1233)
                {
                    Console.WriteLine("  SKIP: TcpClient 前的常量不是 1233（实际 " + portConst.OpCode.Name + "）");
                }
                else
                {
                    // 往回找 hg 的 ldloc（注意 ldloc.0 这种短格式）
                    Local hgLoc = null;
                    for (int k = ti - 2; k >= 0 && k > ti - 12; k--)
                    {
                        var cc = dins[k].OpCode.Code;
                        if (cc == dnlib.DotNet.Emit.Code.Ldloc || cc == dnlib.DotNet.Emit.Code.Ldloc_S)
                        { hgLoc = (Local)dins[k].Operand; break; }
                        if (cc >= dnlib.DotNet.Emit.Code.Ldloc_0 && cc <= dnlib.DotNet.Emit.Code.Ldloc_3)
                        { hgLoc = dbl.Body.Variables[(int)(cc - dnlib.DotNet.Emit.Code.Ldloc_0)]; break; }
                    }
                    if (hgLoc == null) { Console.WriteLine("  SKIP: 没找到 hg 的 ldloc"); }
                    else
                    {
                        var Lkeep = Instruction.Create(OpCodes.Nop);
                        var portSeq = new[]
                        {
                            Instruction.Create(OpCodes.Ldloc, hgLoc),
                            Instruction.Create(OpCodes.Callvirt, getEndPoint),
                            Instruction.Create(OpCodes.Callvirt, ipEpGetPort),
                            Instruction.Create(OpCodes.Dup),
                            Instruction.CreateLdcI4(1232),
                            Instruction.Create(OpCodes.Bne_Un, Lkeep),
                            Instruction.Create(OpCodes.Pop),
                            Instruction.CreateLdcI4(1233),
                            Lkeep,
                        };
                        dins.RemoveAt(ti - 1);
                        for (int k = 0; k < portSeq.Length; k++) dins.Insert(ti - 1 + k, portSeq[k]);
                        if (dbl.Body.MaxStack < 16) dbl.Body.MaxStack = 16;
                        dbl.Body.SimplifyBranches();
                        Console.WriteLine("  LbGameList_DoubleLeftClick: TCP 端口已改为 EndPoint.Port（非 1232 时生效）");
                    }
                }
            }

            // ---- 与 HMOL 对接：进局域网大厅时读 <游戏根目录>\lanip.txt，内容 = 公网地址:端口 ----
            //     读到就删掉（只消费一次），然后当成用户在聊天框里敲了 “/ip 地址:端口”，
            //     复用上面那套逻辑直接发起加入。
            var openM = lobby.Methods.FirstOrDefault(m => m.Name == "SendAlive");
            if (openM == null || !openM.HasBody) { Console.WriteLine("  SKIP: 没找到 SendAlive()，跳过 HMOL 对接"); return 0; }
            var oins = openM.Body.Instructions;

            var gamePathField = (IField)null;
            foreach (var i in dins) { var fi = i.Operand as IField; if (fi != null && fi.Name == "GamePath") { gamePathField = fi; break; } }
            if (gamePathField == null) foreach (var i in cins) { var fi = i.Operand as IField; if (fi != null && fi.Name == "GamePath") { gamePathField = fi; break; } }
            if (gamePathField == null || isNullOrEmpty == null)
            {
                Console.WriteLine("  SKIP: GamePath/IsNullOrEmpty 未找到，跳过 HMOL 对接");
            }
            else
            {
                var corlibRef = ((TypeRef)mod.CorLibTypes.Object.ToTypeDefOrRef()).ResolutionScope;
                var fileType = new TypeRefUser(mod, "System.IO", "File", corlibRef);
                var fileExists = new MemberRefUser(mod, "Exists", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.String), fileType);
                var fileReadAllText = new MemberRefUser(mod, "ReadAllText", MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String), fileType);
                var fileDelete = new MemberRefUser(mod, "Delete", MethodSig.CreateStatic(mod.CorLibTypes.Void, mod.CorLibTypes.String), fileType);

                var fPath = new FieldDefUser("_moDcPath", new FieldSig(mod.CorLibTypes.String), FieldAttributes.Private | FieldAttributes.Static);
                var fTxt = new FieldDefUser("_moDcTxt", new FieldSig(mod.CorLibTypes.String), FieldAttributes.Private | FieldAttributes.Static);
                var fDone = new FieldDefUser("_moDcDone", new FieldSig(mod.CorLibTypes.Boolean), FieldAttributes.Private | FieldAttributes.Static);
                lobby.Fields.Add(fPath);
                lobby.Fields.Add(fTxt);
                lobby.Fields.Add(fDone);

                var Lend = Instruction.Create(OpCodes.Nop);
                var blk2 = new System.Collections.Generic.List<Instruction>();
                Action<OpCode> o2 = c => blk2.Add(Instruction.Create(c));

                // 只跑一次
                blk2.Add(Instruction.Create(OpCodes.Ldsfld, fDone));
                blk2.Add(Instruction.Create(OpCodes.Brtrue, Lend));
                blk2.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                blk2.Add(Instruction.Create(OpCodes.Stsfld, fDone));
                // _moDcPath = ProgramConstants.GamePath + "lanip.txt"
                blk2.Add(Instruction.Create(OpCodes.Ldsfld, gamePathField)); blk2.Add(Instruction.Create(OpCodes.Ldstr, "lanip.txt"));
                blk2.Add(Instruction.Create(OpCodes.Call, concat2)); blk2.Add(Instruction.Create(OpCodes.Stsfld, fPath));
                // if (!File.Exists(path)) goto end
                blk2.Add(Instruction.Create(OpCodes.Ldsfld, fPath)); blk2.Add(Instruction.Create(OpCodes.Call, fileExists));
                blk2.Add(Instruction.Create(OpCodes.Brfalse, Lend));
                // _moDcTxt = File.ReadAllText(path); File.Delete(path);
                blk2.Add(Instruction.Create(OpCodes.Ldsfld, fPath)); blk2.Add(Instruction.Create(OpCodes.Call, fileReadAllText));
                blk2.Add(Instruction.Create(OpCodes.Stsfld, fTxt));
                blk2.Add(Instruction.Create(OpCodes.Ldsfld, fPath)); blk2.Add(Instruction.Create(OpCodes.Call, fileDelete));
                // if (IsNullOrEmpty(txt)) goto end
                blk2.Add(Instruction.Create(OpCodes.Ldsfld, fTxt)); blk2.Add(Instruction.Create(OpCodes.Call, isNullOrEmpty));
                blk2.Add(Instruction.Create(OpCodes.Brtrue, Lend));
                blk2.Add(Instruction.Create(OpCodes.Ldsfld, fTxt)); blk2.Add(Instruction.Create(OpCodes.Callvirt, trimM));
                blk2.Add(Instruction.Create(OpCodes.Stsfld, fTxt));
                blk2.Add(Instruction.Create(OpCodes.Ldsfld, fTxt)); blk2.Add(Instruction.Create(OpCodes.Call, isNullOrEmpty));
                blk2.Add(Instruction.Create(OpCodes.Brtrue, Lend));
                // tbDirectIP.Text = txt，然后触发直连
                o2(OpCodes.Ldarg_0); blk2.Add(Instruction.Create(OpCodes.Ldfld, fTbIp));
                blk2.Add(Instruction.Create(OpCodes.Ldsfld, fTxt));
                blk2.Add(Instruction.Create(OpCodes.Callvirt, setText));
                // 触发直连（sender 必须是 tbDirectIP，否则会被当成聊天框调用而被忽略）
                o2(OpCodes.Ldarg_0); o2(OpCodes.Ldarg_0); blk2.Add(Instruction.Create(OpCodes.Ldfld, fTbIp)); blk2.Add(Instruction.Create(OpCodes.Ldsfld, eventArgsEmpty));
                blk2.Add(Instruction.Create(OpCodes.Call, dcHandler));
                blk2.Add(Lend);

                for (int k = 0; k < blk2.Count; k++) oins.Insert(k, blk2[k]);
                if (openM.Body.MaxStack < 16) openM.Body.MaxStack = 16;
                openM.Body.SimplifyBranches();
                Console.WriteLine("  LANLobby.SendAlive: 已加入 lanip.txt 对接（共 " + blk2.Count + " 条指令，只跑一次）");
            }

            Console.WriteLine("--- 栈自检 ---");
            CheckStack(chat);
            CheckStack(dbl);
            CheckStack(openM);
            CheckStack(initM);
        }

        // ──────────────────────────────────────────────────────────────
        // relay：游戏内 UDP 中继（配合樱花 Frp 单条 TCP 隧道）
        //   1) GameLobbyBase.WriteSpawnIni： [OtherN] Ip→127.0.0.1，Port→12340+序号，
        //      并在这里拉起中继（此时玩家表已固定）
        //   2) LANGameLobby.HandleClientMessage   ：识别 MO-RELAY 帧（房主侧，带发送者）
        //   3) LANGameLobby.HandleMessageFromServer：识别 MO-RELAY 帧（客机侧）
        //      两处都"识别后原样落到原逻辑"，不用 ret，避免踩 try 区域不能返回的坑。
        // ──────────────────────────────────────────────────────────────
        if (mode == "relay")
        {
            foreach (var tr in mod.GetTypeRefs())
            {
                var ns0 = (string)tr.Namespace;
                if (ns0 == "MoLanRelay" && (string)tr.Name == "Relay") { Console.WriteLine("SKIP: relay 补丁已打过"); return 0; }
            }

            var corlibRef = ((TypeRef)mod.CorLibTypes.Object.ToTypeDefOrRef()).ResolutionScope;
            var relayAsm = new AssemblyRefUser("MoLanRelay", new Version(0, 0, 0, 0));
            var relayType = new TypeRefUser(mod, "MoLanRelay", "Relay", relayAsm);
            var ilistType = new TypeRefUser(mod, "System.Collections", "IList", corlibRef);
            var relayIsMsg = new MemberRefUser(mod, "IsRelayMessage", MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.String), relayType);
            var relayOnFrame = new MemberRefUser(mod, "OnFrame", MethodSig.CreateStatic(mod.CorLibTypes.Void, mod.CorLibTypes.String, mod.CorLibTypes.String), relayType);
            var relayStart = new MemberRefUser(mod, "Start",
                MethodSig.CreateStatic(mod.CorLibTypes.Void, ilistType.ToTypeSig(), mod.CorLibTypes.String, mod.CorLibTypes.String), relayType);
            var strT = mod.CorLibTypes.String.ToTypeDefOrRef();
            var concat2 = new MemberRefUser(mod, "Concat", MethodSig.CreateStatic(mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.String), strT);

            // ---------- 1) WriteSpawnIni ----------
            var glb = mod.GetTypes().FirstOrDefault(t => t.Name == "GameLobbyBase");
            var wsi = glb == null ? null : glb.Methods.FirstOrDefault(m => m.Name == "WriteSpawnIni");
            if (wsi == null || !wsi.HasBody) { Console.WriteLine("SKIP: 找不到 WriteSpawnIni"); return 0; }

            var playersF = glb.Fields.FirstOrDefault(f => f.Name == "Players");
            if (playersF == null) { Console.WriteLine("SKIP: 找不到 Players 字段"); return 0; }

            IMethod playerNameP = null;      // PLAYERNAME 是静态属性 get_PLAYERNAME
            IField gamePathF = null;
            var wins = wsi.Body.Instructions;
            // 这两个是别的程序集里的静态字段，WriteSpawnIni 不一定引用到 → 全模块找
            foreach (var t9 in mod.GetTypes())
            {
                foreach (var m9 in t9.Methods)
                {
                    if (!m9.HasBody) continue;
                    foreach (var i9 in m9.Body.Instructions)
                    {
                        var fi9 = i9.Operand as IField;
                        if (fi9 != null && fi9.Name == "GamePath" && gamePathF == null) gamePathF = fi9;
                        var mi9 = i9.Operand as IMethod;
                        if (mi9 != null && mi9.Name == "get_PLAYERNAME" && playerNameP == null) playerNameP = mi9;
                    }
                }
                if (playerNameP != null && gamePathF != null) break;
            }
            if (playerNameP == null || gamePathF == null) { Console.WriteLine("SKIP: 找不到 PLAYERNAME/GamePath"); return 0; }

            // (a) Ip：把 ldarg.0 / ldloc pInfo / callvirt GetIPAddressForPlayer 换成 ldstr "127.0.0.1"
            int ipIdx = -1;
            for (int k = 0; k < wins.Count; k++)
            {
                if (wins[k].OpCode.Code == dnlib.DotNet.Emit.Code.Ldstr && (string)wins[k].Operand == "Ip") { ipIdx = k; break; }
            }
            if (ipIdx < 0) { Console.WriteLine("SKIP: 找不到 [OtherN] Ip"); return 0; }
            // 期望： ipIdx+1 = ldarg.0 ; +2 = ldloc pInfo ; +3 = callvirt GetIPAddressForPlayer
            int ipValStart = ipIdx + 1;
            var ipCall = wins[ipValStart + 2].Operand as IMethod;
            if (ipCall == null || ipCall.Name != "GetIPAddressForPlayer")
            { Console.WriteLine("SKIP: Ip 的取值形态不是预期的 GetIPAddressForPlayer"); return 0; }
            for (int k = 0; k < 3; k++) wins.RemoveAt(ipValStart);
            wins.Insert(ipValStart, Instruction.Create(OpCodes.Ldstr, "127.0.0.1"));
            Console.WriteLine("  WriteSpawnIni: [OtherN] Ip → 127.0.0.1");

            // (b) Port：把 ldloc pInfo / callvirt get_Port 换成 ldloc otherId / 12340 / add
            int portIdx = -1;
            for (int k = 0; k < wins.Count; k++)
            {
                if (wins[k].OpCode.Code == dnlib.DotNet.Emit.Code.Ldstr && (string)wins[k].Operand == "Port") { portIdx = k; break; }
            }
            if (portIdx < 0) { Console.WriteLine("SKIP: 找不到 [OtherN] Port"); return 0; }
            var portCall = wins[portIdx + 2].Operand as IMethod;
            if (portCall == null || portCall.Name != "get_Port") { Console.WriteLine("SKIP: Port 的取值形态不对"); return 0; }
            // 序号变量：Ip 那行上方 ldstr "Other" 之后用的是 ldloca.s V_x
            Local seqLoc = null;
            for (int k = ipIdx; k >= 0 && k > ipIdx - 80; k--)
            {
                var c = wins[k].OpCode.Code;
                if (c == dnlib.DotNet.Emit.Code.Ldloca_S || c == dnlib.DotNet.Emit.Code.Ldloca)
                { seqLoc = (Local)wins[k].Operand; break; }
            }
            if (seqLoc == null) { Console.WriteLine("SKIP: 找不到对端序号局部变量"); return 0; }
            int portValStart = portIdx + 1;
            for (int k = 0; k < 2; k++) wins.RemoveAt(portValStart);
            wins.Insert(portValStart, Instruction.Create(OpCodes.Ldloc, seqLoc));
            wins.Insert(portValStart + 1, Instruction.CreateLdcI4(12340));
            wins.Insert(portValStart + 2, Instruction.Create(OpCodes.Add));
            Console.WriteLine("  WriteSpawnIni: [OtherN] Port → 12340 + 序号（局部 V" + seqLoc.Index + "）");

            // (c) 在 [OtherN] 循环里拉起中继（DLL 侧对同一局重复调用会直接返回）
            var startSeq = new System.Collections.Generic.List<Instruction>();
            startSeq.Add(Instruction.Create(OpCodes.Ldarg_0));
            startSeq.Add(Instruction.Create(OpCodes.Ldfld, playersF));
            startSeq.Add(Instruction.Create(OpCodes.Call, playerNameP));
            startSeq.Add(Instruction.Create(OpCodes.Ldsfld, gamePathF));
            startSeq.Add(Instruction.Create(OpCodes.Ldstr, "MoLanRelay.log"));
            startSeq.Add(Instruction.Create(OpCodes.Call, concat2));
            startSeq.Add(Instruction.Create(OpCodes.Call, relayStart));
            int insAt = ipIdx - 1;              // 插在 "Ip" 那行之前（必在循环体内、直线代码）
            for (int k = 0; k < startSeq.Count; k++) wins.Insert(insAt + k, startSeq[k]);
            if (wsi.Body.MaxStack < 16) wsi.Body.MaxStack = 16;
            Console.WriteLine("  WriteSpawnIni: 已插入 Relay.Start（" + startSeq.Count + " 条）");

            // ---------- 2/3) 两个消息处理器 ----------
            var lgl = mod.GetTypes().FirstOrDefault(t => t.Name == "LANGameLobby");
            if (lgl == null) { Console.WriteLine("SKIP: 找不到 LANGameLobby"); return 0; }

            var getLpName = new MemberRefUser(mod, "get_Name", MethodSig.CreateInstance(mod.CorLibTypes.String),
                lgl.Methods.FirstOrDefault(m => m.Name == "HandleClientMessage").MethodSig.Params[1].ToTypeDefOrRef());

            var hcm = lgl.Methods.FirstOrDefault(m => m.Name == "HandleClientMessage");
            if (hcm != null && hcm.HasBody)
            {
                var skip = Instruction.Create(OpCodes.Nop);
                var seq = new System.Collections.Generic.List<Instruction>();
                seq.Add(Instruction.Create(OpCodes.Ldarg_1));           // message
                seq.Add(Instruction.Create(OpCodes.Call, relayIsMsg));
                seq.Add(Instruction.Create(OpCodes.Brfalse, skip));
                seq.Add(Instruction.Create(OpCodes.Ldarg_2));           // lpInfo
                seq.Add(Instruction.Create(OpCodes.Callvirt, getLpName));
                seq.Add(Instruction.Create(OpCodes.Ldarg_1));           // message
                seq.Add(Instruction.Create(OpCodes.Call, relayOnFrame));
                seq.Add(skip);
                var hins = hcm.Body.Instructions;
                for (int k = 0; k < seq.Count; k++) hins.Insert(k, seq[k]);
                if (hcm.Body.MaxStack < 16) hcm.Body.MaxStack = 16;
                Console.WriteLine("  HandleClientMessage: 已挂 MO-RELAY 识别（" + seq.Count + " 条）");
            }
            else Console.WriteLine("  SKIP: HandleClientMessage 未找到");

            var hms = lgl.Methods.FirstOrDefault(m => m.Name == "HandleMessageFromServer");
            if (hms != null && hms.HasBody)
            {
                var skip2 = Instruction.Create(OpCodes.Nop);
                var seq2 = new System.Collections.Generic.List<Instruction>();
                seq2.Add(Instruction.Create(OpCodes.Ldarg_1));
                seq2.Add(Instruction.Create(OpCodes.Call, relayIsMsg));
                seq2.Add(Instruction.Create(OpCodes.Brfalse, skip2));
                seq2.Add(Instruction.Create(OpCodes.Ldstr, ""));
                seq2.Add(Instruction.Create(OpCodes.Ldarg_1));
                seq2.Add(Instruction.Create(OpCodes.Call, relayOnFrame));
                seq2.Add(skip2);
                var h2 = hms.Body.Instructions;
                for (int k = 0; k < seq2.Count; k++) h2.Insert(k, seq2[k]);
                if (hms.Body.MaxStack < 16) hms.Body.MaxStack = 16;
                Console.WriteLine("  HandleMessageFromServer: 已挂 MO-RELAY 识别（" + seq2.Count + " 条）");
            }
            else Console.WriteLine("  SKIP: HandleMessageFromServer 未找到");

            Console.WriteLine("--- 栈自检 ---");
            CheckStack(wsi);
            CheckStack(hcm);
            CheckStack(hms);
        }

        // ──────────────────────────────────────────────────────────────
        // relaysend：把 LANPlayerInfo.SendMessage 的整个方法体换成
        //   MoLanRelay.Relay.SendLocked(this, message)
        // 这样"大厅聊天"和"游戏 UDP 帧"共用同一条 TCP 时，写入不会交错串帧。
        // ──────────────────────────────────────────────────────────────
        if (mode == "relaysend")
        {
            var lanPi = mod.GetTypes().FirstOrDefault(t => t.Name == "LANPlayerInfo");
            if (lanPi == null) { Console.WriteLine("SKIP: 找不到 LANPlayerInfo"); return 0; }
            MethodDef sm = null;
            foreach (var m in lanPi.Methods)
            {
                if (m.Name != "SendMessage" || !m.HasBody) continue;
                if (m.MethodSig == null || m.MethodSig.Params.Count != 1) continue;
                if (m.MethodSig.Params[0].FullName != "System.String") continue;
                sm = m; break;
            }
            if (sm == null) { Console.WriteLine("SKIP: 找不到 SendMessage(string)"); return 0; }

            bool already = false;
            foreach (var i in sm.Body.Instructions)
            {
                var im = i.Operand as IMethod;
                if (im != null && im.Name == "SendLocked") { already = true; break; }
            }
            if (already) { Console.WriteLine("SKIP: SendMessage 已改过"); return 0; }

            var relayAsm2 = new AssemblyRefUser("MoLanRelay", new Version(0, 0, 0, 0));
            var relayType2 = new TypeRefUser(mod, "MoLanRelay", "Relay", relayAsm2);
            var sendLocked = new MemberRefUser(mod, "SendLocked",
                MethodSig.CreateStatic(mod.CorLibTypes.Void, sm.DeclaringType.ToTypeSig(), mod.CorLibTypes.String), relayType2);

            sm.Body.ExceptionHandlers.Clear();
            var sins = sm.Body.Instructions;
            sins.Clear();
            sins.Add(Instruction.Create(OpCodes.Ldarg_0));
            sins.Add(Instruction.Create(OpCodes.Ldarg_1));
            sins.Add(Instruction.Create(OpCodes.Call, sendLocked));
            sins.Add(Instruction.Create(OpCodes.Ret));
            if (sm.Body.MaxStack < 8) sm.Body.MaxStack = 8;
            Console.WriteLine("  LANPlayerInfo.SendMessage → Relay.SendLocked（已接管写入）");
            Console.WriteLine("--- 栈自检 ---");
            CheckStack(sm);
        }

        // ──────────────────────────────────────────────────────────────
        // relayhost：修"客机 → 房主"方向发不出去。
        //   根因：客机侧 players 里的 LANPlayerInfo.TcpClient 是空的，
        //         真正通往房主的是 LANGameLobby::client（SetUp 里赋值）。
        //   1) SetUp 里把 client/encoding 注册给中继
        //   2) SendMessageToHost 改走 Relay.SendClientLocked（与帧共用同一把写锁）
        // ──────────────────────────────────────────────────────────────
        if (mode == "relayhost")
        {
            // 两个大厅类各有自己的 client(TcpClient) / encoding / SendMessageToHost：
            //   LANGameLobby          —— 正常加入、房主建房
            //   LANGameLoadingLobby   —— 加入"已开始的存档局"（读条界面）
            // 都注册一遍，谁来都用得上。
            string[] lobbyNames = new string[] { "LANGameLobby", "LANGameLoadingLobby" };

            var relayAsm3 = new AssemblyRefUser("MoLanRelay", new Version(0, 0, 0, 0));
            var relayType3 = new TypeRefUser(mod, "MoLanRelay", "Relay", relayAsm3);

            bool any = false;
            foreach (string tn in lobbyNames)
            {
                var lgl2 = mod.GetTypes().FirstOrDefault(t => t.Name == tn);
                if (lgl2 == null) { Console.WriteLine("  SKIP " + tn + ": 类型未找到"); continue; }

                var clientF2 = lgl2.Fields.FirstOrDefault(f => f.Name == "client");
                var encF2 = lgl2.Fields.FirstOrDefault(f => f.Name == "encoding");
                if (clientF2 == null || encF2 == null) { Console.WriteLine("  SKIP " + tn + ": 找不到 client/encoding 字段"); continue; }
                var clientT = clientF2.FieldType.ToTypeDefOrRef().ToTypeSig();
                var encT = encF2.FieldType.ToTypeDefOrRef().ToTypeSig();
                any = true;

                var registerHost = new MemberRefUser(mod, "RegisterHostClient",
                    MethodSig.CreateStatic(mod.CorLibTypes.Void, clientT, encT), relayType3);
                var sendClientLocked = new MemberRefUser(mod, "SendClientLocked",
                    MethodSig.CreateStatic(mod.CorLibTypes.Void, clientT, encT, mod.CorLibTypes.String), relayType3);

                // ---- 1) SetUp ----
                var setup = lgl2.Methods.FirstOrDefault(m => m.Name == "SetUp");
                if (setup != null && setup.HasBody)
                {
                    bool done = false;
                    foreach (var i in setup.Body.Instructions)
                    {
                        var im = i.Operand as IMethod;
                        if (im != null && im.Name == "RegisterHostClient") { done = true; break; }
                    }
                    if (done) Console.WriteLine("  " + tn + ".SetUp: 已注册过，跳过");
                    else
                    {
                        var si = setup.Body.Instructions;
                        int at = -1;
                        for (int k = 0; k < si.Count; k++)
                        {
                            var im = si[k].Operand as IMethod;
                            if (im != null && im.Name == "HandleServerCommunication") { at = k; break; }
                        }
                        if (at < 0) { Console.WriteLine("  SKIP " + tn + ".SetUp: 找不到 HandleServerCommunication"); }
                        else
                        {
                            // 插在 ldftn 之前：此时栈上正好压着 if/else 合并后的 this，
                            // 下面这段 net 压栈为 0，不会破坏 ldftn 需要的实例引用。
                            var ins = new System.Collections.Generic.List<Instruction>();
                            ins.Add(Instruction.Create(OpCodes.Ldarg_0));
                            ins.Add(Instruction.Create(OpCodes.Ldfld, clientF2));
                            ins.Add(Instruction.Create(OpCodes.Ldarg_0));
                            ins.Add(Instruction.Create(OpCodes.Ldfld, encF2));
                            ins.Add(Instruction.Create(OpCodes.Call, registerHost));
                            for (int k = 0; k < ins.Count; k++) si.Insert(at + k, ins[k]);
                            if (setup.Body.MaxStack < 8) setup.Body.MaxStack = 8;
                            Console.WriteLine("  " + tn + ".SetUp: 已插入 RegisterHostClient（" + ins.Count + " 条）");
                        }
                    }
                }
                else Console.WriteLine("  SKIP " + tn + ".SetUp: 未找到");

                // ---- 2) SendMessageToHost ----
                var smh2 = lgl2.Methods.FirstOrDefault(m => m.Name == "SendMessageToHost");
                if (smh2 != null && smh2.HasBody)
                {
                    bool done = false;
                    foreach (var i in smh2.Body.Instructions)
                    {
                        var im = i.Operand as IMethod;
                        if (im != null && im.Name == "SendClientLocked") { done = true; break; }
                    }
                    if (done) Console.WriteLine("  " + tn + ".SendMessageToHost: 已改过，跳过");
                    else
                    {
                        smh2.Body.ExceptionHandlers.Clear();
                        var ci = smh2.Body.Instructions;
                        ci.Clear();
                        ci.Add(Instruction.Create(OpCodes.Ldarg_0));
                        ci.Add(Instruction.Create(OpCodes.Ldfld, clientF2));
                        ci.Add(Instruction.Create(OpCodes.Ldarg_0));
                        ci.Add(Instruction.Create(OpCodes.Ldfld, encF2));
                        ci.Add(Instruction.Create(OpCodes.Ldarg_1));
                        ci.Add(Instruction.Create(OpCodes.Call, sendClientLocked));
                        ci.Add(Instruction.Create(OpCodes.Ret));
                        if (smh2.Body.MaxStack < 8) smh2.Body.MaxStack = 8;
                        Console.WriteLine("  " + tn + ".SendMessageToHost: → Relay.SendClientLocked（" + ci.Count + " 条）");
                    }
                }
                else Console.WriteLine("  SKIP " + tn + ".SendMessageToHost: 未找到");

                Console.WriteLine("--- 栈自检 " + tn + " ---");
                CheckStack(setup);
                CheckStack(smh2);
            }

            if (!any) { Console.WriteLine("SKIP: 没找到任何大厅类型"); return 0; }
        }

        // ──────────────────────────────────────────────────────────────
        // lobbyfix：修上游局域网大厅的一个漏判空（原版就有，不是我们引入的）
        //   LANGameLobby::HandleFileHashCommand 里：
        //       PlayerInfo pInfo = Players.Find(p => p.Name == sender);
        //       pInfo.Verified = true;            // ← 找不到人就 NRE → 客户端崩溃
        //   触发条件：发送者的连接还活着，但它已经不在 Players 里
        //   （典型场景：退出大厅重新建房后，旧连接残留，之后又收到一条旧消息）。
        //   上游在线大厅（CnCNetGameLobby）同一处写的是 if (pInfo != null)，
        //   只有局域网大厅漏了 —— 这里补上同样的判断。
        // ──────────────────────────────────────────────────────────────
        if (mode == "lobbyfix")
        {
            var lglF = mod.GetTypes().FirstOrDefault(t => t.Name == "LANGameLobby");
            if (lglF == null) { Console.WriteLine("SKIP: 找不到 LANGameLobby"); return 0; }
            var hfh = lglF.Methods.FirstOrDefault(m => m.Name == "HandleFileHashCommand");
            if (hfh == null || !hfh.HasBody) { Console.WriteLine("SKIP: 找不到 HandleFileHashCommand"); return 0; }

            var fins = hfh.Body.Instructions;
            bool doneF = false;
            for (int i = 0; i + 3 < fins.Count; i++)
            {
                var c0 = fins[i].OpCode.Code;
                var c1 = fins[i + 1].OpCode.Code;
                if (c0 == dnlib.DotNet.Emit.Code.Dup &&
                    (c1 == dnlib.DotNet.Emit.Code.Brtrue || c1 == dnlib.DotNet.Emit.Code.Brtrue_S) &&
                    fins[i + 2].OpCode.Code == dnlib.DotNet.Emit.Code.Pop &&
                    fins[i + 3].OpCode.Code == dnlib.DotNet.Emit.Code.Ret)
                { doneF = true; break; }
            }
            if (doneF) { Console.WriteLine("  HandleFileHashCommand: 已补过，跳过"); }
            else
            {
                // 找 List<PlayerInfo>::Find(Predicate) 的调用，插在它后面
                int atF = -1;
                for (int i = 0; i < fins.Count; i++)
                {
                    var im = fins[i].Operand as IMethod;
                    if (im != null && im.Name == "Find" && im.DeclaringType != null
                        && (string)im.DeclaringType.Name == "List`1")
                    { atF = i + 1; break; }
                }
                if (atF < 0) { Console.WriteLine("SKIP: 找不到 Find 调用"); return 0; }

                // 栈：Find 之后是 [pInfo]
                //   dup; brtrue LOK; pop; ret;  LOK:
                //   → null 就直接返回，非 null 落回原逻辑
                var lokF = Instruction.Create(OpCodes.Nop);
                fins.Insert(atF, Instruction.Create(OpCodes.Dup));
                fins.Insert(atF + 1, Instruction.Create(OpCodes.Brtrue, lokF));
                fins.Insert(atF + 2, Instruction.Create(OpCodes.Pop));
                fins.Insert(atF + 3, Instruction.Create(OpCodes.Ret));
                fins.Insert(atF + 4, lokF);
                if (hfh.Body.MaxStack < 8) hfh.Body.MaxStack = 8;
                Console.WriteLine("  HandleFileHashCommand: 已补判空（4 条 + 跳转目标）");
            }

            Console.WriteLine("--- 栈自检 ---");
            CheckStack(hfh);
        }

        // ──────────────────────────────────────────────────────────────
        // kick：让局域网大厅的「踢人」生效
        //   GameLobbyBase::CopyPlayerDataFromUI 里，玩家名字下拉框选中第 2 项会调
        //   GameLobbyBase::KickPlayer(int)。在线大厅(CnCNetGameLobby)重写了它，
        //   局域网大厅没有 → 走基类的空实现 → 「点了没反应」。
        //   只替换基类那个空实现，所以：局域网生效、在线大厅（有自己的重写）不受影响。
        //   跨程序集访问不到 protected 成员，因此实现体放在 MoLanRelay.dll 里用反射做。
        // ──────────────────────────────────────────────────────────────
        if (mode == "kick")
        {
            var glbK = mod.GetTypes().FirstOrDefault(t => t.Name == "GameLobbyBase");
            if (glbK == null) { Console.WriteLine("SKIP: 找不到 GameLobbyBase"); return 0; }
            var kp = glbK.Methods.FirstOrDefault(m => m.Name == "KickPlayer");
            if (kp == null || !kp.HasBody) { Console.WriteLine("SKIP: 找不到 GameLobbyBase::KickPlayer"); return 0; }
            if (kp.MethodSig == null || kp.MethodSig.Params.Count != 1
                || kp.MethodSig.Params[0].FullName != "System.Int32")
            { Console.WriteLine("SKIP: KickPlayer 签名不是 (int)"); return 0; }

            bool doneK = false;
            foreach (var i in kp.Body.Instructions)
            {
                var im = i.Operand as IMethod;
                if (im != null && im.Name == "KickPlayer" && im.DeclaringType != null
                    && (string)im.DeclaringType.Name == "Relay") { doneK = true; break; }
            }
            if (doneK) Console.WriteLine("  GameLobbyBase.KickPlayer: 已改过，跳过");
            else
            {
                var relayAsmK = new AssemblyRefUser("MoLanRelay", new Version(0, 0, 0, 0));
                var relayTypeK = new TypeRefUser(mod, "MoLanRelay", "Relay", relayAsmK);
                // 参数用 object，避免引用 GameLobbyBase 这个类型
                var kickCall = new MemberRefUser(mod, "KickPlayer",
                    MethodSig.CreateStatic(mod.CorLibTypes.Void, mod.CorLibTypes.Object, mod.CorLibTypes.Int32), relayTypeK);

                kp.Body.ExceptionHandlers.Clear();
                var kins = kp.Body.Instructions;
                kins.Clear();
                kins.Add(Instruction.Create(OpCodes.Ldarg_0));   // this（大厅实例，引用类型，无需装箱）
                kins.Add(Instruction.Create(OpCodes.Ldarg_1));   // playerIndex
                kins.Add(Instruction.Create(OpCodes.Call, kickCall));
                kins.Add(Instruction.Create(OpCodes.Ret));
                if (kp.Body.MaxStack < 8) kp.Body.MaxStack = 8;
                Console.WriteLine("  GameLobbyBase.KickPlayer → Relay.KickPlayer（局域网踢人生效）");
            }

            Console.WriteLine("--- 栈自检 ---");
            CheckStack(kp);
        }

        // ──────────────────────────────────────────────────────────────
        // mention：@功能（局域网大厅聊天）
        //   LANGameLobby::Player_HandleChatCommand 里，颜色算完之后插一次调用：
        //   消息含 "@我的名字" → 换成高亮色 + 播提示音。
        //   插在 `callvirt LANColor::get_XNAColor()` 之后，那里栈上是
        //   [lbChatMessages, playerName, Color]，我们补 (this, text) 两个参数再调用，
        //   返回值直接替换掉 Color —— 后面的 ChatMessage 构造完全不用动。
        // ──────────────────────────────────────────────────────────────
        if (mode == "mention")
        {
            var lglM = mod.GetTypes().FirstOrDefault(t => t.Name == "LANGameLobby");
            if (lglM == null) { Console.WriteLine("SKIP: 找不到 LANGameLobby"); return 0; }
            var phc = lglM.Methods.FirstOrDefault(m => m.Name == "Player_HandleChatCommand");
            if (phc == null || !phc.HasBody) { Console.WriteLine("SKIP: 找不到 Player_HandleChatCommand"); return 0; }

            var mins = phc.Body.Instructions;
            bool doneM = false;
            foreach (var i in mins)
            {
                var im = i.Operand as IMethod;
                if (im != null && im.Name == "MentionColor" && im.DeclaringType != null
                    && (string)im.DeclaringType.Name == "Relay") { doneM = true; break; }
            }
            if (doneM) Console.WriteLine("  Player_HandleChatCommand: 已改过，跳过");
            else
            {
                int atM = -1;
                TypeSig colorSig = null;
                for (int i = 0; i < mins.Count; i++)
                {
                    var im = mins[i].Operand as IMethod;
                    if (im != null && im.Name == "get_XNAColor" && im.MethodSig != null)
                    { atM = i + 1; colorSig = im.MethodSig.RetType; break; }
                }
                if (atM < 0 || colorSig == null) { Console.WriteLine("SKIP: 找不到 LANColor::get_XNAColor"); return 0; }
                if (phc.Body.Variables.Count < 1) { Console.WriteLine("SKIP: 没有局部变量"); return 0; }

                var relayAsmM = new AssemblyRefUser("MoLanRelay", new Version(0, 0, 0, 0));
                var relayTypeM = new TypeRefUser(mod, "MoLanRelay", "Relay", relayAsmM);
                // 参数全用 object/string —— 不让 DLL 与 exe 之间产生 MonoGame Color 的跨程序集签名依赖
                var colorT = colorSig.ToTypeDefOrRef();
                var mentionCall = new MemberRefUser(mod, "MentionColor",
                    MethodSig.CreateStatic(mod.CorLibTypes.Object, mod.CorLibTypes.Object,
                        mod.CorLibTypes.Object, mod.CorLibTypes.String), relayTypeM);

                var partsLoc = phc.Body.Variables[0];   // stloc.0 = data.Split(...) 的结果
                mins.Insert(atM, Instruction.Create(OpCodes.Box, colorT));         // Color → object
                mins.Insert(atM + 1, Instruction.Create(OpCodes.Ldarg_0));          // this（大厅实例）
                mins.Insert(atM + 2, Instruction.Create(OpCodes.Ldloc, partsLoc));
                mins.Insert(atM + 3, Instruction.CreateLdcI4(2));
                mins.Insert(atM + 4, Instruction.Create(OpCodes.Ldelem_Ref));       // parts[2] = 消息正文
                mins.Insert(atM + 5, Instruction.Create(OpCodes.Call, mentionCall));
                mins.Insert(atM + 6, Instruction.Create(OpCodes.Unbox_Any, colorT)); // object → Color
                if (phc.Body.MaxStack < 16) phc.Body.MaxStack = 16;
                Console.WriteLine("  Player_HandleChatCommand: 已挂 @ 高亮 + 提示音（7 条）");
            }

            Console.WriteLine("--- 栈自检 ---");
            CheckStack(phc);
        }

        // ──────────────────────────────────────────────────────────────
        // shot：截图（局域网大厅）
        //   (a) LANGameLobby::Update 开头插 Relay.Tick(this, gameTime)
        //       —— 每帧轮询 F8，按下就截当前客户端窗口并发出去
        //   (b) HandleClientMessage / HandleMessageFromServer 开头插一段
        //       MO-CTRL 控制帧识别 → Relay.OnCtrl（房主还会原样转发）
        // ──────────────────────────────────────────────────────────────
        if (mode == "shot")
        {
            var lglS = mod.GetTypes().FirstOrDefault(t => t.Name == "LANGameLobby");
            if (lglS == null) { Console.WriteLine("SKIP: 找不到 LANGameLobby"); return 0; }

            var relayAsmS = new AssemblyRefUser("MoLanRelay", new Version(0, 0, 0, 0));
            var relayTypeS = new TypeRefUser(mod, "MoLanRelay", "Relay", relayAsmS);

            // ---- (a) Update ----
            var updS = lglS.Methods.FirstOrDefault(m => m.Name == "Update");
            if (updS == null || !updS.HasBody) Console.WriteLine("  SKIP: 找不到 Update");
            else
            {
                bool doneS = false;
                foreach (var i in updS.Body.Instructions)
                {
                    var im = i.Operand as IMethod;
                    if (im != null && im.Name == "Tick" && im.DeclaringType != null
                        && (string)im.DeclaringType.Name == "Relay") { doneS = true; break; }
                }
                if (doneS) Console.WriteLine("  Update: 已改过，跳过");
                else
                {
                    var tickCall = new MemberRefUser(mod, "Tick",
                        MethodSig.CreateStatic(mod.CorLibTypes.Void, mod.CorLibTypes.Object, mod.CorLibTypes.Object), relayTypeS);
                    var uins = updS.Body.Instructions;
                    uins.Insert(0, Instruction.Create(OpCodes.Call, tickCall));
                    uins.Insert(0, Instruction.Create(OpCodes.Ldarg_1));
                    uins.Insert(0, Instruction.Create(OpCodes.Ldarg_0));
                    if (updS.Body.MaxStack < 8) updS.Body.MaxStack = 8;
                    Console.WriteLine("  Update: 已挂 Relay.Tick（F8 截图热键轮询）");
                }
                CheckStack(updS);
            }

            // ---- (b) 控制帧识别 ----
            var isCtrlCall = new MemberRefUser(mod, "IsCtrlMessage",
                MethodSig.CreateStatic(mod.CorLibTypes.Boolean, mod.CorLibTypes.String), relayTypeS);
            var onCtrlCall = new MemberRefUser(mod, "OnCtrl",
                MethodSig.CreateStatic(mod.CorLibTypes.Void, mod.CorLibTypes.Object, mod.CorLibTypes.String), relayTypeS);

            foreach (string mnS in new string[] { "HandleClientMessage", "HandleMessageFromServer" })
            {
                var mS = lglS.Methods.FirstOrDefault(x => x.Name == mnS);
                if (mS == null || !mS.HasBody) { Console.WriteLine("  SKIP: 找不到 " + mnS); continue; }
                bool doneS2 = false;
                foreach (var i in mS.Body.Instructions)
                {
                    var im = i.Operand as IMethod;
                    if (im != null && im.Name == "IsCtrlMessage") { doneS2 = true; break; }
                }
                if (doneS2) { Console.WriteLine("  " + mnS + ": 已改过，跳过"); continue; }

                var skipLbl = Instruction.Create(OpCodes.Nop);
                var seqS = new System.Collections.Generic.List<Instruction>();
                seqS.Add(Instruction.Create(OpCodes.Ldarg_1));            // data
                seqS.Add(Instruction.Create(OpCodes.Call, isCtrlCall));
                seqS.Add(Instruction.Create(OpCodes.Brfalse, skipLbl));
                seqS.Add(Instruction.Create(OpCodes.Ldarg_0));            // this
                seqS.Add(Instruction.Create(OpCodes.Ldarg_1));            // data
                seqS.Add(Instruction.Create(OpCodes.Call, onCtrlCall));
                seqS.Add(skipLbl);
                var sins = mS.Body.Instructions;
                for (int k = 0; k < seqS.Count; k++) sins.Insert(k, seqS[k]);
                if (mS.Body.MaxStack < 16) mS.Body.MaxStack = 16;
                Console.WriteLine("  " + mnS + ": 已挂 MO-CTRL 识别（" + seqS.Count + " 条）");
                CheckStack(mS);
            }
        }

        // ──────────────────────────────────────────────────────────────
        // hashcheck：把"游戏文件是否一致"讲清楚
        //   LANGameLobby::HandleFileHashCommand(sender, fileHash) 开头插一次调用，
        //   把 (大厅, 发送者, 对方哈希) 交给 Relay.FileHashResult：
        //   一致 → 只写日志；不一致 → 红字提示 + 聊天区广播（带双方哈希前 8 位 + 处理建议）。
        // ──────────────────────────────────────────────────────────────
        if (mode == "hashcheck")
        {
            var lglH = mod.GetTypes().FirstOrDefault(t => t.Name == "LANGameLobby");
            if (lglH == null) { Console.WriteLine("SKIP: 找不到 LANGameLobby"); return 0; }
            var fhc = lglH.Methods.FirstOrDefault(m => m.Name == "HandleFileHashCommand");
            if (fhc == null || !fhc.HasBody) { Console.WriteLine("SKIP: 找不到 HandleFileHashCommand"); return 0; }
            if (fhc.MethodSig == null || fhc.MethodSig.Params.Count != 2) { Console.WriteLine("SKIP: 签名不是 (string,string)"); return 0; }

            bool doneH = false;
            foreach (var i in fhc.Body.Instructions)
            {
                var im = i.Operand as IMethod;
                if (im != null && im.Name == "FileHashResult") { doneH = true; break; }
            }
            if (doneH) Console.WriteLine("  HandleFileHashCommand: 已改过，跳过");
            else
            {
                var relayAsmH = new AssemblyRefUser("MoLanRelay", new Version(0, 0, 0, 0));
                var relayTypeH = new TypeRefUser(mod, "MoLanRelay", "Relay", relayAsmH);
                var fhCall = new MemberRefUser(mod, "FileHashResult",
                    MethodSig.CreateStatic(mod.CorLibTypes.Void, mod.CorLibTypes.Object,
                        mod.CorLibTypes.String, mod.CorLibTypes.String), relayTypeH);

                var hins = fhc.Body.Instructions;
                hins.Insert(0, Instruction.Create(OpCodes.Call, fhCall));
                hins.Insert(0, Instruction.Create(OpCodes.Ldarg_2));
                hins.Insert(0, Instruction.Create(OpCodes.Ldarg_1));
                hins.Insert(0, Instruction.Create(OpCodes.Ldarg_0));
                if (fhc.Body.MaxStack < 8) fhc.Body.MaxStack = 8;
                Console.WriteLine("  HandleFileHashCommand: 已挂 Relay.FileHashResult（文件一致性提示）");
                CheckStack(fhc);
            }
        }

        if (mode == "checkstack")
        {
            foreach (var t in mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody || m.Body.Instructions.Count == 0) continue;
                    CheckStack(m);
                }
            return 0;
        }

        if (mode == "pinvokes")
        {
            Console.WriteLine("== ModuleRefs ==");
            foreach (var mr in mod.GetModuleRefs()) Console.WriteLine("  " + mr.Name);
            Console.WriteLine("== P/Invoke methods ==");
            foreach (var t in mod.GetTypes())
            {
                foreach (var m in t.Methods)
                {
                    if (m.ImplMap == null) continue;
                    Console.WriteLine("  " + t.FullName + "::" + m.Name + "  ->  " + m.ImplMap.Module.Name + "!" + m.ImplMap.Name);
                }
            }
            return 0;
        }

        if (mode == "refs")
        {
            string f = args.Length > 3 ? args[3] : "";
            Console.WriteLine("== 引用 \"" + f + "\" 的位置 ==");
            foreach (var t in mod.GetTypes())
            {
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var i in m.Body.Instructions)
                    {
                        string hit = null;
                        var im = i.Operand as IMethod;
                        if (im != null) hit = im.DeclaringType == null ? im.Name : im.DeclaringType.FullName + "::" + im.Name;
                        var iff = i.Operand as IField;
                        if (iff != null) hit = (iff.DeclaringType == null ? "" : iff.DeclaringType.FullName + "::") + iff.Name;
                        var itr = i.Operand as ITypeDefOrRef;
                        if (itr != null && im == null) hit = itr.FullName;
                        if (hit != null && hit.Contains(f))
                            Console.WriteLine("  " + t.FullName + "::" + m.Name + "  --(" + i.OpCode.Name + ")-->  " + hit);
                    }
                }
            }
            return 0;
        }

        if (mode == "methods")
        {
            string tn0 = args.Length > 3 ? args[3] : "";
            foreach (var t in mod.GetTypes())
            {
                if ((string)t.Name != tn0) continue;
                Console.WriteLine("== " + t.FullName + " ==");
                foreach (var m in t.Methods) Console.WriteLine("  " + m.Name + "  " + (m.MethodSig == null ? "" : m.MethodSig.ToString()));
            }
            return 0;
        }

        if (mode == "types")
        {
            string filter = args.Length > 3 ? args[3] : "";
            foreach (var t in mod.GetTypes())
            {
                if (filter.Length > 0 && !t.FullName.Contains(filter)) continue;
                Console.WriteLine(t.FullName);
            }
            return 0;
        }

        if (mode == "dumpm")
        {
            // dumpm <dll> <out> <TypeName> <MethodName>
            string tn = args.Length > 3 ? args[3] : "";
            string mn = args.Length > 4 ? args[4] : "";
            Console.WriteLine("== " + tn + "::" + mn + " ==");
            foreach (var t in mod.GetTypes())
            {
                if ((string)t.Name != tn) continue;
                foreach (var m in t.Methods)
                {
                    if (m.Name != mn || !m.HasBody) continue;
                    foreach (var i in m.Body.Instructions)
                        Console.WriteLine(i.Offset.ToString("X4") + ": " + i.OpCode.Name + " " + (i.Operand == null ? "" : i.Operand.ToString()));
                }
            }
            return 0;
        }

        if (mode == "dump")
        {
            Console.WriteLine("== ModuleRef (P/Invoke dll) ==");
            foreach (var mr in mod.GetModuleRefs()) Console.WriteLine("  " + mr.Name);
            var dt = mod.GetTypes().FirstOrDefault(t => t.Name == "WinFormsGameForm");
            Console.WriteLine("== WinFormsGameForm members ==");
            foreach (var m in dt.Methods) Console.WriteLine("  " + m.Name + "  " + (m.MethodSig == null ? "" : m.MethodSig.ToString()));
            foreach (var f in dt.Fields) Console.WriteLine("  fld " + f.Name);
            var ctor = dt.Methods.FirstOrDefault(m => m.Name == ".ctor");
            if (ctor != null && ctor.HasBody)
            {
                Console.WriteLine("== WinFormsGameForm::.ctor ==");
                foreach (var i in ctor.Body.Instructions)
                {
                    var im = i.Operand as IMethod;
                    Console.WriteLine(i.Offset.ToString("X4") + ": " + i.OpCode.Name + " " +
                        (im != null ? im.DeclaringType.FullName + "::" + im.Name : (i.Operand == null ? "" : i.Operand.ToString())));
                }
            }
            var dm = dt.Methods.FirstOrDefault(m => m.Name == "WndProc");
            Console.WriteLine("== WndProc ==");
            foreach (var i in dm.Body.Instructions)
                Console.WriteLine(i.Offset.ToString("X4") + ": " + i.OpCode.Name + " " + (i.Operand == null ? "" : i.Operand.ToString()));

            var wt = mod.GetTypes().FirstOrDefault(t => t.Name == "WinFormsGameWindow");
            if (wt != null)
            {
                var okp = wt.Methods.FirstOrDefault(m => m.Name == "OnKeyPress");
                if (okp != null && okp.HasBody)
                {
                    Console.WriteLine("== WinFormsGameWindow::OnKeyPress ==");
                    foreach (var i in okp.Body.Instructions)
                        Console.WriteLine(i.Offset.ToString("X4") + ": " + i.OpCode.Name + " " + (i.Operand == null ? "" : i.Operand.ToString()));
                }
            }
            // 谁订阅了 KeyPress 事件？
            Console.WriteLine("== KeyPress / TextInput 引用 ==");
            foreach (var t in mod.GetTypes())
            {
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var i in m.Body.Instructions)
                    {
                        var im = i.Operand as IMethod;
                        if (im == null) continue;
                        if (im.Name.Contains("KeyPress") || im.Name.Contains("TextInput") || im.Name == "OnTextInput")
                            Console.WriteLine("  " + t.Name + "::" + m.Name + " -> " + im.DeclaringType.FullName + "::" + im.Name + " (" + i.OpCode.Name + ")");
                    }
                }
            }
            return 0;
        }

        if (mode == "patch")
        {
            var type = mod.GetTypes().FirstOrDefault(t => t.Name == "WinFormsGameForm");
            if (type == null) { Console.WriteLine("SKIP: WinFormsGameForm 未找到"); return 0; }
            var method = type.Methods.FirstOrDefault(m => m.Name == "WndProc");
            if (method == null || !method.HasBody) { Console.WriteLine("SKIP: WndProc 未找到"); return 0; }

            var ins = method.Body.Instructions;

            // 找默认分支：紧跟 “ldc.i4 526 ; beq *” 之后的 br
            int defIdx = -1;
            for (int i = 1; i < ins.Count - 1; i++)
            {
                if (ins[i - 1].OpCode.Code == dnlib.DotNet.Emit.Code.Ldc_I4 && ins[i - 1].GetLdcI4Value() == 526 &&
                    (ins[i].OpCode.Code == dnlib.DotNet.Emit.Code.Beq || ins[i].OpCode.Code == dnlib.DotNet.Emit.Code.Beq_S))
                {
                    var nxt = ins[i + 1];
                    if (nxt.OpCode.Code == dnlib.DotNet.Emit.Code.Br || nxt.OpCode.Code == dnlib.DotNet.Emit.Code.Br_S) { defIdx = i + 1; break; }
                }
            }
            if (defIdx < 0) { Console.WriteLine("SKIP: 默认分支未找到"); return 0; }
            var defaultBr = ins[defIdx];
            var defaultTarget = (Instruction)defaultBr.Operand;
            Console.WriteLine("默认分支索引 " + defIdx + " -> " + defaultTarget.Offset.ToString("X4"));

            // 复用方法里已有的引用
            IMethod getMsg = null, getWParam = null, toInt32 = null;
            foreach (var i in ins)
            {
                var im = i.Operand as IMethod;
                if (im == null) continue;
                if (im.Name == "get_Msg") getMsg = im;
                if (im.Name == "get_WParam") getWParam = im;
                if (im.Name == "ToInt32") toInt32 = im;
            }
            if (getMsg == null || getWParam == null || toInt32 == null) { Console.WriteLine("SKIP: 缺引用"); return 0; }

            // KeyPressEventArgs 的 ctor 与 Form 类型：从模块里已有的引用里找
            IMethod kpeCtor = null;
            ITypeDefOrRef kpeType = null, formType = null;
            foreach (var t in mod.GetTypes())
            {
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var i in m.Body.Instructions)
                    {
                        var im = i.Operand as IMethod;
                        if (im == null || im.DeclaringType == null) continue;
                        string dtName = (string)im.DeclaringType.Name;
                        string dtFull = im.DeclaringType.FullName;
                        if (dtName.Contains("KeyPress"))
                            Console.WriteLine("    [diag] " + dtFull + "::" + im.Name + "  params=" + (im.MethodSig == null ? -1 : im.MethodSig.Params.Count) + "  op=" + i.OpCode.Code);
                        if (dtName.Contains("KeyPressEventArgs"))
                        {
                            if (kpeType == null) kpeType = im.DeclaringType;
                            if (im.Name == ".ctor") { kpeCtor = im; kpeType = im.DeclaringType; }
                        }
                        if (dtFull == "System.Windows.Forms.Form") formType = im.DeclaringType;
                    }
                }
            }
            Console.WriteLine("    [diag] kpeType=" + (kpeType == null ? "null" : kpeType.FullName) + " kpeCtor=" + (kpeCtor == null ? "null" : kpeCtor.Name) + " formType=" + (formType == null ? "null" : formType.FullName));
            if (kpeType == null) { Console.WriteLine("SKIP: 未找到 KeyPressEventArgs 类型"); return 0; }
            if (formType == null) { Console.WriteLine("SKIP: 未找到 Form 类型"); return 0; }
            // 模块里没有 KeyPressEventArgs 的构造函数引用（事件参数由 WinForms 创建），自己建一个
            if (kpeCtor == null)
            {
                var ctorSig = MethodSig.CreateInstance(mod.CorLibTypes.Void, mod.CorLibTypes.Char);
                kpeCtor = new MemberRefUser(mod, ".ctor", ctorSig, kpeType);
                Console.WriteLine("    自建 KeyPressEventArgs(char) 引用");
            }
        refsReady:

            // this.OnKeyPress(KeyPressEventArgs) 的方法引用
            var kpeSig = MethodSig.CreateInstance(mod.CorLibTypes.Void, kpeType.ToTypeSig());
            var onKeyPress = new MemberRefUser(mod, "OnKeyPress", kpeSig, formType);

            // 新局部变量存 wParam
            var intPtrType = toInt32.DeclaringType;
            var wparamLoc = new Local(intPtrType.ToTypeSig());
            method.Body.Variables.Add(wparamLoc);

            var newIns = new[]
            {
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getMsg),
                Instruction.CreateLdcI4(646),
                Instruction.Create(OpCodes.Bne_Un, defaultTarget),
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldarg_1),
                Instruction.Create(OpCodes.Call, getWParam),
                Instruction.Create(OpCodes.Stloc, wparamLoc),
                Instruction.Create(OpCodes.Ldloca, wparamLoc),
                Instruction.Create(OpCodes.Call, toInt32),
                Instruction.Create(OpCodes.Conv_U2),
                Instruction.Create(OpCodes.Newobj, kpeCtor),
                Instruction.Create(OpCodes.Call, onKeyPress),
            };
            for (int k = 0; k < newIns.Length; k++) ins.Insert(defIdx + k, newIns[k]);

            if (method.Body.MaxStack < 8) method.Body.MaxStack = 8;
            method.Body.SimplifyBranches();   // 全部改成长跳转，避免短跳转距离不够
            Console.WriteLine("已插入 " + newIns.Length + " 条指令");

            // ── 追加：处理 WM_IME_SETCONTEXT / WM_INPUTLANGCHANGE，主动把输入法关联到本窗口 ──
            // 这是 XNAUI 里原本要做的那件事（ImmAssociateContext + ImmGetContext）。
            // 不关联的话，输入法候选框根本不会出现在这个窗口上。
            {
                // 1) 声明两个 P/Invoke（imm32.dll）
                var imm32 = new ModuleRefUser(mod, "imm32.dll");
                var tgt = type.Module.Types.FirstOrDefault(x => x.Name == "WinFormsGameForm") ?? type;

                var getCtx = new MethodDefUser("ImmGetContext",
                    MethodSig.CreateStatic(mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr),
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl | MethodAttributes.HideBySig)
                { ImplAttributes = MethodImplAttributes.PreserveSig };
                getCtx.ImplMap = new ImplMapUser(imm32, "ImmGetContext", PInvokeAttributes.CallConvWinapi);
                tgt.Methods.Add(getCtx);

                var assocCtx = new MethodDefUser("ImmAssociateContext",
                    MethodSig.CreateStatic(mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr, mod.CorLibTypes.IntPtr),
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl | MethodAttributes.HideBySig)
                { ImplAttributes = MethodImplAttributes.PreserveSig };
                assocCtx.ImplMap = new ImplMapUser(imm32, "ImmAssociateContext", PInvokeAttributes.CallConvWinapi);
                tgt.Methods.Add(assocCtx);

                // 2) Message::get_HWnd 引用
                IMethod getHwnd = null;
                foreach (var i in ins)
                {
                    var im = i.Operand as IMethod;
                    if (im != null && im.Name == "get_Msg") { getHwnd = new MemberRefUser(mod, "get_HWnd", MethodSig.CreateInstance(mod.CorLibTypes.IntPtr), im.DeclaringType); break; }
                }

                if (getHwnd != null)
                {
                    var ldo = Instruction.Create(OpCodes.Nop);
                    var lend = Instruction.Create(OpCodes.Nop);
                    var block = new Instruction[]
                    {
                        Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, getMsg),
                        Instruction.CreateLdcI4(0x281), Instruction.Create(OpCodes.Beq, ldo),
                        Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, getMsg),
                        Instruction.CreateLdcI4(0x51), Instruction.Create(OpCodes.Bne_Un, lend),
                        ldo,                                            // WM_IME_SETCONTEXT / WM_INPUTLANGCHANGE
                        Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, getHwnd),
                        Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, getHwnd),
                        Instruction.Create(OpCodes.Call, getCtx),
                        Instruction.Create(OpCodes.Call, assocCtx),
                        Instruction.Create(OpCodes.Pop),                 // 丢弃返回值，保持栈平衡
                        lend,
                    };
                    int pos = defIdx + newIns.Length;
                    for (int k = 0; k < block.Length; k++) ins.Insert(pos + k, block[k]);
                    method.Body.SimplifyBranches();
                    Console.WriteLine("已加入输入法关联（WM_IME_SETCONTEXT / WM_INPUTLANGCHANGE）");
                }
                else Console.WriteLine("跳过输入法关联（未取得 Message::get_HWnd）");
            }
        }

        mod.Write(args[1]);
        Console.WriteLine("OK -> " + args[1]);
        return 0;
    }

    /// <summary>自写栈深度校验：找出栈不平衡的指令（dnlib 只给一句 "Error calculating max stack value"）。</summary>
    static void CheckStack(MethodDef m)
    {
        var ins = m.Body.Instructions;
        var depth = new System.Collections.Generic.Dictionary<Instruction, int>();
        var order = new System.Collections.Generic.Dictionary<Instruction, int>();
        for (int k = 0; k < ins.Count; k++) order[ins[k]] = k;
        var work = new System.Collections.Generic.Queue<Instruction>();
        depth[ins[0]] = 0;
        work.Enqueue(ins[0]);
        string tag = m.DeclaringType.Name + "::" + m.Name;
        while (work.Count > 0)
        {
            var i = work.Dequeue();
            int d = depth[i], pops, pushes;
            i.CalculateStackUsage(false, out pushes, out pops);
            if (pops < 0 || pushes < 0)
            { Console.WriteLine("  [stack] " + tag + " 未知指令 @0x" + i.Offset.ToString("X4") + " " + i.OpCode.Name); return; }
            int nd = d - pops;
            if (nd < 0)
            { Console.WriteLine("  [stack] " + tag + " 下溢 @0x" + i.Offset.ToString("X4") + " " + i.OpCode.Name + " (depth=" + d + ", pops=" + pops + ")"); return; }
            if (i.OpCode.Code == Code.Ret && nd != 0)
            { Console.WriteLine("  [stack] " + tag + " ret 时栈非空 @0x" + i.Offset.ToString("X4") + " (depth=" + nd + ")"); return; }
            nd += pushes;
            void Goto(Instruction t)
            {
                if (t == null) return;
                int old;
                if (depth.TryGetValue(t, out old))
                { if (old != nd) Console.WriteLine("  [stack] " + tag + " 深度不一致 @0x" + t.Offset.ToString("X4") + " (" + old + " vs " + nd + ")"); }
                else { depth[t] = nd; work.Enqueue(t); }
            }
            var fc = i.OpCode.FlowControl;
            if (fc == FlowControl.Branch) Goto(i.Operand as Instruction);
            else if (fc == FlowControl.Cond_Branch)
            {
                Goto(i.Operand as Instruction);
                var ta = i.Operand as Instruction[];
                if (ta != null) foreach (var t in ta) Goto(t);
            }
            if (fc != FlowControl.Branch && fc != FlowControl.Return && fc != FlowControl.Throw)
            {
                int idx = order[i];
                if (idx + 1 < ins.Count) Goto(ins[idx + 1]);
            }
        }
    }
}
