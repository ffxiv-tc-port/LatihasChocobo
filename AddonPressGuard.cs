using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
/// 與按鈕的 <c>IsEnabled</c> 也都還成立，此時再送一次 <c>ReceiveEvent</c> 就是原生 AccessViolation（C0000005）。
/// AVE 在 .NET Core 是 corrupted-state exception，呼叫端那圈 <c>try</c>/<c>catch</c> 完全無效 ——
/// 唯一的防護是「不要送第二次」。而 <c>Plugin.Press</c> 是 <c>Framework.Update</c> 每幀輪詢，
/// 按下到轉場前的每一幀都會再找到同一扇窗、再按一次。
/// </para>
/// <para>
/// 🔴 記下的是 <c>AtkUnitBase</c> 的位址，<b>只做等值比較，永遠不解參</b>（那塊記憶體隨時可能已經失效）。
/// </para>
/// <para>
/// 🔴🔴 時鐘用守衛自己在 <c>Framework.Update</c> 裡遞增的計數器，<b>絕對不用</b>
/// <c>UiBuilder.FrameCount</c>：後者是在 <c>UiBuilder.OnDraw()</c> 的三個「隱藏 UI」early return
/// <b>之後</b>才遞增的（過場動畫、使用者按下隱藏 UI 熱鍵、GPose，三者的隱藏開關預設全開），
/// 那段期間它完全不前進；而按下點走的是 <c>Framework.Update</c>、照常每幀被叫到
/// ⇒ 按下照常、逃生口卻永不到期。<c>Framework.Update</c> 在遊戲 update hook 內，不受 UI 隱藏影響。
/// </para>
/// <para>
/// 解除點＝輪詢到該位址從同名 addon 清單消失（掃全索引）。<see cref="ReleaseVanished"/> 掛在守衛
/// 自己的 <c>Framework.Update</c> 處理常式裡，<b>不經過</b> <c>Plugin.Press</c> 的
/// <c>Enabled</c>／<c>isRunning</c> 閘門 —— ContentsFinder 那一站是在賽道外（<c>isRunning</c> 為假）
/// 觸發的，解除掃描要是被那道閘門擋住就永遠不會解除。
/// ⚠️ 判準刻意<b>不</b>用「還可不可見／文字對不對」：關閉中那幾幀這些檢查全過，拿它們當「窗不見了」
/// 會正好在最危險的那幾幀把封鎖解除掉。
/// </para>
/// <para>
/// 逃生口（<see cref="RePressEscapeFrames"/>）：按過卻既沒消失也沒被新窗取代，等遠超過關閉所需的幀數後
/// 放行補按一次並寫 <c>Information</c>（使用者跑 LogLevel 2）。🔑 這不是節流 —— 節流記的是
/// 「上一次動作在哪一幀」，不是「這扇窗按過了」；真正的防護是「同一扇窗只按一次」。
/// 這同時也是「位址被新窗重用」的兜底：那種情況最多多等 <see cref="RePressEscapeFrames"/> 幀，不會漏按。
/// </para>
/// <para>
/// 粒度＝一扇窗（名稱＋位址）一次：本外掛會按的兩扇窗每扇只有一顆會被按的鈕
/// （RaceChocoboResult 只有 LeaveButton；ContentsFinder 只按「參加」），兩者都是
/// 「回答一次即終結」的形狀，所以不需要再併按鈕參數。
/// 只在主執行緒使用（<c>Framework.Update</c>、<c>RunOnFrameworkThread</c>、ImGui 繪製都在主執行緒）。
/// </para>
/// </remarks>
internal static class AddonPressGuard {
	/// <summary>
	/// 已經按過、那扇窗卻還沒消失時，最多再等這麼多幀才允許補按一次。
	/// 90 幀（60fps 下約 1.5 秒）遠遠大於「關閉中的那幾幀」，補按永遠不會落在危險窗口內；
	/// 也小於 <c>RequestRace</c> 的 3 秒延遲重試（約 180 幀），重試不會被誤擋。
	/// </summary>
	internal const int RePressEscapeFrames = 90;

	/// <summary>輪詢解除時最多掃到第幾個同名實例；掃到第一個空的就提早停。</summary>
	/// <remarks>
	/// <para>
	/// 📌 256 是<b>遊戲自己夾的上限</b>，不是估出來的數字：<c>AtkUnitManager::GetAddonByName</c>（台服
	/// <c>0x14064B960</c>）走的是 <c>AtkUnitManager.AllLoadedUnitsList</c>（<c>FieldOffset(0x6900)</c>），
	/// 而 <c>AtkUnitList</c> 的項目陣列是 <c>FixedSizeArray256</c>（<c>AtkUnitList.cs:8</c>：項目在 <c>+0x8</c>、
	/// <c>Count</c> 在 <c>+0x808</c> ⇒ 相差 <c>0x800</c> ＝ 256×8）；反組譯裡把 <c>Count</c> 讀進來之後緊接著
	/// <c>mov ebp, 0x100</c> 就把它硬夾成 256。同名實例不可能多過清單本身的長度，所以 256 就是真值。
	/// </para>
	/// <para>
	/// 🔑 <c>index</c> 的語意是「掃完整份清單、數第 <c>index</c> 個<b>同名</b>命中」，<b>不是</b>原始槽位編號
	/// （反組譯：逐項比對名字，命中就把傳入的 index 減 1，減到 0 才回傳）⇒ 同名實例的索引是<b>連續</b>的，
	/// 「掃到第一個空的就停」在數學上不可能漏掉還活著的實例。
	/// </para>
	/// <para>
	/// ⚠️ 這個值以前是 99，沒有任何出處。取太小的後果是 <b>fail-open</b>：被記下的那扇窗排在天花板之外時，
	/// <see cref="IsStillPresent"/> 會回 <see langword="false"/>、<see cref="ReleaseVanished"/> 就把記號清掉
	/// ＝ 對一扇還活著、正在關閉的窗解除封鎖，下一幀再送一次事件就是本檔開頭講的那種 AVE。
	/// 本外掛<b>完全沒有</b> AddonLifecycle 那一軌兜底（整個 repo 0 筆），天花板是唯一的防線。
	/// </para>
	/// <para>
	/// 📌 改大不花成本：這段只在「還有按下記號沒被解除」時才跑（同時存在的記錄實務上 0~2 個），
	/// 而且掃到第一個空的就返回 —— 正常情況下每次只跑 1~2 圈，天花板只有在真的同時開著 256 扇
	/// 同名窗時才碰得到。
	/// </para>
	/// </remarks>
	private const int MaxAddonIndex = 256;

	private readonly record struct PressRecord(nint Address, long Frame, bool Reported);

	private static readonly Dictionary<string, PressRecord> _pressed = new(StringComparer.Ordinal);

	/// <summary>
	/// 守衛自己的幀計數器。<b>只在 <see cref="OnFrameworkUpdate"/> 的第一行遞增，前面不准有任何條件</b> ——
	/// 時鐘一停，逃生口就永遠不到期。
	/// </summary>
	private static long _guardFrame;

	/// <summary>
	/// 0＝沒訂閱、1＝已訂閱。用 <c>Interlocked</c> 而不是 <c>bool</c>：重複訂閱不是「沒效果」，
	/// 而是計數器一個 tick 前進 2 ＝ 所有逃生口對半砍，會把補按往危險窗口推。
	/// </summary>
	private static int _watching;

	/// <summary>
	/// 開始計時並開始每幀掃描解除。
	/// 🔑 要在 <c>Framework.Update += Press</c> <b>之前</b>呼叫：同一個外掛內部的
	/// <c>Framework.Update</c> 沒有 per-handler 例外隔離（整條多播委派包在單一 try/catch），
	/// 排在前面的處理常式擲例外會讓後面全部那個 tick 不被呼叫，所以時鐘要排最前面。
	/// </summary>
	internal static void Attach(IFramework framework) {
		if (framework == null) return;
		if (Interlocked.CompareExchange(ref _watching, 1, 0) != 0) return;
		framework.Update += OnFrameworkUpdate;
	}

	/// <summary>外掛卸載時停掉計時與掃描，並清掉所有紀錄。</summary>
	internal static void Detach(IFramework framework) {
		if (Interlocked.CompareExchange(ref _watching, 0, 1) != 1) return;
		if (framework != null) framework.Update -= OnFrameworkUpdate;
		_pressed.Clear();
	}

	private static void OnFrameworkUpdate(IFramework framework) {
		_guardFrame++;
		ReleaseVanished();
	}

	/// <summary>
	/// 問「這扇窗現在可以按嗎」，可以的話<b>順便記下</b>已經按過。
	/// 呼叫點要放在<b>緊接著送出動作之前</b>、所有「按不按得動」的檢查之後 ——
	/// 這支一回 <see langword="true"/> 就已經把「按過了」記下去，登記完卻不按會白白封鎖到逃生口為止。
	/// </summary>
	/// <param name="addonName">addon 名稱，解除掃描用。</param>
	/// <param name="addon">要按的 addon 位址。<b>只做等值比較，這裡永遠不解參。</b></param>
	/// <returns>
	/// <see langword="true"/> ＝ 可以按（而且已經記下）；
	/// <see langword="false"/> ＝ 這一幀不要按（同一扇窗已經按過、還沒觀察到它收掉）。
	/// 回 <see langword="false"/> 對呼叫端的意義一律是「這一輪沒按到，下一輪再來」，走的是呼叫端
	/// 本來就有的「這次按不動」那條路徑（<c>Click</c> 原本遇到節點取不到時也是直接返回），不改變任何控制流。
	/// </returns>
	internal static bool TryBeginPress(string addonName, nint addon) {
		if (addon == 0 || string.IsNullOrEmpty(addonName)) return false;
		var frame = _guardFrame;
		if (_pressed.TryGetValue(addonName, out var rec) && rec.Address == addon) {
			var waited = frame - rec.Frame;
			if (waited < RePressEscapeFrames) {
				// 🔴 這就是會崩潰的那一幀。只在第一次被擋時記一行（輪詢型呼叫端每幀都會進來，不能每幀寫）。
				if (!rec.Reported) {
					_pressed[addonName] = rec with { Reported = true };
					Plugin.Log.Information($"[AddonPressGuard] 「{addonName}」（實例 0x{addon:X}）按過之後還沒觀察到它收掉，關閉中不再送事件");
				}
				return false;
			}
			// 逃生口：等了遠超過關閉所需的時間，窗仍在。視為那次沒生效（或這是另一扇重用了同一塊記憶體的新窗），放行補按一次。
			Plugin.Log.Information($"[AddonPressGuard] 「{addonName}」（實例 0x{addon:X}）按下後 {waited} 幀仍是同一扇窗，判定為上一次按下沒生效而不是正在關閉，補按一次");
		}
		_pressed[addonName] = new PressRecord(addon, frame, false);
		// 跨外掛重按診斷：只在真的送出按壓時記一行，刻意不節流。
		Plugin.Log.Information($"[按窗診斷] plugin=LatihasChocobo addon={addonName} addr=0x{addon:X} key=");
		return true;
	}

	/// <summary>
	/// 清掉「被記下的那個位址已經不在同名 addon 清單裡」的紀錄 —— 這是唯一能確定
	/// 「上一次按下的那扇已經收乾淨」的證據，下一扇同名窗才會被當成新的窗來處理。
	/// </summary>
	/// <remarks>
	/// 🔴 全程只做位址等值比較，<b>永遠不解參</b>。
	/// 📌 沒有任何紀錄時整支函式就是一個 <c>Count</c> 比較，所以可以放心無條件每幀呼叫。
	/// </remarks>
	private static void ReleaseVanished() {
		if (_pressed.Count == 0) return;
		// 先抄一份鍵：字典在迭代途中不能移除。同時存在的紀錄實務上是 0~2 個。
		foreach (var addonName in _pressed.Keys.ToArray()) {
			if (_pressed.TryGetValue(addonName, out var rec) && !IsStillPresent(addonName, rec.Address))
				_pressed.Remove(addonName);
		}
	}

	/// <summary>
	/// 掃同名 addon 的<b>全部</b>索引找那個位址還在不在。
	/// 🔴 只看索引 1 是不夠的：同名多開時我們按的那扇可能排在後面。
	/// 索引是「同名命中的第幾個」而不是槽位，所以掃到第一個空的就可以停。
	/// </summary>
	private static bool IsStillPresent(string addonName, nint address) {
		for (var i = 1; i <= MaxAddonIndex; i++) {
			var live = Plugin.GameGui.GetAddonByName(addonName, i).Address;
			if (live == nint.Zero) return false;
			if (live == address) return true;
		}
		return false;
	}
}
