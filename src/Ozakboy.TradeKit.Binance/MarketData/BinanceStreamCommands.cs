using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Ozakboy.TradeKit.Binance.MarketData;

/// <summary>
/// 組出幣安行情串流的控制訊息(訂閱、取消訂閱、列出訂閱)。
/// Builds the control messages of the Binance market streams: subscribe, unsubscribe, and list.
/// </summary>
/// <remarks>
/// <para>
/// 格式是 <c>{"method":"SUBSCRIBE","params":["btcusdt@kline_1m"],"id":1}</c>,
/// 成功回 <c>{"result":null,"id":1}</c>,<c>LIST_SUBSCRIPTIONS</c> 回
/// <c>{"result":["btcusdt@kline_1m"],"id":2}</c>,失敗回
/// <c>{"error":{"code":2,"msg":"Invalid request: …"},"id":4}</c>(2026-09-11 於 Testnet 實測)。
/// The shape is <c>{"method":"SUBSCRIBE","params":["btcusdt@kline_1m"],"id":1}</c>, answered with
/// <c>{"result":null,"id":1}</c>; <c>LIST_SUBSCRIPTIONS</c> answers with the array of names, and a rejection
/// answers with <c>{"error":{"code":2,"msg":"Invalid request: …"},"id":4}</c>. All measured on the testnet on
/// 2026-09-11.
/// </para>
/// <para>
/// 訊息一律用 <see cref="Utf8JsonWriter"/> 產生而不是字串拼接。串流名稱雖然已經過字元檢查,
/// 但控制訊息只要有一處引號沒跳脫,幣安就會回 <c>error</c> 並<b>直接關閉連線</b>(實測:送出一則不合法的
/// 控制訊息之後,同一條連線上的後續請求全部沒有回應),接著就是一輪沒有必要的重連。
/// The messages are written with <see cref="Utf8JsonWriter"/> rather than concatenated. Stream names are
/// already character-checked, but a single unescaped quote in a control message earns an <c>error</c> reply and
/// <b>an immediate disconnect</b> — measured: after one malformed control message, nothing else on that
/// connection is ever answered — followed by a reconnect that need never have happened.
/// </para>
/// </remarks>
internal static class BinanceStreamCommands
{
    /// <summary>訂閱。Subscribe.</summary>
    public const string SubscribeMethod = "SUBSCRIBE";

    /// <summary>取消訂閱。Unsubscribe.</summary>
    public const string UnsubscribeMethod = "UNSUBSCRIBE";

    /// <summary>
    /// 列出目前的訂閱。本套件拿它當應用層心跳:它<b>一定</b>會有回覆,
    /// 而「多久沒收到任何訊息」正是 <c>Ozakboy.WebSockets</c> 判斷連線死活的依據。
    /// Lists the current subscriptions. This package uses it as the application-level heartbeat because it
    /// <b>always</b> draws a reply, and "how long since any message arrived" is exactly how
    /// <c>Ozakboy.WebSockets</c> decides whether a connection is still alive.
    /// </summary>
    public const string ListSubscriptionsMethod = "LIST_SUBSCRIPTIONS";

    /// <summary>成功回覆的欄位名。The property carrying a successful reply.</summary>
    public const string ResultProperty = "result";

    /// <summary>失敗回覆的欄位名。The property carrying a rejection.</summary>
    public const string ErrorProperty = "error";

    /// <summary>回覆對應的請求編號欄位名。The property echoing the request id.</summary>
    public const string IdProperty = "id";

    /// <summary>失敗回覆裡的代碼欄位名。The code inside a rejection.</summary>
    public const string ErrorCodeProperty = "code";

    /// <summary>失敗回覆裡的訊息欄位名。The message inside a rejection.</summary>
    public const string ErrorMessageProperty = "msg";

    private const string MethodProperty = "method";

    private const string ParamsProperty = "params";

    /// <summary>
    /// 組出訂閱訊息。
    /// Builds a subscribe message.
    /// </summary>
    /// <param name="id">請求編號,回覆會原樣帶回。The request id, echoed in the reply.</param>
    /// <param name="streamNames">串流名稱。The stream names.</param>
    /// <returns>控制訊息的 JSON 文字。The control message as JSON text.</returns>
    public static string Subscribe(long id, IReadOnlyList<string> streamNames) =>
        Build(SubscribeMethod, id, streamNames);

    /// <summary>
    /// 組出取消訂閱訊息。
    /// Builds an unsubscribe message.
    /// </summary>
    /// <param name="id">請求編號,回覆會原樣帶回。The request id, echoed in the reply.</param>
    /// <param name="streamNames">串流名稱。The stream names.</param>
    /// <returns>控制訊息的 JSON 文字。The control message as JSON text.</returns>
    public static string Unsubscribe(long id, IReadOnlyList<string> streamNames) =>
        Build(UnsubscribeMethod, id, streamNames);

    /// <summary>
    /// 組出列出訂閱的訊息。
    /// Builds a list-subscriptions message.
    /// </summary>
    /// <param name="id">請求編號,回覆會原樣帶回。The request id, echoed in the reply.</param>
    /// <returns>控制訊息的 JSON 文字。The control message as JSON text.</returns>
    public static string ListSubscriptions(long id) => Build(ListSubscriptionsMethod, id, null);

    private static string Build(string method, long id, IReadOnlyList<string>? streamNames)
    {
        var buffer = new ArrayBufferWriter<byte>(128);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(MethodProperty, method);

            if (streamNames is not null)
            {
                writer.WriteStartArray(ParamsProperty);

                foreach (var name in streamNames)
                {
                    writer.WriteStringValue(name);
                }

                writer.WriteEndArray();
            }

            writer.WriteNumber(IdProperty, id);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
