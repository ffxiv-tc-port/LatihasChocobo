using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static LatihasChocobo.Plugin;

namespace LatihasChocobo;

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "InvertIf")]
[SuppressMessage("ReSharper", "SpecifyACultureInStringConversionExplicitly")]
public class MainWindow() : Window("Chocobo=>CCB?", ImGuiWindowFlags.None, false) {
	private const ImGuiTableFlags ImGuiTableFlag = ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg;

	private static readonly Vector4 green = new(0, 1, 0, 1),
		red = new(1, 0, 0, 1);

	private static ushort _mapTerritory = 389;

	private static void DrawRouteMap(ushort territory) {
		var data = TrackMemory.GetData(territory);
		var canvasSize = new Vector2(ImGui.GetContentRegionAvail().X, 340);
		var canvasPos = ImGui.GetCursorScreenPos();
		var dl = ImGui.GetWindowDrawList();
		dl.AddRectFilled(canvasPos, canvasPos + canvasSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.07f, 0.07f, 0.07f, 1f)));
		dl.AddRect(canvasPos, canvasPos + canvasSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.35f, 0.35f, 1f)));
		ImGui.Dummy(canvasSize);

		if (data == null || (data.Waypoints.Count == 0 && data.Objects.Count == 0)) {
			dl.AddText(canvasPos + new Vector2(10, 10), ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), "無資料");
			return;
		}

		var player = ClientState.LocalPlayer;
		var allPts = data.Waypoints.Select(w => (w.X, w.Z))
			.Concat(data.Objects.Select(o => (o.X, o.Z)));
		if (player != null && ClientState.TerritoryType == territory)
			allPts = allPts.Append((player.Position.X, player.Position.Z));
		var ptList = allPts.ToList();
		var minX = ptList.Min(p => p.X); var maxX = ptList.Max(p => p.X);
		var minZ = ptList.Min(p => p.Z); var maxZ = ptList.Max(p => p.Z);

		const float pad = 16f;
		// 地圖旋轉 90° 逆時針：Z 軸反轉 → 水平（canvasX），X 軸 → 垂直（canvasY）
		var rangeH = Math.Max(maxZ - minZ, 1f);
		var rangeV = Math.Max(maxX - minX, 1f);
		var scale = Math.Min((canvasSize.X - pad * 2) / rangeH, (canvasSize.Y - pad * 2) / rangeV);
		var offX = canvasPos.X + (canvasSize.X - rangeH * scale) / 2f;
		var offY = canvasPos.Y + (canvasSize.Y - rangeV * scale) / 2f;
		// world (wx, wz) → canvas: maxZ-Z→X，X→Y
		Vector2 W2C(float wx, float wz) => new(offX + (maxZ - wz) * scale, offY + (wx - minX) * scale);

		// ── 賽道路線：使用 TrackMemory 有序路徑快取 ──
		var confirmedWps = data.Waypoints.Where(w => w.SeenCount >= TrackMemory.ConfirmCount).ToList();
		var orderedPath = TrackMemory.GetOrderedPath(territory);
		if (orderedPath.Count >= 2) {
			var pathCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.6f, 1f, 0.55f));
			var halfTrack = Math.Max(2f, 4f * scale); // ~賽道半寬對應像素
			// 畫賽道帶狀色塊（每段路點間填充四邊形）
			var bandCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.4f, 0.7f, 0.18f));
			for (var i = 0; i < orderedPath.Count - 1; i++) {
				var a = orderedPath[i]; var b = orderedPath[i + 1];
				var pa = W2C(a.X, a.Z); var pb = W2C(b.X, b.Z);
				// 賽道寬度方向（右垂直 world(cos,−sin) → canvas(sin,cos) after 90°CCW）
				var perpA = new Vector2(MathF.Sin(a.Rotation), MathF.Cos(a.Rotation)) * halfTrack;
				var perpB = new Vector2(MathF.Sin(b.Rotation), MathF.Cos(b.Rotation)) * halfTrack;
				dl.AddQuadFilled(pa - perpA, pa + perpA, pb + perpB, pb - perpB, bandCol);
				dl.AddLine(pa, pb, pathCol, 1.5f);
			}
		}

		// ── 邊界線 ──
		var leftBoundary = TrackMemory.GetBoundaryPath(territory, true);
		var rightBoundary = TrackMemory.GetBoundaryPath(territory, false);
		var leftCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.9f, 0.9f, 0.7f));
		var rightCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.5f, 0.1f, 0.7f));
		for (var i = 0; i < leftBoundary.Count - 1; i++)
			dl.AddLine(W2C(leftBoundary[i].X, leftBoundary[i].Z), W2C(leftBoundary[i+1].X, leftBoundary[i+1].Z), leftCol, 1.5f);
		for (var i = 0; i < rightBoundary.Count - 1; i++)
			dl.AddLine(W2C(rightBoundary[i].X, rightBoundary[i].Z), W2C(rightBoundary[i+1].X, rightBoundary[i+1].Z), rightCol, 1.5f);

		// ── 未確認路點（暗色小點）──
		foreach (var w in data.Waypoints.Where(w => w.SeenCount < TrackMemory.ConfirmCount)) {
			dl.AddCircleFilled(W2C(w.X, w.Z), 1.5f, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.3f, 0.5f)));
		}
		// ── 確認路點（綠點）──
		foreach (var w in confirmedWps) {
			dl.AddCircleFilled(W2C(w.X, w.Z), 2.5f, ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.85f, 0.2f, 0.9f)));
		}

		// ── 物件（含確認外圈）──
		// 目前賽道上實際偵測到的物件集合，用於判斷是否需要降亮
		var liveSet = ClientState.TerritoryType == territory
			? ObjectTable
				.Where(obj => (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj
				            || obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
				           && (BadObjectType.ContainsKey(obj.DataId) || GoodObjectType.ContainsKey(obj.DataId)))
				.Select(obj => (obj.DataId, obj.Position))
				.ToList()
			: [];
		foreach (var o in data.Objects) {
			var p = W2C(o.X, o.Z);
			var isLive = liveSet.Any(l => l.DataId == o.DataId && Vector3.Distance(l.Position, o.Position) < 8f);
			var alpha = isLive ? 1f : 0.3f;
			Vector4 col4 = BadObjectType.ContainsKey(o.DataId)
				? new Vector4(1f, 0.25f, 0.25f, alpha)
				: GoodObjectType.ContainsKey(o.DataId)
					? new Vector4(1f, 0.9f, 0.1f, alpha)
					: new Vector4(0.6f, 0.6f, 0.6f, alpha);
			var col = ImGui.ColorConvertFloat4ToU32(col4);
			dl.AddCircleFilled(p, 4.5f, col);
			if (o.SeenCount >= TrackMemory.ConfirmCount) dl.AddCircle(p, 7f, col, 12, 1.5f);
		}

		// ── 玩家（白點＋黃色方向箭頭）──
		if (player != null && ClientState.TerritoryType == territory) {
			var pp = W2C(player.Position.X, player.Position.Z);
			dl.AddCircleFilled(pp, 5f, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)));
			var rot = player.Rotation;
			// world forward (sin,cos) → canvas (−cos,sin) after 90°CCW
			var tip = pp + new Vector2(-MathF.Cos(rot), MathF.Sin(rot)) * 14f;
			dl.AddLine(pp, tip, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 0.1f, 1f)), 2.5f);
		}

		// ── 敵人（紫色，僅當前賽道）──
		if (ClientState.TerritoryType == territory) {
			var enemyCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.75f, 0.3f, 1f, 1f));
			foreach (var obj in ObjectTable) {
				if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc || obj.DataId != 3705) continue;
				var ep = W2C(obj.Position.X, obj.Position.Z);
				dl.AddCircleFilled(ep, 4f, enemyCol);
				var eRot = obj.Rotation;
				var eTip = ep + new Vector2(-MathF.Cos(eRot), MathF.Sin(eRot)) * 10f;
				dl.AddLine(ep, eTip, enemyCol, 1.5f);
			}
		}
	}


	private static void DrawElevationProfile(ushort territory) {
		var path = TrackMemory.GetOrderedPath(territory);
		if (path.Count < 2) return;
		var data = TrackMemory.GetData(territory);

		const float h = 70f;
		var canvasSize = new Vector2(ImGui.GetContentRegionAvail().X, h);
		var canvasPos = ImGui.GetCursorScreenPos();
		var dl = ImGui.GetWindowDrawList();
		dl.AddRectFilled(canvasPos, canvasPos + canvasSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.05f, 0.05f, 1f)));
		dl.AddRect(canvasPos, canvasPos + canvasSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.25f, 0.25f, 1f)));
		ImGui.Dummy(canvasSize);

		var minY = path.Min(w => w.Y);
		var maxY = path.Max(w => w.Y);
		var rangeY = Math.Max(maxY - minY, 1f);

		var dists = new float[path.Count];
		dists[0] = 0f;
		for (var i = 1; i < path.Count; i++)
			dists[i] = dists[i - 1] + Vector3.Distance(path[i - 1].Position, path[i].Position);
		var totalDist = dists[^1];
		if (totalDist < 1f) return;

		const float pad = 4f;
		float PX(float d) => canvasPos.X + pad + (d / totalDist) * (canvasSize.X - pad * 2);
		float PY(float y) => canvasPos.Y + h - pad - ((y - minY) / rangeY) * (h - pad * 2);

		var lineCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.4f, 0.8f, 0.4f, 0.9f));
		for (var i = 0; i < path.Count - 1; i++)
			dl.AddLine(new Vector2(PX(dists[i]), PY(path[i].Y)), new Vector2(PX(dists[i + 1]), PY(path[i + 1].Y)), lineCol, 1.5f);

		if (data != null) {
			foreach (var o in data.Objects.Where(o => o.SeenCount >= TrackMemory.ConfirmCount && BadObjectType.ContainsKey(o.DataId))) {
				var nearestDist = dists[0];
			var minD = float.MaxValue;
				for (var i = 0; i < path.Count; i++) {
					var d = Vector2.Distance(new Vector2(o.X, o.Z), new Vector2(path[i].X, path[i].Z));
					if (d < minD) { minD = d; nearestDist = dists[i]; }
				}
				var trapCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.3f, 0.3f, 0.9f));
				var tx = PX(nearestDist);
				dl.AddLine(new Vector2(tx, canvasPos.Y + pad), new Vector2(tx, canvasPos.Y + h - pad), trapCol, 1f);
				dl.AddCircleFilled(new Vector2(tx, PY(o.Y)), 3f, trapCol);
			}
		}

		var player = ClientState.LocalPlayer;
		if (player != null && ClientState.TerritoryType == territory) {
			var pp2 = new Vector2(player.Position.X, player.Position.Z);
			var nearIdx = 0; var nearD2 = float.MaxValue;
			for (var i = 0; i < path.Count; i++) {
				var d = Vector2.Distance(pp2, new Vector2(path[i].X, path[i].Z));
				if (d < nearD2) { nearD2 = d; nearIdx = i; }
			}
			var px = PX(dists[nearIdx]);
			dl.AddLine(new Vector2(px, canvasPos.Y + pad), new Vector2(px, canvasPos.Y + h - pad),
				ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 0.2f, 0.8f)), 1.5f);
			dl.AddCircleFilled(new Vector2(px, PY(player.Position.Y)), 4f,
				ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)));
		}
	}

	// 競賽能力名稱直接查遊戲自己的 ChocoboRaceAbility 表（row id 就是 CS 給的能力 id），
	// 不再維護手寫對照。原本那份表只列了 40 個能力，對照台服 7.20 的官方表少了 27 個：
	// 每一系的 III 階（衝刺III／療傷III／復原III／活力III／鎮靜III／反射III／沉默III／
	// 震盪III／體力消耗降低III／體力恢復量提高III／吸收II·III／模仿II·III／鳥羽結界II·III／
	// 復生II·III／弱化耐性II·III／減速休息II·III）、加重耐性V、失控耐性V，以及
	// 起跑衝刺、道具變換、超級衝刺。這些原本全部顯示成「未知(0x..)」。
	// 另外手寫表的「陸行鳥偷取I」「經驗值提高I」在官方表裡沒有 I 字尾（是「陸行鳥偷取」
	// 「經驗值提高」），名稱前綴也以官方表為準（例：「衝刺」實際叫「陸行鳥衝刺」），
	// 這樣畫面上的字才跟遊戲內的競賽能力欄位逐字對得起來。
	private static readonly Dictionary<byte, string> _abilityNameCache = new();

	private static string AbilityName(byte id) {
		if (id == 0) return "無";
		if (_abilityNameCache.TryGetValue(id, out var cached)) return cached;
		var name = DataManager.GetExcelSheet<Lumina.Excel.Sheets.ChocoboRaceAbility>()
			.GetRowOrDefault(id)?.Name.ExtractText();
		// 查不到就沿用原本的「未知(0x..)」——「不知道」本身要在列上看得見，不要畫成空字串。
		// 失敗**不進快取**：表還沒載好時查不到是暫時的，快取起來會永久卡住。
		if (string.IsNullOrEmpty(name)) return $"未知(0x{id:X2})";
		_abilityNameCache[id] = name;
		return name;
	}

	private static void NewTab(string tabname, Action act) {
		if (ImGui.BeginTabItem(tabname)) {
			act();
			ImGui.EndTabItem();
		}
	}

	private static void NewTable(string[] header, List<string[]> data) {
		if (data.Count == 0) return;
		if (ImGui.BeginTable("Table", data[0].Length, ImGuiTableFlag)) {
			foreach (var item in header) ImGui.TableSetupColumn(item, ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableHeadersRow();
			foreach (var res in data) {
				ImGui.TableNextRow();
				for (var i = 0; i < res.Length; i++) {
					ImGui.TableSetColumnIndex(i);
					ImGui.Text(res[i]);
				}
			}
			ImGui.EndTable();
		}
	}

	[SuppressMessage("ReSharper", "ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator")]
	public override unsafe void Draw() {
		if (ClientState.LocalPlayer is null) return;
		if (ImGui.BeginTabBar("tab")) {
			NewTab("賽鳥", () => {
				if (ImGui.Checkbox("啟用", ref Configuration.Enabled)) {
					Configuration.Save();
					if (!Configuration.Enabled) isRunning = false;
				}
				ImGui.Separator();
				if (!Configuration.Enabled) return;
				if (ImGui.Checkbox("進入指定地點自動循環匹配", ref Configuration.AutoDuty)) {
					Configuration.Save();
					if (Configuration.AutoDuty) {
						if (Configuration.AutoDutyTerritory.Split('|').Contains(ClientState.TerritoryType.ToString()))
							RequestRace();
					}
				}
				ImGui.SameLine();
				if (ImGui.Button("立刻匹配")) RequestRace();
				if (ImGui.InputText("循環區域(用豎線|分隔)", ref Configuration.AutoDutyTerritory, 256)) Configuration.Save();
				if (ImGui.InputInt("循環間延遲(s)", ref Configuration.AutoDutyWait)) Configuration.Save();
				var mgr = RaceChocoboManager.Instance();
				ImGui.Text($"當前區域: {ClientState.TerritoryType}, 競賽等級: {mgr->Rank}, 經驗: {mgr->ExperienceCurrent}/{mgr->ExperienceMax}, 上場獲得: {LastRaceExpGain}");
				ImGui.Text($"可訓練次數: {mgr->SessionsAvailable}");
				ImGui.TextUnformatted($"最高速度:{mgr->MaximumSpeed}% 加速力:{mgr->Acceleration}% 體力:{mgr->Endurance}% 持久力:{mgr->Stamina}% 適應力:{mgr->Cunning}%");
				ImGui.Text($"先天性:{AbilityName(mgr->AbilityHereditary)}  後天性:{AbilityName(mgr->AbilityLearned)}");
				if (ImGui.InputInt("按鍵時長(ms)", ref Configuration.PressMs)) Configuration.Save();
				if (ImGui.InputFloat("超速也加速機率", ref Configuration.SpeedHighW, 1)) Configuration.Save();
				if (ImGui.Checkbox("低體力/路長禁用超速加速", ref Configuration.DisableSpeedUpWhenLowHP)) Configuration.Save();
				if (ImGui.Checkbox("高體力/路長強制超速加速(後25%)", ref Configuration.EnableSpeedUpWhenHighHP)) Configuration.Save();
				if (ImGui.Checkbox("自動使用道具", ref Configuration.AutoUseItem)) Configuration.Save();
				if (ImGui.Checkbox("滿級模式", ref Configuration.MaxLevelMode)) Configuration.Save();
				ImGui.Separator();
				ImGui.Text($"可使用物品：{(canUseItem ? "是" : "否")}。超速：{(speedHigh ? "是" : "否")}。L:{L}。H:{H}");
				ImGui.TextDisabled($"道具材質：{CanUseItemDebug}");
				var (tPct, remU, totU) = TrackMemory.GetTrackProgress(ClientState.TerritoryType, ClientState.LocalPlayer!.Position);
				var progressStr = totU > 0
					? $"路程：{tPct:F1}% 剩餘 {remU:F0}/{totU:F0}u"
					: $"路程(UI)：{RacePercent}%";
				ImGui.Text($"體力：{HpPercent} / {progressStr} / 速度：{CurrentSpeed:F1} u/s");
			var jumpLead = CurrentSpeed > 1f ? CurrentSpeed * Configuration.JumpPeakTime : Configuration.JumpPeakTime;
			ImGui.TextDisabled($"跳躍：峰值時間 {Configuration.JumpPeakTime:F2}s  峰值高度 {Configuration.JumpPeakHeight:F2}u  預判距離 {jumpLead:F1}u");
				List<string[]> data = [];
				foreach (var obj in GetEventObjects()) {
					var name = "UNK";
					if (BadObjectType.TryGetValue(obj.DataId, out var v1)) name = v1;
					if (GoodObjectType.TryGetValue(obj.DataId, out var v2)) name = v2;
					data.Add([
						GetTargetSide(obj).ToString(),
						obj.Position.X.ToString(),
						obj.Position.Y.ToString(),
						obj.Position.Z.ToString(),
						((int)Vector3.Distance(ClientState.LocalPlayer!.Position, obj.Position)).ToString(),
						obj.DataId.ToString(),
						name
					]);
				}
				NewTable(["狀態", "X", "Y", "Z", "距離", "DataId", "名稱"], data);
			});
			NewTab("背包", () => {
				if (ImGui.InputInt("篩選星級(OR)", ref Configuration.CcbMaxStar)) Configuration.Save();
				List<string[]> data = [];
				string? name = null;
				string? itemType = null;
				string? color = null;
				string? pedigree = null;
				string? ability = null;
				string? breedCount = null;
				try {
					var itemDetailPtr = GameGui.GetAddonByName("ItemDetail", 1).Address;
					var ItemDetail = (AtkUnitBase*)itemDetailPtr;
					// addon 不存在時不解參考（這是 UI 繪製路徑，每幀都會走到）。
					if (itemDetailPtr != nint.Zero && ItemDetail->IsVisible) {
						foreach (var TextNode in AllAtkUnitBaseByType(ItemDetail, (int)NodeType.Text)) {
							// GetAsAtkTextNode() 是 [MemberFunction] 原生呼叫，回 null 時 ->NodeText 就是存取違規；
							// TextOfNode 逐層驗過，取不到回空字串（Contains 對空字串本來就 false）。
							var str = TextOfNode(TextNode.Node);
							if (str.Contains("性陸行鳥配種登記書")) { name = str; itemType = "配種"; }
							else if (str.Contains("性陸行鳥出賽登記書")) { name = str; itemType = "出賽"; }
							else if (str.Contains("性陸行鳥退役登記書")) { name = str; itemType = "退役"; }
						}
						foreach (var BaseComponentNodeA in AllAtkUnitBaseByType(ItemDetail, 1005)) {
							// GetComponent() 回 null 時 ->UldManager 是存取違規入口。
							var component = ComponentOf(BaseComponentNodeA.Node);
							if (component == null) continue;
							var TextNodeA = AllAtkUnitBaseByType(component->UldManager, (int)NodeType.Text);
							foreach (var TextNode in TextNodeA) {
								var str = TextOfNode(TextNode.Node);
								if (str.Contains("顏色：")) color = str;
								else if (str.Contains("血統等級：")) pedigree = str;
								else if (str.Contains("競賽能力：")) ability = str;
								else if (str.Contains("可交配次數：")) breedCount = str;
							}
						}
						foreach (var node in AllAtkUnitBaseByType(ItemDetail, (int)NodeType.Res)) {
							var BaseComponentNodeA = AllAtkUnitBaseByType(node.Node, 1004);
							if (BaseComponentNodeA.Count != 5) continue;
							foreach (var BaseComponentNode in BaseComponentNodeA) {
								var starComponent = ComponentOf(BaseComponentNode.Node);
								if (starComponent == null) continue;
								var ResNode = FirstAtkUnitBaseByType(starComponent->UldManager, (int)NodeType.Res);
								var ndata = AllAtkUnitBaseByType(ResNode, (int)NodeType.Text);
								ndata.Reverse();
								var d = new string[3];
								foreach (var str in ndata.Select(TextNode => TextOfNode(TextNode.Node))) {
									if (str.StartsWith("\u0002H\u0004")) {
										var sp = str.Split('\u3000');
										d[1] = sp[0].Substring(32, 4);
										d[2] = sp[1].Substring(32, 4);
									} else d[0] = str;
								}
								data.Add(d);
							}
						}
					}
				} catch (Exception) {
					// ignored
				}
				var isValid = data.Count != 0 && name != null && itemType != null && color != null;
				var maxcount = 0;
				foreach (var obj in data) {
					foreach (var p in obj) {
						if (p.IsNullOrEmpty()) { isValid = false; break; }
					}
					if (!isValid) break;
					if (obj[1] == "\u2605\u2605\u2605\u2605" && obj[2] == "\u2605\u2605\u2605\u2605") maxcount++;
				}
				if (isValid) {
					var preserve = Configuration.CcbMaxStar <= maxcount;
					var maleColor = new Vector4(0.4f, 0.6f, 1f, 1f);
					var femaleColor = new Vector4(1f, 0.5f, 0.6f, 1f);
					var yellowBg = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 0.5f, 0.15f));
					var lineH = ImGui.GetTextLineHeightWithSpacing();
					foreach (var row in data) {
						var fullStar = row[1] == "★★★★" && row[2] == "★★★★";
						var rowPos = ImGui.GetCursorScreenPos();
						if (fullStar)
							ImGui.GetWindowDrawList().AddRectFilled(rowPos, new Vector2(rowPos.X + ImGui.GetContentRegionAvail().X, rowPos.Y + lineH), yellowBg);
						ImGui.Text(row[0]);
						ImGui.SameLine(75);
						ImGui.TextColored(maleColor, $"♂{row[1]}");
						ImGui.SameLine(165);
						ImGui.TextColored(femaleColor, $"♀{row[2]}");
					}
					ImGui.Separator();
					var isMale = name!.Contains("雄");
					var genderColor = isMale ? maleColor : femaleColor;
					var genderSym = isMale ? "♂" : "♀";
					var pedigreeVal = pedigree != null ? pedigree[(pedigree.IndexOf('：') + 1)..] : "?";
					var colorVal = color != null ? color[(color.IndexOf('：') + 1)..] : "?";
					var breedVal = breedCount != null ? breedCount[(breedCount.IndexOf('：') + 1)..] : null;
					var abilityVal = ability != null ? ability[(ability.IndexOf('：') + 1)..] : "?";
					ImGui.TextColored(genderColor, genderSym);
					ImGui.SameLine();
					if (itemType == "出賽") {
						var tPos = ImGui.GetCursorScreenPos();
						var tSize = ImGui.CalcTextSize($"[{itemType}]");
						ImGui.GetWindowDrawList().AddRectFilled(tPos, new Vector2(tPos.X + tSize.X, tPos.Y + tSize.Y), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 0.3f, 0.4f)));
					}
					ImGui.Text($"[{itemType}]");
					ImGui.SameLine(); ImGui.Text($"  血統:{pedigreeVal}  {colorVal}");
					ImGui.Text($"競賽能力:{abilityVal}");
					var line3 = $"滿星:{maxcount}";
					if (breedVal != null) line3 += $"  可交配:{breedVal}";
					ImGui.Text(line3);
					ImGui.PushStyleColor(ImGuiCol.Text, preserve ? green : red);
					ImGui.Text($"建議{(preserve ? "保留" : "捨棄")}");
					ImGui.PopStyleColor();
				} else ImGui.Text("滑鼠移動到配種登記書上以查看");
			});
			NewTab("按鍵", () => {
				var KC_W = Configuration.KC_W;
				if (ImGui.InputInt("KC_W(前)", ref KC_W)) {
					Configuration.KC_W = KC_W;
					Configuration.Save();
				}
				var KC_A = Configuration.KC_A;
				if (ImGui.InputInt("KC_A(左)", ref KC_A)) {
					Configuration.KC_A = KC_A;
					Configuration.Save();
				}
				var KC_D = Configuration.KC_D;
				if (ImGui.InputInt("KC_D(右)", ref KC_D)) {
					Configuration.KC_D = KC_D;
					Configuration.Save();
				}
				var KC_SPACE = Configuration.KC_SPACE;
				if (ImGui.InputInt("KC_SPACE(跳)", ref KC_SPACE)) {
					Configuration.KC_SPACE = KC_SPACE;
					Configuration.Save();
				}
				var KC_1 = Configuration.KC_1;
				if (ImGui.InputInt("KC_1(技能1)", ref KC_1)) {
					Configuration.KC_1 = KC_1;
					Configuration.Save();
				}
				var KC_2 = Configuration.KC_2;
				if (ImGui.InputInt("KC_2(技能2)", ref KC_2)) {
					Configuration.KC_2 = KC_2;
					Configuration.Save();
				}
			});
			NewTab("賽道", () => {
				ImGui.TextDisabled($"記錄好/壞物件位置與路點，{TrackMemory.ConfirmCount}場後視為確認，用於精準閃避時機與彎道預轉向");
				if (TrackMemory.SavePath != null) {
					ImGui.TextDisabled(TrackMemory.SavePath);
					ImGui.SameLine();
					if (ImGui.SmallButton("開啟資料夾"))
						System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{TrackMemory.SavePath}\"");
					ImGui.SameLine();
					if (ImGui.SmallButton("複製路徑"))
						ImGui.SetClipboardText(TrackMemory.SavePath);
				}
				ImGui.Separator();
				// 邊界模式控制
				if (!Configuration.BoundaryMode) {
					if (ImGui.Button("開始邊界校正（自動跑2場）")) {
						Configuration.BoundaryMode = true;
						Configuration.BoundaryPhase = 1;
						Configuration.Save();
					}
				} else {
					var phaseStr = Configuration.BoundaryPhase == 1 ? "第1場：靠左側行駛中..." : "第2場：靠右側行駛中...";
					ImGui.TextColored(new Vector4(1f, 0.8f, 0.1f, 1f), $"[邊界模式] {phaseStr}");
					ImGui.SameLine();
					if (ImGui.SmallButton("取消")) {
						Configuration.BoundaryMode = false;
						Configuration.BoundaryPhase = 0;
						Configuration.Save();
					}
				}
				ImGui.Separator();
				// 進入賽道時自動切換，否則預設 390
				var cur = ClientState.TerritoryType;
				if (cur is 389 or 390 or 391) _mapTerritory = cur;
				else if (_mapTerritory == 0) _mapTerritory = 390;
				// 路線圖（在物件資料上方）
				var territories = new ushort[] { 389, 390, 391 };
				foreach (var t in territories) {
					if (t != territories[0]) ImGui.SameLine();
					if (ImGui.RadioButton(t.ToString(), _mapTerritory == t)) _mapTerritory = t;
				}
				DrawRouteMap(_mapTerritory);
				DrawElevationProfile(_mapTerritory);
				// 圖例
				var dl2 = ImGui.GetWindowDrawList();
				var dot = ImGui.ColorConvertFloat4ToU32;
				{
					var lp = ImGui.GetCursorScreenPos();
					dl2.AddLine(lp + new Vector2(2, 8), lp + new Vector2(12, 8), dot(new Vector4(0.3f, 0.6f, 1f, 0.7f)), 2);
					ImGui.SetCursorScreenPos(lp + new Vector2(16, 0)); ImGui.TextDisabled("路線");
					ImGui.SameLine();
					lp = ImGui.GetCursorScreenPos();
					dl2.AddCircleFilled(lp + new Vector2(6, 8), 3, dot(new Vector4(0.2f, 0.8f, 0.2f, 1)));
					ImGui.SetCursorScreenPos(lp + new Vector2(14, 0)); ImGui.TextDisabled("確認路點");
					ImGui.SameLine();
					lp = ImGui.GetCursorScreenPos();
					dl2.AddCircleFilled(lp + new Vector2(6, 8), 2, dot(new Vector4(0.35f, 0.35f, 0.35f, 1)));
					ImGui.SetCursorScreenPos(lp + new Vector2(14, 0)); ImGui.TextDisabled("未確認");
					ImGui.SameLine();
					lp = ImGui.GetCursorScreenPos();
					dl2.AddCircleFilled(lp + new Vector2(6, 8), 4, dot(new Vector4(1f, 0.25f, 0.25f, 1)));
					ImGui.SetCursorScreenPos(lp + new Vector2(14, 0)); ImGui.TextDisabled("危險");
					ImGui.SameLine();
					lp = ImGui.GetCursorScreenPos();
					dl2.AddCircleFilled(lp + new Vector2(6, 8), 4, dot(new Vector4(1f, 0.9f, 0.1f, 1)));
					ImGui.SetCursorScreenPos(lp + new Vector2(14, 0)); ImGui.TextDisabled("好物件");
					ImGui.SameLine();
					lp = ImGui.GetCursorScreenPos();
					dl2.AddCircleFilled(lp + new Vector2(6, 8), 4, dot(new Vector4(1f, 1f, 1f, 1)));
					ImGui.SetCursorScreenPos(lp + new Vector2(14, 0)); ImGui.TextDisabled("玩家");
					ImGui.SameLine();
					lp = ImGui.GetCursorScreenPos();
					dl2.AddLine(lp + new Vector2(2, 8), lp + new Vector2(12, 8), dot(new Vector4(0.2f, 0.9f, 0.9f, 0.8f)), 2);
					ImGui.SetCursorScreenPos(lp + new Vector2(16, 0)); ImGui.TextDisabled("左邊界");
					ImGui.SameLine();
					lp = ImGui.GetCursorScreenPos();
					dl2.AddLine(lp + new Vector2(2, 8), lp + new Vector2(12, 8), dot(new Vector4(1f, 0.5f, 0.1f, 0.8f)), 2);
					ImGui.SetCursorScreenPos(lp + new Vector2(16, 0)); ImGui.TextDisabled("右邊界");
					ImGui.SameLine();
					lp = ImGui.GetCursorScreenPos();
					dl2.AddCircleFilled(lp + new Vector2(6, 8), 4, dot(new Vector4(0.75f, 0.3f, 1f, 1f)));
					ImGui.SetCursorScreenPos(lp + new Vector2(14, 0)); ImGui.TextDisabled("敵人");
					ImGui.NewLine();
				}
				ImGui.Separator();
				// 各賽道物件資料
				foreach (var t in territories) {
					var data = TrackMemory.GetData(t);
					if (data == null) { ImGui.TextDisabled($"地圖 {t}：無資料"); continue; }
					var confirmedObjs = data.Objects.Count(o => o.SeenCount >= TrackMemory.ConfirmCount);
					var confirmedWps = data.Waypoints.Count(w => w.SeenCount >= TrackMemory.ConfirmCount);
					var path = TrackMemory.GetOrderedPath(t);
					var pathLen = 0f;
					for (var i = 0; i < path.Count - 1; i++)
						pathLen += Vector2.Distance(new Vector2(path[i].X, path[i].Z), new Vector2(path[i+1].X, path[i+1].Z));
					ImGui.Text($"地圖 {t}：物件 {data.Objects.Count}({confirmedObjs}確認)  路點 {data.Waypoints.Count}({confirmedWps}確認)  路徑 {path.Count}點/{pathLen:F0}u");
					ImGui.SameLine();
					if (ImGui.SmallButton($"清除##{t}")) TrackMemory.Clear(t);
					List<string[]> objData = [];
					foreach (var o in data.Objects) {
						var name = "";
						if (BadObjectType.TryGetValue(o.DataId, out var b)) name = b;
						else if (GoodObjectType.TryGetValue(o.DataId, out var g)) name = g;
						objData.Add([o.DataId.ToString(), name, o.SeenCount.ToString(), o.SeenCount >= TrackMemory.ConfirmCount ? "✓" : ""]);
					}
					if (objData.Count > 0) NewTable(["DataId", "名稱", "次數", "確認"], objData);
				}
			});
			NewTab("物件", () => {
				ImGui.TextDisabled("顯示附近所有物件，用於識別障礙怪物 DataId");
				List<string[]> data = [];
				foreach (var obj in GetNearbyObjects()) {
					var dist = (int)System.Numerics.Vector3.Distance(ClientState.LocalPlayer!.Position, obj.Position);
					var tag = "";
					if (GoodObjectType.TryGetValue(obj.DataId, out var g)) tag = g;
					else if (BadObjectType.TryGetValue(obj.DataId, out var b)) tag = b;
					data.Add([
						obj.ObjectKind.ToString(),
						obj.DataId.ToString(),
						dist.ToString(),
						obj.Position.X.ToString("F1"),
						obj.Position.Y.ToString("F1"),
						obj.Position.Z.ToString("F1"),
						tag,
						obj.Name.ToString()
					]);
				}
				NewTable(["種類", "DataId", "距", "X", "Y", "Z", "標記", "名稱"], data);
			});
			ImGui.EndTabBar();
		}
	}
}