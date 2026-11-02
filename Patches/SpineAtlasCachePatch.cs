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
    /// 【安全なライフサイクル設計 (v0.7.5)】
    /// 1. バニラでは職員・オフィサーがアニメーションを切り替えるたびに ToAtlasRegionPMAClone が呼ばれ、
    ///    new Material() と new Texture2D() (CPU PMA計算) を生成して破棄せず放置していた。
    /// 2. プレイ中（同一日・同一シーン中）にゲーム側が使用中である可能性のあるテクスチャ/マテリアルを
    ///    途中で Destroy してしまうと、MissingReferenceException やピンク抜けを引き起こす危険がある。
    /// 3. そのため、プレイ中はキャッシュを安全に使い続けて一切 Destroy せず、
    ///    シーン遷移時（日の終了・タイトル復帰・ロード時）にのみ、全Spine描画が無効化された安全なタイミングで
    ///    ClearAllAndDestroy を呼び出し、明示的に Object.Destroy でネイティブメモリを完全解放する。
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
            public AtlasRegion Region;
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
        }

        /// <summary>
        /// シーン遷移時（日の終了・タイトル復帰・チェックポイントロード）またはゲーム終了時に安全に全解放
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
                if (entry.CreatedMaterial != null && entry.CreatedTexture != null && entry.Region != null)
                {
                    CacheHitCount++;
                    __result = entry.Region;
                    return false; // new Material / new Texture2D / ApplyPMA を完全スキップ
                }

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
                if (entry.CreatedMaterial != null && entry.CreatedTexture != null && entry.Region != null)
                {
                    CacheHitCount++;
                    __result = entry.Region;
                    return false;
                }

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
                    Region = __result,
                    CreatedMaterial = mat,
                    CreatedTexture = tex
                };
            }
        }
    }
}
