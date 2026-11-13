using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using AutoDuty.Windows;
using ECommons.DalamudServices;

namespace AutoDuty.Helpers;

/// <summary>
/// 路徑步驟以 <c>ChatCommand "/autoduty config 名稱 值"</c> 改使用者設定（指令本身會存檔）：
/// 執行前記下該欄位的原值，停止時寫回，讓中途停止不會把路徑的暫時值留在設定檔裡。
/// </summary>
/// <remarks>
/// 契約：只在框架執行緒呼叫；同一欄位一次開跑只記第一次的原值；<see cref="RestoreInMemory"/> 讀完即清（冪等），
/// 只改記憶體、由呼叫端存檔。正被 IPC 覆寫的欄位不記：那段期間的寫入由 <see cref="ConfigOverrideHelper.Pop"/> 還原、本來就不落地。
/// </remarks>
internal static class PathConfigJournal
{
    private readonly record struct Key(FieldInfo Field, Configuration Target);

    private static readonly Dictionary<Key, object?> Originals = [];

    private static readonly Regex ConfigCommand =
        new(@"^\s*/(?:autoduty|ad)\s+(?:config|cfg)\s+(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>從聊天指令取出要改的設定名；不是 <c>/autoduty config 名稱 …</c>（或 <c>list</c>）回 null。</summary>
    internal static string? ParseConfigName(string command)
    {
        Match m = ConfigCommand.Match(command ?? string.Empty);
        if (!m.Success)
            return null;
        string name = m.Groups[1].Value.Trim('"');
        return name.Length == 0 || name.Equals("list", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    internal static void RecordBeforeCommand(string command)
    {
        string? name = ParseConfigName(command);
        if (name is null)
            return;

        FieldInfo? field = ConfigHelper.FindConfig(name);
        if (field is null || field.FieldType.ToString().Contains("Dalamud.Plugin", StringComparison.InvariantCultureIgnoreCase))
            return;
        if (field.FieldType.IsAssignableTo(typeof(IList)))
        {
            Svc.Log.Information($"[路徑改設定] {name} 是清單型設定，停止時不還原");
            return;
        }

        Configuration target = Plugin.Configuration;
        if (ConfigOverrideHelper.IsOverridden(field, target))
            return;

        Key key = new(field, target);
        if (Originals.ContainsKey(key))
            return;
        object? original = field.GetValue(target);
        Originals[key] = original;
        Svc.Log.Information($"[路徑改設定] 路徑要改 {name}（原值 {original}），停止時還原");
    }

    /// <summary>把本次記下的欄位寫回原值（只改記憶體）；回傳實際改回的欄位數，&gt;0 時呼叫端要存檔。</summary>
    internal static int RestoreInMemory()
    {
        if (Originals.Count == 0)
            return 0;

        int restored = 0;
        List<string> changed = [];
        try
        {
            foreach ((Key key, object? original) in Originals)
            {
                try
                {
                    if (Equals(key.Field.GetValue(key.Target), original))
                        continue;
                    key.Field.SetValue(key.Target, original);
                    restored++;
                    changed.Add($"{FieldDisplayName(key.Field)}={original}");
                }
                catch (Exception ex)
                {
                    Svc.Log.Error($"[路徑改設定] 還原 {FieldDisplayName(key.Field)} 失敗：{ex.Message}");
                }
            }
        }
        finally
        {
            Originals.Clear();
        }

        if (restored > 0)
            Svc.Log.Information($"[路徑改設定] 停止時還原 {restored} 項：{string.Join("、", changed)}");
        return restored;
    }

    private static string FieldDisplayName(FieldInfo field) =>
        field.Name.Replace(">k__BackingField", "").Replace("<", "");
}
