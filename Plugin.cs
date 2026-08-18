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
	[PluginService] internal static IDataManager DataManager { get; private set; } = null!;
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
		// 每幀的 Press() 與 UI 繪製都會呼叫這裡；取不到玩家就當「判斷不出方向」，
		// 回 InValid 走既有的「這次不轉向」路徑（原本的 ! 會在載入畫面丟 NRE）。
		var player = ClientState.LocalPlayer;
		if (player == null) return Direction.InValid;
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
		// root 為 null 時不解參考，直接落到下面既有的 throw（不新增例外型別）。
		var prevNode = root == null ? null : root->ChildNode;
		while (prevNode != null) {
			if ((int)prevNode->Type == type) return prevNode;
			prevNode = prevNode->PrevSiblingNode;
		}
		throw new Exception($"Failed to find BaseComponentNode: {type}");
	}

	internal static unsafe AtkResNode* FirstAtkUnitBaseByType(AtkUldManager UldManager, int type) {
		var node = FindFirstNodeByType(UldManager, type);
		if (node != null) return node;
		throw new Exception($"Failed to find BaseComponentNode: {type}");
	}

	/// <summary>
	/// 不丟例外版的 <see cref="FirstAtkUnitBaseByType(AtkUldManager,int)"/>：找不到回 <c>null</c>。
	/// <para>每幀路徑要用這個版本 —— 丟例外那版在「addon 還沒建好」的每一幀都會丟一次，
	/// 呼叫端接住後又記一行警告，等於每幀洗版。</para>
	/// <para>NodeListCount 可能在 NodeList 還沒配置時就非 0；節點陣列本身也可能有空洞。兩者都不解參考。</para>
	/// </summary>
	internal static unsafe AtkResNode* FindFirstNodeByType(AtkUldManager UldManager, int type) {
		if (UldManager.NodeList == null) return null;
		for (var i = 0; i < UldManager.NodeListCount; i++) {
			var Node = UldManager.NodeList[i];
			if (Node == null) continue;
			if ((int)Node->Type == type) return Node;
		}
		return null;
	}

	/// <summary>
	/// 安全地取節點底下的元件。
	/// <para>🔴 <c>GetComponent()</c> 是 <c>[MemberFunction]</c> 原生呼叫：對 null 節點呼叫即存取違規，
	/// 而且**元件還沒建好時它會回 null**，接著解 <c>-&gt;UldManager</c> 是第二個入口。
	/// AVE 是 .NET Core 的 corrupted-state exception，外圈 <c>try/catch</c> 完全攔不到。</para>
	/// </summary>
	internal static unsafe AtkComponentBase* ComponentOf(AtkResNode* node) => node == null ? null : node->GetComponent();

	/// <summary>
	/// 安全地取文字節點的字串，取不到一律回空字串。
	/// <para>🔴 <c>GetAsAtkTextNode()</c> 同樣是 <c>[MemberFunction]</c> 原生呼叫：對 null 節點呼叫即存取違規；
	/// 節點型別不符時回 null，再解 <c>-&gt;NodeText</c> 又是一個入口。</para>
	/// <para>回空字串是安全的失敗方向：既有的 <c>Contains</c> / <c>StartsWith</c> 判斷對空字串本來就是 false。</para>
	/// </summary>
	internal static unsafe string TextOfNode(AtkResNode* node) {
		if (node == null) return string.Empty;
		var textNode = node->GetAsAtkTextNode();
		return textNode == null ? string.Empty : textNode->NodeText.ToString();
	}

	/// <summary>
	/// 安全地取圖片節點目前使用的 <c>AtkTexture</c>，取不到回 <c>null</c>。
	/// <para>🔴 <c>PartsList-&gt;Parts</c> 是原生指標陣列，**只判空是半套**：
	/// <c>PartId</c> 越界讀到的是堆積垃圾不是 null，再解 <c>UldAsset</c> 就是存取違規。
	/// 上界的權威來源是 <c>AtkUldPartsList.PartCount</c>（<c>+0x4</c>）。</para>
	/// </summary>
	internal static unsafe AtkTexture* TextureOfImageNode(AtkImageNode* imageNode) {
		if (imageNode == null) return null;
		var partsList = imageNode->PartsList;
		if (partsList == null || partsList->Parts == null || imageNode->PartId >= partsList->PartCount) return null;
		var asset = partsList->Parts[imageNode->PartId].UldAsset;
		return asset == null ? null : &asset->AtkTexture;
	}

	/// <summary>
	/// 安全地取材質的檔名，取不到回 <c>null</c>（<c>null</c> ＝ 讀不到，和「檔名是空字串」分開）。
	/// </summary>
	internal static unsafe string? TextureFileName(AtkTexture* texture) {
		if (texture == null || texture->TextureType != TextureType.Resource) return null;
		var resource = texture->Resource;
		if (resource == null || resource->TexFileResourceHandle == null) return null;
		return resource->TexFileResourceHandle->ResourceHandle.FileName.ToString();
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
		if (root == null) return result;
		var prevNode = root->ChildNode;
		while (prevNode != null) {
			if ((int)prevNode->Type == type) result.Add(new AtkResNodeWrapper(prevNode));
			prevNode = prevNode->PrevSiblingNode;
		}
		return result;
	}

	internal static unsafe List<AtkResNodeWrapper> AllAtkUnitBaseByType(AtkUnitBase* root, int type) =>
		root == null ? [] : AllAtkUnitBaseByType(root->UldManager, type);


	internal static unsafe List<AtkResNodeWrapper> AllAtkUnitBaseByType(AtkUldManager UldManager, int type) {
		List<AtkResNodeWrapper> result = [];
		if (UldManager.NodeList == null) return result;
		for (var i = 0; i < UldManager.NodeListCount; i++) {
			var Node = UldManager.NodeList[i];
			if (Node == null) continue;
			if ((int)Node->Type == type) result.Add(new AtkResNodeWrapper(Node));
		}
		return result;
	}

	internal static string CanUseItemDebug = "";

	/// <summary>
	/// 判斷技能列第 1 格上是不是掛著可用的道具。
	/// <para>🔴 每幀路徑（<see cref="Press"/> 每一幀呼叫）。整條 <c>節點-&gt;元件-&gt;節點-&gt;元件…</c> 都是
	/// <c>[MemberFunction]</c> 原生呼叫，任一層回 null 之後繼續 <c>-&gt;</c> 就是攔不到的存取違規
	/// （AVE 是 corrupted-state exception，下面那圈 <c>try/catch</c> 對它完全無效）。
	/// 所以逐層驗，取不到就收斂成既有的「判斷不出來 ⇒ 回 false」，不動作也不記錄。</para>
	/// </summary>
	private static unsafe bool CanUseItem() {
		AtkImageNode* FinalImageNode = null;
		try {
			var _ActionBar = (AtkUnitBase*)GameGui.GetAddonByName("_ActionBar", 1).Address;
			foreach (var BaseComponentNodew in AllAtkUnitBaseByType(_ActionBar, 1005)) {
				var slot = ComponentOf(BaseComponentNodew.Node);
				if (slot == null) continue;
				var TextNode = FindFirstNodeByType(slot->UldManager, (int)NodeType.Text);
				if (TextOfNode(TextNode) != "1") continue;
				var dragDrop = ComponentOf(FindFirstNodeByType(slot->UldManager, 1002));
				if (dragDrop == null) break;
				var icon = ComponentOf(FindFirstNodeByType(dragDrop->UldManager, 1001));
				if (icon == null) break;
				var TmpFinalImageNode = FindFirstNodeByType(icon->UldManager, (int)NodeType.Image);
				if (TmpFinalImageNode == null) break;
				FinalImageNode = TmpFinalImageNode->GetAsAtkImageNode();
				break;
			}
			if (FinalImageNode == null) { CanUseItemDebug = "(null)"; return false; }
			var texture = TextureOfImageNode(FinalImageNode);
			if (texture == null) { CanUseItemDebug = "(parts)"; return false; }
			if (texture->TextureType != TextureType.Resource) { CanUseItemDebug = $"(type={texture->TextureType})"; return false; }
			var fileName = TextureFileName(texture);
			if (fileName == null) { CanUseItemDebug = "(res)"; return false; }
			CanUseItemDebug = fileName;
			return !CanUseItemDebug.Contains("070101");
		} catch (Exception e) {
			Log.Warning(e.ToString());
		}
		return false;
	}

	/// <summary>
	/// 點擊按鈕元件（複用按鈕自身既有的事件）。任何一層取不到就當「這次按不動」直接返回。
	/// <para>🔴 <c>AtkComponentBase</c> 有<b>兩個</b>指標欄位：<c>+0xA0</c> 的 <c>AtkResNode</c> 與
	/// <c>+0xA8</c> 的 <c>OwnerNode</c>，而 CS 的 <c>IsEnabled</c> 解的是<b>後者</b>
	/// （<c>OwnerNode-&gt;AtkResNode.NodeFlags.HasFlag(...)</c>）且對它零 null 檢查。
	/// 所以「先驗 AtkResNode 再讀 IsEnabled」擋不到東西，必須在讀 <c>IsEnabled</c> 之前
	/// 就把 <c>OwnerNode</c> 驗掉。AVE 是 .NET Core 的 corrupted-state exception，
	/// 呼叫端那圈 <c>try/catch</c> 完全攔不到。</para>
	/// </summary>
	private static unsafe void Click(AtkComponentButton* target, AtkUnitBase* addon) {
		if (target == null || addon == null) return;

		var owner = target->AtkComponentBase.OwnerNode;
		if (owner == null) return;

		var res = target->AtkComponentBase.AtkResNode;
		if (res == null) return;

		if (!target->IsEnabled || !res->IsVisible()) return;

		// 和原本一樣先把 OwnerNode 的 AtkResNode 複製成區域變數再取事件，
		// 兩次 ReceiveEvent 用的是同一個事件指標（不做第二次即時重讀）。
		var btnRes = owner->AtkResNode;
		var evt = btnRes.AtkEventManager.Event;
		if (evt == null) return;

		addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
		evt->State.StateFlags = AtkEventStateFlags.None;
		addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
	}

	private static unsafe bool ClickContentsFinderJoin() {
		var cfPtr = GameGui.GetAddonByName("ContentsFinder", 1).Address;
		if (cfPtr == nint.Zero) return false;
		var cf = (AtkUnitBase*)cfPtr;
		if (!cf->IsVisible) return false;
		try {
			foreach (var nodeWrapper in AllAtkUnitBaseByType(cf, 1001)) {
				var node = nodeWrapper.Node;
				if (node == null) continue;
				// GetAsAtkComponentButton 是 [MemberFunction] 原生呼叫：對 null 節點呼叫會 AVE，
				// 而且元件本身還沒建好時它會回 null——回 null 之後直接讀 IsEnabled
				// （解的是沒驗過的 OwnerNode）就是第二個存取違規入口。
				var btn = node->GetAsAtkComponentButton();
				if (btn == null || btn->AtkComponentBase.OwnerNode == null) continue;
				if (!btn->IsEnabled || !node->IsVisible()) continue;
				// 直接用上面已經驗過非 null 的 btn，不再呼叫一次 GetComponent()——
				// 那是另一個原生呼叫，回 null 時 ->UldManager 又是一個存取違規入口。
				// GetAsAtkTextNode() 同樣是原生呼叫且會回 null，所以走 TextOfNode 逐層驗
				// （原本外面那圈 try/catch 對 AVE 完全無效，找不到節點也不需要靠丟例外來 continue）。
				var text = TextOfNode(FindFirstNodeByType(btn->AtkComponentBase.UldManager, (int)NodeType.Text));
				if (!text.Contains("參加") && !text.Contains("参加") && !text.Contains("Join")) continue;
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

	/// <summary>
	/// 從 <c>_RaceChocoboParameter</c> 讀「加速狀態」與「體力百分比」。
	/// <para>🔴 每幀路徑。addon 在賽前／賽後／載入中都不存在，所以「取不到」是常態不是異常：
	/// 一律靜默返回（<see cref="speedHigh"/> 維持 false、<see cref="HpPercent"/> 沿用前值），
	/// **不記錄** —— 這裡記一行就是每幀洗版。</para>
	/// <para>🔴 <c>NodeList</c> 是原生指標陣列，只判空是半套：<c>NodeListCount</c> 為 0 時
	/// <c>[Count - 1]</c> 是**索引 -1**，讀到的是陣列前方的堆積垃圾（不是 null），
	/// 拿去呼叫 <c>GetAsAtkImageNode()</c>（<c>[MemberFunction]</c> 原生呼叫）就是攔不到的存取違規。
	/// 所以要①容器判空②上界／下界檢查③取出的節點再判空。</para>
	/// </summary>
	private static unsafe void UpdateRaceParameter() {
		var ptr = GameGui.GetAddonByName("_RaceChocoboParameter", 1).Address;
		if (ptr == IntPtr.Zero) return;
		var addon = (AtkUnitBase*)ptr;

		var uld = addon->UldManager;
		if (uld.NodeList != null && uld.NodeListCount > 0) {
			var lastNode = uld.NodeList[uld.NodeListCount - 1];
			if (lastNode != null) {
				var speedNode = lastNode->GetAsAtkImageNode();
				// IsVisible() 也是 [MemberFunction]，一樣要在節點確定非 null 之後才叫。
				if (speedNode != null && speedNode->IsVisible()) {
					var fileName = TextureFileName(TextureOfImageNode(speedNode));
					if (fileName != null) speedHigh = fileName.Contains("180043");
				}
			}
		}

		var counterNode = FindFirstNodeByType(addon->UldManager, (int)NodeType.Counter);
		if (counterNode == null) return;
		var counter = counterNode->GetAsAtkCounterNode();
		if (counter == null) return;
		var hpText = counter->NodeText.ToString();
		// 原本是 float.Parse(text[..^1])：文字還沒填好時會丟例外被外圈接住，HpPercent 沿用前值。
		// 改成 TryParse，結果一樣（沿用前值）但不會每幀丟例外＋記一行警告。
		if (hpText.Length >= 2 && float.TryParse(hpText[..^1], out var hp)) HpPercent = hp;
	}

	/// <summary>
	/// 從 <c>_ToDoList</c> 讀賽道進度百分比。🔴 每幀路徑，取不到就沿用前一次的
	/// <see cref="RacePercent"/>（不歸零 —— 歸零會被當成「剛起跑」而改變技能與轉向判斷）。
	/// </summary>
	private static unsafe void UpdateRacePercent() {
		var found = false;
		var _ToDoList = (AtkUnitBase*)GameGui.GetAddonByName("_ToDoList", 1).Address;
		foreach (var BaseComponentNode in AllAtkUnitBaseByType(_ToDoList, 1008)) {
			// GetComponent() 回 null 時 ->UldManager 是存取違規入口（見 ComponentOf 的說明）。
			var component = ComponentOf(BaseComponentNode.Node);
			if (component == null) continue;
			foreach (var NodeText in AllAtkUnitBaseByType(component->UldManager, (int)NodeType.Text)) {
				var str = TextOfNode(NodeText.Node);
				if (!str.StartsWith("進度：")) continue;
				// 「進度：」3 字 + 至少 1 位數字 + 結尾的 '%'。解析失敗沿用前值。
				if (str.Length >= 5 && int.TryParse(str[3..^1], out var pct)) RacePercent = 100 - pct;
				found = true;
				break;
			}
			if (found) break;
		}
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
			var ptr = GameGui.GetAddonByName("RaceChocoboResult", 1).Address;
			if (ptr != IntPtr.Zero) {
				var RaceChocoboResult = (AtkUnitBase*)ptr;
				// 節點找不到時 FirstAtkUnitBaseByType 會丟一般例外（外面這圈接得到）；
				// 但 GetAsAtkComponentButton 是 [MemberFunction] 原生呼叫，對 null 節點呼叫會 AVE
				// （corrupted-state exception，try/catch 攔不到），所以先驗節點再叫。
				var resultNode = FirstAtkUnitBaseByType(RaceChocoboResult->UldManager, 1001);
				if (resultNode != null)
					Click(resultNode->GetAsAtkComponentButton(), RaceChocoboResult);
			}
		} catch (Exception) {
			//ignored
		}
		// Race
		speedHigh = false;
		try {
			UpdateRaceParameter();
		} catch (Exception e) {
			Log.Warning(e.ToString());
		}
		try {
			UpdateRacePercent();
		} catch (Exception e) {
			Log.Warning(e.ToString());
		}
		// 用靜止物件計算速度
		var nowTicks = DateTime.Now.Ticks;
		// 技能2：體力充足(>70%)或進度>75%時使用，最短冷卻20秒
		var skill2Cooldown = nowTicks - LastPress2 > 200_000_000L;
		// 🔴 每幀路徑上唯一沒驗過的解參考：載入畫面／登出的瞬間 LocalPlayer 會是 null，
		// 而 isRunning 這時仍可能是 true（TerritoryChanged 是延遲 AutoDutyWait 秒才設 true 的，
		// 載入還沒跑完就已經翻成 true）。原本寫 ClientState.LocalPlayer!：NRE 會直接竄出
		// Framework.Update，被 Dalamud 的 dispatcher 接住後**每幀**記一行 error，
		// 而且這行之後的整段（速度計算、跳躍錄製、物件掃描、轉向）從此每幀都不執行
		// ⇒ 對使用者的表現是「外掛沒反應」，不是「外掛報錯」。
		// 取不到玩家就當這一幀沒有東西可判斷直接跳過（和原本例外竄出的效果一致，只是不洗版）；
		// 按鍵不會卡住 —— 離開賽道時 TerritoryChanged 已經負責放開 PressTime 裡的所有鍵。
		var player = ClientState.LocalPlayer;
		if (player == null) return;
		// 優先用賽道路程計算實際進度，無資料才退回 UI 百分比
		var (tPct, _, totU) = TrackMemory.GetTrackProgress(ClientState.TerritoryType, player.Position);
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
		// 速度計算（player 已在上面驗過非 null）
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