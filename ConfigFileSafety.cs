using System;
using System.IO;

namespace LatihasChocobo;

// 本檔契約（艦隊範本 config-load-safety）：讀壞的原檔在被覆寫之前先複製成 <檔名>.bad-<時間>；
// 既有備份永不覆寫（CreateNew）；同內容的備份只留一份；永不擲例外。
internal static class ConfigFileSafety
{
    // 回傳這份內容的備份檔名（含「早已備份過」的那一份）；失敗回 null。
    public static string? BackupUnreadable(string path, string reason, string consequence, Action<string> log, Func<DateTime>? now = null)
    {
        var name = Path.GetFileName(path);
        try
        {
            var original = File.ReadAllBytes(path);
            var dir      = Path.GetDirectoryName(Path.GetFullPath(path))!;
            foreach (var old in new DirectoryInfo(dir).EnumerateFiles(name + ".bad-*"))
            {
                if (old.Length == original.Length && File.ReadAllBytes(old.FullName).AsSpan().SequenceEqual(original))
                {
                    log($"[設定] {name}：{reason}；相同內容已備份過（{old.FullName}），這次不再另存。{consequence}");
                    return old.Name;
                }
            }

            var backup = Path.Combine(dir, $"{name}.bad-{(now ?? (() => DateTime.Now))():yyyyMMdd-HHmmss-fff}");
            using (var fs = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.Write(original);
            log($"[設定] {name}：{reason}；原檔已備份為 {backup}。{consequence}");
            return Path.GetFileName(backup);
        }
        catch (Exception e)
        {
            log($"[設定] {name}：{reason}；備份原檔失敗（{e.GetType().Name}: {e.Message}）。{consequence}");
            return null;
        }
    }
}
