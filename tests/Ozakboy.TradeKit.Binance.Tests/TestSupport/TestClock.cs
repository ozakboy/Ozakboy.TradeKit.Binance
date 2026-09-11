namespace Ozakboy.TradeKit.Binance.Tests.TestSupport;

/// <summary>
/// 可手動推進的時鐘。用於驗證快取過期與時間戳,不引入額外的測試套件。
/// A manually advanced clock for exercising cache expiry and timestamps, without pulling in another test
/// package.
/// </summary>
/// <remarks>
/// 只覆寫 <see cref="TimeProvider.GetUtcNow"/>,計時器仍走基底類別的系統實作。
/// 那是刻意的:這些測試要控制的是「現在幾點」,不是「延遲多久」,而假造計時器會讓
/// <c>Ozakboy.Http</c> 內部的等待邏輯在測試裡永遠不前進。
/// Only <see cref="TimeProvider.GetUtcNow"/> is overridden; timers keep the base class's system
/// implementation. That is deliberate: these tests control what time it is, not how long a delay lasts, and a
/// faked timer would leave the waiting logic inside <c>Ozakboy.Http</c> frozen for ever.
/// </remarks>
internal sealed class TestClock : TimeProvider
{
    private long _ticks;

    public TestClock(DateTimeOffset start)
    {
        _ticks = start.UtcTicks;
    }

    public static TestClock AtFixedInstant() => new(new DateTimeOffset(2026, 9, 11, 6, 49, 44, 268, TimeSpan.Zero));

    public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
}
