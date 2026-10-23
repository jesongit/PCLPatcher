using System;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

static class Verify
{
    /// <summary>Compares every transplanted method's IL against the reference. Returns all-OK and logs details.</summary>
    public static bool Check(ModuleDef patched, ModuleDef reference, Action<string> log)
    {
        int same = 0, diff = 0;
        foreach (var (typeName, methodName, paramCount) in Transplant.Targets)
        {
            var pm = Transplant.FindMethod(patched, typeName, methodName, paramCount);
            var rm = Transplant.FindMethod(reference, typeName, methodName, paramCount);
            if (pm == null || rm == null)
            {
                log($"[失败] {typeName}::{methodName} 未找到");
                diff++;
                continue;
            }
            var a = Dump(pm);
            var b = Dump(rm);
            if (a == b)
            {
                log($"[通过] {typeName}::{methodName}（{paramCount} 参数）— 与参考程序逐指令一致（{pm.Body.Instructions.Count} 条指令）");
                same++;
            }
            else
            {
                log($"[差异] {typeName}::{methodName}（{paramCount} 参数）");
                var al = a.Split('\n');
                var bl = b.Split('\n');
                for (int i = 0; i < Math.Max(al.Length, bl.Length); i++)
                {
                    var x = i < al.Length ? al[i] : "<eof>";
                    var y = i < bl.Length ? bl[i] : "<eof>";
                    if (x != y) log($"  补丁后: {x}\n  参考:   {y}");
                }
                diff++;
            }
        }
        log($"验证结果：{same} 个一致，{diff} 个不一致");
        return diff == 0;
    }

    /// <summary>Dev CLI.</summary>
    public static void Run(string patchedPath, string refPath)
    {
        var patched = dnlib.DotNet.ModuleDefMD.Load(patchedPath);
        var reference = dnlib.DotNet.ModuleDefMD.Load(refPath);
        Check(patched, reference, Console.WriteLine);
    }

    static string Dump(MethodDef m)
    {
        var sb = new StringBuilder();
        foreach (var ins in m.Body.Instructions)
        {
            string op = ins.Operand switch
            {
                null => "",
                Instruction i => "IL_" + i.Offset.ToString("X4"),
                Instruction[] arr => string.Join("/", arr.Select(x => "IL_" + x.Offset.ToString("X4"))),
                IMethod im => Describe(im),
                IField f => Describe(f),
                ITypeDefOrRef t => t.FullName,
                Local l => "V_" + l.Index,
                _ => ins.Operand.ToString() ?? "",
            };
            sb.Append(ins.OpCode.Code).Append(' ').Append(op).Append('\n');
        }
        foreach (var eh in m.Body.ExceptionHandlers.OrderBy(e => e.TryStart?.Offset ?? 0))
            sb.Append("EH ").Append(eh.HandlerType).Append(' ')
              .Append(eh.TryStart?.Offset).Append('-').Append(eh.TryEnd?.Offset).Append(' ')
              .Append(eh.HandlerStart?.Offset).Append('-').Append(eh.HandlerEnd?.Offset).Append(' ')
              .Append(eh.CatchType?.FullName).Append('\n');
        return sb.ToString();
    }

    static string Describe(IMemberRef mr)
    {
        string decl = mr.DeclaringType?.FullName ?? "?";
        if (mr is MethodDef md) decl = md.DeclaringType.FullName;
        if (mr is FieldDef fd) decl = fd.DeclaringType.FullName;
        string sig = "";
        if (mr is IMethod im2 && im2.MethodSig != null)
        {
            // 泛型参数统一按序号规范化：Importer 重新生成签名时是 !!0/!!1，
            // 原生 MethodDef 签名则带 TIn/T 之类的名字，二者功能等价
            sig = "(" + string.Join(",", im2.MethodSig.Params.Select(Norm)) + ")";
        }
        return decl + "::" + mr.Name + sig;
    }

    static string Norm(TypeSig t)
    {
        switch (t)
        {
            case null: return "?";
            case GenericVar v: return "!!" + v.Number;
            case GenericMVar v: return "!!" + v.Number;
            case GenericInstSig g:
                return Norm(g.GenericType) + "<" + string.Join(",", g.GenericArguments.Select(Norm)) + ">";
            case SZArraySig a: return Norm(a.Next) + "[]";
            case ByRefSig b: return Norm(b.Next) + "&";
            case PtrSig p: return Norm(p.Next) + "*";
            default: return t.FullName ?? t.TypeName;
        }
    }
}
