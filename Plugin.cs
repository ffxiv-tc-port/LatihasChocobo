using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static LatihasChocobo.Constant;

namespace LatihasChocobo;

[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Local")]
[SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Local")]
[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
public sealed class Plugin : IDalamudPlugin {
	public enum Direction {
		Left,
		Right,
		Front,
		FrontUp,
		InValid
	}

	private static IntPtr mwh;
	internal static bool isRunning;
	internal static int LastRaceExpGain;
	private static short _expOnRaceEnter;
	private static short _expMaxOnRaceEnter;
	private static ushort _raceStartTerritory;
	private static readonly Random _random = new();
	internal static readonly Dictionary<uint, string> GoodObjectType = new() {
		[2005024] = "黃寶箱",
		[2005025] = "藍寶箱",
		[2005038] = "藍加速",
		[2005041] = "綠體力",
		[3597] = "特殊NPC"
	};
	internal static readonly Dictionary<uint, string> BadObjectType = new() {
		[2005039] = "紫減速",
		[2005040] = "紅眩暈",
		[3595] = "障礙怪物",
		[3596] = "移動障礙怪物"
	};
	private static readonly Dictionary<int, long> PressTime = new();
	internal static bool speedHigh, canUseItem, L, H;
	internal static float HpPercent;
	internal static int RacePercent;
	private static long _lastItemUse;
	private static long LastPress2;
	private static long _lastCenterCorrect;
	private static long _lastBoundaryFix;
	private static bool _jumpRecording;
	private static long _jumpPressTick;
	private static long _jumpPeakTick;
	private static float _jumpStartY;
	private static float _jumpPeakY;
	private static Vector3 _prevPos;
	private static long _prevPosTick;
	internal static float CurrentSpeed;
	private readonly MainWindow _mainWindow;
	// ReSharper disable once MemberCanBePrivate.Global
	public readonly WindowSystem WindowSystem = new("LatihasChocobo");

	public Plugin() {
		Configuration = PluginInterface.GetPluginConfig() as MConfiguration ?? new MConfiguration();
		_mainWindow = new MainWindow();
		WindowSystem.AddWindow(_mainWindow);
		var p = new CommandInfo(OnCommand) {
			HelpMessage = "打開主介面"
		};
		CommandManager.AddHandler("/lc", p);
		CommandManager.AddHandler("/latihaschocobo", p);
		PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
		PluginInterface.UiBuilder.OpenMainUi += OnCommand;
		TryFindGameWindow(out mwh);
		TrackMemory.Init(PluginInterface.GetPluginConfigDirectory());
		Framework.Update += Press;
		ClientState.TerritoryChanged += TerritoryChanged;
		if (InRace()) { _raceStartTerritory = ClientState.TerritoryType; TrackMemory.StartRace(); isRunning = true; }
	}

	private static int PRESS_TIME => Configuration.PressMs * 10000;

	internal static MConfiguration Configuration { get; private set; } = null!;
	[PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
	[PluginService] private static IPluginLog Log { get; set; } = null!;
	[PluginService] private static ICommandManager CommandManager { get; set; } = null!;
	[PluginService] private static IFramework Framework { get; set; } = null!;
	[PluginService] internal static IObjectTable ObjectTable { get; set; } = null!;
	[PluginService] internal static IClientState ClientState { get; private set; } = null!;
	[PluginService] internal static IGameGui GameGui { get; private set; } = null!;
[PluginService] private static IChatGui ChatGui { get; set; } = null!;

	private static byte _lastRank;

	public void Dispose() {
		WindowSystem.RemoveAllWindows();
		CommandManager.RemoveHandler("/lc");
		CommandManager.RemoveHandler("/latihaschocobo");
		ClientState.TerritoryChanged -= TerritoryChanged;
		Framework.Update -= Press;
	}

	[DllImport("user32.dll")]
	private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	public static Direction GetTargetSide(IGameObject target) {
		var player = ClientState.LocalPlayer!;
		if (!BadObjectType.ContainsKey(target.DataId) && !GoodObjectType.ContainsKey(target.DataId)) return Direction.InValid;
		var playerPos = player.Position;
		var targetPos = target.Position;
		var rotation = player.Rotation;
		var distance = Vector3.Distance(playerPos, targetPos);
		var forwardDir = new Vector2((float)Math.Sin(rotation), (float)Math.Cos(rotation));
		var toTargetDir = new Vector2(targetPos.X - playerPos.X, targetPos.Z - playerPos.Z);
		var dotProduct = forwardDir.X * toTargetDir.X + forwardDir.Y * toTargetDir.Y;
		var crossProduct = forwardDir.X * toTargetDir.Y - forwardDir.Y * toTargetDir.X;
		var zDiff = targetPos.Y - playerPos.Y;
		if (zDiff < -4 || !(dotProduct > 0)) return Direction.InValid;
		var toTargetNormalized = toTargetDir.LengthSquared() > 0
			? Vector2.Normalize(toTargetDir)
			: Vector2.Zero;
		var cosTheta = Vector2.Dot(forwardDir, toTargetNormalized);
		cosTheta = Math.Clamp(cosTheta, -1f, 1f);
		var angleDeg = (float)(Math.Acos(cosTheta) * 180 / Math.PI);
		var isBadObj = BadObjectType.ContainsKey(target.DataId);
		var confirmed = isBadObj && TrackMemory.IsConfirmed(ClientState.TerritoryType, target.DataId, target.Position);
		// 速度自適應距離：目標在 ~0.75 秒到達範圍內反應，確認物件允許稍遠
		var speedReach = CurrentSpeed > 1f ? CurrentSpeed * 0.75f : 0f;
		var badBase = confirmed ? (Configuration.MaxLevelMode ? 26f : 20f) : (Configuration.MaxLevelMode ? 20f : 15f);
		var badMaxDist = Math.Clamp(MathF.Max(speedReach, badBase), 10f, 32f);
		// 紅紫陷阱：紅色正前方時跳躍，其他依實際位置向反方向閃
		if (target.DataId is 2005039 or 2005040) {
			if (distance > badMaxDist) return Direction.InValid;
			if (target.DataId == 2005040) {
				// 依速度和高低差預判起跳時機
				var baseJumpDist = Configuration.MaxLevelMode ? 20f : 14f;
				var jumpLeadDist = baseJumpDist;
				if (CurrentSpeed > 1f && Configuration.JumpPeakTime > 0f) {
					// 陷阱在坡上（yDiff > 0）：需要更早起跳，因為最高點有效高度被坡度消耗
					var yDiff = targetPos.Y - playerPos.Y;
					var slopeFactor = Configuration.JumpPeakHeight > 0.1f
						? Math.Clamp(yDiff / Configuration.JumpPeakHeight, -0.5f, 1f)
						: 0f;
					jumpLeadDist = Math.Max(baseJumpDist, CurrentSpeed * Configuration.JumpPeakTime * (1f + slopeFactor));
				}
				if (distance < jumpLeadDist) {
					var jumpAngle = confirmed ? 40f : 25f;
					if (angleDeg < jumpAngle) return Direction.FrontUp;
				}
			}
			return crossProduct > 0 ? Direction.Right : Direction.Left;
		}
		if (!isBadObj && distance < (Configuration.MaxLevelMode ? 20 : 14) && angleDeg < 20)
			return zDiff > 2 ? Direction.FrontUp : Direction.Front;
		if (!isBadObj && distance < (Configuration.MaxLevelMode ? 30 : 22) && angleDeg < 15)
			return Direction.Front;
		// 好物件幾乎正前方：自然會拿到，不轉向
		if (!isBadObj && angleDeg < 22) return Direction.InValid;
		// 好物件偏側：確認物件擴大轉向距離，未確認維持原有距離
		if (!isBadObj) {
			var confirmedGood = TrackMemory.IsConfirmed(ClientState.TerritoryType, target.DataId, target.Position);
			var goodSteerDist = confirmedGood ? 45f : (Configuration.MaxLevelMode ? 30f : 22f);
			if (distance > goodSteerDist) return Direction.InValid;
			return crossProduct > 0 ? Direction.Right : Direction.Left;
		}
		// 壞物件（怪物）距離過濾
		if (distance > badMaxDist) return Direction.InValid;
		return crossProduct > 0 ? Direction.Right : Direction.Left;
	}

	internal static IGameObject[] GetEventObjects() {
		if (ClientState.LocalPlayer is null) return [];
		return ObjectTable.Where(obj =>
			Vector3.Distance(ClientState.LocalPlayer.Position, obj.Position) < 75
			&& obj.ObjectKind == ObjectKind.EventObj
		).ToArray();
	}

	internal static IGameObject[] GetNearbyObjects(float range = 50f) {
		if (ClientState.LocalPlayer is null) return [];
		return ObjectTable.Where(obj =>
			obj.ObjectKind != ObjectKind.Player
			&& Vector3.Distance(ClientState.LocalPlayer.Position, obj.Position) < range
		).ToArray();
	}

	private static void TryPress(int code, float percent = 1000) {
		if (!PressTime.ContainsKey(code)) PressTime[code] = DateTime.Now.Ticks;
		if (DateTime.Now.Ticks - PressTime[code] <= PRESS_TIME) return;
		PressTime[code] = DateTime.Now.Ticks;
		if (!(percent > 100) && !(_random.NextDouble() * 100 < percent)) return;
		SendMessage(mwh, WM_KEYDOWN, code, 0);
	}

	// internal static unsafe AtkResNode* FirstAtkUnitBaseByType(AtkUnitBase* root, int type) => FirstAtkUnitBaseByType(root->UldManager, type);
	internal static unsafe AtkResNode* FirstAtkUnitBaseByType(AtkResNode* root, int type) {
		var prevNode = root->ChildNode;
		while (prevNode != null) {
			if ((int)prevNode->Type == type) return prevNode;
			prevNode = prevNode->PrevSiblingNode;
		}
		throw new Exception($"Failed to find BaseComponentNode: {type}");
	}

	internal static unsafe AtkResNode* FirstAtkUnitBaseByType(AtkUldManager UldManager, int type) {
		for (var i = 0; i < UldManager.NodeListCount; i++) {
			var Node = UldManager.NodeList[i];
			if ((int)Node->Type == type) return Node;
		}
		throw new Exception($"Failed to find BaseComponentNode: {type}");
	}
	// internal static unsafe AtkResNode* FirstAtkUnitBaseByType(AtkUldManager UldManager, int type) {
	//     // for (var i = 0; i < UldManager.NodeListCount; i++) {
	//     //     var Node = UldManager.NodeList[i];
	//     //     if ((int)Node->Type == type) return Node;
	//     // }
	//     // throw new Exception($"Failed to find BaseComponentNode: {type}");
	//     
	// }

	// internal static unsafe List<AtkResNodeWrapper> AllAtkUnitBaseByType(AtkUnitBase* root, int type) => AllAtkUnitBaseByType(root->UldManager, type);

	internal static unsafe List<AtkResNodeWrapper> AllAtkUnitBaseByType(AtkResNode* root, int type) {
		List<AtkResNodeWrapper> result = [];
		var prevNode = root->ChildNode;
		while (prevNode != null) {
			if ((int)prevNode->Type == type) result.Add(new AtkResNodeWrapper(prevNode));
			prevNode = prevNode->PrevSiblingNode;
		}
		return result;
	}

	internal static unsafe List<AtkResNodeWrapper> AllAtkUnitBaseByType(AtkUnitBase* root, int type) =>
		AllAtkUnitBaseByType(root->UldManager, type);


	internal static unsafe List<AtkResNodeWrapper> AllAtkUnitBaseByType(AtkUldManager UldManager, int type) {
		List<AtkResNodeWrapper> result = [];
		for (var i = 0; i < UldManager.NodeListCount; i++) {
			var Node = UldManager.NodeList[i];
			if ((int)Node->Type == type) result.Add(new AtkResNodeWrapper(Node));
		}
		return result;
	}

	internal static string CanUseItemDebug = "";

	private static unsafe bool CanUseItem() {
		AtkImageNode* FinalImageNode = null;
		try {
			var _ActionBar = (AtkUnitBase*)GameGui.GetAddonByName("_ActionBar", 1);
			foreach (var BaseComponentNodew in AllAtkUnitBaseByType(_ActionBar, 1005)) {
				var BaseComponentNode = BaseComponentNodew.Node;
				var TextNode = FirstAtkUnitBaseByType(BaseComponentNode->GetComponent()->UldManager, (int)NodeType.Text);
				if (TextNode->GetAsAtkTextNode()->NodeText.ToString() != "1") continue;
				var DragDropComponentNode = FirstAtkUnitBaseByType(BaseComponentNode->GetComponent()->UldManager, 1002);
				var IconComponentNode = FirstAtkUnitBaseByType(DragDropComponentNode->GetComponent()->UldManager, 1001);
				var TmpFinalImageNode = FirstAtkUnitBaseByType(IconComponentNode->GetComponent()->UldManager, (int)NodeType.Image);
				FinalImageNode = TmpFinalImageNode->GetAsAtkImageNode();
				break;
			}
			if (FinalImageNode == null) { CanUseItemDebug = "(null)"; return false; }
			var texture = FinalImageNode->PartsList->Parts[FinalImageNode->PartId].UldAsset->AtkTexture;
			if (texture.TextureType != TextureType.Resource) { CanUseItemDebug = $"(type={texture.TextureType})"; return false; }
			CanUseItemDebug = texture.Resource->TexFileResourceHandle->ResourceHandle.FileName.ToString();
			return !CanUseItemDebug.Contains("070101");
		} catch (Exception e) {
			Log.Warning(e.ToString());
		}
		return false;
	}

	private static unsafe void Click(AtkComponentButton* target, AtkUnitBase* addon) {
		if (!target->IsEnabled || !target->AtkResNode->IsVisible()) return;
		var btnRes = target->AtkComponentBase.OwnerNode->AtkResNode;
		var evt = btnRes.AtkEventManager.Event;
		addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, btnRes.AtkEventManager.Event);
		var resetEvt = btnRes.AtkEventManager.Event;
		resetEvt->State.StateFlags = AtkEventStateFlags.None;
		addon->ReceiveEvent(resetEvt->State.EventType, (int)resetEvt->Param, btnRes.AtkEventManager.Event);
	}

	private static unsafe bool ClickContentsFinderJoin() {
		var cfPtr = GameGui.GetAddonByName("ContentsFinder", 1);
		if (cfPtr == nint.Zero) return false;
		var cf = (AtkUnitBase*)cfPtr;
		if (!cf->IsVisible) return false;
		try {
			foreach (var nodeWrapper in AllAtkUnitBaseByType(cf, 1001)) {
				var btn = nodeWrapper.Node->GetAsAtkComponentButton();
				if (!btn->IsEnabled || !nodeWrapper.Node->IsVisible()) continue;
				try {
					var textNode = FirstAtkUnitBaseByType(nodeWrapper.Node->GetComponent()->UldManager, (int)NodeType.Text);
					var text = textNode->GetAsAtkTextNode()->NodeText.ToString();
					if (!text.Contains("參加") && !text.Contains("参加") && !text.Contains("Join")) continue;
				} catch { continue; }
				Click(btn, cf);
				Log.Info("[RequestRace] 點擊 ContentsFinder 參加按鈕");
				return true;
			}
		} catch (Exception ex) {
			Log.Warning($"[RequestRace] ContentsFinder 按鈕點擊失敗: {ex.Message}");
		}
		return false;
	}

	private static unsafe void OpenContentsFinder() {
		try {
			AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentsFinder)->Show();
		} catch (Exception ex) {
			Log.Warning($"[RequestRace] 開啟義務搜尋器失敗: {ex.Message}");
		}
	}

	internal static void RequestRace() {
		if (ClickContentsFinderJoin()) return;
		OpenContentsFinder();
		Task.Run(async () => {
			await Task.Delay(3000);
			await Framework.RunOnFrameworkThread(() => ClickContentsFinderJoin());
		});
	}

	private static void Notify() {
		unsafe { FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlaySoundEffect(36); }
	}

	private static unsafe void CheckRankUp() {
		var mgr = RaceChocoboManager.Instance();
		var rank = mgr->Rank;
		if (_lastRank != 0 && rank == 40 && _lastRank == 39 && Configuration.AutoDuty) {
			Configuration.AutoDuty = false;
			Configuration.Save();
			isRunning = false;
			ChatGui.Print("[LatihasChocobo] 已達40級，自動循環已停止。");
			Notify();
			Log.Info("[RankUp] 競賽等級升至40，AutoDuty 已關閉");
		}
		_lastRank = rank;
	}

	private static unsafe void Press(IFramework framework) {
		CheckRankUp();
		if (!Configuration.Enabled || !isRunning) return;
		// 確保遊戲視窗在前景，否則 SendMessage 可能被忽略
		if (mwh != IntPtr.Zero && GetForegroundWindow() != mwh)
			SetForegroundWindow(mwh);
		// Item — check first for fastest response
		canUseItem = CanUseItem();
		if (Configuration.AutoUseItem && canUseItem && DateTime.Now.Ticks - _lastItemUse > 100_000_000L) {
			_lastItemUse = DateTime.Now.Ticks;
			// 用 PressTime 機制持按 PRESS_TIME ms 後才放開，避免 0ms 按鍵被遊戲忽略
			PressTime[Configuration.KC_1] = DateTime.Now.Ticks;
			SendMessage(mwh, WM_KEYDOWN, Configuration.KC_1, 0);
		}
		// End
		try {
			var ptr = GameGui.GetAddonByName("RaceChocoboResult", 1);
			if (ptr != IntPtr.Zero) {
				var RaceChocoboResult = (AtkUnitBase*)ptr;
				var ButtonComponentNode = FirstAtkUnitBaseByType(RaceChocoboResult->UldManager, 1001)->GetAsAtkComponentButton();
				Click(ButtonComponentNode, RaceChocoboResult);
			}
		} catch (Exception) {
			//ignored
		}
		// Race
		speedHigh = false;
		try {
			var _RaceChocoboParameter = (AtkUnitBase*)GameGui.GetAddonByName("_RaceChocoboParameter", 1);
			var _RaceChocoboParameterUldManager = _RaceChocoboParameter->UldManager;
			var _RaceChocoboParameterSpeedNode = _RaceChocoboParameterUldManager.NodeList[_RaceChocoboParameterUldManager.NodeListCount - 1]->GetAsAtkImageNode();
			var texture = _RaceChocoboParameterSpeedNode->PartsList->Parts[_RaceChocoboParameterSpeedNode->PartId].UldAsset;
			if (_RaceChocoboParameterSpeedNode->IsVisible() && texture->AtkTexture.TextureType == TextureType.Resource)
				speedHigh = texture->AtkTexture.Resource->TexFileResourceHandle->ResourceHandle.FileName.ToString().Contains("180043");
			var CounterNode = FirstAtkUnitBaseByType(_RaceChocoboParameter->UldManager, (int)NodeType.Counter)->GetAsAtkCounterNode();
			HpPercent = float.Parse(CounterNode->NodeText.ToString()[..^1]);
		} catch (Exception e) {
			Log.Warning(e.ToString());
		}
		try {
			var found = false;
			var _ToDoList = (AtkUnitBase*)GameGui.GetAddonByName("_ToDoList", 1);
			foreach (var BaseComponentNode in AllAtkUnitBaseByType(_ToDoList, 1008)) {
				foreach (var NodeText in AllAtkUnitBaseByType(BaseComponentNode.Node->GetComponent()->UldManager, (int)NodeType.Text)) {
					var str = NodeText.Node->GetAsAtkTextNode()->NodeText.ToString();
					if (!str.StartsWith("進度：")) continue;
					RacePercent = 100 - int.Parse(str[3..^1]);
					found = true;
					break;
				}
				if (found) break;
			}
		} catch (Exception e) {
			Log.Warning(e.ToString());
		}
		// 用靜止物件計算速度
		var nowTicks = DateTime.Now.Ticks;
		// 技能2：體力充足(>70%)或進度>75%時使用，最短冷卻20秒
		var skill2Cooldown = nowTicks - LastPress2 > 200_000_000L;
		// 優先用賽道路程計算實際進度，無資料才退回 UI 百分比
		var (tPct, _, totU) = TrackMemory.GetTrackProgress(ClientState.TerritoryType, ClientState.LocalPlayer!.Position);
		var progressPct = totU > 200f ? tPct : RacePercent;
		var useSkill2 = skill2Cooldown && (HpPercent > 70 || progressPct > 75) && progressPct > 5;
		if (useSkill2) {
			LastPress2 = nowTicks;
			TryPress(Configuration.KC_2);
		}
		L = Configuration.DisableSpeedUpWhenLowHP && HpPercent < RacePercent;
		H = Configuration.EnableSpeedUpWhenHighHP && HpPercent - RacePercent >= 5;
		var notSpeedHigh = !speedHigh || _random.NextDouble() * 100 < Configuration.SpeedHighW && !L || H;
		foreach (var code in PressTime.Select(p => new {
				         p,
				         code = p.Key
			         })
			         .Select(t => new {
				         t,
				         time = t.p.Value
			         })
			         .Where(t => DateTime.Now.Ticks - t.time > PRESS_TIME)
			         .Select(t => t.t.code)) {
			if (notSpeedHigh && code == Configuration.KC_W) continue;
			SendMessage(mwh, WM_KEYUP, code, 0);
		}
		if (notSpeedHigh) TryPress(Configuration.KC_W);
		var player = ClientState.LocalPlayer!;
		// 速度計算
		if (_prevPosTick > 0) {
			var dt = (nowTicks - _prevPosTick) / 10_000_000f;
			if (dt > 0.01f) CurrentSpeed = Vector3.Distance(player.Position, _prevPos) / dt;
		}
		_prevPos = player.Position;
		_prevPosTick = nowTicks;

		// 跳躍弧線錄製：追蹤按下 SPACE 後的 Y 軌跡，計算最高點時間與高度
		if (_jumpRecording) {
			if (player.Position.Y > _jumpPeakY) {
				_jumpPeakY = player.Position.Y;
				_jumpPeakTick = nowTicks;
			}
			// 落地判定：Y 回到起跳點附近且至少過了 0.3s
			if (nowTicks - _jumpPressTick > 3_000_000L && player.Position.Y <= _jumpStartY + 0.3f) {
				var peakTime = (_jumpPeakTick - _jumpPressTick) / 10_000_000f;
				var peakHeight = _jumpPeakY - _jumpStartY;
				if (peakTime > 0.1f && peakHeight > 0.5f) {
					// 指數移動平均，避免單次異常影響
					Configuration.JumpPeakTime = Configuration.JumpPeakTime * 0.7f + peakTime * 0.3f;
					Configuration.JumpPeakHeight = Configuration.JumpPeakHeight * 0.7f + peakHeight * 0.3f;
					Configuration.Save();
				}
				_jumpRecording = false;
			}
		}

		// 邊界模式：全靠左或靠右跑，不閃避物件
		if (Configuration.BoundaryMode && Configuration.BoundaryPhase > 0) {
			TrackMemory.RecordBoundaryWaypoint(player.Position, player.Rotation);
			if (Configuration.BoundaryPhase == 1) TryPress(Configuration.KC_A);
			else TryPress(Configuration.KC_D);
			return;
		}

		IGameObject? badTarget = null, goodTarget = null;
		var badDist = float.MaxValue;
		var goodDist = float.MaxValue;
		foreach (var obj in ObjectTable) {
			if (obj.ObjectKind != ObjectKind.EventObj && obj.ObjectKind != ObjectKind.BattleNpc) continue;
			var d = Vector3.Distance(player.Position, obj.Position);
			if (BadObjectType.ContainsKey(obj.DataId)) {
				TrackMemory.RecordObject(obj.DataId, obj.Position);
				// 只有實際會產生有效閃避方向的壞物件才搶佔優先權
				if (d < badDist && GetTargetSide(obj) != Direction.InValid) { badTarget = obj; badDist = d; }
			} else if (GoodObjectType.ContainsKey(obj.DataId)) {
				TrackMemory.RecordObject(obj.DataId, obj.Position);
				if (d < goodDist) { goodTarget = obj; goodDist = d; }
			}
		}
		TrackMemory.RecordWaypoint(player.Position, player.Rotation);
		foreach (var obj in ObjectTable) {
			if (obj.ObjectKind != ObjectKind.BattleNpc || obj.DataId != 3705) continue;
			if (Vector3.Distance(player.Position, obj.Position) < 150f)
				TrackMemory.RecordOpponentWaypoint(obj.Position, obj.Rotation);
		}
		var target = badTarget ?? goodTarget;
		var isBad = target != null && BadObjectType.ContainsKey(target.DataId);
		var dir = target != null ? GetTargetSide(target) : Direction.InValid;
		// bad target 方向無效時嘗試好物件
		if (dir == Direction.InValid && goodTarget != null) {
			target = goodTarget;
			isBad = false;
			dir = GetTargetSide(target);
		}
		// 邊界修正：靠牆 5u 內強制向內，冷卻 1s，只取本地段落
		var boundaryFix = nowTicks - _lastBoundaryFix > 10_000_000L
			? TrackMemory.GetBoundaryCorrection(ClientState.TerritoryType, player.Position, 5f)
			: Direction.InValid;
		if (boundaryFix == Direction.Left) { _lastBoundaryFix = nowTicks; TryPress(Configuration.KC_A); goto endPress; }
		if (boundaryFix == Direction.Right) { _lastBoundaryFix = nowTicks; TryPress(Configuration.KC_D); goto endPress; }
		switch (dir) {
			case Direction.Left:
				SendMessage(mwh, WM_KEYUP, isBad ? Configuration.KC_A : Configuration.KC_D, 0);
				TryPress(isBad ? Configuration.KC_D : Configuration.KC_A);
				break;
			case Direction.Right:
				SendMessage(mwh, WM_KEYUP, isBad ? Configuration.KC_D : Configuration.KC_A, 0);
				TryPress(isBad ? Configuration.KC_A : Configuration.KC_D);
				break;
			case Direction.FrontUp:
				if (!_jumpRecording && !PressTime.ContainsKey(Configuration.KC_SPACE)) {
					_jumpRecording = true;
					_jumpPressTick = nowTicks;
					_jumpPeakTick = nowTicks;
					_jumpStartY = player.Position.Y;
					_jumpPeakY = player.Position.Y;
				}
				TryPress(Configuration.KC_SPACE);
				break;
			case Direction.Front:
			case Direction.InValid:
			default:
				// 1. 記憶好物件預靠近（live 目標已在上方 switch 處理；這裡處理無 live 目標時）
				var (memGoodDir, memGoodDist) = TrackMemory.GetNearestMemoryGoodObjectAhead(
					ClientState.TerritoryType, player.Position, player.Rotation, GoodObjectType.Keys);
				if (memGoodDir != Direction.InValid) {
					if (memGoodDir == Direction.Left) TryPress(Configuration.KC_A);
					else TryPress(Configuration.KC_D);
				} else {
					// 2. 有序路徑追蹤
					var pathDir = TrackMemory.GetPathDirection(ClientState.TerritoryType, player.Position, player.Rotation);
					if (pathDir == Direction.Left) TryPress(Configuration.KC_A);
					else if (pathDir == Direction.Right) TryPress(Configuration.KC_D);
				}
				break;
		}
		endPress: ;
	}

	private static bool InRace() => ClientState.TerritoryType is 389 or 390 or 391;

	private static unsafe (short exp, short max) GetCurrentExp() {
		var mgr = RaceChocoboManager.Instance();
		return (mgr->ExperienceCurrent, mgr->ExperienceMax);
	}

	private static void TerritoryChanged(ushort u) {
		if (InRace()) {
			_raceStartTerritory = u;
			if (Configuration.BoundaryMode) TrackMemory.StartBoundaryRace();
			else TrackMemory.StartRace();
			(_expOnRaceEnter, _expMaxOnRaceEnter) = GetCurrentExp();
			Task.Run(async () => {
				await Task.Delay(Configuration.AutoDutyWait * 1000);
				isRunning = true;
			});
		} else {
			if (isRunning) {
				isRunning = false;
				foreach (var code in PressTime.Keys)
					SendMessage(mwh, WM_KEYUP, code, 0);
			}
			if (_raceStartTerritory != 0) {
				if (Configuration.BoundaryMode) {
					TrackMemory.EndBoundaryRace(_raceStartTerritory, Configuration.BoundaryPhase == 1);
					if (Configuration.BoundaryPhase == 1) {
						Configuration.BoundaryPhase = 2;
					} else {
						Configuration.BoundaryMode = false;
						Configuration.BoundaryPhase = 0;
					}
					Configuration.Save();
				} else {
					TrackMemory.EndRace(_raceStartTerritory);
				}
				_raceStartTerritory = 0;
			}
			if (_expOnRaceEnter > 0 || _expMaxOnRaceEnter > 0) {
				var (expAfter, maxAfter) = GetCurrentExp();
				if (maxAfter == _expMaxOnRaceEnter)
					LastRaceExpGain = expAfter >= _expOnRaceEnter ? expAfter - _expOnRaceEnter : 0;
				else
					LastRaceExpGain = _expMaxOnRaceEnter - _expOnRaceEnter + expAfter;
				_expOnRaceEnter = 0;
				_expMaxOnRaceEnter = 0;
			}
		}
		if (Configuration.AutoDuty && Configuration.AutoDutyTerritory.Split('|').Contains(ClientState.TerritoryType.ToString())) {
			Task.Run(async () => {
				await Task.Delay(Configuration.AutoDutyWait * 1000);
				if (Configuration.Enabled) await Framework.RunOnFrameworkThread(RequestRace);
			});
		}
	}

	[DllImport("user32.dll")]
	private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

	[DllImport("user32.dll")]
	private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

	private static void TryFindGameWindow(out IntPtr hwnd) {
		hwnd = IntPtr.Zero;
		while (true) {
			hwnd = FindWindowEx(IntPtr.Zero, hwnd, "FFXIVGAME", null);
			if (hwnd == IntPtr.Zero) break;
			GetWindowThreadProcessId(hwnd, out var pid);
			if (pid == Environment.ProcessId) break;
		}
	}

	private void OnCommand(string command, string args) => _mainWindow.Toggle();

	private void OnCommand() => _mainWindow.Toggle();

	internal class AtkResNodeWrapper {
		public unsafe readonly AtkResNode* Node;

		public unsafe AtkResNodeWrapper(AtkResNode* node) {
			Node = node;
		}
	}
}