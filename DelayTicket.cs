using System.Threading;

namespace LatihasChocobo;

/// <summary>
/// 延遲動作的作廢票：排程當下抄一張，換區或卸載時 <see cref="Advance"/>，到期時票對不上就不做。
/// 可跨執行緒呼叫；但「驗票→動作」之間不上鎖，這兩步要和 <see cref="Advance"/> 在同一條執行緒才沒有空窗。
/// </summary>
internal static class DelayTicket {
	private static int _epoch;

	internal static int Current => Volatile.Read(ref _epoch);

	/// <summary>作廢所有已發出的票，回傳新的一張。</summary>
	internal static int Advance() => Interlocked.Increment(ref _epoch);

	internal static bool IsCurrent(int ticket) => Volatile.Read(ref _epoch) == ticket;
}
