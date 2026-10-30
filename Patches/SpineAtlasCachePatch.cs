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
    /// バニラでは職員・オフィサーがアニメーションを切り替えるたびに WorkerSpriteSetter.ChangeSpineData から
    /// AtlasUtilities.ToAtlasRegionPMAClone が呼ばれ、全身約20部位ごとに毎回 new Material() と
    /// new Texture2D() (CPU全ピクセル走査によるPMA乗算＋ミップマップ生成) を新規生成して破棄せず放置していた。
    /// 本パッチでは生成済みの不変 AtlasRegion をキャッシュして再利用し、
    /// ネイティブメモリ・VRAMの永久リークとアニメーション切替時のCPUスパイクを同時に根絶する。
    /// </summary>
    [HarmonyPatch(typeof(AtlasUtilities))]
    public static class SpineAtlasCachePatch
    {
        public struct AtlasCacheKey : IEquatable<AtlasCacheKey>
        {
            public int SpriteInstanceId;
            public int ShaderInstanceId;
            public int Format;
            public bool Mipmaps;
            public int MaterialSourceId;

            public bool Equals(AtlasCacheKey other)
            {
                return SpriteInstanceId == other.SpriteInstanceId
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
                    hash = hash * 31 + SpriteInstanceId;
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
            public WeakReference SpriteRef;
            public AtlasRegion Region;
            public Material CreatedMaterial;
            public Texture2D CreatedTexture;
        }

        private static readonly Dictionary<AtlasCacheKey, CachedAtlasEntry> Cache = new Dictionary<AtlasCacheKey, CachedAtlasEntry>(256);

        // 診断メトリクス
        public static long CacheHitCount = 0;
        public static long CacheMissCount = 0;
        public static int CachedRegionCount => Cache.Count;

        /// <summary>
        /// 破棄済み Sprite に紐づくキャッシュエントリの Material / Texture2D を安全に解放
        /// </summary>
        public static void PruneDeadReferences()
        {
            if (Cache.Count == 0) return;

            List<AtlasCacheKey> deadKeys = null;
            foreach (var kvp in Cache)
            {
                var entry = kvp.Value;
                Sprite targetSprite = entry.SpriteRef != null ? (entry.SpriteRef.Target as Sprite) : null;
                if (targetSprite == null || entry.CreatedMaterial == null || entry.CreatedTexture == null)
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
                        if (entry.CreatedTexture != null) UnityEngine.Object.Destroy(entry.CreatedTexture);
                        if (entry.CreatedMaterial != null) UnityEngine.Object.Destroy(entry.CreatedMaterial);
                        Cache.Remove(key);
                    }
                }
            }
        }

        [HarmonyPatch("ToAtlasRegionPMAClone", new Type[] { typeof(Sprite), typeof(Shader), typeof(TextureFormat), typeof(bool), typeof(Material) })]
        [HarmonyPrefix]
        public static bool Prefix_ToAtlasRegionPMAClone(
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
                SpriteInstanceId = s.GetInstanceID(),
                ShaderInstanceId = shader.GetInstanceID(),
                Format = (int)textureFormat,
                Mipmaps = mipmaps,
                MaterialSourceId = materialPropertySource != null ? materialPropertySource.GetInstanceID() : 0
            };

            if (Cache.TryGetValue(__state, out var entry))
            {
                Sprite cachedSprite = entry.SpriteRef != null ? (entry.SpriteRef.Target as Sprite) : null;
                if (object.ReferenceEquals(cachedSprite, s) && cachedSprite != null && entry.CreatedMaterial != null && entry.CreatedTexture != null && entry.Region != null)
                {
                    CacheHitCount++;
                    __result = entry.Region;
                    return false; // new Material / new Texture2D / ApplyPMA を完全スキップ
                }

                // 無効化されていた場合は古いネイティブオブジェクトを破棄して再生成させる
                if (entry.CreatedTexture != null) UnityEngine.Object.Destroy(entry.CreatedTexture);
                if (entry.CreatedMaterial != null) UnityEngine.Object.Destroy(entry.CreatedMaterial);
                Cache.Remove(__state);
            }

            return true;
        }

        [HarmonyPatch("ToAtlasRegionPMAClone", new Type[] { typeof(Sprite), typeof(Shader), typeof(TextureFormat), typeof(bool), typeof(Material) })]
        [HarmonyPostfix]
        public static void Postfix_ToAtlasRegionPMAClone(
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
                    SpriteRef = new WeakReference(s),
                    Region = __result,
                    CreatedMaterial = mat,
                    CreatedTexture = tex
                };
            }
        }
    }
}
