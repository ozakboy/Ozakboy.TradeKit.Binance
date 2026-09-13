using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Ozakboy.TradeKit.Binance.Tests.TestSupport;

/// <summary>
/// 把寫進來的每一則日誌格式化成字串留下來,讓「日誌裡有沒有出現某個值」變成一個可以斷言的問題。
/// Formats every log entry written to it and keeps the text, so that "did this value reach a log" becomes
/// something a test can assert on.
/// </summary>
/// <remarks>
/// 留的是<b>格式化之後</b>的訊息,不是結構化的參數。這一點是重點:祕密外流幾乎都發生在格式化那一步 ——
/// 參數被換進訊息字串之後,欄位名就不存在了,任何以欄位名為準的遮罩規則從那一刻起都看不見它。
/// The text kept is the <b>formatted</b> message rather than the structured arguments, and that is the point:
/// a secret escapes at formatting time, because once an argument has been substituted into the message string
/// the field name is gone and no rule that works from field names can see it any more.
/// </remarks>
internal sealed class CollectingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<string> _messages = new();

    /// <summary>
    /// 目前收集到的訊息。
    /// The messages collected so far.
    /// </summary>
    public IReadOnlyList<string> Messages => [.. _messages];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CollectingLogger(_messages);

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        // 這個工廠自己就是輸出端,沒有提供者可以掛。
        // This factory is the sink itself, so there is nothing for a provider to attach to.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 沒有需要釋放的資源;介面要求要有這個方法。
        // Nothing to release; the interface requires the method.
    }

    private sealed class CollectingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages;

        public CollectingLogger(ConcurrentQueue<string> messages)
        {
            _messages = messages;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        // 一律啟用。測試要看的正是「這一層到底寫了什麼出去」,依層級過濾會讓答案取決於設定。
        // Always enabled: what this layer actually writes out is the whole question, and filtering by level
        // would make the answer depend on configuration.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            // 例外文字也留下來:憑證夾在例外訊息裡出現,和夾在日誌訊息裡是同一件事。
            // The exception text is kept too: a credential carried by an exception message is the same leak as
            // one carried by the log message.
            _messages.Enqueue(exception is null
                ? formatter(state, exception)
                : $"{formatter(state, exception)} {exception}");
        }
    }
}
