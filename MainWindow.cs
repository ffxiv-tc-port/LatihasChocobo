using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using ImGuiNET;
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

	private static string AbilityName(byte id) => id switch {
		0x00 => "無",
		0x15 => "陸行鳥偷取III",
		0x1D => "體力消耗降低II",
		0x2E => "經驗值提高III",
		_ => $"未知(0x{id:X2})"
	};

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
				ImGui.Text($"體力：{HpPercent}/剩餘路程：{RacePercent}");
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
					var ItemDetail = (AtkUnitBase*)GameGui.GetAddonByName("ItemDetail", 1);
					if (ItemDetail->IsVisible) {
						foreach (var TextNode in AllAtkUnitBaseByType(ItemDetail, (int)NodeType.Text)) {
							var str = TextNode.Node->GetAsAtkTextNode()->NodeText.ToString();
							if (str.Contains("性陸行鳥配種登記書")) { name = str; itemType = "配種"; }
							else if (str.Contains("性陸行鳥出賽登記書")) { name = str; itemType = "出賽"; }
							else if (str.Contains("性陸行鳥退役登記書")) { name = str; itemType = "退役"; }
						}
						foreach (var BaseComponentNodeA in AllAtkUnitBaseByType(ItemDetail, 1005)) {
							var TextNodeA = AllAtkUnitBaseByType(BaseComponentNodeA.Node->GetComponent()->UldManager, (int)NodeType.Text);
							foreach (var TextNode in TextNodeA) {
								var str = TextNode.Node->GetAsAtkTextNode()->NodeText.ToString();
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
								var ResNode = FirstAtkUnitBaseByType(BaseComponentNode.Node->GetComponent()->UldManager, (int)NodeType.Res);
								var ndata = AllAtkUnitBaseByType(ResNode, (int)NodeType.Text);
								ndata.Reverse();
								var d = new string[3];
								foreach (var str in ndata.Select(TextNode => TextNode.Node->GetAsAtkTextNode()->NodeText.ToString())) {
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