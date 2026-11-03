using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace LobotomyModernEngine.Patches
{
    /// <summary>
    /// GraphAstar.SearchPath バッファ再利用パッチ (v0.8.4)
    /// 
    /// 【コード検証結果 (Assembly-CSharp.dll GraphAstar.SearchPath)】
    /// - バニラでは SearchPath 1回ごとに以下を毎回新規ヒープ確保していた:
    ///   1) PriorityQueue<PathScore> (内部 List<PathScore> の生成・拡張)
    ///   2) HashSet<MapNode> (閉リストの生成・拡張)
    ///   3) Dictionary<MapNode, SearchInfo> (最小コスト表の生成・拡張)
    ///   4) 隣接ノード走査ごとの new PathScore(node) および new SearchInfo(cost, edge)
    ///   5) ゴール到達時の ArrayList × 2個、および EdgeDirection enum のボクシング (ArrayList.Add(object))
    /// - これらはすべて SearchPath 内部の一時作業領域であり、戻り値 PathResult(MapEdge[], EdgeDirection[], float) には一切漏出しない。
    /// 
    /// 【安全性保証】
    /// - 1. 状態残留の完全防止:
    ///   探索開始前と finally ブロックの双方で OpenHeap / ClosedSet / BestCostMap / EdgeBuffer / DirectionBuffer /
    ///   オブジェクトプールインデックスを完全リセットし、プールから貸し出す PathScore / SearchInfo は全フィールドを明示的に上書き初期化する。
    /// - 2. 再帰呼出・入れ子呼出・マルチスレッド競合への対策:
    ///   Interlocked.CompareExchange による排他ガード (_workspaceInUse) を設け、
    ///   万一 SearchPath 実行中に再帰・入れ子呼出や別スレッド呼出が発生した場合は、独立した新規 SearchWorkspace インスタンスを生成して処理する。
    /// - 3. 探索結果の完全同一性:
    ///   バニラの PriorityQueue<T> と同一の二分ヒープ比較ロジック (PathScore.CompareTo) をそのまま維持する。
    /// </summary>
    public static class PathfindingBufferPatch
    {
        private static readonly MapEdge[] EmptyEdges = new MapEdge[0];
        private static readonly EdgeDirection[] EmptyDirections = new EdgeDirection[0];

        private sealed class ReusableMinHeap
        {
            private readonly List<GraphAstar.PathScore> _data = new List<GraphAstar.PathScore>(64);

            public int Count => _data.Count;

            public void Clear()
            {
                _data.Clear();
            }

            public void Enqueue(GraphAstar.PathScore item)
            {
                _data.Add(item);
                int ci = _data.Count - 1;
                while (ci > 0)
                {
                    int pi = (ci - 1) / 2;
                    if (_data[ci].CompareTo(_data[pi]) >= 0)
                    {
                        break;
                    }
                    GraphAstar.PathScore tmp = _data[ci];
                    _data[ci] = _data[pi];
                    _data[pi] = tmp;
                    ci = pi;
                }
            }

            public GraphAstar.PathScore Dequeue()
            {
                int li = _data.Count - 1;
                GraphAstar.PathScore frontItem = _data[0];
                _data[0] = _data[li];
                _data.RemoveAt(li);
                li--;
                int pi = 0;
                while (true)
                {
                    int ci = pi * 2 + 1;
                    if (ci > li)
                    {
                        break;
                    }
                    int rc = ci + 1;
                    if (rc <= li && _data[rc].CompareTo(_data[ci]) < 0)
                    {
                        ci = rc;
                    }
                    if (_data[pi].CompareTo(_data[ci]) <= 0)
                    {
                        break;
                    }
                    GraphAstar.PathScore tmp = _data[pi];
                    _data[pi] = _data[ci];
                    _data[ci] = tmp;
                    pi = ci;
                }
                return frontItem;
            }
        }

        private sealed class SearchWorkspace
        {
            public readonly ReusableMinHeap OpenQueue = new ReusableMinHeap();
            public readonly HashSet<MapNode> ClosedSet = new HashSet<MapNode>();
            public readonly Dictionary<MapNode, GraphAstar.SearchInfo> BestCostMap = new Dictionary<MapNode, GraphAstar.SearchInfo>(64);
            public readonly List<MapEdge> EdgeBuffer = new List<MapEdge>(32);
            public readonly List<EdgeDirection> DirectionBuffer = new List<EdgeDirection>(32);

            private readonly List<GraphAstar.PathScore> _scorePool = new List<GraphAstar.PathScore>(128);
            private readonly List<GraphAstar.SearchInfo> _infoPool = new List<GraphAstar.SearchInfo>(128);
            private int _scoreIndex = 0;
            private int _infoIndex = 0;

            public GraphAstar.PathScore RentPathScore(MapNode node, float cost, float h, out bool reused)
            {
                if (_scoreIndex < _scorePool.Count)
                {
                    GraphAstar.PathScore item = _scorePool[_scoreIndex++];
                    item.node = node;
                    item.cost = cost;
                    item.h = h;
                    reused = true;
                    return item;
                }
                GraphAstar.PathScore created = new GraphAstar.PathScore(node);
                created.cost = cost;
                created.h = h;
                _scorePool.Add(created);
                _scoreIndex++;
                reused = false;
                return created;
            }

            public GraphAstar.SearchInfo RentSearchInfo(float cost, MapEdge edge, out bool reused)
            {
                if (_infoIndex < _infoPool.Count)
                {
                    GraphAstar.SearchInfo item = _infoPool[_infoIndex++];
                    item.cost = cost;
                    item.edge = edge;
                    reused = true;
                    return item;
                }
                GraphAstar.SearchInfo created = new GraphAstar.SearchInfo(cost, edge);
                _infoPool.Add(created);
                _infoIndex++;
                reused = false;
                return created;
            }

            public void ResetState()
            {
                OpenQueue.Clear();
                ClosedSet.Clear();
                BestCostMap.Clear();
                EdgeBuffer.Clear();
                DirectionBuffer.Clear();

                // 今回の探索で使用したプール要素の参照フィールドをクリアし、MapNode/MapEdge への参照残留を防止
                for (int i = 0; i < _scoreIndex; i++)
                {
                    _scorePool[i].node = null;
                    _scorePool[i].cost = 0f;
                    _scorePool[i].h = 0f;
                }
                for (int i = 0; i < _infoIndex; i++)
                {
                    _infoPool[i].edge = null;
                    _infoPool[i].cost = 0f;
                }

                _scoreIndex = 0;
                _infoIndex = 0;
            }
        }

        private static readonly SearchWorkspace PrimaryWorkspace = new SearchWorkspace();
        private static int _workspaceInUse = 0;

        // 診断・比較用メトリクス
        public static long ReusedSearchCallCount = 0;
        public static long ReentrantFallbackCount = 0;
        public static long ReusedPathScoreCount = 0;
        public static long ReusedSearchInfoCount = 0;

        public static void ClearCache()
        {
            if (Interlocked.CompareExchange(ref _workspaceInUse, 1, 0) == 0)
            {
                try
                {
                    PrimaryWorkspace.ResetState();
                }
                finally
                {
                    Interlocked.Exchange(ref _workspaceInUse, 0);
                }
            }
        }

        public static void ResetStageMetrics()
        {
            ReusedSearchCallCount = 0;
            ReentrantFallbackCount = 0;
            ReusedPathScoreCount = 0;
            ReusedSearchInfoCount = 0;
        }

        private static PathResult ExecuteSearchWithWorkspace(SearchWorkspace ws, MapNode startPoint, MapNode endPoint, bool isRabbit)
        {
            ws.ResetState();
            long localReusedScores = 0;
            long localReusedInfos = 0;

            try
            {
                bool reused;
                GraphAstar.PathScore startScore = ws.RentPathScore(startPoint, 0f, 0f, out reused);
                if (reused) localReusedScores++;
                ws.OpenQueue.Enqueue(startScore);

                Vector2 endPos = endPoint.GetPosition();

                while (ws.OpenQueue.Count > 0)
                {
                    GraphAstar.PathScore current = ws.OpenQueue.Dequeue();
                    if (current.node == endPoint)
                    {
                        MapNode traceNode = current.node;
                        float totalCost = 0f;

                        while (true)
                        {
                            GraphAstar.SearchInfo info;
                            if (!ws.BestCostMap.TryGetValue(traceNode, out info))
                            {
                                break;
                            }
                            MapEdge edge = info.edge;
                            traceNode = edge.ConnectedNode(traceNode);
                            ws.EdgeBuffer.Add(edge);
                            totalCost += edge.cost;
                            if (edge.node1 == traceNode)
                            {
                                ws.DirectionBuffer.Add(EdgeDirection.FORWARD);
                            }
                            else
                            {
                                ws.DirectionBuffer.Add(EdgeDirection.BACKWARD);
                            }
                        }

                        int edgeCount = ws.EdgeBuffer.Count;
                        if (edgeCount == 0)
                        {
                            return new PathResult(EmptyEdges, EmptyDirections, totalCost);
                        }

                        // ArrayList.Reverse() + ToArray(Type) と完全に同一の逆順配列を直接構築（ボクシングゼロ）
                        MapEdge[] resultEdges = new MapEdge[edgeCount];
                        EdgeDirection[] resultDirs = new EdgeDirection[edgeCount];
                        for (int i = 0; i < edgeCount; i++)
                        {
                            int revIdx = edgeCount - 1 - i;
                            resultEdges[i] = ws.EdgeBuffer[revIdx];
                            resultDirs[i] = ws.DirectionBuffer[revIdx];
                        }

                        return new PathResult(resultEdges, resultDirs, totalCost);
                    }

                    ws.ClosedSet.Add(current.node);
                    MapEdge[] edges = current.node.GetEdges();
                    for (int i = 0; i < edges.Length; i++)
                    {
                        MapEdge mapEdge = edges[i];
                        MapNode neighbor = mapEdge.ConnectedNode(current.node);
                        if (neighbor == null || !neighbor.activate || ws.ClosedSet.Contains(neighbor) || (isRabbit && current.node.rabbitUnpassable))
                        {
                            continue;
                        }

                        float newCost = current.cost + mapEdge.cost;
                        float h = GraphAstar.ComputeHeuristic(neighbor.GetPosition(), endPos);

                        GraphAstar.SearchInfo existingInfo;
                        bool hasExisting = ws.BestCostMap.TryGetValue(neighbor, out existingInfo);
                        if (!hasExisting || existingInfo.cost > newCost)
                        {
                            GraphAstar.PathScore nextScore = ws.RentPathScore(neighbor, newCost, h, out reused);
                            if (reused) localReusedScores++;
                            ws.OpenQueue.Enqueue(nextScore);

                            if (!hasExisting)
                            {
                                GraphAstar.SearchInfo newInfo = ws.RentSearchInfo(newCost, mapEdge, out reused);
                                if (reused) localReusedInfos++;
                                ws.BestCostMap.Add(neighbor, newInfo);
                            }
                            else
                            {
                                existingInfo.cost = newCost;
                                existingInfo.edge = mapEdge;
                            }
                        }
                    }
                }

                return new PathResult(EmptyEdges, EmptyDirections, 0f);
            }
            finally
            {
                ReusedPathScoreCount += localReusedScores;
                ReusedSearchInfoCount += localReusedInfos;
                ws.ResetState();
            }
        }

        [HarmonyPatch(typeof(GraphAstar), "SearchPath")]
        public static class GraphAstar_SearchPath_ReusePatch
        {
            [HarmonyPrefix]
            public static bool Prefix(MapNode startPoint, MapNode endPoint, bool isRabbit, ref PathResult __result)
            {
                // 排他取得できた場合は共有 SearchWorkspace を再利用、再帰・競合時は独立インスタンスで安全に処理
                if (Interlocked.CompareExchange(ref _workspaceInUse, 1, 0) == 0)
                {
                    try
                    {
                        ReusedSearchCallCount++;
                        __result = ExecuteSearchWithWorkspace(PrimaryWorkspace, startPoint, endPoint, isRabbit);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _workspaceInUse, 0);
                    }
                }
                else
                {
                    ReentrantFallbackCount++;
                    SearchWorkspace tempWorkspace = new SearchWorkspace();
                    __result = ExecuteSearchWithWorkspace(tempWorkspace, startPoint, endPoint, isRabbit);
                }

                return false;
            }
        }
    }
}
