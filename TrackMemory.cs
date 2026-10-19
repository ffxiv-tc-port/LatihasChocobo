using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LatihasChocobo;

public class TrackedObject {
	public uint DataId { get; set; }
	public float X { get; set; }
	public float Y { get; set; }
	public float Z { get; set; }
	public int SeenCount { get; set; }
	[JsonIgnore] public Vector3 Position => new(X, Y, Z);
}

public class TrackWaypoint {
	public float X { get; set; }
	public float Y { get; set; }
	public float Z { get; set; }
	public float Rotation { get; set; }
	public int SeenCount { get; set; }
	[JsonIgnore] public Vector3 Position => new(X, Y, Z);
}

public class TrackData {
	public List<TrackedObject> Objects { get; set; } = [];
	public List<TrackWaypoint> Waypoints { get; set; } = [];
	public List<TrackWaypoint> LeftBoundary { get; set; } = [];
	public List<TrackWaypoint> RightBoundary { get; set; } = [];
}

public static class TrackMemory {
	private const float ObjMerge = 7f;
	private const float WpMerge = 10f;
	private const float WpStep = 5f;
	public const int ConfirmCount = 2;

	private static string? _savePath;
	private static Dictionary<ushort, TrackData> _db = new();
	private static readonly Dictionary<ushort, List<TrackWaypoint>> _pathCache = new();

	private static readonly List<TrackedObject> _curObjs = [];
	private static readonly List<TrackWaypoint> _curWps = [];
	private static Vector3 _lastWpPos;

	private static readonly List<TrackWaypoint> _curBoundary = [];
	private static Vector3 _lastBoundaryPos;

	public static void Init(string dir) {
		_savePath = Path.Combine(dir, "track_memory.json");
		Load();
	}

	private static void Load() {
		if (_savePath == null || !File.Exists(_savePath)) return;
		try { _db = JsonSerializer.Deserialize<Dictionary<ushort, TrackData>>(File.ReadAllText(_savePath)) ?? new(); }
		catch { _db = new(); }
	}

	public static void Save() {
		if (_savePath == null) return;
		try { File.WriteAllText(_savePath, JsonSerializer.Serialize(_db, new JsonSerializerOptions { WriteIndented = true })); }
		catch { }
	}

	public static void StartRace() {
		_curObjs.Clear();
		_curWps.Clear();
		_lastWpPos = Vector3.Zero;
	}

	public static void StartBoundaryRace() {
		_curBoundary.Clear();
		_lastBoundaryPos = Vector3.Zero;
	}

	public static void RecordBoundaryWaypoint(Vector3 pos, float rot) {
		if (Vector3.Distance(pos, _lastBoundaryPos) < WpStep) return;
		_lastBoundaryPos = pos;
		_curBoundary.Add(new TrackWaypoint { X = pos.X, Y = pos.Y, Z = pos.Z, Rotation = rot });
	}

	public static void EndBoundaryRace(ushort territory, bool isLeft) {
		if (!_db.ContainsKey(territory)) _db[territory] = new TrackData();
		var data = _db[territory];
		var target = isLeft ? data.LeftBoundary : data.RightBoundary;
		foreach (var w in _curBoundary) {
			var match = target.FirstOrDefault(e => Vector3.Distance(e.Position, w.Position) < WpMerge);
			if (match != null) {
				var sx = MathF.Sin(match.Rotation) * match.SeenCount + MathF.Sin(w.Rotation);
				var cx = MathF.Cos(match.Rotation) * match.SeenCount + MathF.Cos(w.Rotation);
				match.Rotation = MathF.Atan2(sx, cx);
				match.SeenCount++;
			} else {
				target.Add(new TrackWaypoint { X = w.X, Y = w.Y, Z = w.Z, Rotation = w.Rotation, SeenCount = 1 });
			}
		}
		Save();
		_curBoundary.Clear();
	}

	public static List<TrackWaypoint> GetBoundaryPath(ushort territory, bool isLeft) {
		if (!_db.TryGetValue(territory, out var data)) return [];
		return isLeft ? data.LeftBoundary : data.RightBoundary;
	}

	// 移動障礙怪物(3596)位置不固定，不記入賽道記憶
	private static readonly HashSet<uint> SkipRecording = [3596];

	public static void RecordObject(uint dataId, Vector3 pos) {
		if (SkipRecording.Contains(dataId)) return;
		if (_curObjs.Any(o => o.DataId == dataId && Vector3.Distance(o.Position, pos) < ObjMerge)) return;
		_curObjs.Add(new TrackedObject { DataId = dataId, X = pos.X, Y = pos.Y, Z = pos.Z });
	}

	public static void RecordWaypoint(Vector3 pos, float rot) {
		if (Vector3.Distance(pos, _lastWpPos) < WpStep) return;
		_lastWpPos = pos;
		_curWps.Add(new TrackWaypoint { X = pos.X, Y = pos.Y, Z = pos.Z, Rotation = rot });
	}

	// 記錄對手位置作為賽道路點（對手一定在賽道上）
	public static void RecordOpponentWaypoint(Vector3 pos, float rot) {
		if (_curWps.Any(w => Vector3.Distance(w.Position, pos) < WpStep)) return;
		_curWps.Add(new TrackWaypoint { X = pos.X, Y = pos.Y, Z = pos.Z, Rotation = rot });
	}

	public static void EndRace(ushort territory) {
		if (!_db.ContainsKey(territory)) _db[territory] = new TrackData();
		var data = _db[territory];

		foreach (var o in _curObjs) {
			var match = data.Objects.FirstOrDefault(e => e.DataId == o.DataId && Vector3.Distance(e.Position, o.Position) < ObjMerge);
			if (match != null) match.SeenCount++;
			else data.Objects.Add(new TrackedObject { DataId = o.DataId, X = o.X, Y = o.Y, Z = o.Z, SeenCount = 1 });
		}

		foreach (var w in _curWps) {
			var match = data.Waypoints.FirstOrDefault(e => Vector3.Distance(e.Position, w.Position) < WpMerge);
			if (match != null) {
				// circular average for rotation
				var sx = MathF.Sin(match.Rotation) * match.SeenCount + MathF.Sin(w.Rotation);
				var cx = MathF.Cos(match.Rotation) * match.SeenCount + MathF.Cos(w.Rotation);
				match.Rotation = MathF.Atan2(sx, cx);
				match.SeenCount++;
			} else {
				data.Waypoints.Add(new TrackWaypoint { X = w.X, Y = w.Y, Z = w.Z, Rotation = w.Rotation, SeenCount = 1 });
			}
		}

		_pathCache.Remove(territory);
		Save();
		_curObjs.Clear();
		_curWps.Clear();
	}

	// 從已確認路點建立有序路徑（nearest-neighbor，從最南端出發）
	private static List<TrackWaypoint> BuildOrderedPath(List<TrackWaypoint> wps) {
		if (wps.Count < 2) return wps;
		var remaining = new List<TrackWaypoint>(wps);
		var path = new List<TrackWaypoint>();
		var cur = remaining.MinBy(w => w.Z)!;
		path.Add(cur); remaining.Remove(cur);
		const float maxStep = 25f;
		while (remaining.Count > 0) {
			var fwd = new Vector2(MathF.Sin(cur.Rotation), MathF.Cos(cur.Rotation));
			var next = remaining
				.Select(w => {
					var toW = new Vector2(w.X - cur.X, w.Z - cur.Z);
					var dist2D = toW.Length();
					var dot = dist2D > 0 ? Vector2.Dot(fwd, toW / dist2D) : 0f;
					// 以 3D 距離過濾，但用 2D 方向評分
					var dist3D = Vector3.Distance(cur.Position, w.Position);
					return (w, dist2D, dist3D, dot);
				})
				.Where(x => x.dist3D < maxStep && x.dot > 0.1f)
				.OrderBy(x => x.dist3D / (x.dot + 0.5f))
				.Select(x => x.w)
				.FirstOrDefault();
			if (next == null) break;
			path.Add(next); remaining.Remove(next);
			cur = next;
		}
		return path;
	}

	public static List<TrackWaypoint> GetOrderedPath(ushort territory) {
		if (_pathCache.TryGetValue(territory, out var cached)) return cached;
		if (!_db.TryGetValue(territory, out var data)) return [];
		var confirmed = data.Waypoints.Where(w => w.SeenCount >= ConfirmCount).ToList();
		var path = BuildOrderedPath(confirmed);
		_pathCache[territory] = path;
		return path;
	}

	public static bool IsConfirmed(ushort territory, uint dataId, Vector3 pos) {
		if (!_db.TryGetValue(territory, out var d)) return false;
		return d.Objects.Any(o => o.DataId == dataId && o.SeenCount >= ConfirmCount && Vector3.Distance(o.Position, pos) < ObjMerge);
	}

	// 賽道中心修正：回傳 (方向, 偏移距離)，偏移 < deadZone 時回傳 InValid
	public static (Plugin.Direction dir, float offset) GetCenterCorrection(ushort territory, Vector3 playerPos, float playerRot, float deadZone = 3f) {
		if (!_db.TryGetValue(territory, out var data)) return (Plugin.Direction.InValid, 0f);
		if (data.LeftBoundary.Count == 0 || data.RightBoundary.Count == 0) return (Plugin.Direction.InValid, 0f);
		var p2 = new Vector2(playerPos.X, playerPos.Z);
		var leftNearest = data.LeftBoundary.MinBy(w => Vector2.Distance(p2, new Vector2(w.X, w.Z)))!;
		var rightNearest = data.RightBoundary.MinBy(w => Vector2.Distance(p2, new Vector2(w.X, w.Z)))!;
		var center = (new Vector2(leftNearest.X, leftNearest.Z) + new Vector2(rightNearest.X, rightNearest.Z)) / 2f;
		var toCenter = center - p2;
		var offset = toCenter.Length();
		if (offset < deadZone) return (Plugin.Direction.InValid, 0f);
		var fwd = new Vector2(MathF.Sin(playerRot), MathF.Cos(playerRot));
		var cross = fwd.X * toCenter.Y - fwd.Y * toCenter.X;
		return (cross > 0 ? Plugin.Direction.Right : Plugin.Direction.Left, offset);
	}

	// 邊界修正：若玩家太靠近左/右邊界，回傳應向哪側修正
	// 只取 localRadius 範圍內的邊界點，避免彎道時抓到遠處不相關段落
	public static Plugin.Direction GetBoundaryCorrection(ushort territory, Vector3 playerPos, float warningDist = 4f, float localRadius = 15f) {
		if (!_db.TryGetValue(territory, out var data)) return Plugin.Direction.InValid;
		if (data.LeftBoundary.Count == 0 || data.RightBoundary.Count == 0) return Plugin.Direction.InValid;
		var p2 = new Vector2(playerPos.X, playerPos.Z);
		var localLeft = data.LeftBoundary.Where(w => Vector2.Distance(p2, new Vector2(w.X, w.Z)) < localRadius).ToList();
		var localRight = data.RightBoundary.Where(w => Vector2.Distance(p2, new Vector2(w.X, w.Z)) < localRadius).ToList();
		if (localLeft.Count == 0 || localRight.Count == 0) return Plugin.Direction.InValid;
		var leftDist = localLeft.Min(w => Vector2.Distance(p2, new Vector2(w.X, w.Z)));
		var rightDist = localRight.Min(w => Vector2.Distance(p2, new Vector2(w.X, w.Z)));
		if (leftDist < warningDist && leftDist < rightDist) return Plugin.Direction.Right;
		if (rightDist < warningDist && rightDist < leftDist) return Plugin.Direction.Left;
		return Plugin.Direction.InValid;
	}

	// 近距路線追蹤：找到當前最近路點後，取前方 ~15u 的路點作為目標方向（跳過短距擺動）
	public static Plugin.Direction GetPathDirection(ushort territory, Vector3 playerPos, float playerRot,
		float lookAheadDist = 15f, float angleThreshold = 12f) {
		var path = GetOrderedPath(territory);
		if (path.Count < 2) return Plugin.Direction.InValid;
		var fwd = new Vector2(MathF.Sin(playerRot), MathF.Cos(playerRot));
		var p2 = new Vector2(playerPos.X, playerPos.Z);

		// 找最近且在前方的路點索引（搜尋範圍放寬到 20u）
		var bestIdx = -1;
		var bestScore = float.MaxValue;
		for (var i = 0; i < path.Count; i++) {
			var toW = new Vector2(path[i].X - p2.X, path[i].Z - p2.Y);
			var dist = toW.Length();
			if (dist > 20f) continue;
			var dot = dist > 0 ? Vector2.Dot(fwd, toW / dist) : 0f;
			if (dot < 0.1f) continue;
			var score = dist - dot * 5f; // 近且在前方的優先
			if (score < bestScore) { bestScore = score; bestIdx = i; }
		}
		if (bestIdx < 0) return Plugin.Direction.InValid;

		// 從 bestIdx 往後走，找第一個累積路徑距離 >= lookAheadDist 的路點
		var accumulated = 0f;
		var targetIdx = bestIdx;
		for (var i = bestIdx + 1; i < path.Count; i++) {
			accumulated += Vector2.Distance(
				new Vector2(path[i - 1].X, path[i - 1].Z),
				new Vector2(path[i].X, path[i].Z));
			targetIdx = i;
			if (accumulated >= lookAheadDist) break;
		}
		if (targetIdx == bestIdx) return Plugin.Direction.InValid;

		var target = path[targetIdx];
		var toTarget = new Vector2(target.X - p2.X, target.Z - p2.Y);
		var len = toTarget.Length();
		if (len < 5f) return Plugin.Direction.InValid;
		var dotT = Vector2.Dot(fwd, toTarget / len);
		if (dotT < 0.2f) return Plugin.Direction.InValid;
		var cross = fwd.X * toTarget.Y - fwd.Y * toTarget.X;
		var angleDeg = MathF.Acos(Math.Clamp(dotT, -1f, 1f)) * 180f / MathF.PI;
		if (angleDeg < angleThreshold) return Plugin.Direction.InValid;
		return cross > 0 ? Plugin.Direction.Right : Plugin.Direction.Left;
	}

	// 找前方最近的確認好物件位置（記憶中），回傳 (方向, 距離)；無時回傳 InValid
	public static (Plugin.Direction dir, float dist) GetNearestMemoryGoodObjectAhead(
		ushort territory, Vector3 playerPos, float playerRot,
		IEnumerable<uint> goodDataIds, float maxDist = 40f) {
		if (!_db.TryGetValue(territory, out var data)) return (Plugin.Direction.InValid, 0f);
		var goodSet = new HashSet<uint>(goodDataIds);
		var fwd = new Vector2(MathF.Sin(playerRot), MathF.Cos(playerRot));
		var p2 = new Vector2(playerPos.X, playerPos.Z);
		var best = (dir: Plugin.Direction.InValid, dist: float.MaxValue);
		foreach (var o in data.Objects) {
			if (!goodSet.Contains(o.DataId) || o.SeenCount < ConfirmCount) continue;
			var toObj = new Vector2(o.X - p2.X, o.Z - p2.Y);
			var dist = toObj.Length();
			if (dist > maxDist || dist < 3f) continue;
			var dot = Vector2.Dot(fwd, toObj / dist);
			if (dot < 0.3f) continue; // 必須在前方
			if (dist >= best.dist) continue;
			var cross = fwd.X * toObj.Y - fwd.Y * toObj.X;
			var angleDeg = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 180f / MathF.PI;
			if (angleDeg < 5f) continue; // 幾乎正前方，不需轉向
			best = (cross > 0 ? Plugin.Direction.Right : Plugin.Direction.Left, dist);
		}
		return best;
	}

	// 判斷前方走廊（半寬 corridorHalf 單位，距離 5-maxDist）內是否有確認好物件
	public static bool HasConfirmedGoodObjectOnPath(ushort territory, Vector3 playerPos, float playerRot,
		IEnumerable<uint> goodDataIds, float corridorHalf = 7f, float maxDist = 50f) {
		if (!_db.TryGetValue(territory, out var data)) return false;
		var goodSet = new HashSet<uint>(goodDataIds);
		var fwd = new Vector2(MathF.Sin(playerRot), MathF.Cos(playerRot));
		foreach (var o in data.Objects) {
			if (!goodSet.Contains(o.DataId) || o.SeenCount < ConfirmCount) continue;
			var toObj = new Vector2(o.X - playerPos.X, o.Z - playerPos.Z);
			var dot = Vector2.Dot(fwd, toObj);
			if (dot < 5f || dot > maxDist) continue;
			if (MathF.Abs(fwd.X * toObj.Y - fwd.Y * toObj.X) < corridorHalf) return true;
		}
		return false;
	}

	// 遠距彎道預測：minDist-maxDist 單位前的路點旋轉方向，提前轉向
	public static Plugin.Direction GetUpcomingCurve(ushort territory, Vector3 playerPos, float playerRot,
		float minDist = 25f, float maxDist = 50f, float angleThreshold = 0.26f) {
		if (!_db.TryGetValue(territory, out var data)) return Plugin.Direction.InValid;
		var forwardDir = new Vector2(MathF.Sin(playerRot), MathF.Cos(playerRot));
		var upcoming = data.Waypoints
			.Where(w => w.SeenCount >= ConfirmCount)
			.Select(w => {
				var toTarget = new Vector2(w.X - playerPos.X, w.Z - playerPos.Z);
				var len = toTarget.Length();
				var dot = len > 0 ? Vector2.Dot(forwardDir, toTarget / len) : 0f;
				return (dist: len, dot, wp: w);
			})
			.Where(x => x.dot > 0.4f && x.dist > minDist && x.dist < maxDist)
			.OrderBy(x => x.dist)
			.Select(x => x.wp)
			.Take(5)
			.ToList();
		if (upcoming.Count < 2) return Plugin.Direction.InValid;

		var lookAheadWps = upcoming.Skip(upcoming.Count / 2).ToList();
		var sx = lookAheadWps.Sum(w => MathF.Sin(w.Rotation));
		var cx = lookAheadWps.Sum(w => MathF.Cos(w.Rotation));
		var targetRot = MathF.Atan2(sx, cx);

		var diff = AngleDiff(playerRot, targetRot);
		if (MathF.Abs(diff) < angleThreshold) return Plugin.Direction.InValid;
		return diff > 0 ? Plugin.Direction.Right : Plugin.Direction.Left;
	}

	private static float AngleDiff(float a, float b) {
		var d = b - a;
		while (d > MathF.PI) d -= 2 * MathF.PI;
		while (d < -MathF.PI) d += 2 * MathF.PI;
		return d;
	}

	// 根據有序路徑計算實際路程進度
	// 回傳 (已行進%, 剩餘距離(u), 總長度(u))；無資料時回傳 default
	public static (float traveledPct, float remainingU, float totalU) GetTrackProgress(ushort territory, Vector3 playerPos) {
		var path = GetOrderedPath(territory);
		if (path.Count < 2) return default;

		var p2 = new Vector2(playerPos.X, playerPos.Z);

		// 總賽道長（3D，含高低差）
		var total = 0f;
		for (var i = 0; i < path.Count - 1; i++)
			total += Vector3.Distance(path[i].Position, path[i + 1].Position);
		// 路徑太短代表資料不完整，退回讓上層用 UI 百分比
		if (total < 200f) return default;

		// 找最近路點索引（用 2D 避免高低差誤導）
		var bestIdx = 0;
		var bestDist = float.MaxValue;
		for (var i = 0; i < path.Count; i++) {
			var d = Vector2.Distance(p2, new Vector2(path[i].X, path[i].Z));
			if (d < bestDist) { bestDist = d; bestIdx = i; }
		}

		// 累加已行進距離（3D）
		var traveled = 0f;
		for (var i = 0; i < bestIdx; i++)
			traveled += Vector3.Distance(path[i].Position, path[i + 1].Position);

		return (traveled / total * 100f, total - traveled, total);
	}

	public static TrackData? GetData(ushort territory) => _db.TryGetValue(territory, out var d) ? d : null;
	public static void Clear(ushort territory) { _db.Remove(territory); _pathCache.Remove(territory); Save(); }
	public static IReadOnlyCollection<ushort> KnownTerritories => _db.Keys;
	public static string? SavePath => _savePath;

	public static void ImportFrom(string path) {
		try {
			var imported = JsonSerializer.Deserialize<Dictionary<ushort, TrackData>>(File.ReadAllText(path));
			if (imported == null) return;
			foreach (var (territory, srcData) in imported) {
				if (!_db.ContainsKey(territory)) _db[territory] = new TrackData();
				var dst = _db[territory];
				foreach (var o in srcData.Objects) {
					var match = dst.Objects.FirstOrDefault(e => e.DataId == o.DataId && Vector3.Distance(e.Position, o.Position) < ObjMerge);
					if (match != null) match.SeenCount = Math.Max(match.SeenCount, o.SeenCount);
					else dst.Objects.Add(o);
				}
				foreach (var w in srcData.Waypoints) {
					var match = dst.Waypoints.FirstOrDefault(e => Vector3.Distance(e.Position, w.Position) < WpMerge);
					if (match != null) match.SeenCount = Math.Max(match.SeenCount, w.SeenCount);
					else dst.Waypoints.Add(w);
				}
			}
			_pathCache.Clear();
			Save();
		} catch { }
	}
}
