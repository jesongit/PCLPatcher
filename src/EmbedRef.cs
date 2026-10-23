using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;

// embedref 模式：把参考 EXE 作为托管资源内嵌进补丁工具本体，
// 使补丁工具成为单文件。用法：PCLPatcher embedref <工具exe> <参考exe>

static class EmbedRef
{
    public const string ResourceName = "PCLPatchReference";

    public static void Run(string patcherExe, string referenceExe)
    {
        if (!File.Exists(patcherExe)) throw new FileNotFoundException("工具文件不存在", patcherExe);
        if (!File.Exists(referenceExe)) throw new FileNotFoundException("参考文件不存在", referenceExe);

        var mod = ModuleDefMD.Load(patcherExe);
        var existing = mod.Resources.OfType<EmbeddedResource>().FirstOrDefault(r => r.Name == ResourceName);
        if (existing != null) mod.Resources.Remove(existing);
        mod.Resources.Add(new EmbeddedResource(ResourceName, File.ReadAllBytes(referenceExe)));

        string tmp = patcherExe + ".embedding.tmp";
        Transplant.WriteModule(mod, tmp);
        File.Delete(patcherExe);
        File.Move(tmp, patcherExe);
        Console.WriteLine($"embedded {ResourceName} ({new FileInfo(referenceExe).Length} bytes) into {patcherExe}");
    }

    /// <summary>运行时读取内嵌的参考程序；没有则返回 null。</summary>
    public static byte[] TryReadEmbedded()
    {
        using (var s = typeof(EmbedRef).Assembly.GetManifestResourceStream(ResourceName))
        {
            if (s == null) return null;
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}
