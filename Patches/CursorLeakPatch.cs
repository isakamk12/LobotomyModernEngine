using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// 弾丸（スナイプ）カーソルおよび照準アニメーションの Texture2D 永久リーク修正パッチ (Cursor / Reticle Memory Leak Fix)
    /// 
    /// 【ネイティブメモリ所有権と解放の設計 (v0.7.4 監査硬化)】
    /// 1. バニラでは弾丸選択時の毎フレーム Update() で WorkerSpriteManager.ScaleTexture を呼び、
    ///    new Texture2D を毎秒60個生成して1つも Destroy していなかった。
    ///    また WorkerSpriteManager.LoadCustomSprite 内の new Texture2D(2,2) 一時テクスチャも ScaleTexture 後に放置されていた。
    /// 2. 本パッチではカーソル用スケールテクスチャをソース WeakReference + 強参照 Texture2D で管理し、
    ///    ソース破棄時 (PruneDeadReferences) や上限超過時に明示的に UnityEngine.Object.Destroy を呼んでネイティブメモリを解放する。
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

        private class CachedCursorEntry
        {
            public WeakReference SourceTexRef;
            public Texture2D ScaledTexture;
        }

        private static readonly Dictionary<CursorScaleKey, CachedCursorEntry> ScaledCursorCache = new Dictionary<CursorScaleKey, CachedCursorEntry>(64);

        private static Texture2D _lastBaseCursorTex;
        private static int _lastScaledWidth = -1;
        private static int _lastScaledHeight = -1;

        public static long AvoidedCursorTexAllocations = 0;
        public static long DestroyedCursorTexCount = 0;
        public static int CachedCursorTextureCount => ScaledCursorCache.Count;

        public static void PruneDeadReferences()
        {
            if (ScaledCursorCache.Count == 0) return;

            List<CursorScaleKey> deadKeys = null;
            foreach (var kvp in ScaledCursorCache)
            {
                var entry = kvp.Value;
                Texture2D src = entry.SourceTexRef != null ? (entry.SourceTexRef.Target as Texture2D) : null;
                if (src == null || entry.ScaledTexture == null)
                {
                    if (deadKeys == null) deadKeys = new List<CursorScaleKey>();
                    deadKeys.Add(kvp.Key);
                }
            }

            if (deadKeys != null)
            {
                for (int i = 0; i < deadKeys.Count; i++)
                {
                    var key = deadKeys[i];
                    if (ScaledCursorCache.TryGetValue(key, out var entry))
                    {
                        if (entry.ScaledTexture != null)
                        {
                            UnityEngine.Object.Destroy(entry.ScaledTexture);
                            DestroyedCursorTexCount++;
                        }
                        ScaledCursorCache.Remove(key);
                    }
                }
            }
        }

        public static void ClearAllAndDestroy()
        {
            foreach (var kvp in ScaledCursorCache)
            {
                if (kvp.Value.ScaledTexture != null)
                {
                    UnityEngine.Object.Destroy(kvp.Value.ScaledTexture);
                    DestroyedCursorTexCount++;
                }
            }
            ScaledCursorCache.Clear();
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

                if (ScaledCursorCache.TryGetValue(__state, out var entry))
                {
                    Texture2D cachedSrc = entry.SourceTexRef != null ? (entry.SourceTexRef.Target as Texture2D) : null;
                    if (object.ReferenceEquals(cachedSrc, source) && cachedSrc != null && entry.ScaledTexture != null)
                    {
                        AvoidedCursorTexAllocations++;
                        __result = entry.ScaledTexture;
                        return false;
                    }
                    if (entry.ScaledTexture != null && !object.ReferenceEquals(entry.ScaledTexture, source))
                    {
                        UnityEngine.Object.Destroy(entry.ScaledTexture);
                        DestroyedCursorTexCount++;
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
                    ScaledCursorCache[__state] = new CachedCursorEntry
                    {
                        SourceTexRef = new WeakReference(source),
                        ScaledTexture = __result
                    };
                }
            }
        }
    }
}
