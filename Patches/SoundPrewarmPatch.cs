using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// v0.8.6: 音声事前ロード (Sound Prewarming)。
    /// 
    /// 設計原則:
    /// 1. メインスレッドで動作し、非同期 LoadAsync または 1フレームあたり数個ずつの分割ロードで実行。
    /// 2. 対象を厳選: 当該日に施設に配置されている幻想体 (CreatureManager) の soundTable と、
    ///    職員・オフィサーの共通効果音 (銃声、悲鳴、作業音等)。全音声を無差別に読まない。
    /// 3. 一度に大量ロードせず、コルーチンによりフレーム負荷を分散する。
    /// 4. SoundEffectCachePatch の既存キャッシュをそのまま再利用し、二重保持や解放漏れを防ぐ。
    /// </summary>
    public static class SoundPrewarmPatch
    {
        // 職員・オフィサー等の頻出共通効果音
        private static readonly string[] CommonSoundPaths = new string[]
        {
            "Sounds/Weapons/officerGun",
            "Sounds/Agent/officerDie",
            "Sounds/Agent/Scream/1",
            "Sounds/Agent/Scream/2",
            "Sounds/Agent/Scream/3",
            "Sounds/Agent/Scream/4",
            "Sounds/Agent/Scream/5",
            "Sounds/Agent/Scream/6",
            "Sounds/Agent/Scream/7",
            "Sounds/Agent/Scream/8",
            "Sounds/Agent/Scream/9",
            "Sounds/Effect/Panic",
            "Sounds/Effect/door_open",
            "Sounds/Effect/door_close"
        };

        private static Coroutine _prewarmCoroutine;

        public static void StopPrewarm(MonoBehaviour host)
        {
            if (_prewarmCoroutine != null && host != null)
            {
                host.StopCoroutine(_prewarmCoroutine);
                _prewarmCoroutine = null;
            }
        }

        [HarmonyPatch(typeof(GameManager), "StartStage")]
        public static class GameManager_StartStage_PrewarmPatch
        {
            public static void Postfix(GameManager __instance)
            {
                if (!Plugin.EnableSoundPrewarming.Value)
                {
                    return;
                }

                if (__instance != null)
                {
                    if (_prewarmCoroutine != null)
                    {
                        __instance.StopCoroutine(_prewarmCoroutine);
                    }
                    _prewarmCoroutine = __instance.StartCoroutine(PrewarmRoutine());
                }
            }
        }

        private static IEnumerator PrewarmRoutine()
        {
            // 配置フェーズ直後の初期化負荷と被らないよう数フレーム待機
            yield return null;
            yield return null;

            // 1. 収集フェーズ: 重複のないロード対象パスを抽出
            var loadTargets = new List<string>(128);
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            // A. 共通効果音
            foreach (var path in CommonSoundPaths)
            {
                string norm = NormalizeKey(path);
                if (!seenKeys.Contains(norm) && !SoundEffectCachePatch.IsCached(norm))
                {
                    seenKeys.Add(norm);
                    loadTargets.Add(path);
                }
            }

            // B. 配置済み幻想体の soundTable 音声
            var creatureManager = CreatureManager.instance;
            if (creatureManager != null)
            {
                var creatureArray = creatureManager.GetCreatureList();
                if (creatureArray != null)
                {
                    for (int i = 0; i < creatureArray.Length; i++)
                    {
                        var creature = creatureArray[i];
                        if (creature?.metaInfo?.soundTable != null)
                        {
                            foreach (var kvp in creature.metaInfo.soundTable)
                            {
                                string soundPath = kvp.Value;
                                if (!string.IsNullOrEmpty(soundPath))
                                {
                                    string norm = NormalizeKey(soundPath);
                                    if (!seenKeys.Contains(norm) && !SoundEffectCachePatch.IsCached(norm))
                                    {
                                        seenKeys.Add(norm);
                                        loadTargets.Add(soundPath);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (loadTargets.Count == 0)
            {
                _prewarmCoroutine = null;
                yield break;
            }

            if (Plugin.Logger != null && (Plugin.EnableProfilerLog == null || Plugin.EnableProfilerLog.Value))
            {
                Plugin.Logger.LogInfo(string.Format("[LME 音声プリウォーム] 開始: 対象 {0} 件の音声を非同期/分散ロードします。", loadTargets.Count));
            }

            // 2. 分散ロードフェーズ: 1フレームあたり最大2件ずつ Resources.LoadAsync
            int perFrameCount = 2;
            int loadedCount = 0;

            for (int i = 0; i < loadTargets.Count; i++)
            {
                string path = loadTargets[i];
                string resourcePath = path.StartsWith("Sounds/") ? path : ("Sounds/" + path);
                string cacheKey = NormalizeKey(path);

                ResourceRequest request = Resources.LoadAsync<AudioClip>(resourcePath);
                if (request != null)
                {
                    while (!request.isDone)
                    {
                        yield return null;
                    }

                    AudioClip clip = request.asset as AudioClip;
                    SoundEffectCachePatch.RegisterPrewarmedClip(cacheKey, clip);
                    if (clip != null)
                    {
                        loadedCount++;
                    }
                }

                if (i % perFrameCount == 0)
                {
                    yield return null;
                }
            }

            if (Plugin.Logger != null && (Plugin.EnableProfilerLog == null || Plugin.EnableProfilerLog.Value))
            {
                Plugin.Logger.LogInfo(string.Format("[LME 音声プリウォーム] 完了: {0} 件の音声をキャッシュに登録しました。", loadedCount));
            }

            _prewarmCoroutine = null;
        }

        private static string NormalizeKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            if (path.StartsWith("Sounds/")) return path.Substring(7);
            return path;
        }
    }
}
