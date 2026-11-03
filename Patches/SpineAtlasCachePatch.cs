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
            public Sprite SourceSprite;
            public Texture2D SourceTexture;
            public AtlasRegion Region;
            public Material CreatedMaterial;
            public Texture2D CreatedTexture;
        }

        private static readonly Dictionary<AtlasCacheKey, CachedAtlasEntry> Cache = new Dictionary<AtlasCacheKey, CachedAtlasEntry>(256);

        // 診断メトリクス
        public static long CacheHitCount = 0;
        public static long CacheMissCount = 0;
        public static long DestroyedNativeObjectCount = 0;
        public static int RetainedInUseObjectCount = 0;
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
            entry.SourceSprite = null;
            entry.SourceTexture = null;
        }

        /// <summary>
        /// シーン遷移時の安全なリソース整理 (v0.8.2 Source-Lifetime Reference Verification)
        /// 
        /// 【重要修正 (v0.8.2 ピンク矩形化バグ根治)】
        /// Main シーンのロード直後 (GameState.STOP / 配置フェーズ中) は、AgentUnit の SpineRenderer が
        /// optimizeLevel = 5 でメッシュ更新を待機しているため、Spine の Skin/AtlasRegion には
        /// すでに CreatedMaterial / CreatedTexture が紐付いているにもかかわらず、
        /// MeshRenderer.sharedMaterials にはまだ反映されていない。
        /// そのため Renderer.sharedMaterials 走査で Destroy すると、現存する職員の Spine スキン素材を
        /// 破壊してしまい、職員がマゼンタ（ピンク矩形）化する。
        /// 
        /// 本キャッシュは元 Sprite / Texture2D 単位で PMA クローンを一意化（重複生成を完全遮断）しているため、
        /// 元の Unity Sprite / Texture2D アセット自体が生存している限りキャッシュを破棄せず保持・再利用し、
        /// 元アセットが Unity 側でアンロード (== null) された孤立エントリのみを安全に Destroy する。
        /// </summary>
        public static void SafeClearUnusedResources()
        {
            if (Cache.Count == 0) return;

            List<AtlasCacheKey> toRemove = new List<AtlasCacheKey>();
            int retained = 0;

            foreach (var kvp in Cache)
            {
                var entry = kvp.Value;
                if (entry == null)
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }

                // 生成済みマテリアルまたはテクスチャが外部要因で失われている場合はエントリを除去
                if (entry.CreatedMaterial == null || entry.CreatedTexture == null || entry.Region == null)
                {
                    DestroyEntryResources(entry);
                    toRemove.Add(kvp.Key);
                    continue;
                }

                // 元となる Unity Sprite または Texture2D アセットが生存している限り、
                // 既存の Spine Skin / AtlasRegion や次回以降の ChangeSpineData で再利用されるため保持する
                bool sourceAlive = (entry.SourceSprite != null) || (entry.SourceTexture != null);
                if (sourceAlive)
                {
                    retained++;
                }
                else
                {
                    DestroyEntryResources(entry);
                    toRemove.Add(kvp.Key);
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                Cache.Remove(toRemove[i]);
            }

            RetainedInUseObjectCount = retained;
        }

        /// <summary>
        /// プラグイン終了時など、全リソースを強制破棄する場合
        /// </summary>
        public static void ForceClearAllAndDestroy()
        {
            foreach (var kvp in Cache)
            {
                DestroyEntryResources(kvp.Value);
            }
            Cache.Clear();
            RetainedInUseObjectCount = 0;
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
                if (entry.CreatedMaterial != null && entry.CreatedTexture != null && entry.Region != null && entry.Region.page != null)
                {
                    entry.Region.page.rendererObject = entry.CreatedMaterial;
                    entry.CreatedMaterial.mainTexture = entry.CreatedTexture;
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
                    SourceSprite = s,
                    SourceTexture = s.texture,
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
                if (entry.CreatedMaterial != null && entry.CreatedTexture != null && entry.Region != null && entry.Region.page != null)
                {
                    entry.Region.page.rendererObject = entry.CreatedMaterial;
                    entry.CreatedMaterial.mainTexture = entry.CreatedTexture;
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
                    SourceSprite = null,
                    SourceTexture = t,
                    Region = __result,
                    CreatedMaterial = mat,
                    CreatedTexture = tex
                };
            }
        }
    }
}
