using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

// McRestorePatcher: restores the Minecraft-download entry points that official
// PCL 2.99.0.0 compiled away (compliance restriction), following the pre-2.99
// (2.13.1.1 / 2.14.0.0) implementation.
//
// Usage: McRestorePatcher <input.exe> <output.exe>
//   or:  McRestorePatcher --dump <exe> <typeFullName> <methodName>
var inPath = args[0];
var outPath = args[1];

if (inPath == "--dump")
{
    var dm = ModuleDefMD.Load(outPath);
    var dumpType = dm.GetTypes().First(t => t.FullName == args[2]);
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
    return;
}

if (inPath == "--refs")
{
    var rm = ModuleDefMD.Load(outPath);
    var set = new SortedSet<string>();
    foreach (var t in rm.GetTypes())
        foreach (var mm in t.Methods)
        {
            if (!mm.HasBody) continue;
            foreach (var ins in mm.Body.Instructions)
                if (ins.Operand is IMethod mr && mr.FullName.Contains(args[2])) set.Add(mr.FullName);
        }
    foreach (var s in set) Console.WriteLine(s);
    return;
}

var module = ModuleDefMD.Load(inPath);

// ---------- helpers ----------
TypeDef FindType(string fullName)
{
    var t = module.GetTypes().FirstOrDefault(x => x.FullName == fullName);
    if (t == null) throw new Exception("Type not found: " + fullName);
    return t;
}

MethodDef FindMethod(TypeDef t, string name, int paramCount)
{
    var candidates = t.Methods.Where(x => x.Name == name).ToList();
    var m = candidates.FirstOrDefault(x => (x.MethodSig == null ? x.Parameters.Count : x.MethodSig.Params.Count) == paramCount);
    if (m == null) throw new Exception($"Method not found: {t.FullName}::{name}/{paramCount}; candidates: {string.Join(" | ", candidates.Select(c => c.Name + " sig=" + (c.MethodSig == null ? "-" : c.MethodSig.ToString()) + " params=" + c.Parameters.Count + " static=" + c.IsStatic + " body=" + c.HasBody))}");
    return m;
}

FieldDef FindField(TypeDef t, string name)
{
    var f = t.Fields.FirstOrDefault(x => x.Name == name);
    if (f == null) throw new Exception($"Field not found: {t.FullName}::{name}");
    return f;
}

Instruction FindInstr(Func<Instruction, bool> pred)
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

IMethod FindMethodRef(string fullNamePrefix)
    => (IMethod)(FindInstr(i => i.Operand is IMethod x && x.FullName.StartsWith(fullNamePrefix))?.Operand
        ?? throw new Exception("Method ref not found: " + fullNamePrefix));

ITypeDefOrRef FindTypeRef(string fullNamePrefix)
    => (ITypeDefOrRef)(FindInstr(i => i.Operand is ITypeDefOrRef x && x.FullName.StartsWith(fullNamePrefix))?.Operand
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

Instruction I(OpCode op, object operand = null) => new Instruction(op, operand);

// =====================================================================
// 1) PageDownloadInstall.LoaderInit -> use the real DlClientListLoader
// =====================================================================
{
    var t = FindType("PCL.PageDownloadInstall");
    var m = FindMethod(t, "LoaderInit", 0);
    var instrs = m.Body.Instructions;
    var start = instrs.ToList().FindIndex(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == "关闭下载");
    if (start < 0) throw new Exception("LoaderInit: 关闭下载 string not found");
    var end = instrs.ToList().FindIndex(i => i.OpCode.Code == Code.Newobj
        && i.Operand is IMethodDefOrRef mdr && mdr.FullName.Contains("LoaderTask`2<System.String,PCL.ModDownload/DlClientListResult>::.ctor"));
    if (end < 0 || end < start) throw new Exception("LoaderInit: LoaderTask ctor not found");
    var loaderField = FindField(FindType("PCL.ModDownload"), "DlClientListLoader");
    InsertRange(instrs, start, new[] { I(OpCodes.Ldsfld, loaderField) });
    RemoveRange(instrs, start + 1, end - start + 1);
    ExpandShortBranches(m);
    Console.WriteLine("[OK] PageDownloadInstall.LoaderInit");
}

// =====================================================================
// 2) PageDownloadModify.LoadInstanceFinished -> CardMinecraft visible
// =====================================================================
{
    var t = FindType("PCL.PageDownloadModify");
    var m = FindMethod(t, "LoadInstanceFinished", 1);
    var instrs = m.Body.Instructions;
    var idx = -1;
    for (var i = 1; i < instrs.Count - 1; i++)
    {
        if (instrs[i].OpCode.Code != Code.Ldc_I4_2) continue;
        if (instrs[i - 1].OpCode.Code != Code.Call && instrs[i - 1].OpCode.Code != Code.Callvirt) continue;
        if (instrs[i - 1].Operand is IMethodDefOrRef prev && prev.FullName.Contains("get_CardMinecraft")
            && instrs[i + 1].OpCode.Code == Code.Callvirt
            && instrs[i + 1].Operand is IMethodDefOrRef nxt && nxt.FullName.Contains("set_Visibility"))
        { idx = i; break; }
    }
    if (idx < 0) throw new Exception("LoadInstanceFinished: CardMinecraft visibility not found");
    instrs[idx].OpCode = OpCodes.Ldc_I4_0;
    ExpandShortBranches(m);
    Console.WriteLine("[OK] PageDownloadModify.LoadInstanceFinished");
}

// =====================================================================
// 3) ModModpack.ModpackInstall() -> restore file dialog + RunInThread
//    (IL mirrors the 2.13.1.1 implementation exactly)
// =====================================================================
{
    var modModpack = FindType("PCL.ModModpack");
    var m = FindMethod(modModpack, "ModpackInstall", 0);
    var body = m.Body;
    var closureType = FindType("PCL.ModModpack/_Closure$__0-0");
    var closureCtor = closureType.Methods.First(x => x.Name == ".ctor" && PCount(x) == 0);
    var closureField = closureType.Fields.First(x => x.Name == "$VB$Local_File");
    var closureLambda = closureType.Methods.First(x => x.Name == "_Lambda$__0" && PCount(x) == 0);

    var selectFile = FindMethodRef("System.Collections.Generic.List`1<System.String> MeloongCore.Wpf.Dialogs::SelectFile");
    var firstOrDefault = FindMethodRef("System.String System.Linq.Enumerable::FirstOrDefault<System.String>(System.Collections.Generic.IEnumerable`1<System.String>)");
    var isNullOrEmpty = FindMethodRef("System.Boolean System.String::IsNullOrEmpty(System.String)");
    var tupleCtor = FindMethodRef("System.Void System.ValueTuple`2<System.String[],System.String>::.ctor");
    var oneOfImplicit = FindMethodRef("MeloongCore.OneOf`2<System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>,System.String> MeloongCore.OneOf`2<System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>,System.String>::op_Implicit(System.Collections.Generic.IEnumerable");
    var nullableCtor = FindMethodRef("System.Void System.Nullable`1<MeloongCore.OneOf`2<System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>,System.String>>::.ctor");
    var actionCtor = FindMethodRef("System.Void System.Action::.ctor(System.Object,System.IntPtr)");
    var runInThread = FindMethod(FindType("PCL.ModBase"), "RunInThread", 1);
    var stringType = FindTypeRef("System.String");
    var tupleType = FindTypeRef("System.ValueTuple`2<System.String[],System.String>");

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
    Console.WriteLine("[OK] ModModpack.ModpackInstall()");
}

// =====================================================================
// 4) ModModpack.ModpackInstall(File,...) -> restore pack-type dispatch
// =====================================================================
{
    var modModpack = FindType("PCL.ModModpack");
    var m = FindMethod(modModpack, "ModpackInstall", 3);
    var body = m.Body;
    var instrs = body.Instructions.ToList();

    var idxStart = instrs.FindIndex(i => i.OpCode.Code == Code.Ldstr && (string)i.Operand == "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……");
    if (idxStart < 0) throw new Exception("ModpackInstall(File): compliance string not found");
    var oldFirst = instrs[idxStart];
    if (instrs[idxStart + 1].OpCode.Code != Code.Newobj || instrs[idxStart + 2].OpCode.Code != Code.Throw)
        throw new Exception("ModpackInstall(File): unexpected pattern after compliance string");

    var installCurseForge = FindMethod(modModpack, "InstallPackCurseForge", 5);
    var installHMCL = FindMethod(modModpack, "InstallPackHMCL", 3);
    var installMMC = FindMethod(modModpack, "InstallPackMMC", 3);
    var installMCBBS = FindMethod(modModpack, "InstallPackMCBBS", 4);
    var installModrinth = FindMethod(modModpack, "InstallPackModrinth", 5);
    var installLauncherPack = FindMethod(modModpack, "InstallPackLauncherPack", 3);
    var installCompress = FindMethod(modModpack, "InstallPackCompress", 2);

    var v1 = body.Variables[1]; // ZipArchive
    var v2 = body.Variables[2]; // String  ArchiveBaseFolder
    var v4 = body.Variables[4]; // Int32   PackType
    var retLocal = new Local(m.ReturnType);
    body.Variables.Add(retLocal);

    var argFile = m.Parameters[0];
    var argInstance = m.Parameters[1];
    var argLogo = m.Parameters[2];

    var afterFinal = I(OpCodes.Ldloc, retLocal); // final label: loads return value

    List<Instruction> Args(int packType)
    {
        var l = new List<Instruction> { I(OpCodes.Ldarg, argFile), I(OpCodes.Ldloc, v1), I(OpCodes.Ldloc, v2) };
        switch (packType)
        {
            case 0: l.Add(I(OpCodes.Ldarg, argInstance)); l.Add(I(OpCodes.Ldarg, argLogo)); break;
            case 3: l.Add(I(OpCodes.Ldarg, argInstance)); break;
            case 4: l.Add(I(OpCodes.Ldarg, argInstance)); l.Add(I(OpCodes.Ldarg, argLogo)); break;
        }
        return l;
    }

    var cases = new (int PackType, MethodDef Method)[]
    {
        (0, installCurseForge), (1, installHMCL), (2, installMMC),
        (3, installMCBBS), (4, installModrinth), (9, installLauncherPack)
    };

    var blockStarts = cases.Select(_ => I(OpCodes.Ldloc, v4)).ToArray();
    var defaultBlock = new List<Instruction>
    {
        I(OpCodes.Ldarg, argFile), I(OpCodes.Ldloc, v1),
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
    Console.WriteLine("[OK] ModModpack.ModpackInstall(File...)");
}

// =====================================================================
// 5) PageDownloadResourceDetail.InitializeStackResourceVersions -> install button
// =====================================================================
{
    var t = FindType("PCL.PageDownloadResourceDetail");
    var m = FindMethod(t, "InitializeStackResourceVersions", 1);
    var saveClick = FindMethod(t, "Save_Click", 2);
    var installClick = FindMethod(t, "Install_Click", 2);
    var targetTypeField = FindField(t, "TargetResourceType");
    var instrs = m.Body.Instructions;
    var idx = instrs.ToList().FindIndex(i => i.OpCode.Code == Code.Ldftn && ReferenceEquals(i.Operand, saveClick));
    if (idx < 0) throw new Exception("InitializeStackResourceVersions: Save_Click ldftn not found");
    if ((instrs[idx - 1].OpCode.Code != Code.Ldarg_0 && instrs[idx - 1].OpCode.Code != Code.Ldarg) || instrs[idx + 1].OpCode.Code != Code.Newobj || instrs[idx + 2].OpCode.Code != Code.Ldnull)
        throw new Exception("InitializeStackResourceVersions: unexpected pattern");
    var cehCtor = (IMethodDefOrRef)instrs[idx + 1].Operand;
    var nextAfter = instrs[idx + 3]; // ldloc HasDuplicates
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

    RemoveRange(instrs, idx - 1, 4);
    InsertRange(instrs, idx - 1, blk);
    ExpandShortBranches(m);
    Console.WriteLine("[OK] PageDownloadResourceDetail.InitializeStackResourceVersions");
}

// ---------- write ----------
var opts = new ModuleWriterOptions(module) { Logger = DummyLogger.NoThrowInstance };
module.Write(outPath, opts);
Console.WriteLine("Written: " + outPath);
