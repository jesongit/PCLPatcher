using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

// Transplant: copy method bodies from the RestrictMcDownload=False reference build into
// the official EXE, re-importing every token by member name. Fails hard if any token
// cannot be resolved, so a silent half-patch is impossible.

static class Transplant
{
    // (type fullname, method name, param count) — the complete RestrictMcDownload amputation sites.
    public static readonly (string Type, string Method, int ParamCount)[] Targets =
    [
        ("PCL.ModModpack", "ModpackInstall", 0),
        ("PCL.ModModpack", "ModpackInstall", 3),
        ("PCL.PageDownloadInstall", "LoaderInit", 0),
        ("PCL.PageDownloadResourceDetail", "InitializeStackResourceVersions", 1),
        ("PCL.PageDownloadModify", "LoadInstanceFinished", 1),
    ];

    /// <summary>Dev CLI: transplant between files.</summary>
    public static void Run(string officialPath, string refPath, string outPath)
    {
        var official = ModuleDefMD.Load(officialPath);
        var reference = ModuleDefMD.Load(refPath);
        var patched = ApplyTo(official, reference);
        Console.WriteLine($"total patched: {patched.Count}/{Targets.Length}");
        WriteModule(official, outPath);
        Console.WriteLine($"written: {outPath}");
    }

    /// <summary>Transplant every target method. Throws on the first unresolved token or missing method.</summary>
    public static List<string> ApplyTo(ModuleDef official, ModuleDef reference)
    {
        var importer = new Importer(official, ImporterOptions.TryToUseDefs);
        var patched = new List<string>();
        foreach (var (typeName, methodName, paramCount) in Targets)
        {
            var officialMethod = FindMethod(official, typeName, methodName, paramCount)
                ?? throw new Exception($"官方程序中找不到目标方法：{typeName}::{methodName}（{paramCount} 参数）—— 版本不匹配或文件被修改");
            var refMethod = FindMethod(reference, typeName, methodName, paramCount)
                ?? throw new Exception($"参考程序中找不到目标方法：{typeName}::{methodName}（{paramCount} 参数）—— 参考文件版本错误");
            TransplantBody(officialMethod, refMethod, importer);
            patched.Add($"{typeName}::{methodName}({paramCount})");
        }
        return patched;
    }

    public static void WriteModule(ModuleDef module, string outPath)
    {
        var opts = new ModuleWriterOptions(module);
        opts.Logger = DummyLogger.NoThrowInstance;
        module.Write(outPath, opts);
    }

    /// <summary>Detects an already-patched launcher: transplanted LoaderInit references DlClientListLoader.</summary>
    public static bool IsAlreadyPatched(ModuleDef module)
    {
        var m = FindMethod(module, "PCL.PageDownloadInstall", "LoaderInit", 0);
        if (m?.Body == null) return false;
        foreach (var ins in m.Body.Instructions)
            if (ins.Operand is IField f && f.Name == "DlClientListLoader")
                return true;
        return false;
    }

    /// <summary>Structural pre-check: every target must exist in both modules. Returns error text or null.</summary>
    public static string CheckTargetsPresent(ModuleDef official, ModuleDef reference)
    {
        foreach (var (typeName, methodName, paramCount) in Targets)
        {
            if (FindMethod(official, typeName, methodName, paramCount) == null)
                return $"官方程序缺少目标方法 {typeName}::{methodName}（{paramCount} 参数），可能不是被限制的官方版本";
            if (FindMethod(reference, typeName, methodName, paramCount) == null)
                return $"参考程序缺少目标方法 {typeName}::{methodName}（{paramCount} 参数）";
        }
        return null;
    }

    public static MethodDef FindMethod(ModuleDef mod, string typeName, string methodName, int paramCount)
    {
        foreach (var t in mod.GetTypes())
        {
            if (t.FullName != typeName) continue;
            foreach (var m in t.Methods)
            {
                if (m.Name != methodName) continue;
                if (m.MethodSig.GetParamCount() != paramCount) continue;
                return m;
            }
        }
        return null;
    }

    static void TransplantBody(MethodDef target, MethodDef source, Importer importer)
    {
        if (!source.HasBody) throw new Exception($"source has no body: {source.FullName}");
        var srcBody = source.Body;

        // locals (cloned first: ldloca/ldloc operands reference them)
        var bodyLocals = new List<Local>(srcBody.Variables.Count);
        var localMap = new Dictionary<Local, Local>();
        foreach (var local in srcBody.Variables)
        {
            var nl = new Local(importer.Import(local.Type));
            bodyLocals.Add(nl);
            localMap[local] = nl;
        }

        // instruction 1:1 clone list
        var newInstrs = new List<Instruction>(srcBody.Instructions.Count);
        var map = new Dictionary<Instruction, Instruction>();
        foreach (var ins in srcBody.Instructions)
        {
            var clone = new Instruction(ins.OpCode);
            newInstrs.Add(clone);
            map[ins] = clone;
        }

        // operand re-import
        for (int i = 0; i < srcBody.Instructions.Count; i++)
        {
            var src = srcBody.Instructions[i];
            var dst = newInstrs[i];
            switch (src.Operand)
            {
                case null:
                    dst.Operand = null;
                    break;
                case Instruction branchTarget:
                    dst.Operand = map[branchTarget]; // single-target branch
                    break;
                case Instruction[] switchTargets:
                    dst.Operand = switchTargets.Select(t => map[t]).ToArray();
                    break;
                case string s:
                    dst.Operand = s;
                    break;
                case sbyte b:
                    dst.Operand = b;
                    break;
                case byte ub:
                    dst.Operand = ub;
                    break;
                case int int32:
                    dst.Operand = int32;
                    break;
                case long int64:
                    dst.Operand = int64;
                    break;
                case float f:
                    dst.Operand = f;
                    break;
                case double d:
                    dst.Operand = d;
                    break;
                case IMethod m:
                    dst.Operand = importer.Import(m);
                    break;
                case IField f:
                    dst.Operand = importer.Import(f);
                    break;
                case ITypeDefOrRef t:
                    dst.Operand = importer.Import(t);
                    break;
                case Local local:
                    dst.Operand = localMap[local];
                    break;
                default:
                    throw new Exception($"无法处理的 IL 操作数类型 {src.Operand?.GetType().Name}（IL_{src.Offset:X4} @ {source.FullName}）");
            }
        }

        // exception handlers
        var newEhs = new List<ExceptionHandler>(srcBody.ExceptionHandlers.Count);
        foreach (var eh in srcBody.ExceptionHandlers)
        {
            newEhs.Add(new ExceptionHandler(eh.HandlerType)
            {
                TryStart = map[eh.TryStart],
                TryEnd = eh.TryEnd is null ? null : map[eh.TryEnd],
                HandlerStart = eh.HandlerStart is null ? null : map[eh.HandlerStart],
                HandlerEnd = eh.HandlerEnd is null ? null : map[eh.HandlerEnd],
                FilterStart = eh.FilterStart is null ? null : map[eh.FilterStart],
                CatchType = eh.CatchType is null ? null : importer.Import(eh.CatchType),
            });
        }

        var body = new CilBody(srcBody.InitLocals, newInstrs, newEhs, bodyLocals);
        body.MaxStack = Math.Max(srcBody.MaxStack, (ushort)8);
        body.UpdateInstructionOffsets();

        target.Body = body;
        // signature & calling convention stay untouched — both builds come from identical source.
    }
}
