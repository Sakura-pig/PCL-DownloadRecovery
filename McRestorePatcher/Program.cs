using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

// McRestorePatcher —— PCL 下载恢复通用修改器（高容错版）
//
// 用法：
//   McRestorePatcher <官方 exe> <输出 exe>        打补丁（自动识别版本；全部成功返回 0）
//   McRestorePatcher --dump <exe> <类型全名> <方法名>   反汇编方法（调试用）
//   McRestorePatcher --refs <exe> <子串>               列出含子串的方法引用（调试用）
//   McRestorePatcher --strings <exe> <正则>            列出含正则的用户字符串（调试用）
//
// 设计目标：对官方 PCL 2.99.0.x 正式版恢复被 RestrictMcDownload 合规裁剪掉的下载
// 入口。官方在编译期把受限分支常量折叠掉，因此补丁按“语义锚点 + 折叠形态”逐处
// 还原。所有匹配都尽量宽松：忽略 nop 填充、容忍短/长指令变体（ldc.i4.0~8、ldc.i4、
// stub 常量）、锚点失效时退回结构特征扫描；每处补丁自带幂等检查，重复运行不会
// 二次修改。任一必需补丁无法应用时拒绝写出输出文件（返回 1）。

// 交互模式：双击运行，把 PCL 的 exe 拖进控制台窗口（文件路径会被粘贴进来）后回车。
var interactive = false;
if (args.Length == 0)
{
    var prompted = AskInputAndOutput();
    if (prompted == null) return 0;
    args = new[] { prompted.Value.input, prompted.Value.output };
    interactive = true;
}
else if (args.Length == 1 && !args[0].StartsWith("--", StringComparison.Ordinal))
{
    var prompted = NormalizeInput(args[0]);
    if (prompted == null) return 0;
    args = new[] { prompted.Value.input, prompted.Value.output };
    interactive = true;
}

var mode = args.Length > 0 ? args[0] : "";
if (mode == "--help" || mode == "-h" || mode == "/?")
{
    Console.WriteLine("用法:");
    Console.WriteLine("  McRestorePatcher <官方 exe> <输出 exe>");
    Console.WriteLine("  McRestorePatcher --dump <exe> <类型全名> <方法名>");
    Console.WriteLine("  McRestorePatcher --refs <exe> <子串>");
    Console.WriteLine("  McRestorePatcher --strings <exe> <正则>");
    Console.WriteLine();
    Console.WriteLine("也可以不带参数运行，按提示把 PCL 的 exe 拖进窗口。");
    return 0;
}

if (mode == "--dump")
{
    var dm = ModuleDefMD.Load(args[1]);
    var dumpType = dm.GetTypes().FirstOrDefault(t => t.FullName == args[2]);
    if (dumpType == null) { Console.Error.WriteLine("类型不存在: " + args[2]); return 1; }
    foreach (var dumpMethod in dumpType.Methods.Where(m => m.Name == args[3]))
    {
        Console.WriteLine($"### {dumpMethod.FullName}");
        Console.WriteLine($"    static={dumpMethod.IsStatic} ret={dumpMethod.ReturnType} params=[{string.Join("; ", dumpMethod.Parameters.Select(p => p.Name + ":" + p.Type))}]");
        if (!dumpMethod.HasBody) { Console.WriteLine(); continue; }
        var b = dumpMethod.Body;
        Console.WriteLine($"    locals=[{string.Join("; ", b.Variables.Select((v, i) => "V" + i + ":" + v.Type))}]");
        foreach (var eh in b.ExceptionHandlers)
            Console.WriteLine($"    EH {eh.HandlerType}: try[{eh.TryStart?.Offset:X4}..{eh.TryEnd?.Offset:X4}] handler[{eh.HandlerStart?.Offset:X4}..{eh.HandlerEnd?.Offset:X4}]");
        foreach (var ins in b.Instructions)
            Console.WriteLine($"    IL_{ins.Offset:X4}: {ins.OpCode.Name,-14} {ins.Operand}");
        Console.WriteLine();
    }
    return 0;
}

if (mode == "--refs")
{
    var rm = ModuleDefMD.Load(args[1]);
    var set = new SortedSet<string>();
    foreach (var t in rm.GetTypes())
        foreach (var mm in t.Methods)
        {
            if (!mm.HasBody) continue;
            foreach (var ins in mm.Body.Instructions)
                if (ins.Operand is IMethod mr && mr.FullName.Contains(args[2])) set.Add(mr.FullName);
        }
    foreach (var s in set) Console.WriteLine(s);
    return 0;
}

if (mode == "--strings")
{
    var sm = ModuleDefMD.Load(args[1]);
    var re = new System.Text.RegularExpressions.Regex(args[2]);
    var found = new SortedSet<string>();
    foreach (var t in sm.GetTypes())
        foreach (var mm in t.Methods)
        {
            if (!mm.HasBody) continue;
            foreach (var ins in mm.Body.Instructions)
                if (ins.OpCode.Code == Code.Ldstr && ins.Operand is string s && re.IsMatch(s)) found.Add(s);
        }
    foreach (var s in found) Console.WriteLine(s);
    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("用法: McRestorePatcher <官方 exe> <输出 exe>");
    return 1;
}

var module = ModuleDefMD.Load(args[0]);
var versionStr = GetVersion(module);
Version? version = versionStr != null && Version.TryParse(versionStr, out var v) ? v : null;
var isEra = version == null || version >= new Version(2, 99, 0, 1);

Console.WriteLine($"检测到版本: {(versionStr ?? "未知")}");

var results = new List<Outcome>();
var onlyFilter = Environment.GetEnvironmentVariable("MCR_ONLY");
var skipFilter = Environment.GetEnvironmentVariable("MCR_SKIP");
void Run(string name, Func<ModuleDefMD, Outcome> patch, bool required)
{
    if (!string.IsNullOrWhiteSpace(onlyFilter)
        && !onlyFilter.Split(',').Any(t => name.IndexOf(t.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
        return;
    if (!string.IsNullOrWhiteSpace(skipFilter)
        && skipFilter.Split(',').Any(t => name.IndexOf(t.Trim(), StringComparison.OrdinalIgnoreCase) >= 0))
        return;
    Outcome o;
    try { o = patch(module); }
    catch (Exception ex) { o = Failed(ex.Message); }
    o.Name = name;
    o.Required = required;
    results.Add(o);
}

Run("PageDownloadInstall.LoaderInit", P_LoaderInit, true);
Run("PageDownloadModify.LoadInstanceFinished", P_LoadInstanceFinished, true);
Run("PCL.ModModpack.ModpackInstall()", P_ModpackInstall0, true);
Run("PCL.ModModpack.ModpackInstall(File)", P_ModpackInstallFile, true);
Run("PageDownloadResourceDetail.InitializeStackResourceVersions", P_ResourceVersions, true);

Run("PageDownloadLeft..ctor(隐藏左栏)", P_DownloadLeftCtor, isEra);
Run("PageResource.set_PageType(安装整合包按钮)", P_PageType, isEra);
Run("PageInstanceModDisabled.BtnDownload_Loaded(下载按钮)", P_ModDisabledDownloadBtn, isEra);
Run("PageLaunchLeft.RefreshButtonsUI(无版本状态)", P_LaunchState, isEra);
Run("PageSelectLeft.McFolderListUI(导入整合包)", P_SelectLeftImport, isEra);
Run("PageSelectRight.McInstanceListUI(空状态下载按钮)", P_SelectRightEmpty, isEra);
Run("ModMinecraft.McDownloadClientUpdateHint(更新弹窗下载按钮)", P_UpdateDialog, isEra);

var fail = false;
foreach (var o in results)
{
    var tag = o.Kind switch
    {
        OutKind.Applied => "[OK]   ",
        OutKind.Already => "[SKIP] ",
        OutKind.NotFound => o.Required ? "[FAIL] " : "[MISS] ",
        _ => "[FAIL] "
    };
    Console.WriteLine($"{tag}{o.Name}{o.Note}");
    if (o.Kind == OutKind.Failed) fail = true;
    if (o.Kind == OutKind.NotFound && o.Required) fail = true;
}

if (fail)
{
    Console.Error.WriteLine("补丁失败：该版本可能不兼容，未写出输出文件。");
    if (interactive) PauseBeforeExit();
    return 1;
}

var opts = new ModuleWriterOptions(module) { Logger = DummyLogger.NoThrowInstance };
module.Write(args[1], opts);
Console.WriteLine("完成，已写出: " + args[1]);
if (interactive)
{
    Console.WriteLine();
    Console.WriteLine("用输出文件替换原来的 PCL 启动器即可（原文件建议先备份）。");
    PauseBeforeExit();
}
return 0;

// =====================================================================
// 交互模式辅助
// =====================================================================

(string input, string output)? NormalizeInput(string raw)
{
    var p = raw.Trim().Trim('"');
    if (p.Length == 0) return null;
    if (Directory.Exists(p)) p = Path.Combine(p, "Plain Craft Launcher 2.exe");
    if (!File.Exists(p)) { Console.Error.WriteLine("文件不存在: " + p); return null; }
    var full = Path.GetFullPath(p);
    var dir = Path.GetDirectoryName(full);
    var name = Path.GetFileNameWithoutExtension(full);
    if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
    var output = Path.Combine(dir, name + " (dl-restore).exe");
    return (full, output);
}

(string input, string output)? AskInputAndOutput()
{
    Console.Title = "PCL 下载恢复补丁工具";
    Console.WriteLine("PCL 下载恢复补丁工具");
    Console.WriteLine("================================");
    Console.WriteLine("把 PCL 的 exe 拖进这个窗口（路径会自动粘贴），然后按回车。");
    Console.WriteLine("也可以直接输入 exe 或它所在文件夹的路径。");
    Console.WriteLine();
    while (true)
    {
        Console.Write("PCL exe 路径: ");
        var raw = Console.ReadLine();
        if (raw == null || raw.Trim().Length == 0) return null;
        var r = NormalizeInput(raw);
        if (r != null) return r;
        Console.WriteLine("没有找到文件，请重试（直接回车退出）。");
    }
}

void PauseBeforeExit()
{
    Console.WriteLine();
    Console.WriteLine("按任意键退出...");
    try { Console.ReadKey(true); } catch { }
}

// =====================================================================
// 补丁实现（全部高容错：忽略 nop、宽松常量/指令形态、结构回退）
// =====================================================================

Outcome P_LoaderInit(ModuleDefMD module)
{
    var t = MustType(module, "PCL.PageDownloadInstall");
    var m = MustMethod(t, "LoaderInit", 0);
    var list = m.Body.Instructions.ToList();

    var ltIdx = list.FindIndex(i => i.OpCode.Code == Code.Newobj
        && i.Operand is IMethod im && im.FullName.Contains("LoaderTask`2<System.String,PCL.ModDownload/DlClientListResult>::.ctor"));
    if (ltIdx >= 0)
    {
        // 受限形态：LoaderTask(字符串, 抛异常回调)。字符串文案可能变动，向前扫最近的 ldstr。
        var strIdx = PrevLdstrIndex(list, ltIdx, 40);
        if (strIdx < 0) return FailedOutcome("LoaderTask 前的字符串未找到");
        var loaderField = MustField(MustType(module, "PCL.ModDownload"), "DlClientListLoader");
        InsertRange(m.Body.Instructions, strIdx, new[] { I(OpCodes.Ldsfld, loaderField) });
        RemoveRange(m.Body.Instructions, strIdx + 1, ltIdx - strIdx + 1);
        ExpandShortBranches(m);
        return AppliedOutcome();
    }

    var hasLoader = list.Any(i => i.OpCode.Code == Code.Ldsfld && i.Operand is IField f && f.Name == "DlClientListLoader");
    return hasLoader ? AlreadyOutcome() : NotFoundOutcome();
}

Outcome P_LoadInstanceFinished(ModuleDefMD module)
{
    var t = MustType(module, "PCL.PageDownloadModify");
    var m = MustMethod(t, "LoadInstanceFinished", 1);
    var list = m.Body.Instructions.ToList();
    for (var i = 1; i < list.Count - 1; i++)
    {
        if (!IsLdcI4(list[i], 2)) continue;
        var p = PrevNonNop(list, i);
        var n = NextNonNopAfter(list, i);
        if (p == null || n == null) continue;
        if (!IsCallTo(list[p.Value], "get_CardMinecraft")) continue;
        if (!IsCallTo(list[n.Value], "set_Visibility")) continue;
        list[i].OpCode = OpCodes.Ldc_I4_0;
        ExpandShortBranches(m);
        return AppliedOutcome();
    }
    if (list.Any(i => IsCallTo(i, "get_CardMinecraft"))) return AlreadyOutcome();
    return NotFoundOutcome();
}

Outcome P_ModpackInstall0(ModuleDefMD module)
{
    var modModpack = MustType(module, "PCL.ModModpack");
    var m = MustMethod(modModpack, "ModpackInstall", 0);
    var body = m.Body;
    var list = body.Instructions.ToList();

    if (list.Any(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == "选择整合包压缩文件"))
        return AlreadyOutcome();

    // 受限形态：极短的方法体 + Hint 调用（文案可能变化，用结构特征判断）。
    var restricted = list.Any(i => i.OpCode.Code == Code.Ldstr && i.Operand is string s && s.Contains("为遵守相关法律法规"))
        || (list.Count <= 12 && list.Any(i => IsCallTo(i, "Hint")));
    if (!restricted) return NotFoundOutcome();

    var closureType = MustType(module, "PCL.ModModpack/_Closure$__0-0");
    var closureCtor = closureType.Methods.First(x => x.Name == ".ctor" && PCount(x) == 0);
    var closureField = closureType.Fields.First(x => x.Name == "$VB$Local_File");
    var closureLambda = closureType.Methods.First(x => x.Name == "_Lambda$__0" && PCount(x) == 0);

    var selectFile = FindMethodRef(module, "System.Collections.Generic.List`1<System.String> MeloongCore.Wpf.Dialogs::SelectFile");
    var firstOrDefault = FindMethodRef(module, "System.String System.Linq.Enumerable::FirstOrDefault<System.String>(System.Collections.Generic.IEnumerable`1<System.String>)");
    var isNullOrEmpty = FindMethodRef(module, "System.Boolean System.String::IsNullOrEmpty(System.String)");
    var tupleCtor = FindMethodRef(module, "System.Void System.ValueTuple`2<System.String[],System.String>::.ctor");
    var oneOfImplicit = FindMethodRef(module, "MeloongCore.OneOf`2<System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>,System.String> MeloongCore.OneOf`2<System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>,System.String>::op_Implicit(System.Collections.Generic.IEnumerable");
    var nullableCtor = FindMethodRef(module, "System.Void System.Nullable`1<MeloongCore.OneOf`2<System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>,System.String>>::.ctor");
    var actionCtor = FindMethodRef(module, "System.Void System.Action::.ctor(System.Object,System.IntPtr)");
    var runInThread = MustMethod(MustType(module, "PCL.ModBase"), "RunInThread", 1);
    var stringType = FindTypeRef(module, "System.String");
    var tupleType = FindTypeRef(module, "System.ValueTuple`2<System.String[],System.String>");

    var closureLocal = new Local(closureType.ToTypeSig());
    body.Variables.Clear();
    body.Variables.Add(closureLocal);
    body.InitLocals = true;

    var retInstr = I(OpCodes.Ret);
    var instrs = new List<Instruction>
    {
        I(OpCodes.Newobj, closureCtor),
        I(OpCodes.Stloc, closureLocal),
        I(OpCodes.Ldloc, closureLocal),
        I(OpCodes.Ldstr, "选择整合包压缩文件"),
        I(OpCodes.Ldc_I4_0),
        I(OpCodes.Ldnull),
        I(OpCodes.Ldc_I4, 1),
        I(OpCodes.Newarr, tupleType),
        I(OpCodes.Dup),
        I(OpCodes.Ldc_I4_0),
        I(OpCodes.Ldc_I4, 3),
        I(OpCodes.Newarr, stringType),
        I(OpCodes.Dup), I(OpCodes.Ldc_I4_0), I(OpCodes.Ldstr, "rar"), I(OpCodes.Stelem_Ref),
        I(OpCodes.Dup), I(OpCodes.Ldc_I4_1), I(OpCodes.Ldstr, "zip"), I(OpCodes.Stelem_Ref),
        I(OpCodes.Dup), I(OpCodes.Ldc_I4_2), I(OpCodes.Ldstr, "mrpack"), I(OpCodes.Stelem_Ref),
        I(OpCodes.Ldstr, "整合包文件"),
        I(OpCodes.Newobj, tupleCtor),
        I(OpCodes.Stelem, tupleType),
        I(OpCodes.Call, oneOfImplicit),
        I(OpCodes.Newobj, nullableCtor),
        I(OpCodes.Call, selectFile),
        I(OpCodes.Call, firstOrDefault),
        I(OpCodes.Stfld, closureField),
        I(OpCodes.Ldloc, closureLocal),
        I(OpCodes.Ldfld, closureField),
        I(OpCodes.Call, isNullOrEmpty),
        I(OpCodes.Brtrue, retInstr),
        I(OpCodes.Ldloc, closureLocal),
        I(OpCodes.Ldftn, closureLambda),
        I(OpCodes.Newobj, actionCtor),
        I(OpCodes.Call, runInThread),
        retInstr,
    };
    body.Instructions.Clear();
    foreach (var it in instrs) body.Instructions.Add(it);
    ExpandShortBranches(m);
    return AppliedOutcome();
}

Outcome P_ModpackInstallFile(ModuleDefMD module)
{
    var modModpack = MustType(module, "PCL.ModModpack");
    var m = MustMethod(modModpack, "ModpackInstall", 3);
    var body = m.Body;
    var list = body.Instructions.ToList();

    // 结构锚点：合规异常 = ldstr; newobj Exception(String); throw（catch 里的异常是双参构造，天然排除）。
    var idxStart = -1;
    for (var i = 2; i < list.Count; i++)
    {
        if (list[i].OpCode.Code != Code.Throw) continue;
        var n1 = PrevNonNop(list, i);
        if (n1 == null || list[n1.Value].OpCode.Code != Code.Newobj) continue;
        if (list[n1.Value].Operand is not IMethod exCtor || exCtor.Name != ".ctor"
            || exCtor.DeclaringType?.FullName != "System.Exception") continue;
        if (exCtor.MethodSig == null || exCtor.MethodSig.Params.Count != 1) continue;
        var n2 = PrevNonNop(list, n1.Value);
        if (n2 == null || list[n2.Value].OpCode.Code != Code.Ldstr) continue;
        idxStart = n2.Value;
        break;
    }
    if (idxStart < 0)
    {
        if (list.Any(i => i.OpCode.Code == Code.Call && i.Operand is IMethod im && im.Name == "InstallPackCurseForge"))
            return AlreadyOutcome();
        return NotFoundOutcome();
    }
    var oldFirst = list[idxStart];

    var installCurseForge = MustMethod(modModpack, "InstallPackCurseForge", 5);
    var installHMCL = MustMethod(modModpack, "InstallPackHMCL", 3);
    var installMMC = MustMethod(modModpack, "InstallPackMMC", 3);
    var installMCBBS = MustMethod(modModpack, "InstallPackMCBBS", 4);
    var installModrinth = MustMethod(modModpack, "InstallPackModrinth", 5);
    var installLauncherPack = MustMethod(modModpack, "InstallPackLauncherPack", 3);
    var installCompress = MustMethod(modModpack, "InstallPackCompress", 2);

    var v1 = body.Variables[1];
    var v2 = body.Variables[2];
    var v4 = body.Variables[4];
    var retLocal = new Local(m.ReturnType);
    body.Variables.Add(retLocal);
    var argFile = m.Parameters[0];
    var argInstance = m.Parameters[1];
    var argLogo = m.Parameters[2];

    Instruction Ld(Parameter p) => new(OpCodes.Ldarg, p);
    Instruction Loc(Local l) => new(OpCodes.Ldloc, l);

    var afterFinal = Loc(retLocal);

    List<Instruction> Args(int packType)
    {
        var l = new List<Instruction> { Ld(argFile), Loc(v1), Loc(v2) };
        switch (packType)
        {
            case 0: l.Add(Ld(argInstance)); l.Add(Ld(argLogo)); break;
            case 3: l.Add(Ld(argInstance)); break;
            case 4: l.Add(Ld(argInstance)); l.Add(Ld(argLogo)); break;
        }
        return l;
    }

    var cases = new (int PackType, MethodDef Method)[]
    {
        (0, installCurseForge), (1, installHMCL), (2, installMMC),
        (3, installMCBBS), (4, installModrinth), (9, installLauncherPack)
    };

    var blockStarts = cases.Select(_ => Loc(v4)).ToArray();
    var defaultBlock = new List<Instruction>
    {
        Ld(argFile), Loc(v1),
        I(OpCodes.Call, installCompress), I(OpCodes.Stloc, retLocal), I(OpCodes.Leave, afterFinal)
    };

    var chain = new List<Instruction>();
    for (var ci = 0; ci < cases.Length; ci++)
    {
        var next = ci + 1 < cases.Length ? blockStarts[ci + 1] : defaultBlock[0];
        var blk = new List<Instruction>
        {
            blockStarts[ci],
            I(OpCodes.Ldc_I4, cases[ci].PackType),
            I(OpCodes.Bne_Un, next),
        };
        blk.AddRange(Args(cases[ci].PackType));
        blk.Add(I(OpCodes.Call, cases[ci].Method));
        blk.Add(I(OpCodes.Stloc, retLocal));
        blk.Add(I(OpCodes.Leave, afterFinal));
        chain.AddRange(blk);
    }
    chain.AddRange(defaultBlock);

    var newFirst = blockStarts[0];

    RemoveRange(body.Instructions, idxStart, 3);
    InsertRange(body.Instructions, idxStart, chain);
    Retarget(m, oldFirst, newFirst);

    foreach (var eh in body.ExceptionHandlers)
    {
        if (eh.HandlerType == ExceptionHandlerType.Catch && ReferenceEquals(eh.HandlerEnd, oldFirst))
            eh.HandlerEnd = newFirst;
        if (eh.HandlerType == ExceptionHandlerType.Finally && eh.HandlerEnd == null && eh.HandlerStart != null)
            eh.HandlerEnd = afterFinal;
    }
    body.Instructions.Add(afterFinal);
    body.Instructions.Add(I(OpCodes.Ret));
    ExpandShortBranches(m);
    return AppliedOutcome();
}

Outcome P_ResourceVersions(ModuleDefMD module)
{
    var t = MustType(module, "PCL.PageDownloadResourceDetail");
    var m = MustMethod(t, "InitializeStackResourceVersions", 1);
    var saveClick = MustMethod(t, "Save_Click", 2);
    var installClick = MustMethod(t, "Install_Click", 2);
    var targetTypeField = MustField(t, "TargetResourceType");
    var list = m.Body.Instructions.ToList();

    var idx = list.FindIndex(i => i.OpCode.Code == Code.Ldftn && ReferenceEquals(i.Operand, saveClick));
    if (idx < 0) return NotFoundOutcome();
    if (list.Any(i => i.OpCode.Code == Code.Ldftn && ReferenceEquals(i.Operand, installClick)))
        return AlreadyOutcome();

    var p = PrevNonNop(list, idx);
    if (p == null || !IsLdThis(list[p.Value])) return FailedOutcome("邻接指令不符合预期");
    var n1 = NextNonNopAfter(list, idx);
    var n2 = n1 == null ? null : NextNonNopAfter(list, n1.Value);
    if (n1 == null || n2 == null || list[n1.Value].OpCode.Code != Code.Newobj || list[n2.Value].OpCode.Code != Code.Ldnull)
        return FailedOutcome("委托构造模式异常");

    var cehCtor = (IMethod)list[n1.Value].Operand;
    var nextAfterIdx = NextNonNopAfter(list, n2.Value);
    if (nextAfterIdx == null) return FailedOutcome("ToListItem 前指令缺失");
    var nextAfter = list[nextAfterIdx.Value];

    var arg0 = m.Parameters[0];
    var elseStart = I(OpCodes.Ldarg, arg0);
    var blk = new List<Instruction>
    {
        I(OpCodes.Ldarg, arg0),
        I(OpCodes.Ldfld, targetTypeField),
        I(OpCodes.Ldc_I4_2),
        I(OpCodes.Bne_Un, elseStart),
        I(OpCodes.Ldarg, arg0), I(OpCodes.Ldftn, installClick), I(OpCodes.Newobj, cehCtor),
        I(OpCodes.Ldarg, arg0), I(OpCodes.Ldftn, saveClick), I(OpCodes.Newobj, cehCtor),
        I(OpCodes.Br, nextAfter),
        elseStart,
        I(OpCodes.Ldarg, arg0), I(OpCodes.Ldftn, saveClick), I(OpCodes.Newobj, cehCtor),
        I(OpCodes.Ldnull),
    };

    RemoveRange(m.Body.Instructions, p.Value, n2.Value - p.Value + 1);
    InsertRange(m.Body.Instructions, p.Value, blk);
    ExpandShortBranches(m);
    return AppliedOutcome();
}

Outcome P_DownloadLeftCtor(ModuleDefMD module)
{
    var t = MustType(module, "PCL.PageDownloadLeft");
    var m = MustMethod(t, ".ctor", 0);
    var list = m.Body.Instructions.ToList();
    var idx = list.FindIndex(i => (i.OpCode.Code == Code.Callvirt || i.OpCode.Code == Code.Call)
        && i.Operand is IMethod im && im.Name == "get_ItemInstall");
    if (idx >= 0)
    {
        var start = PrevNonNop(list, idx);
        if (start == null || !IsLdThis(list[start.Value])) return FailedOutcome("隐藏段起始指令异常");
        RemoveRange(m.Body.Instructions, start.Value, m.Body.Instructions.Count - start.Value);
        m.Body.Instructions.Add(I(OpCodes.Ret));
        return AppliedOutcome();
    }
    if (list.Any(i => IsCallTo(i, "InitializeComponent"))) return AlreadyOutcome();
    return NotFoundOutcome();
}

Outcome P_PageType(ModuleDefMD module)
{
    var t = MustType(module, "PCL.PageResource");
    var m = MustMethod(t, "set_PageType", 1);
    var list = m.Body.Instructions.ToList();
    var idx = list.FindIndex(i => (i.OpCode.Code == Code.Callvirt || i.OpCode.Code == Code.Call)
        && i.Operand is IMethod im && im.Name == "get_BtnSearchInstallModPack");
    if (idx < 0) return NotFoundOutcome();

    var a1 = NextNonNopAfter(list, idx);
    var a2 = a1 == null ? null : NextNonNopAfter(list, a1.Value);
    var a3 = a2 == null ? null : NextNonNopAfter(list, a2.Value);
    var a4 = a3 == null ? null : NextNonNopAfter(list, a3.Value);
    var a5 = a4 == null ? null : NextNonNopAfter(list, a4.Value);
    var a6 = a5 == null ? null : NextNonNopAfter(list, a5.Value);
    if (a1 == null || a2 == null || a3 == null || a4 == null || a5 == null || a6 == null) return FailedOutcome("方法体长度异常");

    if (!IsLdArg1(list[a1.Value])) return FailedOutcome("条件模式异常");
    if (!IsLdcI4(list[a2.Value], 2)) return FailedOutcome("条件模式异常");
    if (IsCondBranch(list[a3.Value]) || list[a3.Value].OpCode.Code == Code.Ceq) return AlreadyOutcome(); // 正常版：条件已存在（beq/ceq/brtrue 等）
    if (list[a3.Value].OpCode.Code != Code.Pop || list[a4.Value].OpCode.Code != Code.Pop
        || !IsLdcI4(list[a5.Value], 2) || !IsCallTo(list[a6.Value], "set_Visibility"))
        return FailedOutcome("折叠残渣模式异常");

    var ldarg1 = list[a1.Value];
    var modpackConst = list[a2.Value];
    var collapsedConst = list[a5.Value];
    var setVis = list[a6.Value];
    var lvis = I(OpCodes.Ldc_I4_0);
    var blk = new List<Instruction>
    {
        CopyOp(ldarg1),
        CopyOp(modpackConst),
        I(OpCodes.Ceq),
        I(OpCodes.Brtrue, lvis),
        CopyOp(collapsedConst),
        I(OpCodes.Br, setVis),
        lvis,
    };
    RemoveRange(m.Body.Instructions, a1.Value, a5.Value - a1.Value + 1);
    InsertRange(m.Body.Instructions, a1.Value, blk);
    ExpandShortBranches(m);
    return AppliedOutcome();
}

Outcome P_ModDisabledDownloadBtn(ModuleDefMD module)
{
    var t = MustType(module, "PCL.PageInstanceModDisabled");
    var m = MustMethod(t, "BtnDownload_Loaded", 0);
    var list = m.Body.Instructions.ToList();
    if (list.Any(i => i.OpCode.Code == Code.Call && i.Operand is IMethod im && im.FullName.StartsWith("System.Boolean PCL.Settings::Get<System.Boolean>")))
        return AlreadyOutcome();

    var first = NextNonNop(list, 0);
    if (first == null) return NotFoundOutcome();
    var firstInstr = list[first.Value];
    if (!IsLdcI4(firstInstr, 2) && !IsLdcI4(firstInstr, 1)) return NotFoundOutcome();

    // 组合条件：Settings.Get("UiHiddenPageDownload") AndAlso Not HiddenForceShow
    //          OrElse If(FrmSelectRight Is Nothing, False, FrmSelectRight.ShowHidden)
    var settingsGet = FindInstr(module,
        i => i.OpCode.Code == Code.Call && i.Operand is IMethod im
            && im.FullName.StartsWith("System.Boolean PCL.Settings::Get<System.Boolean>"))
        ?? throw new Exception("Settings.Get<Boolean> 引用不存在");
    var hfsGet = FindInstr(module,
        i => (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt)
            && i.Operand is IMethod im && im.Name == "get_HiddenForceShow")
        ?? throw new Exception("get_HiddenForceShow 引用不存在");
    var frmRight = FindInstr(module,
        i => i.OpCode.Code == Code.Ldsfld && i.Operand is IField f && f.Name == "FrmSelectRight")
        ?? throw new Exception("FrmSelectRight 引用不存在");
    var showHidden = FindInstr(module,
        i => i.OpCode.Code == Code.Ldfld && i.Operand is IField f && f.Name == "ShowHidden")
        ?? throw new Exception("ShowHidden 引用不存在");

    var lTrue = I(OpCodes.Ldc_I4_1);
    var lFalseB = I(OpCodes.Ldc_I4_0);
    var lDoneB = I(OpCodes.Nop);
    var lTrue2 = I(OpCodes.Ldc_I4_1);
    var lcondEnd = I(OpCodes.Nop);
    var lvis = I(OpCodes.Ldc_I4_0);
    var lset = I(OpCodes.Nop);
    var stlocIdx = NextNonNopAfter(list, first.Value);
    if (stlocIdx == null || !IsStloc0(list[stlocIdx.Value], m)) return FailedOutcome("赋值指令模式异常");
    var stloc = list[stlocIdx.Value];
    var collapsed = firstInstr;

    var blk = new List<Instruction>
    {
        I(OpCodes.Ldstr, "UiHiddenPageDownload"),
        I(OpCodes.Ldnull),
        CopyOp(settingsGet),
        CopyOp(hfsGet),
        I(OpCodes.Ldc_I4_0),
        I(OpCodes.Ceq),
        I(OpCodes.And),
        I(OpCodes.Brtrue, lTrue),
        CopyOp(frmRight),
        I(OpCodes.Brfalse, lFalseB),
        CopyOp(frmRight),
        CopyOp(showHidden),
        I(OpCodes.Br, lDoneB),
        lFalseB,
        lDoneB,
        I(OpCodes.Brtrue, lTrue),
        I(OpCodes.Ldc_I4_0),
        I(OpCodes.Br, lcondEnd),
        lTrue,
        lcondEnd,
        I(OpCodes.Brfalse, lvis),
        CopyOp(collapsed),
        I(OpCodes.Br, lset),
        lvis,
        lset,
        stloc,
    };
    RemoveRange(m.Body.Instructions, first.Value, stlocIdx.Value - first.Value + 1);
    InsertRange(m.Body.Instructions, first.Value, blk);
    ExpandShortBranches(m);
    return AppliedOutcome();
}

Outcome P_LaunchState(ModuleDefMD module)
{
    var t = MustType(module, "PCL.PageLaunchLeft");
    var m = MustMethod(t, "RefreshButtonsUI", 0);
    var list = m.Body.Instructions.ToList();

    for (var s = 1; s < list.Count - 1; s++)
    {
        if (!IsStloc0(list[s], m)) continue;
        var p = PrevNonNop(list, s);
        if (p == null || !IsLdcI4(list[p.Value], 1)) continue;
        var q = PrevNonNop(list, p.Value);
        if (q == null || !IsCondBranch(list[q.Value])) continue;
        var r = PrevNonNop(list, q.Value);
        if (r == null || !IsCallTo(list[r.Value], "get_McInstanceSelected")) continue;

        var settingsGet = FindInstr(module,
            i => i.OpCode.Code == Code.Call && i.Operand is IMethod im
                && im.FullName.StartsWith("System.Boolean PCL.Settings::Get<System.Boolean>"))
            ?? throw new Exception("Settings.Get<Boolean> 引用不存在");
        var hfsGet = FindInstr(module,
            i => (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt)
                && i.Operand is IMethod im && im.Name == "get_HiddenForceShow")
            ?? throw new Exception("get_HiddenForceShow 引用不存在");

        var stloc = list[s];
        var l2 = I(OpCodes.Ldc_I4_2);
        var blk = new List<Instruction>
        {
            I(OpCodes.Ldstr, "UiHiddenPageDownload"),
            I(OpCodes.Ldnull),
            CopyOp(settingsGet),
            CopyOp(hfsGet),
            I(OpCodes.Ldc_I4_0),
            I(OpCodes.Ceq),
            I(OpCodes.And),
            I(OpCodes.Brfalse, l2),
            I(OpCodes.Ldc_I4_1),
            I(OpCodes.Br, stloc),
            l2,
            stloc,
        };
        RemoveRange(m.Body.Instructions, p.Value, s - p.Value + 1);
        InsertRange(m.Body.Instructions, p.Value, blk);
        ExpandShortBranches(m);
        return AppliedOutcome();
    }
    if (list.Any(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == "UiHiddenPageDownload"))
        return AlreadyOutcome();
    return NotFoundOutcome();
}

Outcome P_SelectLeftImport(ModuleDefMD module)
{
    var t = MustType(module, "PCL.PageSelectLeft");
    var m = MustMethod(t, "McFolderListUI", 0);
    var list = m.Body.Instructions.ToList();
    var idx = list.FindIndex(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == "导入整合包");
    if (idx < 0) return NotFoundOutcome();

    var j = -1;
    for (var k = idx; k < Math.Min(idx + 400, list.Count); k++)
    {
        if (list[k].OpCode.Code != Code.Callvirt) continue;
        if (list[k].Operand is IMethod im && im.Name == "add_Click" && im.DeclaringType?.FullName == "PCL.MyListItem")
        { j = k; break; }
    }
    if (j < 0) return FailedOutcome("add_Click 未找到");

    Local? itemLocal = null;
    for (var k = j - 1; k >= Math.Max(0, j - 20); k--)
    {
        var code = list[k].OpCode.Code;
        if (code is Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3 || code == Code.Ldloc_S || code == Code.Ldloc)
        { itemLocal = LocalOf(list[k], m); break; }
    }
    if (itemLocal == null) return FailedOutcome("ItemInstall 局部变量未找到");

    // 幂等检查：方法里是否已经存在对同一局部变量的 Add（2.99.0.0 在 AddHandler 之前，
    // 打过补丁的在 AddHandler 之后，两种形态都覆盖）。
    for (var k = 0; k < list.Count; k++)
    {
        if (!IsCallTo(list[k], "Add")) continue;
        var prev = PrevNonNop(list, k);
        if (prev != null && LocalOf(list[prev.Value], m) is Local pl && ReferenceEquals(pl, itemLocal))
            return AlreadyOutcome();
    }

    var frm = FindInstr(module, i => i.OpCode.Code == Code.Ldsfld && i.Operand is IField f && f.Name == "FrmSelectLeft")
        ?? throw new Exception("FrmSelectLeft 引用不存在");
    var panList = FindInstr(module, i => IsCallTo(i, "get_PanList"))
        ?? throw new Exception("get_PanList 引用不存在");
    var children = FindInstr(module, i => IsCallTo(i, "get_Children"))
        ?? throw new Exception("get_Children 引用不存在");
    var add = FindInstr(module, i => i.OpCode.Code == Code.Callvirt && i.Operand is IMethod im
            && im.Name == "Add" && im.DeclaringType?.FullName == "System.Windows.Controls.UIElementCollection")
        ?? throw new Exception("UIElementCollection.Add 引用不存在");

    var blk = new List<Instruction>
    {
        CopyOp(frm),
        CopyOp(panList),
        CopyOp(children),
        I(OpCodes.Ldloc, itemLocal),
        CopyOp(add),
        I(OpCodes.Pop),
    };
    InsertRange(m.Body.Instructions, j + 1, blk);
    ExpandShortBranches(m);
    return AppliedOutcome();
}

Outcome P_SelectRightEmpty(ModuleDefMD module)
{
    var t = MustType(module, "PCL.PageSelectRight");
    var m = MustMethod(t, "McInstanceListUI", 1);
    var list = m.Body.Instructions.ToList();

    var positions = new List<int>();
    for (var k = 0; k < list.Count; k++)
        if (IsCallTo(list[k], "get_BtnEmptyDownload")) positions.Add(k);
    if (positions.Count < 2) return NotFoundOutcome();

    var j = positions[1]; // ShowHidden 分支之后的那处（else 分支）

    for (var k = j; k < Math.Min(j + 25, list.Count); k++)
        if (list[k].OpCode.Code == Code.Call && list[k].Operand is IMethod im
            && im.FullName.StartsWith("System.Boolean PCL.Settings::Get<System.Boolean>"))
            return AlreadyOutcome();

    var n1 = NextNonNopAfter(list, j);
    if (n1 == null || !IsLdcI4(list[n1.Value], 2)) return FailedOutcome("按钮可见性模式异常");
    var n2 = NextNonNopAfter(list, n1.Value);
    if (n2 == null || !IsCallTo(list[n2.Value], "set_Visibility")) return FailedOutcome("按钮可见性模式异常");

    var settingsGet = FindInstr(module,
        i => i.OpCode.Code == Code.Call && i.Operand is IMethod im
            && im.FullName.StartsWith("System.Boolean PCL.Settings::Get<System.Boolean>"))
        ?? throw new Exception("Settings.Get<Boolean> 引用不存在");
    var hfsGet = FindInstr(module,
        i => (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt)
            && i.Operand is IMethod im && im.Name == "get_HiddenForceShow")
        ?? throw new Exception("get_HiddenForceShow 引用不存在");

    var setVis = list[n2.Value];
    var vis = I(OpCodes.Ldc_I4_0);
    var collapsed = list[n1.Value];
    var blk = new List<Instruction>
    {
        I(OpCodes.Ldstr, "UiHiddenPageDownload"),
        I(OpCodes.Ldnull),
        CopyOp(settingsGet),
        CopyOp(hfsGet),
        I(OpCodes.Ldc_I4_0),
        I(OpCodes.Ceq),
        I(OpCodes.And),
        I(OpCodes.Brfalse, vis),
        CopyOp(collapsed),
        I(OpCodes.Br, setVis),
        vis,
    };
    RemoveRange(m.Body.Instructions, n1.Value, 1);
    InsertRange(m.Body.Instructions, n1.Value, blk);
    ExpandShortBranches(m);
    return AppliedOutcome();
}

Outcome P_UpdateDialog(ModuleDefMD module)
{
    var t = MustType(module, "PCL.ModMinecraft");
    var m = MustMethod(t, "McDownloadClientUpdateHint", 2);
    var list = m.Body.Instructions.ToList();

    var idx = list.FindIndex(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == "确定");
    if (idx < 0)
    {
        // 回退：MyMsgBox 前第 4 个字符串参数即按钮 2 文案。
        var boxIdx = list.FindIndex(i => i.OpCode.Code == Code.Call && i.Operand is IMethod im && im.Name == "MyMsgBox");
        if (boxIdx < 0) return NotFoundOutcome();
        var strs = Enumerable.Range(0, boxIdx).Where(i => list[i].OpCode.Code == Code.Ldstr).ToList();
        if (strs.Count < 4) return NotFoundOutcome();
        idx = strs[3] - 1; // 以“确定”前一位为参照，下移一步到按钮 2
    }

    var next = NextNonNopAfter(list, idx);
    if (next == null) return NotFoundOutcome();
    var ni = list[next.Value];
    if (ni.OpCode.Code != Code.Ldstr) return NotFoundOutcome();
    var text = (string)ni.Operand;
    if (text == "下载") return AlreadyOutcome();
    if (text == "")
    {
        ni.Operand = "下载";
        return AppliedOutcome();
    }
    if (text.Contains("下载")) return AlreadyOutcome();
    return NotFoundOutcome();
}

// =====================================================================
// 基础工具
// =====================================================================

string? GetVersion(ModuleDefMD module)
{
    try
    {
        var asm = module.Assembly;
        if (asm == null) return null;
        foreach (var ca in asm.CustomAttributes)
        {
            if (ca.Constructor?.DeclaringType?.FullName != "System.Reflection.AssemblyFileVersionAttribute") continue;
            if (ca.ConstructorArguments.Count == 0) continue;
            return ca.ConstructorArguments[0].Value switch
            {
                string s => s,
                UTF8String u => u.ToString(),
                _ => ca.ConstructorArguments[0].Value?.ToString()
            };
        }
    }
    catch { }
    return null;
}

TypeDef MustType(ModuleDefMD module, string fullName)
{
    var t = module.GetTypes().FirstOrDefault(x => x.FullName == fullName);
    if (t == null) throw new Exception("Type not found: " + fullName);
    return t;
}

MethodDef MustMethod(TypeDef t, string name, int paramCount)
{
    var candidates = t.Methods.Where(x => x.Name == name).ToList();
    var m = candidates.FirstOrDefault(x => (x.MethodSig == null ? x.Parameters.Count : x.MethodSig.Params.Count) == paramCount);
    if (m == null) throw new Exception($"Method not found: {t.FullName}::{name}/{paramCount}");
    return m;
}

FieldDef MustField(TypeDef t, string name)
{
    var f = t.Fields.FirstOrDefault(x => x.Name == name);
    if (f == null) throw new Exception($"Field not found: {t.FullName}::{name}");
    return f;
}

Instruction? FindInstr(ModuleDefMD module, Func<Instruction, bool> pred)
{
    foreach (var t in module.GetTypes())
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var ins in m.Body.Instructions)
                if (pred(ins)) return ins;
        }
    return null;
}

IMethod FindMethodRef(ModuleDefMD module, string fullNamePrefix)
    => (IMethod)(FindInstr(module, i => i.Operand is IMethod x && x.FullName.StartsWith(fullNamePrefix))?.Operand
        ?? throw new Exception("Method ref not found: " + fullNamePrefix));

ITypeDefOrRef FindTypeRef(ModuleDefMD module, string fullNamePrefix)
    => (ITypeDefOrRef)(FindInstr(module, i => i.Operand is ITypeDefOrRef x && x.FullName.StartsWith(fullNamePrefix))?.Operand
        ?? throw new Exception("Type ref not found: " + fullNamePrefix));

void ExpandShortBranches(MethodDef m)
{
    var map = new Dictionary<Code, OpCode>
    {
        { Code.Br_S, OpCodes.Br }, { Code.Brfalse_S, OpCodes.Brfalse }, { Code.Brtrue_S, OpCodes.Brtrue },
        { Code.Beq_S, OpCodes.Beq }, { Code.Bge_S, OpCodes.Bge }, { Code.Bgt_S, OpCodes.Bgt }, { Code.Ble_S, OpCodes.Ble },
        { Code.Blt_S, OpCodes.Blt }, { Code.Bne_Un_S, OpCodes.Bne_Un }, { Code.Bge_Un_S, OpCodes.Bge_Un },
        { Code.Bgt_Un_S, OpCodes.Bgt_Un }, { Code.Ble_Un_S, OpCodes.Ble_Un }, { Code.Blt_Un_S, OpCodes.Blt_Un },
        { Code.Leave_S, OpCodes.Leave }
    };
    foreach (var ins in m.Body.Instructions)
        if (map.TryGetValue(ins.OpCode.Code, out var op)) ins.OpCode = op;
}

void Retarget(MethodDef m, Instruction oldTarget, Instruction newTarget)
{
    foreach (var ins in m.Body.Instructions)
        if (ReferenceEquals(ins.Operand, oldTarget))
            ins.Operand = newTarget;
}

void InsertRange(System.Collections.Generic.IList<Instruction> list, int index, IEnumerable<Instruction> items)
{
    var i = index;
    foreach (var it in items) { list.Insert(i, it); i++; }
}

void RemoveRange(System.Collections.Generic.IList<Instruction> list, int index, int count)
{
    for (var i = 0; i < count; i++) list.RemoveAt(index);
}

int PCount(MethodDef mm) => mm.MethodSig == null ? mm.Parameters.Count : mm.MethodSig.Params.Count;

static Instruction I(OpCode op, object? operand = null) => new(op, operand);

static Instruction CopyOp(Instruction src) => new(src.OpCode, src.Operand);

bool IsLdcI4(Instruction ins, int v)
{
    var c = ins.OpCode.Code;
    return c switch
    {
        Code.Ldc_I4_M1 => v == -1,
        Code.Ldc_I4_0 => v == 0,
        Code.Ldc_I4_1 => v == 1,
        Code.Ldc_I4_2 => v == 2,
        Code.Ldc_I4_3 => v == 3,
        Code.Ldc_I4_4 => v == 4,
        Code.Ldc_I4_5 => v == 5,
        Code.Ldc_I4_6 => v == 6,
        Code.Ldc_I4_7 => v == 7,
        Code.Ldc_I4_8 => v == 8,
        Code.Ldc_I4_S => ins.Operand is sbyte s && s == v,
        Code.Ldc_I4 => ins.Operand is int i && i == v,
        _ => false
    };
}

bool IsLdThis(Instruction ins)
    => ins.OpCode.Code == Code.Ldarg_0 || (ins.OpCode.Code == Code.Ldarg && ins.Operand is Parameter p && p.Index == 0);

bool IsLdArg1(Instruction ins)
    => ins.OpCode.Code == Code.Ldarg_1 || (ins.OpCode.Code == Code.Ldarg && ins.Operand is Parameter p && p.Index == 1);

bool IsStloc0(Instruction ins, MethodDef m)
{
    var c = ins.OpCode.Code;
    if (c == Code.Stloc_0) return true;
    if (c == Code.Stloc_S || c == Code.Stloc)
        return ins.Operand is Local l && m.Body.Variables.Count > 0 && ReferenceEquals(l, m.Body.Variables[0]);
    return false;
}

Local? LocalOf(Instruction ins, MethodDef m)
{
    var c = ins.OpCode.Code;
    var vars = m.Body.Variables;
    return c switch
    {
        Code.Ldloc_0 or Code.Stloc_0 => vars.Count > 0 ? vars[0] : null,
        Code.Ldloc_1 or Code.Stloc_1 => vars.Count > 1 ? vars[1] : null,
        Code.Ldloc_2 or Code.Stloc_2 => vars.Count > 2 ? vars[2] : null,
        Code.Ldloc_3 or Code.Stloc_3 => vars.Count > 3 ? vars[3] : null,
        Code.Ldloc_S or Code.Ldloc or Code.Stloc_S or Code.Stloc => ins.Operand as Local,
        _ => null
    };
}

bool IsCallTo(Instruction ins, string methodName)
    => (ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt || ins.OpCode.Code == Code.Newobj)
        && ins.Operand is IMethod im && im.Name == methodName;

bool IsCondBranch(Instruction ins)
    => ins.OpCode.FlowControl == FlowControl.Cond_Branch;

int? NextNonNop(System.Collections.Generic.IList<Instruction> list, int from)
{
    for (var i = from; i < list.Count; i++)
        if (list[i].OpCode.Code != Code.Nop) return i;
    return null;
}

int? NextNonNopAfter(System.Collections.Generic.IList<Instruction> list, int from)
{
    for (var i = from + 1; i < list.Count; i++)
        if (list[i].OpCode.Code != Code.Nop) return i;
    return null;
}

int? PrevNonNop(System.Collections.Generic.IList<Instruction> list, int from)
{
    for (var i = from - 1; i >= 0; i--)
        if (list[i].OpCode.Code != Code.Nop) return i;
    return null;
}

int PrevLdstrIndex(System.Collections.Generic.IList<Instruction> list, int from, int maxBack)
{
    var start = Math.Max(0, from - maxBack);
    for (var i = from - 1; i >= start; i--)
        if (list[i].OpCode.Code == Code.Ldstr) return i;
    return -1;
}

static Outcome AppliedOutcome() => new() { Kind = OutKind.Applied };
static Outcome AlreadyOutcome() => new() { Kind = OutKind.Already };
static Outcome NotFoundOutcome() => new() { Kind = OutKind.NotFound };
static Outcome FailedOutcome(string note) => new() { Kind = OutKind.Failed, Note = " " + note };
static Outcome Failed(string note) => new() { Kind = OutKind.Failed, Note = " " + note };

// ---------------------------------------------------------------
enum OutKind { Applied, Already, NotFound, Failed }

sealed class Outcome
{
    public string Name = "";
    public bool Required = true;
    public OutKind Kind;
    public string Note = "";
}
