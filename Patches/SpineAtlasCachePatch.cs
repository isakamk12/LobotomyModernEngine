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
        }

        /// <summary>
        /// シーン遷移時の安全なリソース解放 (v0.7.6 Live Reference Verification)
        /// 単に全破棄するのではなく、DontDestroyOnLoad やアクティブな Renderer / UI Graphic で
        /// 現在も参照されているマテリアル・テクスチャを走査・特定し、
        /// 使用中素材はキャッシュ内にそのまま安全に保持 (Retained) し、
        /// 参照が完全にゼロとなった孤立リソースのみを UnityEngine.Object.Destroy で解放する。
        /// </summary>
        public static void SafeClearUnusedResources()
        {
            if (Cache.Count == 0) return;

            // 1. 現在生存している全Renderer・UIコンポーネントから使用中Material/Textureを収集
            HashSet<Material> activeMaterials = new HashSet<Material>();
            HashSet<Texture> activeTextures = new HashSet<Texture>();

            try
            {
                Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        Renderer r = renderers[i];
                        if (r == null) continue;
                        Material[] mats = r.sharedMaterials;
                        if (mats != null)
                        {
                            for (int j = 0; j < mats.Length; j++)
                            {
                                Material m = mats[j];
                                if (m != null)
                                {
                                    activeMaterials.Add(m);
                                    if (m.mainTexture != null) activeTextures.Add(m.mainTexture);
                                }
                            }
                        }
                    }
                }

                UnityEngine.UI.Graphic[] graphics = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Graphic>();
                if (graphics != null)
                {
                    for (int i = 0; i < graphics.Length; i++)
                    {
                        UnityEngine.UI.Graphic g = graphics[i];
                        if (g == null) continue;
                        if (g.material != null)
                        {
                            activeMaterials.Add(g.material);
                            if (g.material.mainTexture != null) activeTextures.Add(g.material.mainTexture);
                        }
                        if (g.mainTexture != null)
                        {
                            activeTextures.Add(g.mainTexture);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 万一の走査例外時は安全側に倒し、破棄を行わずに次回へ繰り延べる
                return;
            }

            // 2. キャッシュ内の各エントリを検証し、アクティブ参照のない孤立エントリのみを破棄
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

                bool inUse = false;
                if (entry.CreatedMaterial != null && activeMaterials.Contains(entry.CreatedMaterial))
                {
                    inUse = true;
                }
                if (entry.CreatedTexture != null && activeTextures.Contains(entry.CreatedTexture))
                {
                    inUse = true;
                }

                if (inUse)
                {
                    // ゲーム側で現在も使用中（DontDestroyOnLoad等）のため破棄せずキャッシュに保持
                    retained++;
                }
                else
                {
                    // 誰からも参照されていない孤立リソースであることが確定 -> 安全にDestroy
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
