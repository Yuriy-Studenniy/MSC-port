using UnityEngine;
using System.Collections.Generic;

namespace V_Companion
{
	public class GraphVisualizer : MonoBehaviour
	{
		public enum Mode { Off, Full, NodesOnly, NavOnly }
		public static GraphVisualizer Instance { get; private set; }

		private Mode _mode = Mode.Off;
		private RouteGraph _graph;

		private List<GameObject> _edgeObjs = new List<GameObject>();
		private List<GameObject> _nodeObjs = new List<GameObject>();
		private List<GameObject> _labelObjs = new List<GameObject>();

		private GameObject _highlightObj;
		private LineRenderer _highlightLine;

		private Material _matEdgeOneWay, _matEdgeTwoWay;
		private Material _matNodeJunction, _matNodePass, _matNodeDead, _matNodeOrphan;
		private Material _matHighlight;

		private const float Y_OFFSET = 0.5f;
		private const float LINE_WIDTH = 0.35f;
		private const float NODE_SIZE = 1.5f;
		private const float ARROW_LEN = 1.2f;
		private const float ARROW_SPREAD = 0.7f;

		public string ModeName
		{
			get
			{
				switch (_mode)
				{
					case Mode.Full: return "FULL";
					case Mode.NodesOnly: return "NODES";
					case Mode.NavOnly: return "NAV";
					default: return "OFF";
				}
			}
		}

		void Awake() { Instance = this; }
		void OnDestroy() { if (Instance == this) Instance = null; }

		// ═══ Публичный API ═══

		public void BuildFrom(RouteGraph graph)
		{
			_graph = graph;
			ClearAll();
			EnsureMaterials();
			BuildEdges();
			BuildNodes();
			BuildHighlight();
			ApplyMode();
		}

		public void CycleMode()
		{
			switch (_mode)
			{
				case Mode.Off: _mode = Mode.Full; break;
				case Mode.Full: _mode = Mode.NodesOnly; break;
				case Mode.NodesOnly: _mode = Mode.NavOnly; break;
				case Mode.NavOnly: _mode = Mode.Off; break;
			}
			ApplyMode();
		}

		public void HighlightEdge(RouteGraph.Edge edge, bool reverse = false)
		{
			if (_highlightObj == null || edge == null || edge.Route == null) return;
			var pts = edge.Route.Points;
			if (pts == null || pts.Count < 2) { ClearHighlight(); return; }

			_highlightLine.SetVertexCount(pts.Count);
			if (!reverse)
			{
				for (int i = 0; i < pts.Count; i++)
					_highlightLine.SetPosition(i, pts[i] + Vector3.up * (Y_OFFSET + 0.1f));
			}
			else
			{
				for (int i = 0; i < pts.Count; i++)
					_highlightLine.SetPosition(i, pts[pts.Count - 1 - i] + Vector3.up * (Y_OFFSET + 0.1f));
			}
			_highlightObj.SetActive(true);
		}

		public void ClearHighlight()
		{
			if (_highlightObj != null) _highlightObj.SetActive(false);
		}

		// ═══ Внутренности ═══

		private void ApplyMode()
		{
			bool showEdges = _mode == Mode.Full;
			bool showNodes = _mode == Mode.Full || _mode == Mode.NodesOnly;
			bool showLabels = _mode == Mode.Full || _mode == Mode.NodesOnly;
			bool allowHighlight = _mode == Mode.Full || _mode == Mode.NavOnly;

			foreach (var o in _edgeObjs) if (o != null) o.SetActive(showEdges);
			foreach (var o in _nodeObjs) if (o != null) o.SetActive(showNodes);
			foreach (var o in _labelObjs) if (o != null) o.SetActive(showLabels);
			if (!allowHighlight && _highlightObj != null) _highlightObj.SetActive(false);
		}

		private void EnsureMaterials()
		{
			if (_matEdgeOneWay != null) return;
			_matEdgeOneWay = MakeMat(new Color(1f, 0.25f, 0.25f, 0.95f));
			_matEdgeTwoWay = MakeMat(new Color(0.25f, 0.6f, 1f, 0.95f));
			_matNodeJunction = MakeMat(new Color(1f, 0.9f, 0.15f, 1f));
			_matNodePass = MakeMat(new Color(0.25f, 1f, 0.25f, 1f));
			_matNodeDead = MakeMat(new Color(0.65f, 0.65f, 0.65f, 1f));
			_matNodeOrphan = MakeMat(new Color(0.9f, 0.2f, 0.9f, 1f));
			_matHighlight = MakeMat(new Color(1f, 1f, 0.2f, 1f));
		}

		private Material MakeMat(Color c)
		{
			Shader sh = Shader.Find("Sprites/Default");
			if (sh == null) sh = Shader.Find("Unlit/Transparent");
			if (sh == null) sh = Shader.Find("Unlit/Color");
			if (sh == null) sh = Shader.Find("Standard");
			var m = new Material(sh);
			if (m.HasProperty("_Color")) m.SetColor("_Color", c);
			m.color = c;
			return m;
		}

		private void ClearAll()
		{
			foreach (var o in _edgeObjs) if (o != null) Destroy(o);
			foreach (var o in _nodeObjs) if (o != null) Destroy(o);
			foreach (var o in _labelObjs) if (o != null) Destroy(o);
			_edgeObjs.Clear();
			_nodeObjs.Clear();
			_labelObjs.Clear();
			if (_highlightObj != null) Destroy(_highlightObj);
			_highlightObj = null;
			_highlightLine = null;
		}

		private bool IsOneWay(RouteGraph.Route r)
		{
			return r.Name.ToLower().Contains("ring");
		}

		private void BuildEdges()
		{
			if (_graph == null) return;
			foreach (var e in _graph.Edges)
			{
				if (e.Route == null || e.Route.Points == null || e.Route.Points.Count < 2) continue;
				var pts = e.Route.Points;

				var go = new GameObject("e_" + e.Route.Name);
				go.transform.SetParent(transform, false);

				var lr = go.AddComponent<LineRenderer>();
				lr.useWorldSpace = true;
				lr.sharedMaterial = IsOneWay(e.Route) ? _matEdgeOneWay : _matEdgeTwoWay;
				lr.SetVertexCount(pts.Count);
				lr.SetWidth(LINE_WIDTH, LINE_WIDTH);

				for (int i = 0; i < pts.Count; i++)
					lr.SetPosition(i, pts[i] + Vector3.up * Y_OFFSET);

				int mid = pts.Count / 2;
				if (mid >= 1 && mid <= pts.Count - 2)
				{
					Vector3 mp = pts[mid] + Vector3.up * Y_OFFSET;
					Vector3 dir = (pts[mid + 1] - pts[mid - 1]).normalized;
					CreateArrow(go.transform, mp, dir, lr.sharedMaterial);
					if (!IsOneWay(e.Route))
						CreateArrow(go.transform, mp, -dir, lr.sharedMaterial);
				}

				_edgeObjs.Add(go);
			}
		}

		private void CreateArrow(Transform parent, Vector3 pos, Vector3 dir, Material mat)
		{
			dir.y = 0f;
			if (dir.sqrMagnitude < 0.01f) return;
			dir.Normalize();
			Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;

			var go = new GameObject("arrow");
			go.transform.SetParent(parent, false);

			var lr = go.AddComponent<LineRenderer>();
			lr.useWorldSpace = true;
			lr.sharedMaterial = mat;
			lr.SetVertexCount(3);
			lr.SetWidth(LINE_WIDTH * 1.2f, LINE_WIDTH * 1.2f);

			Vector3 tip = pos + dir * (ARROW_LEN * 0.5f);
			Vector3 back = pos - dir * (ARROW_LEN * 0.5f);
			lr.SetPosition(0, back + right * ARROW_SPREAD);
			lr.SetPosition(1, tip);
			lr.SetPosition(2, back - right * ARROW_SPREAD);
		}

		private void BuildNodes()
		{
			if (_graph == null) return;
			foreach (var n in _graph.Nodes)
			{
				Vector3 p = n.Pos + Vector3.up * Y_OFFSET;

				Material mat;
				float scale;
				if (n.Ends.Count >= 3) { mat = _matNodeJunction; scale = NODE_SIZE * 1.3f; }
				else if (n.Ends.Count == 2) { mat = _matNodePass; scale = NODE_SIZE; }
				else if (n.Ends.Count == 1) { mat = _matNodeDead; scale = NODE_SIZE * 1.1f; }
				else { mat = _matNodeOrphan; scale = NODE_SIZE * 1.5f; }

				var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
				go.name = "n_" + n.Id;
				go.transform.SetParent(transform, false);
				go.transform.position = p;
				go.transform.localScale = Vector3.one * scale;

				var col = go.GetComponent<Collider>();
				if (col != null) Destroy(col);
				var rend = go.GetComponent<Renderer>();
				if (rend != null) rend.sharedMaterial = mat;

				_nodeObjs.Add(go);

				var lbl = new GameObject("lbl_" + n.Id);
				lbl.transform.SetParent(transform, false);
				lbl.transform.position = p + Vector3.up * 2.5f;

				var tm = lbl.AddComponent<TextMesh>();
				tm.text = "N" + n.Id + " (" + n.Ends.Count + ")";
				tm.fontSize = 30;
				tm.characterSize = 0.2f;
				tm.color = new Color(1f, 1f, 0.9f, 1f);
				tm.anchor = TextAnchor.MiddleCenter;
				tm.alignment = TextAlignment.Center;

				Font f = Resources.GetBuiltinResource<Font>("Arial.ttf");
				if (f == null) f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
				if (f != null) tm.font = f;

				_labelObjs.Add(lbl);
			}
		}

		private void BuildHighlight()
		{
			_highlightObj = new GameObject("highlight");
			_highlightObj.transform.SetParent(transform, false);

			_highlightLine = _highlightObj.AddComponent<LineRenderer>();
			_highlightLine.useWorldSpace = true;
			_highlightLine.sharedMaterial = _matHighlight;
			_highlightLine.SetWidth(LINE_WIDTH * 2f, LINE_WIDTH * 2f);
			_highlightLine.SetVertexCount(0);

			_highlightObj.SetActive(false);
		}
	}
}