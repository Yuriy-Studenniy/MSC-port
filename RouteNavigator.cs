using UnityEngine;
using System;
using System.Collections.Generic;

namespace V_Companion
{
    public class RouteNavigator
    {
        public class NavStep
        {
            public RouteGraph.Edge Edge;
            public int FromNode;
            public int ToNode;
            public int StartPointIndex = 0; // Индекс в точках, уже развернутых по направлению движения
            public override string ToString() =>
                Edge.Route.Name + " N" + FromNode + "→N" + ToNode;
        }

        private RouteGraph graph;

        // A* state must retain the incoming directed edge. Reaching the same
        // junction from two different roads can lead to different next-turn costs.
        private struct SearchState : IEquatable<SearchState>
        {
            public int NodeId;
            public int IncomingArcId;

            public bool Equals(SearchState other)
            {
                return NodeId == other.NodeId && IncomingArcId == other.IncomingArcId;
            }

            public override bool Equals(object obj)
            {
                return obj is SearchState && Equals((SearchState)obj);
            }

            public override int GetHashCode()
            {
                unchecked { return (NodeId * 397) ^ IncomingArcId; }
            }
        }

        private class DirectedArc
        {
            public int Id;
            public RouteGraph.Edge Edge;
            public int FromNode;
            public int ToNode;
            public bool Forward;
            public float BaseCost;
        }

        // Relative route weights. These are preferences, not literal travel times.
        private const float HIGHWAY_COST_MULTIPLIER = 0.85f;
        private const float CONNECTOR_COST_MULTIPLIER = 1.00f;
        private const float GRAVEL_COST_MULTIPLIER = 1.15f;

        // A 90-degree turn costs 12 equivalent metres; a 180-degree turn 48.
        // The quadratic curve avoids the old hard jumps at 30/90/150 degrees.
        private const float TURN_COST_AT_90_DEGREES = 12f;

        public RouteNavigator(RouteGraph g) { graph = g; }

        // Уникальные destinations (X и Y из X_to_Y)
        public List<string> GetAllDestinations()
        {
            var set = new HashSet<string>();
            foreach (var r in graph.Routes)
            {
                // Убираем суффиксы нарезки (_s0) и направления (_left, _right)
                string cleanName = System.Text.RegularExpressions.Regex.Replace(r.Name, @"(_s\d+|_left|_right)$", "");

                int idx = cleanName.IndexOf("_to_");
                if (idx < 0) continue;
                string a = cleanName.Substring(0, idx);
                string b = cleanName.Substring(idx + 4);
                if (!string.IsNullOrEmpty(a)) set.Add(a);
                if (!string.IsNullOrEmpty(b)) set.Add(b);
            }
            var list = new List<string>(set);
            list.Sort();
            return list;
        }

        // Узел, где находится destination
        public int FindDestinationNode(string dest)
        {
            List<RouteGraph.Route> incoming = new List<RouteGraph.Route>();
            List<RouteGraph.Route> outgoing = new List<RouteGraph.Route>();

            foreach (var r in graph.Routes)
            {
                // Очищаем имя от _s0, _s1, _s2 для правильного поиска
                string cleanName = System.Text.RegularExpressions.Regex.Replace(r.Name, @"_s\d+$", "");

                if (cleanName.EndsWith("_to_" + dest)) incoming.Add(r);
                if (cleanName.StartsWith(dest + "_to_")) outgoing.Add(r);
            }

            // 1. Если есть входящие маршруты (например, ringL_to_factory)
            if (incoming.Count > 0)
            {
                // Ищем идеальное совпадение: конец входящего = начало исходящего (factory_ringL)
                foreach (var inc in incoming)
                {
                    foreach (var outg in outgoing)
                    {
                        if (inc.EndNode == outg.StartNode) return inc.EndNode;
                    }
                }

                // Если идеального нет (например, для gravel), берем самый длинный сегмент (обычно это _s2 или _s3)
                RouteGraph.Route lastInc = incoming[0];
                foreach (var inc in incoming)
                {
                    if (inc.Name.Length > lastInc.Name.Length) lastInc = inc;
                }
                return lastInc.EndNode;
            }

            // 2. Если входящих нет (например, для home), ищем по исходящим
            if (outgoing.Count > 0)
            {
                return outgoing[0].StartNode;
            }

            return -1;
        }

        public int FindNearestNode(Vector3 pos)
        {
            int best = -1; float bestDist = float.MaxValue;
            foreach (var n in graph.Nodes)
            {
                float d = Vector3.Distance(n.Pos, pos);
                if (d < bestDist) { bestDist = d; best = n.Id; }
            }
            return best;
        }

        // A*
        // Проверяем, является ли дорога односторонней
        private bool IsOneWayRoute(RouteGraph.Route route)
        {
            string n = route.Name.ToLower();
            // Все дороги, в названии которых есть "ring" (кольца трассы и все съезды)
            // считаются односторонними.
            return n.Contains("ring");
        }

        // Ищет ближайшую точку на дороге и строит маршрут от неё
        public List<NavStep> GetPathFromPosition(Vector3 pos, Vector3 fwd, int goalNode, out int startNodeId)
        {
            RouteGraph.Edge bestEdge = null;
            float bestDist = float.MaxValue;
            int bestPtIdx = -1;

            // 1. Ищем ближайшую точку на всех маршрутах
            // 1. Ищем ближайшую точку на всех маршрутах (С УЧЕТОМ НАПРАВЛЕНИЯ!)
            float bestScore = float.MaxValue;
            foreach (var e in graph.Edges)
            {
                var pts = e.Route.Points;
                for (int i = 0; i < pts.Count; i++)
                {
                    float d = Vector3.Distance(pos, pts[i]);

                    // Штрафуем точки, которые находятся ПОЗАДИ машины
                    Vector3 toPt = pts[i] - pos;
                    toPt.y = 0f;
                    float dot = toPt.sqrMagnitude > 0.1f ? Vector3.Dot(fwd, toPt.normalized) : 1f;

                    float score = d;
                    if (dot < -0.2f) score += 50f; // Если точка сзади — добавляем 50 метров штрафа

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestDist = d; // Сохраняем реальную дистанцию для логов
                        bestEdge = e;
                        bestPtIdx = i;
                    }
                }
            }

            if (bestEdge == null || bestPtIdx < 0)
            {
                Debug.Log("[V] ❌ В графе нет ни одного маршрута!");
                startNodeId = -1;
                return null;
            }

            Debug.Log("[V] 📍 Ближайшая точка: " + bestEdge.Route.Name + " (дистанция " + bestDist.ToString("F1") + "м)");

            if (bestDist > 30f)
            {
                Debug.LogWarning("[V] ⚠️ Ты находишься далеко от дороги (" + bestDist.ToString("F1") + "м). V придется ехать через поле.");
            }

            // 2. Определяем направление движения по этому сегменту
            bool forward;
            if (IsOneWayRoute(bestEdge.Route))
            {
                forward = true; // Односторонняя — всегда от FromNode к ToNode
            }
            else
            {
                Vector3 edgeDir;
                if (bestPtIdx < bestEdge.Route.Points.Count - 1)
                    edgeDir = (bestEdge.Route.Points[bestPtIdx + 1] - bestEdge.Route.Points[bestPtIdx]).normalized;
                else
                    edgeDir = (bestEdge.Route.Points[bestPtIdx] - bestEdge.Route.Points[bestPtIdx - 1]).normalized;

                forward = Vector3.Dot(fwd, edgeDir) >= 0;
            }

            // 3. Определяем узел, к которому едем
            int nodeToGo;
            NavStep startStep;

            if (forward)
            {
                nodeToGo = bestEdge.ToNode;
                startStep = new NavStep { Edge = bestEdge, FromNode = bestEdge.FromNode, ToNode = bestEdge.ToNode, StartPointIndex = bestPtIdx };
            }
            else
            {
                nodeToGo = bestEdge.FromNode;
                startStep = new NavStep { Edge = bestEdge, FromNode = bestEdge.ToNode, ToNode = bestEdge.FromNode, StartPointIndex = bestEdge.Route.Points.Count - 1 - bestPtIdx };
            }

            startNodeId = nodeToGo;

            // Если узел, к которому мы едем, и есть наша цель
            if (nodeToGo == goalNode)
            {
                Debug.Log("[V] ✅ Мы уже на финишном отрезке! Едем прямо к цели.");
                var finalPath = new List<NavStep>();
                finalPath.Add(startStep);
                return finalPath;
            }

            var path = FindPath(nodeToGo, goalNode, startStep);

            // 4. Если пути нет (например, едем не в ту сторону по односторонней или уперлись в тупик)
            if (path == null && !IsOneWayRoute(bestEdge.Route))
            {
                Debug.Log("[V] ⚠️ Не удалось найти путь вперед. Пробую развернуться и поехать в другую сторону...");
                // Пробуем противоположное направление
                if (forward)
                {
                    nodeToGo = bestEdge.FromNode;
                    startStep = new NavStep { Edge = bestEdge, FromNode = bestEdge.ToNode, ToNode = bestEdge.FromNode, StartPointIndex = bestEdge.Route.Points.Count - 1 - bestPtIdx };
                }
                else
                {
                    nodeToGo = bestEdge.ToNode;
                    startStep = new NavStep { Edge = bestEdge, FromNode = bestEdge.FromNode, ToNode = bestEdge.ToNode, StartPointIndex = bestPtIdx };
                }
                startNodeId = nodeToGo;

                if (nodeToGo == goalNode)
                {
                    var finalPath = new List<NavStep>();
                    finalPath.Add(startStep);
                    return finalPath;
                }

                path = FindPath(nodeToGo, goalNode, startStep);
            }

            if (path == null)
            {
                Debug.Log("[V] ❌ Тупик! От узла N" + nodeToGo + " невозможно доехать до N" + goalNode + ". Возможно, дорога перекрыта.");
                return null;
            }

            path.Insert(0, startStep);
            return path;
        }

        // Public entry point retained for callers that do not have an incoming segment.
        public List<NavStep> FindPath(int startNode, int goalNode)
        {
            return FindPath(startNode, goalNode, null);
        }

        // A* over directed-edge states. The incoming segment is part of the state,
        // otherwise two arrivals at the same node would incorrectly share one score.
        private List<NavStep> FindPath(int startNode, int goalNode, NavStep incomingStep)
        {
            if (graph == null || startNode < 0 || goalNode < 0 ||
                startNode >= graph.Nodes.Count || goalNode >= graph.Nodes.Count)
                return null;

            if (startNode == goalNode)
                return new List<NavStep>();

            var neighbors = new Dictionary<int, List<DirectedArc>>();
            var arcsById = new Dictionary<int, DirectedArc>();
            int nextArcId = 0;

            foreach (var edge in graph.Edges)
            {
                if (edge == null || edge.Route == null || edge.Route.Points == null ||
                    edge.Route.Points.Count < 2)
                    continue;

                float routeLength = GetRouteLength(edge.Route);
                float classMultiplier = GetRouteClassMultiplier(edge.Route.Name);

                // The graph groups nearby endpoints into one logical node but keeps
                // their physical coordinates. Include those small connector gaps in
                // the edge cost; this also keeps the Euclidean heuristic admissible.
                AddDirectedArc(edge, true, routeLength, classMultiplier,
                    neighbors, arcsById, ref nextArcId);

                if (!IsOneWayRoute(edge.Route) && edge.FromNode != edge.ToNode)
                {
                    AddDirectedArc(edge, false, routeLength, classMultiplier,
                        neighbors, arcsById, ref nextArcId);
                }
            }

            // Synthetic incoming arc: it is not traversable, it only supplies the
            // correct direction for scoring the first turn after the current segment.
            const int INITIAL_INCOMING_ARC_ID = -2;
            int initialIncomingId = -1;
            if (incomingStep != null && incomingStep.Edge != null)
            {
                bool incomingForward = incomingStep.FromNode == incomingStep.Edge.FromNode;
                var initialArc = new DirectedArc
                {
                    Id = INITIAL_INCOMING_ARC_ID,
                    Edge = incomingStep.Edge,
                    FromNode = incomingStep.FromNode,
                    ToNode = incomingStep.ToNode,
                    Forward = incomingForward,
                    BaseCost = 0f
                };
                arcsById[INITIAL_INCOMING_ARC_ID] = initialArc;
                initialIncomingId = INITIAL_INCOMING_ARC_ID;
            }

            var startState = new SearchState
            {
                NodeId = startNode,
                IncomingArcId = initialIncomingId
            };

            var open = new List<SearchState> { startState };
            var cameFrom = new Dictionary<SearchState, SearchState>();
            var cameFromStep = new Dictionary<SearchState, NavStep>();
            var gScore = new Dictionary<SearchState, float>();
            var fScore = new Dictionary<SearchState, float>();

            gScore[startState] = 0f;
            fScore[startState] = GetHeuristic(startNode, goalNode);

            int expansions = 0;
            const int MAX_EXPANSIONS = 20000;

            while (open.Count > 0 && expansions++ < MAX_EXPANSIONS)
            {
                int bestIndex = 0;
                float bestF = GetScore(fScore, open[0]);
                for (int i = 1; i < open.Count; i++)
                {
                    float candidateF = GetScore(fScore, open[i]);
                    if (candidateF < bestF)
                    {
                        bestF = candidateF;
                        bestIndex = i;
                    }
                }

                SearchState current = open[bestIndex];
                open.RemoveAt(bestIndex);

                if (current.NodeId == goalNode)
                    return ReconstructPath(startState, current, cameFrom, cameFromStep);

                List<DirectedArc> outgoing;
                if (!neighbors.TryGetValue(current.NodeId, out outgoing))
                    continue;

                DirectedArc previousArc = null;
                if (current.IncomingArcId >= 0 || current.IncomingArcId == INITIAL_INCOMING_ARC_ID)
                    arcsById.TryGetValue(current.IncomingArcId, out previousArc);

                foreach (var arc in outgoing)
                {
                    // Do not immediately reverse over the very same road segment.
                    // A U-turn onto a different segment remains possible, but costly.
                    if (previousArc != null && previousArc.Edge == arc.Edge &&
                        previousArc.Forward != arc.Forward)
                        continue;

                    float turnCost = 0f;
                    if (previousArc != null)
                    {
                        Vector3 incomingDirection = GetArcIncomingDirection(previousArc);
                        Vector3 outgoingDirection = GetArcOutgoingDirection(arc);
                        if (incomingDirection.sqrMagnitude > 0.001f &&
                            outgoingDirection.sqrMagnitude > 0.001f)
                        {
                            float turnAngle = Vector3.Angle(incomingDirection, outgoingDirection);
                            float normalized = turnAngle / 90f;
                            turnCost = normalized * normalized * TURN_COST_AT_90_DEGREES;
                        }
                    }

                    float tentativeG = GetScore(gScore, current) + arc.BaseCost + turnCost;
                    var nextState = new SearchState
                    {
                        NodeId = arc.ToNode,
                        IncomingArcId = arc.Id
                    };

                    if (tentativeG < GetScore(gScore, nextState))
                    {
                        cameFrom[nextState] = current;
                        cameFromStep[nextState] = new NavStep
                        {
                            Edge = arc.Edge,
                            FromNode = arc.FromNode,
                            ToNode = arc.ToNode
                        };
                        gScore[nextState] = tentativeG;
                        fScore[nextState] = tentativeG + GetHeuristic(arc.ToNode, goalNode);

                        if (!open.Contains(nextState))
                            open.Add(nextState);
                    }
                }
            }

            if (expansions >= MAX_EXPANSIONS)
                Debug.LogWarning("[V][NAV] Поиск пути достиг лимита состояний (" + MAX_EXPANSIONS + ")");

            return null;
        }

        private void AddDirectedArc(
            RouteGraph.Edge edge,
            bool forward,
            float routeLength,
            float classMultiplier,
            Dictionary<int, List<DirectedArc>> neighbors,
            Dictionary<int, DirectedArc> arcsById,
            ref int nextArcId)
        {
            int fromNode = forward ? edge.FromNode : edge.ToNode;
            int toNode = forward ? edge.ToNode : edge.FromNode;
            if (fromNode < 0 || toNode < 0 ||
                fromNode >= graph.Nodes.Count || toNode >= graph.Nodes.Count)
                return;

            Vector3 routeStart = forward ? edge.Route.First : edge.Route.Last;
            Vector3 routeEnd = forward ? edge.Route.Last : edge.Route.First;
            float endpointLinks =
                Vector3.Distance(graph.Nodes[fromNode].Pos, routeStart) +
                Vector3.Distance(graph.Nodes[toNode].Pos, routeEnd);

            var arc = new DirectedArc
            {
                Id = nextArcId++,
                Edge = edge,
                FromNode = fromNode,
                ToNode = toNode,
                Forward = forward,
                BaseCost = (routeLength + endpointLinks) * classMultiplier
            };

            List<DirectedArc> list;
            if (!neighbors.TryGetValue(fromNode, out list))
            {
                list = new List<DirectedArc>();
                neighbors[fromNode] = list;
            }
            list.Add(arc);
            arcsById[arc.Id] = arc;
        }

        private static float GetRouteLength(RouteGraph.Route route)
        {
            float length = 0f;
            for (int i = 1; i < route.Points.Count; i++)
                length += Vector3.Distance(route.Points[i - 1], route.Points[i]);
            return length;
        }

        private static float GetRouteClassMultiplier(string routeName)
        {
            string name = string.IsNullOrEmpty(routeName) ? "" : routeName.ToLowerInvariant();

            // The highway ring has a soft preference, not an absolute priority.
            if (name.Contains("highway") || name.Contains("ring"))
                return HIGHWAY_COST_MULTIPLIER;

            // Ramps/connectors are neither gravel nor full highway. In the current
            // route set they are named *_to_*, *_trassa, or explicitly as ramps.
            if (name.Contains("_to_") || name.Contains("trassa") ||
                name.Contains("ramp") || name.Contains("connector") || name.Contains("exit"))
                return CONNECTOR_COST_MULTIPLIER;

            return GRAVEL_COST_MULTIPLIER;
        }

        private float GetHeuristic(int fromNode, int goalNode)
        {
            // Every traversable edge costs at least its geometric displacement
            // multiplied by HIGHWAY_COST_MULTIPLIER (the lowest road weight).
            return Vector3.Distance(graph.Nodes[fromNode].Pos, graph.Nodes[goalNode].Pos) *
                   HIGHWAY_COST_MULTIPLIER;
        }

        private static Vector3 GetArcIncomingDirection(DirectedArc arc)
        {
            return FlattenDirection(arc.Forward ? arc.Edge.EndDir : -arc.Edge.StartDir);
        }

        private static Vector3 GetArcOutgoingDirection(DirectedArc arc)
        {
            return FlattenDirection(arc.Forward ? arc.Edge.StartDir : -arc.Edge.EndDir);
        }

        private static Vector3 FlattenDirection(Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.001f)
                direction.Normalize();
            return direction;
        }

        private static float GetScore(Dictionary<SearchState, float> scores, SearchState state)
        {
            float value;
            return scores.TryGetValue(state, out value) ? value : float.MaxValue;
        }

        private static List<NavStep> ReconstructPath(
            SearchState startState,
            SearchState goalState,
            Dictionary<SearchState, SearchState> cameFrom,
            Dictionary<SearchState, NavStep> cameFromStep)
        {
            var path = new List<NavStep>();
            SearchState current = goalState;

            while (!current.Equals(startState))
            {
                NavStep step;
                SearchState previous;
                if (!cameFromStep.TryGetValue(current, out step) ||
                    !cameFrom.TryGetValue(current, out previous))
                    return null;

                path.Add(step);
                current = previous;
            }

            path.Reverse();
            return path;
        }
    }

    /// <summary>
    /// Builds a smooth connector between two consecutive graph segments.
    /// The route selected by A* is never changed: this class only plans how
    /// the vehicle transfers from the end of one selected segment to the next.
    /// </summary>
    public class RouteTransitionPlanner
    {
        public const int CANDIDATE_COUNT = 6;

        private const int CURVE_SAMPLE_COUNT = 12;
        private const int FUTURE_POINTS_TO_CHECK = 6;
        private const float TANGENT_LOOKAHEAD = 12f;
        private const float MAX_HANDLE_LENGTH = 10f;
        private const float MIN_COMFORT_RADIUS = 5f;
        private const float ROAD_CORRIDOR_SOFT_LIMIT = 7f;

        public class TransitionPlan
        {
            // Index in the next segment's travel-oriented point list.
            public int NextStartPointIndex;

            // Points from just after the current segment end through the
            // selected point on the next segment (the selected point is included).
            public List<Vector3> ConnectorPoints = new List<Vector3>();

            public float Score;
            public int CandidateIndex;
            public float ConnectorLength;
        }

        /// <summary>
        /// Plans all transitions before the car starts moving.
        /// The returned list has path.Count - 1 entries.
        /// </summary>
        public List<TransitionPlan> BuildPlans(List<RouteNavigator.NavStep> path)
        {
            var plans = new List<TransitionPlan>();
            if (path == null || path.Count < 2) return plans;

            for (int i = 0; i < path.Count - 1; i++)
            {
                TransitionPlan plan = BuildPlan(path[i], path[i + 1]);
                plans.Add(plan);

                Debug.Log("[V][TRANSITION] " + path[i].Edge.Route.Name + " -> " +
                          path[i + 1].Edge.Route.Name +
                          ": candidate " + (plan.CandidateIndex + 1) + "/" + CANDIDATE_COUNT +
                          ", score=" + plan.Score.ToString("F1") +
                          ", connector=" + plan.ConnectorLength.ToString("F1") + "m");
            }

            return plans;
        }

        private TransitionPlan BuildPlan(RouteNavigator.NavStep currentStep, RouteNavigator.NavStep nextStep)
        {
            List<Vector3> currentPoints = GetTravelPoints(currentStep);
            List<Vector3> nextPoints = GetTravelPoints(nextStep);

            var fallback = new TransitionPlan();
            fallback.NextStartPointIndex = 0;
            fallback.CandidateIndex = 0;

            if (currentPoints.Count < 2 || nextPoints.Count < 2)
            {
                if (nextPoints.Count > 0)
                {
                    fallback.ConnectorPoints.Add(nextPoints[0]);
                    fallback.ConnectorLength = Vector3.Distance(
                        currentPoints.Count > 0 ? currentPoints[currentPoints.Count - 1] : nextPoints[0],
                        nextPoints[0]);
                }
                return fallback;
            }

            Vector3 start = currentPoints[currentPoints.Count - 1];
            Vector3 incomingDirection = GetIncomingTangent(currentPoints);

            float bestScore = float.MaxValue;
            TransitionPlan bestPlan = null;
            // The segment's final endpoint belongs to the next graph transition;
            // do not choose it as an entry point and accidentally skip this segment.
            int candidateLimit = Mathf.Min(CANDIDATE_COUNT, Mathf.Max(1, nextPoints.Count - 1));

            for (int candidate = 0; candidate < candidateLimit; candidate++)
            {
                Vector3 target = nextPoints[candidate];
                Vector3 outgoingDirection = GetOutgoingTangent(nextPoints, candidate);

                List<Vector3> connector = BuildBezierConnector(
                    start, target, incomingDirection, outgoingDirection);

                float connectorLength = GetPolylineLength(start, connector);
                float skippedRouteLength = GetPolylineLength(nextPoints, 0, candidate);
                float maxDeviation = GetMaxCorridorDeviation(
                    connector, currentPoints, nextPoints, candidate);

                List<Vector3> evaluationPath = BuildEvaluationPath(
                    currentPoints, connector, nextPoints, candidate);

                float totalAbsTurn;
                float netSignedTurn;
                float maxTurn;
                float minRadius;
                CalculateTurnMetrics(evaluationPath,
                    out totalAbsTurn, out netSignedTurn, out maxTurn, out minRadius);

                // A right-then-left S-bend has a large total absolute turn,
                // while its net turn is small. Penalize that reversal strongly.
                float reversalPenalty = Mathf.Max(0f,
                    totalAbsTurn - Mathf.Abs(netSignedTurn));

                float chordLength = FlatDistance(start, target);
                float detourRatio = chordLength > 0.5f ? connectorLength / chordLength : 1f;
                float detourPenalty = Mathf.Max(0f, detourRatio - 1.30f) * 35f;

                float radiusPenalty = 0f;
                if (minRadius < MIN_COMFORT_RADIUS)
                {
                    float radiusError = MIN_COMFORT_RADIUS - minRadius;
                    radiusPenalty = radiusError * radiusError * 8f;
                }

                float deviationPenalty = 0f;
                if (maxDeviation > ROAD_CORRIDOR_SOFT_LIMIT)
                {
                    float deviationError = maxDeviation - ROAD_CORRIDOR_SOFT_LIMIT;
                    deviationPenalty = deviationError * deviationError * 3f;
                }

                // Shorter transfers are preferable, but not at the expense of
                // a direction reversal or an unnecessarily tight curve.
                float score = connectorLength * 0.65f +
                              skippedRouteLength * 0.30f +
                              totalAbsTurn * 0.35f +
                              reversalPenalty * 3.0f +
                              maxTurn * 1.5f +
                              detourPenalty +
                              radiusPenalty +
                              deviationPenalty;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestPlan = new TransitionPlan();
                    bestPlan.NextStartPointIndex = candidate;
                    bestPlan.CandidateIndex = candidate;
                    bestPlan.ConnectorPoints = connector;
                    bestPlan.Score = score;
                    bestPlan.ConnectorLength = connectorLength;
                }

                Debug.Log("[V][TRANSITION]   candidate " + (candidate + 1) +
                          ": score=" + score.ToString("F1") +
                          ", turn=" + totalAbsTurn.ToString("F0") + "/" +
                          netSignedTurn.ToString("F0") +
                          ", radius=" + minRadius.ToString("F1") +
                          ", deviation=" + maxDeviation.ToString("F1"));
            }

            return bestPlan ?? fallback;
        }

        private List<Vector3> GetTravelPoints(RouteNavigator.NavStep step)
        {
            var result = new List<Vector3>();
            if (step == null || step.Edge == null || step.Edge.Route == null ||
                step.Edge.Route.Points == null) return result;

            List<Vector3> source = step.Edge.Route.Points;
            if (step.FromNode == step.Edge.FromNode)
            {
                result.AddRange(source);
            }
            else
            {
                for (int i = source.Count - 1; i >= 0; i--)
                    result.Add(source[i]);
            }
            return result;
        }

        private Vector3 GetIncomingTangent(List<Vector3> points)
        {
            int last = points.Count - 1;
            float distance = 0f;
            for (int i = last - 1; i >= 0; i--)
            {
                distance += FlatDistance(points[i], points[i + 1]);
                if (distance >= TANGENT_LOOKAHEAD * 0.65f)
                {
                    Vector3 dir = points[last] - points[i];
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 0.01f) return dir.normalized;
                }
            }

            Vector3 fallback = points[last] - points[0];
            fallback.y = 0f;
            return fallback.sqrMagnitude > 0.01f ? fallback.normalized : Vector3.forward;
        }

        private Vector3 GetOutgoingTangent(List<Vector3> points, int startIndex)
        {
            if (startIndex >= points.Count - 1)
            {
                Vector3 lastDirection = points[points.Count - 1] - points[points.Count - 2];
                lastDirection.y = 0f;
                return lastDirection.sqrMagnitude > 0.01f ? lastDirection.normalized : Vector3.forward;
            }

            int endIndex = startIndex + 1;
            float walked = 0f;
            for (int i = startIndex + 1; i < points.Count; i++)
            {
                walked += FlatDistance(points[i - 1], points[i]);
                endIndex = i;
                if (walked >= TANGENT_LOOKAHEAD) break;
            }

            Vector3 dir = points[endIndex] - points[startIndex];
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f) return dir.normalized;

            dir = points[points.Count - 1] - points[startIndex];
            dir.y = 0f;
            return dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward;
        }

        private List<Vector3> BuildBezierConnector(
            Vector3 start, Vector3 end, Vector3 incoming, Vector3 outgoing)
        {
            var result = new List<Vector3>();
            incoming.y = 0f;
            outgoing.y = 0f;

            if (incoming.sqrMagnitude < 0.01f) incoming = (end - start).normalized;
            if (outgoing.sqrMagnitude < 0.01f) outgoing = incoming;
            incoming.Normalize();
            outgoing.Normalize();

            float chord = FlatDistance(start, end);
            if (chord < 0.1f)
            {
                // The graph segments already share the same physical point.
                // Returning the endpoint here would append a duplicate to the
                // current segment; the playback index can then get stuck on
                // the first of two identical points and never finish the segment.
                return result;
            }

            float handle = Mathf.Min(MAX_HANDLE_LENGTH, chord * 0.45f);

            Vector3 control1 = start + incoming * handle;
            Vector3 control2 = end - outgoing * handle;
            control1.y = Mathf.Lerp(start.y, end.y, 0.30f);
            control2.y = Mathf.Lerp(start.y, end.y, 0.70f);

            for (int i = 1; i <= CURVE_SAMPLE_COUNT; i++)
            {
                float t = (float)i / (float)CURVE_SAMPLE_COUNT;
                float u = 1f - t;
                Vector3 point =
                    (u * u * u * start) +
                    (3f * u * u * t * control1) +
                    (3f * u * t * t * control2) +
                    (t * t * t * end);
                result.Add(point);
            }

            return result;
        }

        private List<Vector3> BuildEvaluationPath(
            List<Vector3> currentPoints,
            List<Vector3> connector,
            List<Vector3> nextPoints,
            int candidateIndex)
        {
            var result = new List<Vector3>();

            int currentStart = Mathf.Max(0, currentPoints.Count - 4);
            for (int i = currentStart; i < currentPoints.Count; i++)
                result.Add(currentPoints[i]);

            for (int i = 0; i < connector.Count; i++)
                result.Add(connector[i]);

            int futureEnd = Mathf.Min(nextPoints.Count - 1,
                candidateIndex + FUTURE_POINTS_TO_CHECK);
            for (int i = candidateIndex + 1; i <= futureEnd; i++)
                result.Add(nextPoints[i]);

            return result;
        }

        private void CalculateTurnMetrics(
            List<Vector3> points,
            out float totalAbsTurn,
            out float netSignedTurn,
            out float maxTurn,
            out float minRadius)
        {
            totalAbsTurn = 0f;
            netSignedTurn = 0f;
            maxTurn = 0f;
            minRadius = 99999f;

            Vector3 previousDirection = Vector3.zero;
            bool hasPreviousDirection = false;

            for (int i = 1; i < points.Count; i++)
            {
                Vector3 direction = points[i] - points[i - 1];
                direction.y = 0f;
                if (direction.magnitude < 0.35f) continue;
                direction.Normalize();

                if (hasPreviousDirection)
                {
                    float signed = SignedAngle(previousDirection, direction);
                    float absolute = Mathf.Abs(signed);
                    totalAbsTurn += absolute;
                    netSignedTurn += signed;
                    if (absolute > maxTurn) maxTurn = absolute;
                }

                previousDirection = direction;
                hasPreviousDirection = true;
            }

            for (int i = 1; i < points.Count - 1; i++)
            {
                float a = FlatDistance(points[i - 1], points[i]);
                float b = FlatDistance(points[i], points[i + 1]);
                float c = FlatDistance(points[i - 1], points[i + 1]);
                if (a < 0.35f || b < 0.35f || c < 0.35f) continue;

                float twiceArea = Mathf.Abs(CrossXZ(
                    points[i] - points[i - 1], points[i + 1] - points[i - 1]));
                if (twiceArea < 0.01f) continue;

                // R = (a*b*c)/(4*triangleArea); twiceArea = 2*area.
                float radius = (a * b * c) / (2f * twiceArea);
                if (radius < minRadius) minRadius = radius;
            }

            if (minRadius == 99999f) minRadius = 9999f;
        }

        private float GetMaxCorridorDeviation(
            List<Vector3> connector,
            List<Vector3> currentPoints,
            List<Vector3> nextPoints,
            int candidateIndex)
        {
            float maxDeviation = 0f;
            int currentStart = Mathf.Max(0, currentPoints.Count - 5);

            for (int p = 0; p < connector.Count - 1; p++)
            {
                Vector3 sample = connector[p];
                float nearest = float.MaxValue;

                for (int i = currentStart; i < currentPoints.Count - 1; i++)
                {
                    float d = DistancePointToSegment(sample, currentPoints[i], currentPoints[i + 1]);
                    if (d < nearest) nearest = d;
                }

                if (candidateIndex == 0)
                {
                    float d = FlatDistance(sample, nextPoints[0]);
                    if (d < nearest) nearest = d;
                }
                else
                {
                    for (int i = 0; i < candidateIndex; i++)
                    {
                        float d = DistancePointToSegment(sample, nextPoints[i], nextPoints[i + 1]);
                        if (d < nearest) nearest = d;
                    }
                }

                if (nearest > maxDeviation) maxDeviation = nearest;
            }

            return maxDeviation;
        }

        private float DistancePointToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 p = new Vector3(point.x, 0f, point.z);
            Vector3 av = new Vector3(a.x, 0f, a.z);
            Vector3 bv = new Vector3(b.x, 0f, b.z);
            Vector3 ab = bv - av;
            float denominator = ab.sqrMagnitude;
            if (denominator < 0.0001f) return Vector3.Distance(p, av);

            float t = Mathf.Clamp01(Vector3.Dot(p - av, ab) / denominator);
            Vector3 closest = av + ab * t;
            return Vector3.Distance(p, closest);
        }

        private float GetPolylineLength(Vector3 start, List<Vector3> points)
        {
            float length = 0f;
            Vector3 previous = start;
            for (int i = 0; i < points.Count; i++)
            {
                length += FlatDistance(previous, points[i]);
                previous = points[i];
            }
            return length;
        }

        private float GetPolylineLength(List<Vector3> points, int startIndex, int endIndex)
        {
            float length = 0f;
            int first = Mathf.Max(0, startIndex);
            int last = Mathf.Min(points.Count - 1, endIndex);
            for (int i = first + 1; i <= last; i++)
                length += FlatDistance(points[i - 1], points[i]);
            return length;
        }

        private float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private float CrossXZ(Vector3 a, Vector3 b)
        {
            return a.x * b.z - a.z * b.x;
        }

        private float SignedAngle(Vector3 from, Vector3 to)
        {
            float cross = CrossXZ(from, to);
            float dot = from.x * to.x + from.z * to.z;
            return Mathf.Atan2(cross, dot) * Mathf.Rad2Deg;
        }
    }
}
