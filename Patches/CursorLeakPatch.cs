using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// 弾丸（スナイプ）カーソルおよび照準アニメーションの Texture2D 永久リーク修正パッチ (Cursor / Reticle Memory Leak Fix)
    /// 
    /// 【安全なライフサイクル設計 (v0.7.5)】
    /// プレイ中はキャッシュを保持して毎フレームの ScaleTexture リークを遮断し、
    /// シーン遷移時またはゲーム終了時に ClearAllAndDestroy で安全に解放する。
    /// </summary>
    public static class CursorLeakPatch
    {
        public struct CursorScaleKey : IEquatable<CursorScaleKey>
        {
            public int SourceTexId;
            public int Width;
            public int Height;
            public bool Mipmap;

            public bool Equals(CursorScaleKey other)
            {
                return SourceTexId == other.SourceTexId
                    && Width == other.Width
                    && Height == other.Height
                    && Mipmap == other.Mipmap;
            }

            public override bool Equals(object obj)
            {
                return obj is CursorScaleKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + SourceTexId;
                    hash = hash * 31 + Width;
                    hash = hash * 31 + Height;
                    hash = hash * 31 + (Mipmap ? 1 : 0);
                    return hash;
                }
            }
        }

        private class ScaledCursorEntry
        {
            public Texture2D SourceTexture;
            public Texture2D ScaledTexture;
        }

        private static readonly Dictionary<CursorScaleKey, ScaledCursorEntry> ScaledCursorCache = new Dictionary<CursorScaleKey, ScaledCursorEntry>(64);

        private static Texture2D _lastBaseCursorTex;
        private static int _lastScaledWidth = -1;
        private static int _lastScaledHeight = -1;

        public static long AvoidedCursorTexAllocations = 0;
        public static long DestroyedCursorTexCount = 0;
        public static int RetainedCursorCount = 0;
        public static int CachedCursorTextureCount => ScaledCursorCache.Count;

        /// <summary>
        /// シーン遷移時の安全なリソース整理 (v0.8.2 Source-Lifetime Reference Verification)
        /// 元となるカーソル Texture2D アセットが生存している限り量子化済み縮小テクスチャを保持・再利用し、
        /// 元テクスチャがアンロードされた孤立エントリのみを安全に Object.Destroy で解放する。
        /// </summary>
        public static void SafeClearUnusedResources()
        {
            if (ScaledCursorCache.Count == 0) return;

            List<CursorScaleKey> toRemove = new List<CursorScaleKey>();
            int retained = 0;

            foreach (var kvp in ScaledCursorCache)
            {
                var entry = kvp.Value;
                if (entry == null || entry.ScaledTexture == null)
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }

                if (entry.SourceTexture != null)
                {
                    retained++;
                }
                else
                {
                    UnityEngine.Object.Destroy(entry.ScaledTexture);
                    entry.ScaledTexture = null;
                    DestroyedCursorTexCount++;
                    toRemove.Add(kvp.Key);
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                ScaledCursorCache.Remove(toRemove[i]);
            }

            RetainedCursorCount = retained;
            _lastBaseCursorTex = null;
            _lastScaledWidth = -1;
            _lastScaledHeight = -1;
        }

        /// <summary>
        /// プラグイン終了時など、全解放を行う場合
        /// </summary>
        public static void ForceClearAllAndDestroy()
        {
            foreach (var kvp in ScaledCursorCache)
            {
                if (kvp.Value != null && kvp.Value.ScaledTexture != null)
                {
                    UnityEngine.Object.Destroy(kvp.Value.ScaledTexture);
                    kvp.Value.ScaledTexture = null;
                    DestroyedCursorTexCount++;
                }
            }
            ScaledCursorCache.Clear();
            RetainedCursorCount = 0;
            _lastBaseCursorTex = null;
            _lastScaledWidth = -1;
            _lastScaledHeight = -1;
        }

        /// <summary>
        /// CursorManager.SetCursorScale(float factor) の毎フレーム Texture2D 生成リークを遮断
        /// </summary>
        [HarmonyPatch(typeof(CursorManager), "SetCursorScale", new Type[] { typeof(float) })]
        public static class CursorManager_SetCursorScale_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(
                CursorManager __instance,
                float factor,
                Texture2D ___currentCursorTexture,
                Vector2 ____currentHotspot)
            {
                if (!Plugin.EnableCursorLeakFix.Value) return true;
                if (___currentCursorTexture == null) return false;

                // 等倍復帰時はスケール自体不要
                if (Mathf.Abs(factor - 1f) < 0.01f)
                {
                    if (_lastBaseCursorTex == ___currentCursorTexture && _lastScaledWidth == ___currentCursorTexture.width && _lastScaledHeight == ___currentCursorTexture.height)
                    {
                        AvoidedCursorTexAllocations++;
                        return false;
                    }

                    _lastBaseCursorTex = ___currentCursorTexture;
                    _lastScaledWidth = ___currentCursorTexture.width;
                    _lastScaledHeight = ___currentCursorTexture.height;
                    Cursor.SetCursor(___currentCursorTexture, ____currentHotspot, __instance.cursorMode);
                    AvoidedCursorTexAllocations++;
                    return false;
                }

                // 2ピクセル単位に量子化してキャッシュ種類数を極小化（視覚差ほぼゼロで再利用性最大化）
                int targetW = Mathf.Max(2, ((int)(factor * ___currentCursorTexture.width)) & ~1);
                int targetH = Mathf.Max(2, ((int)(factor * ___currentCursorTexture.height)) & ~1);

                // 前フレームと同一サイズなら SetCursor も ScaleTexture も完全スキップ
                if (_lastBaseCursorTex == ___currentCursorTexture && _lastScaledWidth == targetW && _lastScaledHeight == targetH)
                {
                    AvoidedCursorTexAllocations++;
                    return false;
                }

                _lastBaseCursorTex = ___currentCursorTexture;
                _lastScaledWidth = targetW;
                _lastScaledHeight = targetH;

                Vector2 scaledHotspot = ____currentHotspot * factor;
                Texture2D scaledTex = WorkerSpriteManager.ScaleTexture(___currentCursorTexture, targetW, targetH, false);
                Cursor.SetCursor(scaledTex, scaledHotspot, __instance.cursorMode);
                return false;
            }
        }

        /// <summary>
        /// WorkerSpriteManager.ScaleTexture をフックし、カーソルテクスチャの縮小・拡大結果をキャッシュ
        /// </summary>
        [HarmonyPatch(typeof(WorkerSpriteManager), "ScaleTexture", new Type[] { typeof(Texture2D), typeof(int), typeof(int), typeof(bool) })]
        public static class WorkerSpriteManager_ScaleTexture_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(
                Texture2D source,
                int targetWidth,
                int targetHeight,
                bool mipmap,
                ref Texture2D __result,
                out CursorScaleKey __state)
            {
                __state = default(CursorScaleKey);
                if (!Plugin.EnableCursorLeakFix.Value || source == null) return true;

                // サイズが元と全く同じなら新規生成せずそのまま返す
                if (source.width == targetWidth && source.height == targetHeight && !mipmap)
                {
                    AvoidedCursorTexAllocations++;
                    __result = source;
                    return false;
                }

                // CursorManager からの呼び出し（または mipmap=false のカーソル縮小）をキャッシュ
                bool isCursorCall = !mipmap || (CursorManager.instance != null && CursorManager.instance.cursorSprite != null && CursorManager.instance.cursorSprite.Contains(source));
                if (!isCursorCall) return true;

                __state = new CursorScaleKey
                {
                    SourceTexId = source.GetInstanceID(),
                    Width = targetWidth,
                    Height = targetHeight,
                    Mipmap = mipmap
                };

                if (ScaledCursorCache.TryGetValue(__state, out var cachedEntry))
                {
                    if (cachedEntry != null && cachedEntry.ScaledTexture != null)
                    {
                        AvoidedCursorTexAllocations++;
                        __result = cachedEntry.ScaledTexture;
                        return false;
                    }
                    ScaledCursorCache.Remove(__state);
                }

                return true;
            }

            [HarmonyPostfix]
            public static void Postfix(
                Texture2D source,
                bool mipmap,
                Texture2D __result,
                CursorScaleKey __state)
            {
                if (!Plugin.EnableCursorLeakFix.Value || source == null || __result == null || __state.SourceTexId == 0)
                {
                    return;
                }

                if (!ScaledCursorCache.ContainsKey(__state) && !object.ReferenceEquals(__result, source))
                {
                    ScaledCursorCache[__state] = new ScaledCursorEntry
                    {
                        SourceTexture = source,
                        ScaledTexture = __result
                    };
                }
            }
        }
    }
}
