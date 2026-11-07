using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;

namespace V_Companion
{
    public class RouteGraph
    {
        public class Route
        {
            public string Name;
            public string FilePath;
            public List<Vector3> Points;
            public int StartNode = -1;
            public int EndNode = -1;

            public Vector3 First => Points[0];
            public Vector3 Last => Points[Points.Count - 1];
            public int Count => Points.Count;
        }

        public class Node
        {
            public int Id;
            public Vector3 Pos;
            public List<RouteEnd> Ends = new List<RouteEnd>();
        }

        public class RouteEnd
        {
            public Route Route;
            public bool IsStart;
            public override string ToString() =>
                Route.Name + (IsStart ? ".start" : ".end");
        }

        public class Edge
        {
            public Route Route;
            public int FromNode;
            public int ToNode;
            public Vector3 StartDir; // Направление движения в начале
            public Vector3 EndDir;   // Направление движения в конце
        }

        public List<Route> Routes = new List<Route>();
        public List<Node> Nodes = new List<Node>();
        public List<Edge> Edges = new List<Edge>();

        public const float NODE_RADIUS = 15f;   // радиус поиска разреза
        public const float SAME_NODE = 20f;     // радиус объединения (увеличили до 20!)
        public const float MIN_SEG_DIST = 30f;  // минимальная дистанция между соседними разрезами

        // ═══ Загрузка ═══
        public void LoadAll(string dir)
        {
            Routes.Clear();
            Nodes.Clear();
            Edges.Clear();

            if (!Directory.Exists(dir))
            {
                Debug.Log("[V][GRAPH] ❌ Папка не найдена: " + dir);
                return;
            }

            var files = Directory.GetFiles(dir, "*.txt");
            var ci = System.Globalization.CultureInfo.InvariantCulture;

            foreach (var path in files)
            {
                string fname = Path.GetFileNameWithoutExtension(path);
                if (fname.StartsWith("_")) continue;

                try
                {
                    var route = new Route
                    {
                        Name = fname,
                        FilePath = path,
                        Points = new List<Vector3>()
                    };

                    foreach (var line in File.ReadAllLines(path))
                    {
                        if (string.IsNullOrEmpty(line) || line.Trim().Length == 0) continue;
                        var parts = line.Split(',');
                        if (parts.Length != 3) continue;
                        route.Points.Add(new Vector3(
                            float.Parse(parts[0], ci),
                            float.Parse(parts[1], ci),
                            float.Parse(parts[2], ci)));
                    }

                    if (route.Points.Count < 2) continue;
                    Routes.Add(route);
                }
                catch (Exception e)
                {
                    Debug.Log("[V][GRAPH] ❌ " + fname + ": " + e.Message);
                }
            }

            Debug.Log("[V][GRAPH] Загружено: " + Routes.Count + " оригинальных маршрутов");
        }

        // ═══ Главный метод ═══
        public void BuildGraph()
        {
            int origCount = Routes.Count;

            // 1. Разрезать маршруты в точках касания
            CutRoutesAtEnds();

            // 2. Построить узлы
            BuildNodes();

            // 3. Построить рёбра
            BuildEdges();

            Debug.Log("[V][GRAPH] ✅ Граф: " + origCount + " ориг → " +
                      Routes.Count + " сегментов, " + Nodes.Count + " узлов, " +
                      Edges.Count + " рёбер");
        }

        // ═══ Авторезание (правильное) ═══
        private void CutRoutesAtEnds()
        {
            if (Routes.Count == 0) return;

            // Собираем ВСЕ концы оригинальных маршрутов
            var allEnds = new List<Vector3>(Routes.Count * 2);
            foreach (var r in Routes)
            {
                allEnds.Add(r.First);
                allEnds.Add(r.Last);
            }

            // Обходим ОРИГИНАЛЫ, не трогаем новые куски
            var originals = new List<Route>(Routes);
            var toReplace = new Dictionary<Route, List<Route>>();

            foreach (var r in originals)
            {
                if (r.Points.Count < 10) continue;

                // ═══ НОВОЕ: ЗАПРЕЩАЕМ РЕЗАТЬ СЪЕЗДЫ (все, где есть ring, кроме highway) ═══
                string routeNameLower = r.Name.ToLower();
                if (routeNameLower.Contains("ring") && !routeNameLower.Contains("highway"))
                {
                    continue; // Оставляем съезд как единый цельный кусок
                }

                // Ищем точки разреза
                var cuts = new List<int>();
                Vector3 prevCutPos = Vector3.zero;
                bool hasPrevCut = false;

                for (int i = 0; i < r.Points.Count; i++)
                {
                    Vector3 pt = r.Points[i];

                    // Не резать близко к началу/концу самого маршрута
                    if (Vector3.Distance(pt, r.First) < SAME_NODE * 2) continue;
                    if (Vector3.Distance(pt, r.Last) < SAME_NODE * 2) continue;

                    // Есть ли конец другого маршрута рядом?
                    bool nearEnd = false;
                    foreach (var end in allEnds)
                    {
                        if (Vector3.Distance(pt, end) < NODE_RADIUS) { nearEnd = true; break; }
                    }
                    if (!nearEnd) continue;

                    // Защита от слишком близких разрезов
                    if (hasPrevCut && Vector3.Distance(prevCutPos, pt) < MIN_SEG_DIST) continue;

                    cuts.Add(i);
                    prevCutPos = pt;
                    hasPrevCut = true;
                }

                if (cuts.Count == 0) continue;

                // Разрезаем ОДИН раз на все куски
                var segments = new List<Route>();
                int prevIdx = 0;
                int segNum = 0;

                foreach (var cutIdx in cuts)
                {
                    if (cutIdx - prevIdx < 2) { prevIdx = cutIdx; continue; }
                    var seg = new Route
                    {
                        Name = r.Name + "_s" + segNum,
                        FilePath = r.FilePath,
                        Points = r.Points.GetRange(prevIdx, cutIdx - prevIdx + 1)
                    };
                    segments.Add(seg);
                    segNum++;
                    prevIdx = cutIdx;
                }
                // Финальный кусок до конца
                if (r.Points.Count - prevIdx >= 2)
                {
                    var seg = new Route
                    {
                        Name = r.Name + "_s" + segNum,
                        FilePath = r.FilePath,
                        Points = r.Points.GetRange(prevIdx, r.Points.Count - prevIdx)
                    };
                    segments.Add(seg);
                }

                if (segments.Count >= 2)
                {
                    toReplace[r] = segments;
                    Debug.Log("[V][GRAPH] ✂️ " + r.Name + " → " + segments.Count + " кусков");
                }
            }

            // Применяем
            if (toReplace.Count > 0)
            {
                foreach (var kv in toReplace)
                {
                    Routes.Remove(kv.Key);
                    Routes.AddRange(kv.Value);
                }
            }
        }

        // ═══ Узлы ═══
        private void BuildNodes()
        {
            Nodes.Clear();
            foreach (var r in Routes)
            {
                AttachPoint(r, r.First, true);
                AttachPoint(r, r.Last, false);
            }
        }

        private void AttachPoint(Route r, Vector3 pt, bool isStart)
        {
            int bestNode = -1;
            float bestDist = SAME_NODE;
            for (int i = 0; i < Nodes.Count; i++)
            {
                float d = Vector3.Distance(Nodes[i].Pos, pt);
                if (d < bestDist) { bestDist = d; bestNode = i; }
            }

            if (bestNode >= 0)
            {
                Nodes[bestNode].Ends.Add(new RouteEnd { Route = r, IsStart = isStart });

                if (isStart)
                {
                    r.StartNode = bestNode;
                    // УБРАНО: r.Points[0] = Nodes[bestNode].Pos;
                    // Теперь маршрут сохраняет свою физическую форму. 
                }
                else
                {
                    r.EndNode = bestNode;
                    // УБРАНО: r.Points[r.Points.Count - 1] = Nodes[bestNode].Pos;
                }
            }
            else
            {
                var node = new Node { Id = Nodes.Count, Pos = pt };
                node.Ends.Add(new RouteEnd { Route = r, IsStart = isStart });
                Nodes.Add(node);
                if (isStart) r.StartNode = node.Id; else r.EndNode = node.Id;
            }
        }

        // ═══ Рёбра ═══
        private void BuildEdges()
        {
            Edges.Clear();
            foreach (var r in Routes)
            {
                var edge = new Edge { Route = r, FromNode = r.StartNode, ToNode = r.EndNode };

                // Вычисляем направления (векторы) на основе первых и последних точек
                if (r.Points.Count >= 2)
                {
                    edge.StartDir = (r.Points[1] - r.Points[0]).normalized;
                    edge.EndDir = (r.Points[r.Points.Count - 1] - r.Points[r.Points.Count - 2]).normalized;
                }

                Edges.Add(edge);
            }
        }

        // ═══ Дамп ═══
        public string Dump()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine("[V][GRAPH] ГРАФ МАРШРУТОВ");
            sb.AppendLine("========================================");
            sb.AppendLine("Сегментов: " + Routes.Count);
            sb.AppendLine("Узлов: " + Nodes.Count);
            sb.AppendLine("Рёбер: " + Edges.Count);
            sb.AppendLine("");

            sb.AppendLine("─── УЗЛЫ (только с 3+ концами) ───");
            foreach (var n in Nodes)
            {
                if (n.Ends.Count < 3) continue;
                sb.AppendLine("N" + n.Id + " @ " + n.Pos + " (" + n.Ends.Count + " концов)");
                foreach (var e in n.Ends)
                    sb.AppendLine("   ← " + e);
            }
            sb.AppendLine("");

            sb.AppendLine("─── РЁБРА ───");
            foreach (var e in Edges)
            {
                bool isLoop = e.FromNode == e.ToNode;
                sb.AppendLine("N" + e.FromNode + " → N" + e.ToNode +
                    " " + e.Route.Name +
                    " (" + e.Route.Count + "т)" +
                    (isLoop ? " [ПЕТЛЯ]" : ""));
            }
            sb.AppendLine("");

            sb.AppendLine("─── ВНИМАНИЕ ───");
            int problems = 0;
            foreach (var r in Routes)
            {
                if (r.StartNode == -1 || r.EndNode == -1)
                {
                    sb.AppendLine("⚠️ " + r.Name + ": узел не определён");
                    problems++;
                    continue;
                }
                // Маршрут-тупик: оба конца в узлах с 1 концом каждый
                var sN = Nodes[r.StartNode];
                var eN = Nodes[r.EndNode];
                if (sN.Ends.Count == 1 && eN.Ends.Count == 1 && r.StartNode != r.EndNode)
                {
                    sb.AppendLine("⚠️ " + r.Name + ": изолирован (тупик с двух сторон)");
                    problems++;
                }
            }
            if (problems == 0) sb.AppendLine("✅ Все маршруты соединены");

            sb.AppendLine("========================================");
            return sb.ToString();
        }
    }
}