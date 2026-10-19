using System;
using System.Collections.Generic;

namespace LatihasChocobo;

/// <summary>
/// 插件送過「按下」而還沒送「放開」的鍵。只在主執行緒上用。
/// 只記帳與補放開，沒有任何會送出「按下」的路徑。
/// </summary>
internal static class HeldKeys {
	private static readonly HashSet<int> _down = new();

	internal static int Count => _down.Count;

	internal static void MarkDown(int code) => _down.Add(code);

	internal static void MarkUp(int code) => _down.Remove(code);

	/// <summary>對每個還按著的鍵呼叫一次 <paramref name="sendUp"/> 並清空；沒有按著的鍵時不呼叫。</summary>
	internal static void ReleaseAll(Action<int> sendUp) {
		if (_down.Count == 0) return;
		var codes = new int[_down.Count];
		_down.CopyTo(codes);
		_down.Clear();
		foreach (var code in codes) sendUp(code);
	}
}
