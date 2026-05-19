using System.Threading;

public static partial class CancellationTokenSourceExtensionMethods
{
    /// <summary>
    /// 安全 Cancel + Dispose 並把 ref 變數清為 null，一條龍避免 ObjectDisposedException。
    /// 物理意義：外部 field 持有的 CTS 在被另一處 Dispose 後若沒清 null，下次 if(cts != null) 判斷會誤通過，
    /// 接著 cts.Cancel() 就丟 ObjectDisposedException。本方法以 ref 接 field 強制呼叫端清空。
    /// 數值影響：對 null 輸入 no-op；對已 disposed 的 CTS 吞掉 ObjectDisposedException 不向上拋。
    /// </summary>
    /// <param name="iCts">外部持有的 CancellationTokenSource field 或 local。呼叫後一定為 null。</param>
    public static void SafeCancelAndDispose(ref CancellationTokenSource iCts)
    {
        // 區塊職責：null guard
        // 物理意義：caller 可能傳入已是 null 的 ref，這時什麼都不該做
        // 數值影響：直接 return，不丟例外
        if (iCts == null) return;

        // 區塊職責：嘗試 Cancel
        // 物理意義：若 CTS 已被別處 Dispose，Cancel 會丟 ObjectDisposedException — 吞掉視為已取消
        // 數值影響：仍會走到下方 Dispose / 清 null，確保 ref 變數最終一定是 null
        try
        {
            iCts.Cancel();
        }
        catch (System.ObjectDisposedException)
        {
            // 已被 dispose，無需再 cancel
        }

        // 區塊職責：嘗試 Dispose
        // 物理意義：Dispose 多次呼叫官方保證安全 (idempotent)，但仍包 try-catch 防呆
        // 數值影響：無論成功與否，最後一定把 ref 設 null
        try
        {
            iCts.Dispose();
        }
        catch (System.ObjectDisposedException)
        {
            // 已被 dispose
        }

        // 區塊職責：清空 ref 變數
        // 物理意義：這是本方法存在的核心理由 — 強制呼叫端 field 在出函式後一定為 null
        // 數值影響：下次 if(cts != null) 判斷會正確 fall-through
        iCts = null;
    }

    /// <summary>
    /// 只 Dispose + 清 null，不 Cancel。場景：CTS 已經自然完結（task 跑完），只想釋放資源。
    /// 物理意義：避免對已完結 CTS 多餘 Cancel 造成 listener 收到不必要的 cancel callback。
    /// 數值影響：對 null 輸入 no-op；對已 disposed 的 CTS 吞掉 ObjectDisposedException。
    /// </summary>
    /// <param name="iCts">外部持有的 CancellationTokenSource field 或 local。呼叫後一定為 null。</param>
    public static void SafeDispose(ref CancellationTokenSource iCts)
    {
        if (iCts == null) return;

        try
        {
            iCts.Dispose();
        }
        catch (System.ObjectDisposedException)
        {
            // 已被 dispose
        }

        iCts = null;
    }
}
