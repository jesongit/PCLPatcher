using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using dnlib.DotNet;

// 一键补丁流程（双击 PCLPatcher.exe 触发，无控制台窗口）：
//   1. 环境检测：参考程序 / 启动器存在性、版本号、是否已打过补丁、启动器是否运行、文件是否被占用
//      —— 任何一项不通过：弹窗提示原因，原样退出，不做任何改动
//   2. 检测通过：在内存中完成移植、写入临时文件、逐指令验证
//      —— 失败：删除临时文件，弹窗报错，不做任何改动
//   3. 弹二次确认窗口，说明即将执行的操作；确认后仅做两个文件移动（官方版改名备份、临时文件改为原名）
//      —— 移动失败自动回滚
// 运行过程全程记录在内存转录中；仅失败时落盘「PCLPatcher-错误日志.txt」，成功不产生任何额外文件。
static class AutoPatch
{
    const string LauncherFileName = "Plain Craft Launcher 2.exe";
    const string BackupFileName = "官方原版备份 - Plain Craft Launcher 2.exe";
    const string ReferenceFileName = "PCLPatcher-Reference.exe";
    const string TempFileName = ".PCLPatch-temp.exe";
    const string ErrorLogFileName = "PCLPatcher-错误日志.txt";
    const string ExpectedVersion = "2.99.0.0";
    static bool AutoMode;
    static readonly List<string> Transcript = new List<string>();

    public static int Run(string[] args)
    {
        AutoMode = args.Contains("--auto");
        args = args.Where(a => a != "--auto").ToArray();
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string launcherPath = Path.Combine(baseDir, LauncherFileName);
        string referencePath = Path.Combine(baseDir, ReferenceFileName);
        string backupPath = Path.Combine(baseDir, BackupFileName);
        string tempPath = Path.Combine(baseDir, TempFileName);

        if (args.Length >= 1) launcherPath = Path.GetFullPath(args[0]);
        if (args.Length >= 2) referencePath = Path.GetFullPath(args[1]);

        Log("==============================================");
        Log("  PCL 下载功能补丁工具 v1.0");
        Log("  目标版本：官方 PCL " + ExpectedVersion + " (413)");
        Log("==============================================");

        try
        {
            // ---------- 环境检测 ----------
            Log("[检测 1/6] 参考程序");
            byte[] referenceBytes = EmbedRef.TryReadEmbedded();
            string referenceSource;
            if (referenceBytes != null)
            {
                referenceSource = "工具内嵌资源";
                if (File.Exists(referencePath))
                    Log("  说明：检测到外部参考文件，已优先使用内嵌版本。");
            }
            else if (File.Exists(referencePath))
            {
                referenceSource = referencePath;
                referenceBytes = File.ReadAllBytes(referencePath);
            }
            else
                return Fail($"本工具未内嵌参考程序，且同目录也找不到「{ReferenceFileName}」。\n\n请重新获取完整版工具。");
            Log("  通过：" + referenceSource);

            Log("[检测 2/6] 启动器文件");
            if (!File.Exists(launcherPath))
                return Fail($"在以下位置找不到官方启动器「{LauncherFileName}」：\n{Path.GetDirectoryName(launcherPath)}\n\n请把本工具放在启动器旁边后再运行。\n若你已打过补丁，则无需再次运行。");
            Log("  通过：" + launcherPath);

            Log("[检测 3/6] 是否已打过补丁");
            byte[] launcherBytes = File.ReadAllBytes(launcherPath);
            ModuleDef launcherMod, referenceMod;
            try { launcherMod = ModuleDefMD.Load(launcherBytes); }
            catch { return Fail("启动器文件无法解析，可能已损坏或不是 PCL 程序。"); }
            try { referenceMod = ModuleDefMD.Load(referenceBytes); }
            catch { return Fail("参考程序无法解析，可能已损坏，请重新获取。"); }

            if (Transplant.IsAlreadyPatched(launcherMod))
                return Info("检测到该启动器已经打过补丁（下载功能已是恢复状态），无需重复操作。");
            Log("  通过：尚未打过补丁");

            Log("[检测 4/6] 版本号");
            string launcherVer = AsmVersion(launcherMod);
            string referenceVer = AsmVersion(referenceMod);
            if (launcherVer != ExpectedVersion)
                return Fail($"启动器版本为 {launcherVer}，本工具只支持 {ExpectedVersion}。\n\n官方发布新版本后，请用新版源码重新制作参考程序再打补丁（见 README）。");
            if (referenceVer != ExpectedVersion)
                return Fail($"参考程序版本为 {referenceVer}，与预期的 {ExpectedVersion} 不符，请重新获取完整版工具。");
            Log($"  通过：启动器 {launcherVer}，参考 {referenceVer}");

            Log("[检测 5/6] 启动器是否正在运行");
            var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LauncherFileName));
            if (running.Length == 0)
                running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(launcherPath));
            if (running.Length > 0)
                return Fail($"检测到启动器正在运行（PID：{string.Join("、", running.Select(p => p.Id.ToString()))}）。\n\n请先完全退出 PCL 再运行本工具。");
            Log("  通过：未在运行");

            Log("[检测 6/6] 文件占用");
            if (!IsWritable(launcherPath, out string lockReason))
                return Fail($"启动器文件当前被占用，无法修改：\n{lockReason}\n\n请关闭可能使用它的程序（或等待杀毒软件扫描结束）后重试。");
            Log("  通过：文件可写");

            Log("[结构检查] 目标方法存在性");
            string structureError = Transplant.CheckTargetsPresent(launcherMod, referenceMod);
            if (structureError != null)
                return Fail(structureError + $"\n\n当前启动器版本号虽为 {ExpectedVersion}，但内部结构与预期不符，已中止。");
            Log("  通过：5 个目标方法全部存在");

            // ---------- 预打补丁（内存 + 临时文件，不动原文件） ----------
            Log("[生成] 正在移植 5 个方法体……");
            var patchedNames = Transplant.ApplyTo(launcherMod, referenceMod);
            foreach (var n in patchedNames) Log("  [完成] " + n);
            Log("[生成] 写入临时文件……");
            if (File.Exists(tempPath)) File.Delete(tempPath);
            Transplant.WriteModule(launcherMod, tempPath);

            Log("[验证] 与参考程序逐指令比对……");
            ModuleDef patchedMod = ModuleDefMD.Load(tempPath);
            bool ok = Verify.Check(patchedMod, referenceMod, Log);
            if (!ok)
            {
                TryDelete(tempPath);
                return Fail("补丁结果与参考程序不一致，已中止。原文件未做任何改动。");
            }

            // ---------- 二次确认 ----------
            string confirmMsg =
                "环境检测与补丁校验全部通过，可以开始。\n\n" +
                "本工具将执行以下操作：\n" +
                "  1. 将官方启动器改名备份为「" + BackupFileName + "」\n" +
                "  2. 将打好补丁的启动器放回原文件名（下载、安装、整合包等功能恢复）\n" +
                "  3. 如中途出错会自动回滚，不会损坏任何文件\n\n" +
                "是否继续？";
            if (!AutoMode && MessageBox.Show(confirmMsg, "PCL 补丁工具 — 二次确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1) != DialogResult.Yes)
            {
                TryDelete(tempPath);
                Log("用户取消，临时文件已清理。");
                return 2;
            }

            // ---------- 替换（仅文件移动，失败自动回滚） ----------
            Log("[替换] 备份官方启动器……");
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(launcherPath, backupPath);
            try
            {
                File.Move(tempPath, launcherPath);
            }
            catch
            {
                // 回滚：把备份改回原名
                if (!File.Exists(launcherPath) && File.Exists(backupPath))
                    File.Move(backupPath, launcherPath);
                throw;
            }
            TryDelete(tempPath);

            Log("补丁成功。已备份官方版：" + BackupFileName + "，补丁版已就位：" + LauncherFileName);
            if (!AutoMode)
                MessageBox.Show(
                    "补丁成功！下载相关功能已恢复。\n\n" +
                    "已生成：\n" +
                    "  • " + LauncherFileName + "（补丁版，原文件名）\n" +
                    "  • " + BackupFileName + "（官方原版备份）\n\n" +
                    "想还原时，把备份文件改回原名即可。\n" +
                    "注意：以后若让 PCL 自动更新，更新会覆盖补丁，需要重新打补丁。",
                    "PCL 补丁工具", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            Log("发生异常：" + ex);
            return Fail("补丁过程中出现错误（原文件未受影响或已回滚）：\n\n" + ex.Message);
        }
    }

    static void Log(string line)
    {
        Transcript.Add(line);
    }

    static int Fail(string message)
    {
        Log("[未通过] " + message.Replace("\n\n", "\n"));
        string logNote = "";
        try
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ErrorLogFileName);
            File.WriteAllLines(logPath, Transcript, Encoding.UTF8);
            logNote = "\n\n详细信息已写入：「" + ErrorLogFileName + "」";
        }
        catch { }
        if (!AutoMode)
            MessageBox.Show(message + logNote, "PCL 补丁工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
    }

    static int Info(string message)
    {
        Log("[提示] " + message);
        if (!AutoMode)
            MessageBox.Show(message, "PCL 补丁工具", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static string AsmVersion(ModuleDef mod)
    {
        try { return (mod.Assembly as AssemblyDef)?.Version.ToString() ?? "(无版本信息)"; }
        catch { return "(无法读取版本)"; }
    }

    static bool IsWritable(string path, out string reason)
    {
        reason = null;
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                return true;
        }
        catch (IOException ex) { reason = ex.Message; return false; }
        catch (UnauthorizedAccessException ex) { reason = ex.Message + "（也可能是权限不足）"; return false; }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
