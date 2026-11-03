using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LobotomyModernEngine.Core;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// SoundEffectPlayer / CreatureUnit.PlaySound 音声ロード計測・キャッシュ化パッチ (v0.8.4)
    /// 
    /// 【コード・IL検証結果 (Assembly-CSharp.dll SoundEffectPlayer / CreatureUnit)】
    /// - バニラの SoundEffectPlayer (PlayOnce 4種 / Play 2種 / PlaySequence 1種) は、効果音再生のたびに毎回
    ///   String.Concat("Sounds/", filename) + Resources.Load&lt;AudioClip&gt;(string) を同期実行していた。
    /// - さらに CreatureUnit.PlaySound / PlaySoundLoop は、幻想体の soundTable に "enter" 等のキーが存在しない場合や
    ///   対応する AudioClip が存在しない場合に、入室・行動のたび毎回 Debug.Log("Error in sound founding " + soundKey) を出力し、
    ///   文字列結合アロケーションと Unity/BepInEx の同期ファイルI/Oを発生させていた。
    /// 
    /// 【実装仕様】
    /// 1. 実際の呼出頻度と所要時間の個別計測:
    ///    - 初回 (Cold) の Resources.Load&lt;AudioClip&gt; 実行回数・累積ミリ秒・最大ミリ秒を厳密に計測し、
    ///      1.0ms 以上要したロードはログへ個別出力する。
    ///    - SoundEffect 全体の所要時間を GameProfiler.SubSoundLoad (3g) としてリアルタイム集計する。
    /// 2. ポジティブキャッシュと AudioClip の寿命管理:
    ///    - ロード成功した AudioClip を Dictionary&lt;string, AudioClip&gt; にキャッシュする。
    ///    - 取得時は必ず Unity ネイティブ寿命判定 (cachedClip != null) を行い、万一 Resources.UnloadUnusedAssets 等で
    ///      アンロードされていた場合は安全に再取得する。
    ///    - AudioClip は共有アセットであるため Object.Destroy は絶対に呼ばず、シーン遷移時に無効参照の掃除を行う。
    /// 3. ネガティブキャッシュ（存在しない音声キーの再検索・エラーログ出力削減）:
    ///    - Resources.Load&lt;AudioClip&gt; が null を返した音声パスを HashSet&lt;string&gt; に記録し、以後の Resources 再検索を遮断する。
    ///    - CreatureUnit.PlaySound / PlaySoundLoop で soundTable に存在しないキーの Debug.Log スパムを抑止する。
    /// </summary>
    public static class SoundEffectCachePatch
    {
        private static readonly Dictionary<string, AudioClip> AudioClipCache = new Dictionary<string, AudioClip>(256, StringComparer.Ordinal);
        private static readonly HashSet<string> MissingAudioFiles = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> _pruneBuffer = new List<string>(32);

        // 計測・比較用メトリクス
        public static long TotalSoundRequestCount = 0;
        public static long PositiveCacheHits = 0;
        public static long NegativeCacheHits = 0;
        public static long MissingSoundTableKeyHits = 0;
        public static long ColdResourcesLoadCount = 0;
        public static double ColdResourcesLoadTotalMs = 0.0;
        public static double ColdResourcesLoadMaxMs = 0.0;
        public static double LastSoundCallMs = 0.0;
        public static double LastColdLoadMs = 0.0;
        public static int TranspiledMethodCount = 0;
        public static long PrewarmedClipCount = 0;

        public static int CachedClipCount => AudioClipCache.Count;
        public static int CachedMissingPathCount => MissingAudioFiles.Count;

        /// <summary>
        /// v0.8.6: 既存キャッシュへの直接登録（プリウォーム用）
        /// </summary>
        public static bool RegisterPrewarmedClip(string cacheKey, AudioClip clip)
        {
            if (string.IsNullOrEmpty(cacheKey)) return false;
            if (clip != null)
            {
                AudioClipCache[cacheKey] = clip;
                PrewarmedClipCount++;
                return true;
            }
            else
            {
                MissingAudioFiles.Add(cacheKey);
                return false;
            }
        }

        public static bool IsCached(string cacheKey)
        {
            if (string.IsNullOrEmpty(cacheKey)) return false;
            return MissingAudioFiles.Contains(cacheKey) || AudioClipCache.ContainsKey(cacheKey);
        }

        /// <summary>
        /// SoundEffectPlayer 内の String.Concat("Sounds/", filename) + Resources.Load&lt;AudioClip&gt; を置換するエントリ
        /// </summary>
        public static AudioClip LoadSoundEffectClip(string prefix, string filename)
        {
            long startTicks = Stopwatch.GetTimestamp();
            TotalSoundRequestCount++;

            if (string.IsNullOrEmpty(filename))
            {
                LastSoundCallMs = GameProfiler.RecordSubMetric(GameProfiler.SubSoundLoad, startTicks);
                return null;
            }

            // 標準の "Sounds/" プレフィックス時は filename 単体をキーとして文字列結合自体も回避
            string cacheKey = (prefix == "Sounds/") ? filename : (prefix + filename);

            // 1. ネガティブキャッシュ判定（存在しない音声アセットの Resources 再検索を回避）
            if (MissingAudioFiles.Contains(cacheKey))
            {
                NegativeCacheHits++;
                LastSoundCallMs = GameProfiler.RecordSubMetric(GameProfiler.SubSoundLoad, startTicks);
                return null;
            }

            // 2. ポジティブキャッシュ判定 + Unity ネイティブオブジェクト寿命検証 (clip != null)
            AudioClip cachedClip;
            if (AudioClipCache.TryGetValue(cacheKey, out cachedClip))
            {
                if (cachedClip != null)
                {
                    PositiveCacheHits++;
                    LastSoundCallMs = GameProfiler.RecordSubMetric(GameProfiler.SubSoundLoad, startTicks);
                    return cachedClip;
                }
                // アンロード済みの場合は辞書から除去して再ロードへ進む
                AudioClipCache.Remove(cacheKey);
            }

            // 3. Cold Load（実際の Resources.Load<AudioClip> 実行と所要時間計測）
            string fullResourcePath = (prefix == "Sounds/") ? ("Sounds/" + filename) : cacheKey;
            long loadStart = Stopwatch.GetTimestamp();
            AudioClip loadedClip = Resources.Load<AudioClip>(fullResourcePath);
            double loadMs = (Stopwatch.GetTimestamp() - loadStart) * 1000.0 / Stopwatch.Frequency;

            ColdResourcesLoadCount++;
            ColdResourcesLoadTotalMs += loadMs;
            LastColdLoadMs = loadMs;
            if (loadMs > ColdResourcesLoadMaxMs)
            {
                ColdResourcesLoadMaxMs = loadMs;
            }

            if (loadedClip != null)
            {
                AudioClipCache[cacheKey] = loadedClip;
            }
            else
            {
                MissingAudioFiles.Add(cacheKey);
            }

            if (loadMs >= 1.0 && Plugin.Logger != null && (Plugin.EnableProfilerLog == null || Plugin.EnableProfilerLog.Value))
            {
                Plugin.Logger.LogInfo(string.Format(
                    "[LME 音声Resources.Load実測] Path:\"{0}\" | 所要:{1:F2} ms | 結果:{2}",
                    fullResourcePath,
                    loadMs,
                    loadedClip != null ? "成功(キャッシュ登録)" : "アセット不在(ネガティブキャッシュ登録)"));
            }

            LastSoundCallMs = GameProfiler.RecordSubMetric(GameProfiler.SubSoundLoad, startTicks);
            return loadedClip;
        }

        /// <summary>
        /// フォールバック用（単一引数の Resources.Load&lt;AudioClip&gt;(string) 置換用）
        /// </summary>
        public static AudioClip LoadAudioClipByFullPath(string fullPath)
        {
            return LoadSoundEffectClip(string.Empty, fullPath);
        }

        /// <summary>
        /// アンロードされた AudioClip 参照の安全な掃除（プレイ中5秒周期およびシーン遷移時）
        /// </summary>
        public static void PruneDeadReferences()
        {
            if (AudioClipCache.Count == 0) return;

            _pruneBuffer.Clear();
            foreach (var kvp in AudioClipCache)
            {
                if (kvp.Value == null)
                {
                    _pruneBuffer.Add(kvp.Key);
                }
            }
            for (int i = 0; i < _pruneBuffer.Count; i++)
            {
                AudioClipCache.Remove(_pruneBuffer[i]);
            }
            _pruneBuffer.Clear();
        }

        /// <summary>
        /// シーン遷移時の寿命管理（タイトル画面復帰時は参照を手放し、管理パート間は有効なクリップを維持）
        /// </summary>
        public static void OnSceneTransition(string sceneName)
        {
            if (sceneName == "MainMenu" || sceneName == "TitleEndScene")
            {
                AudioClipCache.Clear();
            }
            else
            {
                PruneDeadReferences();
            }
        }

        public static void ResetStageMetrics()
        {
            TotalSoundRequestCount = 0;
            PositiveCacheHits = 0;
            NegativeCacheHits = 0;
            MissingSoundTableKeyHits = 0;
            ColdResourcesLoadCount = 0;
            ColdResourcesLoadTotalMs = 0.0;
            ColdResourcesLoadMaxMs = 0.0;
            LastSoundCallMs = 0.0;
            LastColdLoadMs = 0.0;
        }

        // SoundEffectPlayer の全7メソッドの Resources.Load<AudioClip> をキャッシュ経由に置換
        [HarmonyPatch]
        public static class SoundEffectPlayer_ResourcesLoad_Transpiler
        {
            private static readonly MethodInfo StringConcat2Method = AccessTools.Method(
                typeof(string),
                "Concat",
                new Type[] { typeof(string), typeof(string) });

            private static readonly MethodInfo LoadSoundEffectClipMethod = AccessTools.Method(
                typeof(SoundEffectCachePatch),
                nameof(LoadSoundEffectClip));

            private static readonly MethodInfo LoadAudioClipByFullPathMethod = AccessTools.Method(
                typeof(SoundEffectCachePatch),
                nameof(LoadAudioClipByFullPath));

            [HarmonyTargetMethods]
            public static IEnumerable<MethodBase> TargetMethods()
            {
                Type t = typeof(SoundEffectPlayer);
                yield return AccessTools.Method(t, "PlaySequence", new Type[] { typeof(Vector2), typeof(string[]) });
                yield return AccessTools.Method(t, "PlayOnce", new Type[] { typeof(string), typeof(Vector2) });
                yield return AccessTools.Method(t, "PlayOnce", new Type[] { typeof(string), typeof(float), typeof(Vector2) });
                yield return AccessTools.Method(t, "PlayOnce", new Type[] { typeof(string), typeof(Vector2), typeof(float) });
                yield return AccessTools.Method(t, "PlayOnce", new Type[] { typeof(string), typeof(Vector2), typeof(AudioRolloffMode) });
                yield return AccessTools.Method(t, "Play", new Type[] { typeof(string), typeof(Transform) });
                yield return AccessTools.Method(t, "Play", new Type[] { typeof(string), typeof(Transform), typeof(float) });
            }

            private static bool IsResourcesLoadAudioClip(MethodInfo mi)
            {
                if (mi == null || mi.Name != "Load" || mi.DeclaringType != typeof(Resources))
                    return false;
                if (!mi.IsGenericMethod)
                    return false;
                Type[] genericArgs = mi.GetGenericArguments();
                if (genericArgs == null || genericArgs.Length != 1 || genericArgs[0] != typeof(AudioClip))
                    return false;
                ParameterInfo[] pars = mi.GetParameters();
                return pars != null && pars.Length == 1 && pars[0].ParameterType == typeof(string);
            }

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
                int replaced = 0;

                for (int i = 0; i < codes.Count; i++)
                {
                    var inst = codes[i];
                    if ((inst.opcode == OpCodes.Call || inst.opcode == OpCodes.Callvirt) &&
                        inst.operand is MethodInfo mi &&
                        IsResourcesLoadAudioClip(mi))
                    {
                        if (i > 0 &&
                            codes[i - 1].opcode == OpCodes.Call &&
                            Equals(codes[i - 1].operand, StringConcat2Method))
                        {
                            // String.Concat(prefix, filename) を Nop 化し、2引数を直接 LoadSoundEffectClip(prefix, filename) に渡す
                            codes[i - 1].opcode = OpCodes.Nop;
                            codes[i - 1].operand = null;
                            inst.opcode = OpCodes.Call;
                            inst.operand = LoadSoundEffectClipMethod;
                        }
                        else
                        {
                            inst.opcode = OpCodes.Call;
                            inst.operand = LoadAudioClipByFullPathMethod;
                        }
                        replaced++;
                    }
                }

                if (replaced > 0)
                {
                    TranspiledMethodCount++;
                }
                return codes;
            }
        }

        // CreatureUnit.PlaySound / PlaySoundLoop の不在キー事前判定と Debug.Log("Error in sound founding ...") スパム抑止
        [HarmonyPatch(typeof(CreatureUnit), "PlaySound", new Type[] { typeof(string) })]
        public static class CreatureUnit_PlaySound1_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(CreatureUnit __instance, string soundKey, ref SoundEffectPlayer __result)
            {
                if (__instance == null || __instance.model == null || __instance.model.metaInfo == null || __instance.model.metaInfo.soundTable == null || string.IsNullOrEmpty(soundKey))
                {
                    MissingSoundTableKeyHits++;
                    __result = null;
                    return false;
                }

                string filename;
                if (!__instance.model.metaInfo.soundTable.TryGetValue(soundKey, out filename) || string.IsNullOrEmpty(filename))
                {
                    MissingSoundTableKeyHits++;
                    LastSoundCallMs = 0.0;
                    __result = null;
                    return false;
                }

                __result = SoundEffectPlayer.PlayOnce(filename, __instance.transform.position);
                return false;
            }
        }

        [HarmonyPatch(typeof(CreatureUnit), "PlaySound", new Type[] { typeof(string), typeof(float) })]
        public static class CreatureUnit_PlaySound2_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(CreatureUnit __instance, string soundKey, float volume, ref SoundEffectPlayer __result)
            {
                if (__instance == null || __instance.model == null || __instance.model.metaInfo == null || __instance.model.metaInfo.soundTable == null || string.IsNullOrEmpty(soundKey))
                {
                    MissingSoundTableKeyHits++;
                    __result = null;
                    return false;
                }

                string filename;
                if (!__instance.model.metaInfo.soundTable.TryGetValue(soundKey, out filename) || string.IsNullOrEmpty(filename))
                {
                    MissingSoundTableKeyHits++;
                    LastSoundCallMs = 0.0;
                    __result = null;
                    return false;
                }

                __result = SoundEffectPlayer.PlayOnce(filename, __instance.transform.position, volume);
                return false;
            }
        }

        [HarmonyPatch(typeof(CreatureUnit), "PlaySound", new Type[] { typeof(string), typeof(AudioRolloffMode) })]
        public static class CreatureUnit_PlaySound3_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(CreatureUnit __instance, string soundKey, AudioRolloffMode mode, ref SoundEffectPlayer __result)
            {
                if (__instance == null || __instance.model == null || __instance.model.metaInfo == null || __instance.model.metaInfo.soundTable == null || string.IsNullOrEmpty(soundKey))
                {
                    MissingSoundTableKeyHits++;
                    __result = null;
                    return false;
                }

                string filename;
                if (!__instance.model.metaInfo.soundTable.TryGetValue(soundKey, out filename) || string.IsNullOrEmpty(filename))
                {
                    MissingSoundTableKeyHits++;
                    LastSoundCallMs = 0.0;
                    __result = null;
                    return false;
                }

                __result = SoundEffectPlayer.PlayOnce(filename, __instance.transform.position, mode);
                return false;
            }
        }

        [HarmonyPatch(typeof(CreatureUnit), "PlaySoundLoop", new Type[] { typeof(string) })]
        public static class CreatureUnit_PlaySoundLoop1_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(CreatureUnit __instance, string soundKey, ref SoundEffectPlayer __result)
            {
                if (__instance == null || __instance.model == null || __instance.model.metaInfo == null || __instance.model.metaInfo.soundTable == null || string.IsNullOrEmpty(soundKey))
                {
                    MissingSoundTableKeyHits++;
                    __result = null;
                    return false;
                }

                string filename;
                if (!__instance.model.metaInfo.soundTable.TryGetValue(soundKey, out filename) || string.IsNullOrEmpty(filename))
                {
                    MissingSoundTableKeyHits++;
                    LastSoundCallMs = 0.0;
                    __result = null;
                    return false;
                }

                __result = SoundEffectPlayer.Play(filename, __instance.transform);
                return false;
            }
        }

        [HarmonyPatch(typeof(CreatureUnit), "PlaySoundLoop", new Type[] { typeof(string), typeof(float) })]
        public static class CreatureUnit_PlaySoundLoop2_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(CreatureUnit __instance, string soundKey, float volume, ref SoundEffectPlayer __result)
            {
                if (__instance == null || __instance.model == null || __instance.model.metaInfo == null || __instance.model.metaInfo.soundTable == null || string.IsNullOrEmpty(soundKey))
                {
                    MissingSoundTableKeyHits++;
                    __result = null;
                    return false;
                }

                string filename;
                if (!__instance.model.metaInfo.soundTable.TryGetValue(soundKey, out filename) || string.IsNullOrEmpty(filename))
                {
                    MissingSoundTableKeyHits++;
                    LastSoundCallMs = 0.0;
                    __result = null;
                    return false;
                }

                __result = SoundEffectPlayer.Play(filename, __instance.transform, volume);
                return false;
            }
        }
    }
}
