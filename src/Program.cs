using System;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

// 模式：
//   （无参数）/ --auto / <启动器路径>   一键补丁流程（双击使用，见 AutoPatch.cs）
//   transplant <官> <参考> <输出>   开发者：移植并写出
//   verify <补丁EXE> <参考EXE>      开发者：逐指令验证
//   dumpil / dumpclosures / types   开发者：IL 检查
var devModes = new[] { "transplant", "verify", "dumpil", "dumpclosures", "types", "embedref" };
var mode = args.Length > 0 ? args[0] : "";
if (!devModes.Contains(mode))
{
    Environment.ExitCode = AutoPatch.Run(args);
}
else if (mode == "embedref")
{
    EmbedRef.Run(args[1], args[2]);
}
else if (mode == "transplant")
{
    Transplant.Run(args[1], args[2], args[3]);
}
else if (mode == "dumpil")
{
    var mod = ModuleDefMD.Load(args[1]);
    foreach (var t in mod.GetTypes())
    {
        if (!t.FullName.Contains(args[2])) continue;
        foreach (var m in t.Methods)
        {
            if (!m.Name.String.Contains(args[3])) continue;
            Console.WriteLine($"=== {t.FullName}::{m.Name} ===");
            DumpIL(m);
        }
    }
}
else if (mode == "dumpclosures")
{
    var mod = ModuleDefMD.Load(args[1]);
    foreach (var t in mod.GetTypes())
    {
        if (!t.FullName.Contains(args[2])) continue;
        foreach (var n in t.NestedTypes)
        {
            if (!n.Name.String.Contains("Closure") && !n.Name.String.Contains("Delegate")) continue;
            Console.WriteLine($"--- {n.FullName} ---");
            foreach (var f in n.Fields) Console.WriteLine($"    field {f.FieldType.TypeName} {f.Name}");
            foreach (var m in n.Methods)
            {
                if (!m.HasBody || m.Body.Instructions.Count == 0) continue;
                if (m.Name == ".ctor" || m.Name == ".cctor") continue;
                var sb = new StringBuilder();
                foreach (var i in m.Body.Instructions.Take(40))
                    sb.Append(i.OpCode).Append(' ').Append(OperandStr(i.Operand)).Append("; ");
                Console.WriteLine($"    [{m.Name}] {sb}");
            }
        }
    }
}
else if (mode == "verify")
{
    Verify.Run(args[1], args[2]);
}
else if (mode == "types")
{
    var mod = ModuleDefMD.Load(args[1]);
    foreach (var t in mod.GetTypes())
    {
        if (t.FullName.IndexOf(args[2], StringComparison.OrdinalIgnoreCase) < 0) continue;
        Console.WriteLine(t.FullName + (t.IsNested ? "  [nested in " + t.DeclaringType.FullName + "]" : ""));
    }
}
else
{
    Console.WriteLine("unknown mode");
}

static string OperandStr(object op)
{
    if (op == null) return "";
    if (op is IMethod m) return ResolveName(m);
    if (op is IField f) return ResolveName(f) + " : " + f.FieldSig.Type.TypeName;
    if (op is ITypeDefOrRef t) return t.FullName;
    if (op is Instruction i) return "IL_" + i.Offset.ToString("X4");
    if (op is Instruction[] arr) return "IL_" + string.Join("/IL_", arr.Select(x => x.Offset.ToString("X4")));
    return op.ToString() ?? "";
}

static string ResolveName(IMemberRef mr)
{
    if (mr is MemberRef mdr)
    {
        var par = mdr.Class;
        string pn = par switch
        {
            TypeDef td => td.FullName,
            TypeRef tr => tr.FullName,
            _ => par?.ToString() ?? "?"
        };
        return pn + "::" + mdr.Name;
    }
    if (mr is MethodDef md) return md.DeclaringType.FullName + "::" + md.Name;
    if (mr is FieldDef fd) return fd.DeclaringType.FullName + "::" + fd.Name;
    return mr.ToString() ?? "?";
}

static void DumpIL(MethodDef m)
{
    if (m.Body == null) { Console.WriteLine("  <no body>"); return; }
    foreach (var ins in m.Body.Instructions)
    {
        string extra = "";
        if (ins.Operand is IMethod or IField or ITypeDefOrRef) extra = "   // " + OperandStr(ins.Operand);
        Console.WriteLine($"  IL_{ins.Offset:X4}: {ins.OpCode,-12} {OperandStr(ins.Operand)}{extra}");
    }
    if (m.Body.ExceptionHandlers.Count > 0)
    {
        Console.WriteLine("  EH:");
        foreach (var eh in m.Body.ExceptionHandlers)
            Console.WriteLine($"    {eh.HandlerType} try IL_{eh.TryStart.Offset:X4}-IL_{eh.TryEnd?.Offset:X4} handler IL_{eh.HandlerStart?.Offset:X4}-IL_{eh.HandlerEnd?.Offset:X4} ({eh.CatchType?.FullName})");
    }
}
