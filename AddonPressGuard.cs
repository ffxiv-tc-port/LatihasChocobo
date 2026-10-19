using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;

namespace LatihasChocobo;

/// <summary>
/// 「同一扇窗按過就不要再按，直到它真的收掉」的守衛。本外掛所有按鈕點擊都經
/// <c>Plugin.Click</c> 一處送出，守衛就下沉在那裡，任何按法都繞不過。
/// </summary>
/// <remarks>
/// <para>
/// 🔴🔴 這是在防一種 <c>try</c>/<c>catch</c> 攔不住的崩潰：按下即關的窗（RaceChocoboResult 的「離開」鈕）
/// 被按下之後有「正在關閉中」的幾幀，這段期間 <c>GetAddonByName</c> 仍回得到實例、<c>IsVisible</c>
/// 與按鈕的 <c>IsEnabled</c> 也都還成立，此時再送一次 <c>ReceiveEvent</c> 就是原生 AccessViolation。
/// 唯一的防護是「不要送第二次」；而 <c>Plugin.Press</c> 是 <c>Framework.Update</c> 每幀輪詢。
/// </para>
/// <para>
/// 紀錄身分＝（窗名, 位址）；位址<b>只做等值比較，永遠不解參</b>。本外掛會按的兩扇窗每扇只有一顆鈕，
/// 都是「回答一次即終結」，所以不分按法。解除點：位址從同名清單消失（每幀輪詢）、同位址 <c>PostSetup</c>
/// （這一幀才登記的不清）、常駐窗連續隱藏 <see cref="HiddenReleaseFrames"/> 幀、逃生口。
/// <c>PreFinalize</c> 是<b>上鎖點</b>，鎖到位址消失或同位址重建。判準刻意不用「還看不看得見」。
/// </para>
/// <para>只在主執行緒使用；任何不確定一律不放行。</para>
/// </remarks>
internal static class AddonPressGuard {
	/// <summary>
	/// 已經按過、那扇窗卻還沒消失時，最多再等這麼多幀才允許補按一次（寫 <c>Information</c>）。
	/// 也小於 <c>RequestRace</c> 的 3 秒延遲重試（約 180 幀），重試不會被誤擋。
	/// </summary>
	internal const int RePressEscapeFrames = 90;

	/// <summary>同名實例最多掃到第幾個（<c>AtkUnitList</c> 的長度）；掃到第一個空的就提早停。</summary>
	private const int MaxAddonIndex = 256;

	/// <summary>常駐窗按下後，要連續觀察到「還在清單裡、但已隱藏」這麼多幀才解除；看到可見就歸零重數。</summary>
	private const int HiddenReleaseFrames = 20;

	/// <summary>超過這麼多幀沒再按過、也沒上鎖的紀錄定期回收（早就過了逃生口，不改變任何判定）。</summary>
	private const int SweepMinAgeFrames = 3600;

	private const int SweepIntervalFrames = 60;

	/// <summary>常駐窗：關閉只是隱藏，位址整場留在清單裡。送出前必須看得見，改由連續隱藏解除。</summary>
	/// <remarks>本外掛目前不按這三扇；名單與艦隊契約一致，日後接上按點時預設就是對的。</remarks>
	private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal) {
		"ContextIconMenu",
		"ContextMenu",
		"Talk",
	};

	private sealed class PressRecord(string addonName) {
		public string AddonName { get; set; } = addonName;

		/// <summary>最後一次真的按下的守衛幀號；只由 <c>PreFinalize</c> 上鎖建立的紀錄沒按過，為 <see langword="null"/>。</summary>
		public long? Frame { get; set; }

		public long LastFrame { get; set; }

		/// <summary>這次按下後的第一次阻擋已寫過 log（輪詢型呼叫端每幀都會進來）。</summary>
		public bool Reported { get; set; }

		/// <summary>登記時（或之後的輪詢）在同名清單裡找得到這個位址；只有它成立才准用「從清單消失」解除。</summary>
		public bool Listed { get; set; }

		/// <summary>看過 <c>PreFinalize</c>：位址消失或同位址重建之前一律不准按，也沒有逃生口。</summary>
		public bool Finalizing { get; set; }

		public int HiddenFrames { get; set; }
	}

	/// <summary>這一幀在同名清單裡對某個位址的觀察。零值必須是「不解除」的答案。</summary>
	private enum AddonPresence {
		/// <summary>查詢不成立（名稱空、位址 0、同名實例超過掃描上限）。</summary>
		Unknown = 0,
		NotListed = 1,
		Visible = 2,
		Hidden = 3,
	}

	private static readonly Dictionary<nint, PressRecord> _pressed = [];

	/// <summary>曾經守護過的窗名：這些名字的實例一進 <c>PreFinalize</c> 就上鎖（即使還沒按過）。</summary>
	private static readonly HashSet<string> _guardedNames = new(StringComparer.Ordinal);

	private static readonly List<nint> _releaseBuf = [];

	private static readonly HashSet<string> _hiddenReleaseReported = new(StringComparer.Ordinal);

	/// <summary>守衛自己的幀計數器，只在 <see cref="OnFrameworkUpdate"/> 第一行遞增。</summary>
	/// <remarks>🔴 刻意不用 <c>UiBuilder.FrameCount</c>：過場／隱藏 UI 期間它不前進，逃生口會永遠不到期。</remarks>
	private static long _guardFrame;

	/// <summary>0＝沒訂閱。用 <c>Interlocked</c>：重複訂閱會讓計數器一個 tick 前進 2，逃生口對半砍。</summary>
	private static int _watching;

	private static IAddonLifecycle? _lifecycle;
	private static IAddonLifecycle.AddonEventDelegate? _lifecycleHandler;

	/// <summary>開始計時、每幀解除掃描與生命週期解除。</summary>
	/// <remarks>🔑 要在 <c>Framework.Update += Press</c> 之前呼叫：同一外掛的處理常式共用一個 try/catch，時鐘要排最前面。</remarks>
	internal static void Attach(IFramework framework, IAddonLifecycle lifecycle) {
		if (framework == null || lifecycle == null) return;
		if (Interlocked.CompareExchange(ref _watching, 1, 0) != 0) return;
		framework.Update += OnFrameworkUpdate;
		_lifecycle = lifecycle;
		_lifecycleHandler = OnAddonLifecycle;
		lifecycle.RegisterListener(AddonEvent.PostSetup, _lifecycleHandler);
		lifecycle.RegisterListener(AddonEvent.PreFinalize, _lifecycleHandler);
	}

	/// <summary>外掛卸載時停掉計時、拆掉監聽器，並清掉所有紀錄。</summary>
	internal static void Detach(IFramework framework) {
		if (Interlocked.CompareExchange(ref _watching, 0, 1) != 1) return;
		if (framework != null) framework.Update -= OnFrameworkUpdate;
		if (_lifecycle != null && _lifecycleHandler != null) {
			_lifecycle.UnregisterListener(AddonEvent.PostSetup, _lifecycleHandler);
			_lifecycle.UnregisterListener(AddonEvent.PreFinalize, _lifecycleHandler);
		}
		_lifecycle = null;
		_lifecycleHandler = null;
		_pressed.Clear();
		_guardedNames.Clear();
	}

	private static void OnFrameworkUpdate(IFramework framework) {
		_guardFrame++;
		PollRecords();
		if (_guardFrame % SweepIntervalFrames == 0) SweepColdRecords();
	}

	/// <summary>
	/// 問「這扇窗現在可以按嗎」，可以的話<b>順便記下</b>已經按過。
	/// 呼叫點要放在<b>緊接著送出動作之前</b>、所有「按不按得動」的檢查之後。
	/// </summary>
	/// <param name="addonName">addon 名稱，解除掃描用；空的一律不放行。</param>
	/// <param name="addon">要按的 addon 位址。<b>只做等值比較，這裡永遠不解參。</b></param>
	/// <returns>
	/// <see langword="true"/> ＝ 可以按（而且已經記下）；<see langword="false"/> ＝ 這一輪沒按到，下一輪再來。
	/// </returns>
	internal static bool TryBeginPress(string addonName, nint addon) {
		if (addon == 0 || string.IsNullOrEmpty(addonName)) return false;
		var frame = _guardFrame;

		// 常駐窗的解除條件是「連續隱藏」，這裡是它的反面：隱藏中（或這一幀查不到）的常駐窗一律不送。
		if (PersistentAddons.Contains(addonName) && LookUp(addonName, addon, readVisibility: true) != AddonPresence.Visible)
			return false;

		_guardedNames.Add(addonName);
		if (_pressed.TryGetValue(addon, out var rec)) {
			if (rec.Finalizing) {
				ReportHold(rec, addonName, addon, "已經看到它開始銷毀、還沒看到它消失");
				return false;
			}
			if (rec.Frame is { } pressedAt) {
				var waited = frame - pressedAt;
				if (waited < RePressEscapeFrames) {
					ReportHold(rec, addonName, addon, "按過之後還沒觀察到它收掉");
					return false;
				}
				Plugin.Log.Information($"[AddonPressGuard] 「{addonName}」（實例 0x{addon:X}）按下後 {waited} 幀仍是同一扇窗，判定為上一次按下沒生效而不是正在關閉，補按一次");
			}
		} else {
			rec = new PressRecord(addonName);
			_pressed[addon] = rec;
		}

		if (!rec.Listed && LookUp(addonName, addon, readVisibility: false) == AddonPresence.Visible) {
			rec.AddonName = addonName;
			rec.Listed = true;
		}
		rec.Frame = frame;
		rec.LastFrame = frame;
		rec.Reported = false;
		rec.HiddenFrames = 0;
		// 跨外掛重按診斷：只在真的送出按壓時記一行，刻意不節流。
		Plugin.Log.Verbose($"[按窗診斷] plugin=LatihasChocobo addon={addonName} addr=0x{addon:X} key=");
		return true;
	}

	private static void ReportHold(PressRecord rec, string addonName, nint addon, string reason) {
		if (rec.Reported) return;
		rec.Reported = true;
		Plugin.Log.Information($"[AddonPressGuard] 「{addonName}」（實例 0x{addon:X}）{reason}，關閉中不再送事件");
	}

	/// <summary>
	/// 每幀的解除路徑：①找得到過、現在從同名清單消失 ⇒ 解除（含上鎖的）；②常駐窗連續隱藏 ⇒ 解除。
	/// 掛在守衛自己的 <c>Framework.Update</c>，不經過 <c>Plugin.Press</c> 的 <c>Enabled</c>／<c>isRunning</c> 閘門。
	/// </summary>
	private static void PollRecords() {
		if (_pressed.Count == 0) return;
		_releaseBuf.Clear();
		foreach (var (address, rec) in _pressed) {
			var persistent = PersistentAddons.Contains(rec.AddonName);
			var presence = LookUp(rec.AddonName, address, readVisibility: persistent);
			var present = presence != AddonPresence.NotListed;
			if (!present) {
				if (rec.Listed) _releaseBuf.Add(address);
				continue;
			}
			if (presence != AddonPresence.Unknown) rec.Listed = true;
			if (!persistent || rec.Finalizing) continue;
			if (presence != AddonPresence.Hidden) {
				rec.HiddenFrames = 0;
				continue;
			}
			if (++rec.HiddenFrames < HiddenReleaseFrames) continue;
			_releaseBuf.Add(address);
			if (_hiddenReleaseReported.Add(rec.AddonName))
				Plugin.Log.Information($"[AddonPressGuard] 「{rec.AddonName}」是常駐視窗（隱藏而不銷毀），按下記號改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個視窗名每次遊戲只寫一次。");
		}
		foreach (var address in _releaseBuf) _pressed.Remove(address);
		_releaseBuf.Clear();
	}

	/// <summary>在同名的所有實例裡找 <paramref name="address"/>；讀的是這一幀 <c>GetAddonByName</c> 交回來的實例。</summary>
	/// <remarks>索引是「同名命中的第幾個」而不是槽位，所以掃到第一個空的就可以停。不讀可見性時找到一律回 Visible。</remarks>
	private static AddonPresence LookUp(string addonName, nint address, bool readVisibility) {
		if (string.IsNullOrEmpty(addonName) || address == 0) return AddonPresence.Unknown;
		for (var i = 1; i <= MaxAddonIndex; i++) {
			var unit = Plugin.GameGui.GetAddonByName(addonName, i);
			if (unit.IsNull) return AddonPresence.NotListed;
			if (unit.Address != address) continue;
			if (!readVisibility) return AddonPresence.Visible;
			return unit.IsVisible ? AddonPresence.Visible : AddonPresence.Hidden;
		}
		return AddonPresence.Unknown;
	}

	private static void SweepColdRecords() {
		if (_pressed.Count == 0) return;
		_releaseBuf.Clear();
		foreach (var (address, rec) in _pressed) {
			if (rec.Finalizing || _guardFrame - rec.LastFrame < SweepMinAgeFrames) continue;
			_releaseBuf.Add(address);
		}
		foreach (var address in _releaseBuf) _pressed.Remove(address);
		_releaseBuf.Clear();
	}

	/// <summary>該位址進入銷毀（上鎖）或重新開始（解除）生命週期。按位址比對，不是按名稱清空。</summary>
	private static void OnAddonLifecycle(AddonEvent type, AddonArgs args) {
		var address = (nint)args.Addon.Address;
		if (address == 0) return;

		if (type == AddonEvent.PreFinalize) {
			LockFinalizing(address, args);
			return;
		}

		if (!_pressed.TryGetValue(address, out var rec)) return;
		// 這一幀才登記的 ⇒ 是有人在這次 PostSetup 派送裡按下，不是新的一扇。
		if (rec.Frame == _guardFrame) {
			rec.Finalizing = false;
			return;
		}
		_pressed.Remove(address);
	}

	private static void LockFinalizing(nint address, AddonArgs args) {
		if (_pressed.TryGetValue(address, out var rec)) {
			rec.Finalizing = true;
			return;
		}
		if (_guardedNames.Count == 0) return;
		var name = args.AddonName;
		if (!_guardedNames.Contains(name)) return;
		_pressed[address] = new PressRecord(name) { Listed = true, Finalizing = true, LastFrame = _guardFrame };
	}
}
