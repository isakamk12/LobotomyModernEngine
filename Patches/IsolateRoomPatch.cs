using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// 全収容室が毎フレーム UI Canvas を Dirty にしてメッシュ再構築を引き起こす病巣を遮断するパッチ
    /// WeakReference による世代管理と、HarmonyFinalizer による例外時リスト復元保証を備えた硬化実装
    /// </summary>
    [HarmonyPatch(typeof(IsolateRoom))]
    public static class IsolateRoomPatch
    {
        private static readonly Dictionary<int, RoomState> Cache = new Dictionary<int, RoomState>();

        private static readonly List<MaskableGraphic> EmptyGraphicList = new List<MaskableGraphic>(0);

        private class RoomState
        {
            public System.WeakReference TargetRef;
            public int LastCounter = -999;
            public Color LastRoomColor = Color.clear;
            public int LastCubeCount = -999;
            public bool Initialized;
            public List<MaskableGraphic> ShelvedGraphics;
        }

        public static void ClearCache()
        {
            Cache.Clear();
        }

        public static void PruneDeadReferences()
        {
            List<int> deadKeys = null;
            foreach (var kvp in Cache)
            {
                if (kvp.Value.TargetRef == null || !kvp.Value.TargetRef.IsAlive)
                {
                    if (deadKeys == null) deadKeys = new List<int>();
                    deadKeys.Add(kvp.Key);
                }
            }
            if (deadKeys != null)
            {
                for (int i = 0; i < deadKeys.Count; i++)
                {
                    Cache.Remove(deadKeys[i]);
                }
            }
        }

        private static RoomState GetOrCreateState(IsolateRoom room)
        {
            int id = room.GetInstanceID();
            if (!Cache.TryGetValue(id, out var state) || state.TargetRef == null || !state.TargetRef.IsAlive || !object.ReferenceEquals(state.TargetRef.Target, room))
            {
                state = new RoomState { TargetRef = new System.WeakReference(room) };
                Cache[id] = state;
            }
            return state;
        }

        /// <summary>
        /// クリフォトカウンターの毎FixedUpdate更新を値変更時のみに限定
        /// </summary>
        [HarmonyPatch("CheckQliphothCounter")]
        [HarmonyPrefix]
        public static bool Prefix_CheckQliphothCounter(IsolateRoom __instance, bool ____counterObserved, bool ____counterEnabled)
        {
            if (!Plugin.EnableIsolateRoomOptimization.Value) return true;
            if (!____counterObserved || !____counterEnabled) return true;
            if (__instance.TargetUnit == null || __instance.TargetUnit.model == null) return true;

            var state = GetOrCreateState(__instance);
            int current = __instance.TargetUnit.model.qliphothCounter;

            if (state.Initialized && state.LastCounter == current)
            {
                return false; // 値が変わっていないので、ToString() と Canvas Dirty 代入を完全スキップ
            }

            state.LastCounter = current;
            state.Initialized = true;

            if (__instance.CounterText != null)
            {
                __instance.CounterText.text = current.ToString();
                if (current == 0)
                {
                    __instance.CounterText.color = __instance.CounterColor_White;
                    if (__instance.CounterColor != null) __instance.CounterColor.color = __instance.CounterColor_Red;
                }
                else
                {
                    __instance.CounterText.color = __instance.CounterColor_Normal;
                    if (__instance.CounterColor != null) __instance.CounterColor.color = __instance.CounterColor_Black;
                }
            }

            return false;
        }

        /// <summary>
        /// Update() 内の ColorSetted ループによる毎フレーム Canvas Dirty を阻止
        /// 色が変わっていない場合は ColorSetted を一時的に空にしてループを空振りさせる
        /// </summary>
        [HarmonyPatch("Update")]
        [HarmonyPrefix]
        public static void Prefix_Update(IsolateRoom __instance)
        {
            if (!Plugin.EnableIsolateRoomOptimization.Value) return;

            var state = GetOrCreateState(__instance);
            if (state.LastRoomColor == __instance.RoomColor && state.LastRoomColor != Color.clear)
            {
                // 色が変わっていないのでループを実行させないために退避
                state.ShelvedGraphics = __instance.ColorSetted;
                __instance.ColorSetted = EmptyGraphicList;
            }
            else
            {
                state.LastRoomColor = __instance.RoomColor;
                state.ShelvedGraphics = null;
            }
        }

        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        public static void Postfix_Update(IsolateRoom __instance)
        {
            if (!Plugin.EnableIsolateRoomOptimization.Value) return;
            RestoreShelvedGraphics(__instance);
        }

        /// <summary>
        /// 監査指摘4対応: Update() 内で例外が発生しても必ず ColorSetted を復元する Finalizer
        /// </summary>
        [HarmonyPatch("Update")]
        [HarmonyFinalizer]
        public static Exception Finalizer_Update(IsolateRoom __instance, Exception __exception)
        {
            RestoreShelvedGraphics(__instance);
            return __exception;
        }

        private static void RestoreShelvedGraphics(IsolateRoom room)
        {
            if (room == null) return;
            int id = room.GetInstanceID();
            if (Cache.TryGetValue(id, out var state) && state.ShelvedGraphics != null)
            {
                room.ColorSetted = state.ShelvedGraphics;
                state.ShelvedGraphics = null;
            }
        }
    }
}
