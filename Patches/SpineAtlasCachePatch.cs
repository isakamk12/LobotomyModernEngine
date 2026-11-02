using System;
using System.Collections.Generic;
using HarmonyLib;
using Spine;
using Spine.Unity.Modules.AttachmentTools;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// アニメーション切替時の Spine テクスチャ・マテリアル永久リーク修正パッチ (Animation Memory Leak Fix)
    /// 
    /// 【ネイティブメモリ所有権と解放の設計 (v0.7.4 監査硬化)】
    /// 1. バニラでは WorkerSpriteSetter.ChangeSpineData から AtlasUtilities.ToAtlasRegionPMAClone が呼ばれるたびに
    ///    new Material() と new Texture2D() を生成して一度も Object.Destroy せず放置していた。
    /// 2. WeakReference 単体では C# ラッパーが GC 回収されても Unity C++ ネイティブ側の Texture2D/Material は解放されない。
    ///    そのため本キャッシュ(CachedAtlasEntry)が CreatedMaterial と CreatedTexture の「強参照および唯一の破棄所有権」を保持し、
    ///    入力 Sprite の破棄検知時 (PruneDeadReferences) や全クリア時 (ClearAllAndDestroy) に
    ///    必ず明示的に UnityEngine.Object.Destroy(CreatedTexture) / Destroy(CreatedMaterial) を呼び出して VRAM/RAM を解放する。
    /// 3. さらに Texture2D を入力とする ToAtlasRegionPMAClone オーバーロードも同様にキャッシュ・所有権管理を行う。
    /// </summary>
    [HarmonyPatch(typeof(AtlasUtilities))]
    public static class SpineAtlasCachePatch
    {
        public struct AtlasCacheKey : IEquatable<AtlasCacheKey>
        {
            public int SourceInstanceId;
            public int ShaderInstanceId;
            public int Format;
            public bool Mipmaps;
            public int MaterialSourceId;

            public bool Equals(AtlasCacheKey other)
            {
                return SourceInstanceId == other.SourceInstanceId
                    && ShaderInstanceId == other.ShaderInstanceId
                    && Format == other.Format
                    && Mipmaps == other.Mipmaps
                    && MaterialSourceId == other.MaterialSourceId;
            }

            public override bool Equals(object obj)
            {
                return obj is AtlasCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + SourceInstanceId;
                    hash = hash * 31 + ShaderInstanceId;
                    hash = hash * 31 + Format;
                    hash = hash * 31 + (Mipmaps ? 1 : 0);
                    hash = hash * 31 + MaterialSourceId;
                    return hash;
                }
            }
        }

        private class CachedAtlasEntry
        {
            // 入力元の Sprite または Texture2D (UnityEngine.Object の破棄検知用)
            public WeakReference SourceRef;
            public AtlasRegion Region;
            // ネイティブ解放所有権を持つ生成物（強参照で保持し、退거時に確実に Object.Destroy する）
            public Material CreatedMaterial;
            public Texture2D CreatedTexture;
        }

        private static readonly Dictionary<AtlasCacheKey, CachedAtlasEntry> Cache = new Dictionary<AtlasCacheKey, CachedAtlasEntry>(256);

        // 診断メトリクス
        public static long CacheHitCount = 0;
        public static long CacheMissCount = 0;
        public static long DestroyedNativeObjectCount = 0;
        public static int CachedRegionCount => Cache.Count;

        private static void DestroyEntryResources(CachedAtlasEntry entry)
        {
            if (entry == null) return;
            if (entry.CreatedTexture != null)
            {
                UnityEngine.Object.Destroy(entry.CreatedTexture);
                entry.CreatedTexture = null;
                DestroyedNativeObjectCount++;
            }
            if (entry.CreatedMaterial != null)
            {
                UnityEngine.Object.Destroy(entry.CreatedMaterial);
                entry.CreatedMaterial = null;
                DestroyedNativeObjectCount++;
            }
            entry.Region = null;
            entry.SourceRef = null;
        }

        /// <summary>
        /// 破棄済み Sprite / Texture2D に紐づくキャッシュエントリの Material / Texture2D を明示的に Destroy してネイティブメモリを解放
        /// </summary>
        public static void PruneDeadReferences()
        {
            if (Cache.Count == 0) return;

            List<AtlasCacheKey> deadKeys = null;
            foreach (var kvp in Cache)
            {
                var entry = kvp.Value;
                UnityEngine.Object targetObj = entry.SourceRef != null ? (entry.SourceRef.Target as UnityEngine.Object) : null;
                // Unity のカスタム == null 演算子により、C++ 側で破棄済みの Object も確実に検出
                if (targetObj == null || entry.CreatedMaterial == null || entry.CreatedTexture == null)
                {
                    if (deadKeys == null) deadKeys = new List<AtlasCacheKey>();
                    deadKeys.Add(kvp.Key);
                }
            }

            if (deadKeys != null)
            {
                for (int i = 0; i < deadKeys.Count; i++)
                {
                    var key = deadKeys[i];
                    if (Cache.TryGetValue(key, out var entry))
                    {
                        DestroyEntryResources(entry);
                        Cache.Remove(key);
                    }
                }
            }
        }

        /// <summary>
        /// シャットダウン時や完全リセット時に全キャッシュのネイティブリソースを明示的に Destroy
        /// </summary>
        public static void ClearAllAndDestroy()
        {
            foreach (var kvp in Cache)
            {
                DestroyEntryResources(kvp.Value);
            }
            Cache.Clear();
        }

        [HarmonyPatch("ToAtlasRegionPMAClone", new Type[] { typeof(Sprite), typeof(Shader), typeof(TextureFormat), typeof(bool), typeof(Material) })]
        [HarmonyPrefix]
        public static bool Prefix_ToAtlasRegionPMAClone_Sprite(
            Sprite s,
            Shader shader,
            TextureFormat textureFormat,
            bool mipmaps,
            Material materialPropertySource,
            ref AtlasRegion __result,
            out AtlasCacheKey __state)
        {
            __state = default(AtlasCacheKey);
            if (!Plugin.EnableSpineAtlasCache.Value || s == null || shader == null)
            {
                return true;
            }

            __state = new AtlasCacheKey
            {
                SourceInstanceId = s.GetInstanceID(),
                ShaderInstanceId = shader.GetInstanceID(),
                Format = (int)textureFormat,
                Mipmaps = mipmaps,
                MaterialSourceId = materialPropertySource != null ? materialPropertySource.GetInstanceID() : 0
            };

            if (Cache.TryGetValue(__state, out var entry))
            {
                Sprite cachedSprite = entry.SourceRef != null ? (entry.SourceRef.Target as Sprite) : null;
                if (object.ReferenceEquals(cachedSprite, s) && cachedSprite != null && entry.CreatedMaterial != null && entry.CreatedTexture != null && entry.Region != null)
                {
                    CacheHitCount++;
                    __result = entry.Region;
                    return false; // new Material / new Texture2D / ApplyPMA を完全スキップ
                }

                // 無効化されていた場合は古いネイティブオブジェクトを確実に Destroy して再生成させる
                DestroyEntryResources(entry);
                Cache.Remove(__state);
            }

            return true;
        }

        [HarmonyPatch("ToAtlasRegionPMAClone", new Type[] { typeof(Sprite), typeof(Shader), typeof(TextureFormat), typeof(bool), typeof(Material) })]
        [HarmonyPostfix]
        public static void Postfix_ToAtlasRegionPMAClone_Sprite(
            Sprite s,
            Shader shader,
            AtlasRegion __result,
            AtlasCacheKey __state)
        {
            if (!Plugin.EnableSpineAtlasCache.Value || s == null || shader == null || __result == null || __result.page == null)
            {
                return;
            }

            if (Cache.ContainsKey(__state))
            {
                return;
            }

            Material mat = __result.page.rendererObject as Material;
            Texture2D tex = mat != null ? (mat.mainTexture as Texture2D) : null;
            if (mat != null && tex != null)
            {
                CacheMissCount++;
                Cache[__state] = new CachedAtlasEntry
                {
                    SourceRef = new WeakReference(s),
                    Region = __result,
                    CreatedMaterial = mat,
                    CreatedTexture = tex
                };
            }
        }

        [HarmonyPatch("ToAtlasRegionPMAClone", new Type[] { typeof(Texture2D), typeof(Shader), typeof(TextureFormat), typeof(bool), typeof(Material) })]
        [HarmonyPrefix]
        public static bool Prefix_ToAtlasRegionPMAClone_Texture(
            Texture2D t,
            Shader shader,
            TextureFormat textureFormat,
            bool mipmaps,
            Material materialPropertySource,
            ref AtlasRegion __result,
            out AtlasCacheKey __state)
        {
            __state = default(AtlasCacheKey);
            if (!Plugin.EnableSpineAtlasCache.Value || t == null || shader == null)
            {
                return true;
            }

            __state = new AtlasCacheKey
            {
                SourceInstanceId = t.GetInstanceID(),
                ShaderInstanceId = shader.GetInstanceID(),
                Format = (int)textureFormat,
                Mipmaps = mipmaps,
                MaterialSourceId = materialPropertySource != null ? materialPropertySource.GetInstanceID() : 0
            };

            if (Cache.TryGetValue(__state, out var entry))
            {
                Texture2D cachedTex = entry.SourceRef != null ? (entry.SourceRef.Target as Texture2D) : null;
                if (object.ReferenceEquals(cachedTex, t) && cachedTex != null && entry.CreatedMaterial != null && entry.CreatedTexture != null && entry.Region != null)
                {
                    CacheHitCount++;
                    __result = entry.Region;
                    return false;
                }

                DestroyEntryResources(entry);
                Cache.Remove(__state);
            }

            return true;
        }

        [HarmonyPatch("ToAtlasRegionPMAClone", new Type[] { typeof(Texture2D), typeof(Shader), typeof(TextureFormat), typeof(bool), typeof(Material) })]
        [HarmonyPostfix]
        public static void Postfix_ToAtlasRegionPMAClone_Texture(
            Texture2D t,
            Shader shader,
            AtlasRegion __result,
            AtlasCacheKey __state)
        {
            if (!Plugin.EnableSpineAtlasCache.Value || t == null || shader == null || __result == null || __result.page == null)
            {
                return;
            }

            if (Cache.ContainsKey(__state))
            {
                return;
            }

            Material mat = __result.page.rendererObject as Material;
            Texture2D tex = mat != null ? (mat.mainTexture as Texture2D) : null;
            if (mat != null && tex != null)
            {
                CacheMissCount++;
                Cache[__state] = new CachedAtlasEntry
                {
                    SourceRef = new WeakReference(t),
                    Region = __result,
                    CreatedMaterial = mat,
                    CreatedTexture = tex
                };
            }
        }
    }
}
