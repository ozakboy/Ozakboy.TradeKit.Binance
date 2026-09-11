namespace Ozakboy.TradeKit.Binance;

/// <summary>
/// 幣安 USDⓈ-M 合約 API 回傳的數值錯誤碼。
/// The numeric error codes the Binance USDⓈ-M futures API returns.
/// </summary>
/// <remarks>
/// <para>
/// 來源:幣安官方文件〈Error Code〉(USDⓈ-M Futures),
/// <c>https://developers.binance.com/docs/derivatives/usds-margined-futures/error-code</c>,擷取日期 2026-09-11。
/// 官方把代碼分成四段:10xx 伺服器與網路、11xx 請求參數、20xx 處理階段、40xx 篩選器與其他。
/// Source: the official Binance "Error Code" page for USDⓈ-M Futures, retrieved 2026-09-11. Binance groups the
/// codes into 10xx server and network, 11xx request issues, 20xx processing issues, and 40xx filter failures.
/// </para>
/// <para>
/// 這裡只收錄本套件實際對映的代碼。沒收錄的代碼不會被當成未知而丟掉,
/// <see cref="BinanceErrorMapper"/> 會以 <see cref="TradeErrorCodes.UnknownExchangeError"/> 承接,
/// 並把原始代碼寫進 <see cref="Error.Data"/>。
/// Only the codes this package actually maps are listed. Unlisted codes are not dropped:
/// <see cref="BinanceErrorMapper"/> catches them as <see cref="TradeErrorCodes.UnknownExchangeError"/> and keeps
/// the raw code in <see cref="Error.Data"/>.
/// </para>
/// </remarks>
public static class BinanceApiErrorCodes
{
    /// <summary>-1000 UNKNOWN:處理請求時發生未知錯誤。An unknown error occurred while processing the request.</summary>
    public const int Unknown = -1000;

    /// <summary>-1001 DISCONNECTED:內部錯誤,無法處理請求。Internal error; unable to process your request.</summary>
    public const int Disconnected = -1001;

    /// <summary>-1002 UNAUTHORIZED:沒有執行這個請求的權限。You are not authorized to execute this request.</summary>
    public const int Unauthorized = -1002;

    /// <summary>-1003 TOO_MANY_REQUESTS:請求過於頻繁,已超過每分鐘上限。Too many requests; the per-minute limit was exceeded.</summary>
    public const int TooManyRequests = -1003;

    /// <summary>-1006 UNEXPECTED_RESP:從訊息匯流排收到非預期的回應。An unexpected response was received from the message bus.</summary>
    public const int UnexpectedResponse = -1006;

    /// <summary>-1007 TIMEOUT:等待後端伺服器回應逾時。Timeout waiting for response from backend server.</summary>
    public const int Timeout = -1007;

    /// <summary>-1008 Request Throttled:伺服器目前負載過高。The server is currently overloaded with other requests.</summary>
    public const int RequestThrottled = -1008;

    /// <summary>-1013 INVALID_MESSAGE:訊息格式不合法。Invalid message format.</summary>
    public const int InvalidMessage = -1013;

    /// <summary>-1014 UNKNOWN_ORDER_COMPOSITION:不支援的委託欄位組合。Unsupported order combination.</summary>
    public const int UnknownOrderComposition = -1014;

    /// <summary>-1015 TOO_MANY_ORDERS:新單數量過多,超過下單速率上限。Too many new orders.</summary>
    public const int TooManyOrders = -1015;

    /// <summary>-1016 SERVICE_SHUTTING_DOWN:服務已停止提供。This service is no longer available.</summary>
    public const int ServiceShuttingDown = -1016;

    /// <summary>-1020 UNSUPPORTED_OPERATION:不支援這項操作。This operation is not supported.</summary>
    public const int UnsupportedOperation = -1020;

    /// <summary>
    /// -1021 INVALID_TIMESTAMP:時間戳超出 <c>recvWindow</c>,或超前伺服器時間。
    /// Timestamp for this request is outside of the recvWindow, or ahead of the server's time.
    /// </summary>
    public const int InvalidTimestamp = -1021;

    /// <summary>-1022 INVALID_SIGNATURE:這個請求的簽章不合法。Signature for this request is not valid.</summary>
    public const int InvalidSignature = -1022;

    /// <summary>-1100 ILLEGAL_CHARS:參數中含有不合法字元。Illegal characters found in a parameter.</summary>
    public const int IllegalCharacters = -1100;

    /// <summary>-1101 TOO_MANY_PARAMETERS:這個端點收到過多參數。Too many parameters sent for this endpoint.</summary>
    public const int TooManyParameters = -1101;

    /// <summary>-1102 MANDATORY_PARAM_EMPTY_OR_MALFORMED:必要參數缺漏或格式錯誤。A mandatory parameter was empty or malformed.</summary>
    public const int MandatoryParameterMissing = -1102;

    /// <summary>-1103 UNKNOWN_PARAM:送出了未知的參數。An unknown parameter was sent.</summary>
    public const int UnknownParameter = -1103;

    /// <summary>-1104 UNREAD_PARAMETERS:並非所有送出的參數都被讀取。Not all sent parameters were read.</summary>
    public const int UnreadParameters = -1104;

    /// <summary>-1105 PARAM_EMPTY:某個參數是空的。A parameter was empty.</summary>
    public const int ParameterEmpty = -1105;

    /// <summary>-1106 PARAM_NOT_REQUIRED:送出了不需要的參數。A parameter was sent when not required.</summary>
    public const int ParameterNotRequired = -1106;

    /// <summary>-1108 BAD_ASSET:資產代碼不合法。Invalid asset.</summary>
    public const int BadAsset = -1108;

    /// <summary>-1109 BAD_ACCOUNT:帳戶不合法。Invalid account.</summary>
    public const int BadAccount = -1109;

    /// <summary>-1110 BAD_INSTRUMENT_TYPE:商品類型不合法。Invalid symbolType.</summary>
    public const int BadInstrumentType = -1110;

    /// <summary>-1111 BAD_PRECISION:精度超過這個商品允許的上限。Precision is over the maximum defined for this asset.</summary>
    public const int BadPrecision = -1111;

    /// <summary>-1112 NO_DEPTH:這個商品目前掛單簿上沒有委託。No orders on book for symbol.</summary>
    public const int NoDepth = -1112;

    /// <summary>-1114 TIF_NOT_REQUIRED:不需要 <c>timeInForce</c> 卻送出了。TimeInForce sent when not required.</summary>
    public const int TimeInForceNotRequired = -1114;

    /// <summary>-1115 INVALID_TIF:<c>timeInForce</c> 不合法。Invalid timeInForce.</summary>
    public const int InvalidTimeInForce = -1115;

    /// <summary>-1116 INVALID_ORDER_TYPE:委託類型不合法。Invalid orderType.</summary>
    public const int InvalidOrderType = -1116;

    /// <summary>-1117 INVALID_SIDE:買賣方向不合法。Invalid side.</summary>
    public const int InvalidSide = -1117;

    /// <summary>-1118 EMPTY_NEW_CL_ORD_ID:新的用戶端訂單編號是空的。New client order ID was empty.</summary>
    public const int EmptyNewClientOrderId = -1118;

    /// <summary>-1119 EMPTY_ORG_CL_ORD_ID:原用戶端訂單編號是空的。Original client order ID was empty.</summary>
    public const int EmptyOriginalClientOrderId = -1119;

    /// <summary>-1120 BAD_INTERVAL:K 線週期不合法。Invalid interval.</summary>
    public const int BadInterval = -1120;

    /// <summary>-1121 BAD_SYMBOL:交易對代碼不合法。Invalid symbol.</summary>
    public const int BadSymbol = -1121;

    /// <summary>-1125 INVALID_LISTEN_KEY:這個 <c>listenKey</c> 不存在。This listenKey does not exist.</summary>
    public const int InvalidListenKey = -1125;

    /// <summary>-1127 MORE_THAN_XX_HOURS:查詢區間過長。Lookup interval is too big.</summary>
    public const int LookupIntervalTooBig = -1127;

    /// <summary>-1128 OPTIONAL_PARAMS_BAD_COMBO:選填參數的組合不合法。Combination of optional parameters invalid.</summary>
    public const int BadOptionalParameterCombination = -1128;

    /// <summary>-1130 INVALID_PARAMETER:某個參數的內容不合法。Invalid data sent for a parameter.</summary>
    public const int InvalidParameter = -1130;

    /// <summary>-1136 INVALID_NEW_ORDER_RESP_TYPE:<c>newOrderRespType</c> 不合法。Invalid newOrderRespType.</summary>
    public const int InvalidNewOrderResponseType = -1136;

    /// <summary>-2010 NEW_ORDER_REJECTED:新單在處理階段被拒絕。The new order was rejected during processing.</summary>
    public const int NewOrderRejected = -2010;

    /// <summary>-2011 CANCEL_REJECTED:撤單失敗,找不到未成交的委託。Cancel request failure as open order not found.</summary>
    public const int CancelRejected = -2011;

    /// <summary>-2012 CANCEL_ALL_FAIL:批次撤單失敗。Batch cancel failure.</summary>
    public const int CancelAllFailed = -2012;

    /// <summary>-2013 NO_SUCH_ORDER:委託不存在。Order does not exist.</summary>
    public const int NoSuchOrder = -2013;

    /// <summary>-2014 BAD_API_KEY_FMT:API 金鑰格式不合法。API-key format invalid.</summary>
    public const int BadApiKeyFormat = -2014;

    /// <summary>
    /// -2015 REJECTED_MBX_KEY:API 金鑰、IP 或權限不合法。
    /// Invalid API-key, IP, or permissions for action.
    /// </summary>
    public const int RejectedApiKey = -2015;

    /// <summary>-2016 NO_TRADING_WINDOW:找不到這個商品的交易時段。No trading window could be found for the symbol.</summary>
    public const int NoTradingWindow = -2016;

    /// <summary>-2017 API_KEYS_LOCKED:這個帳戶的 API 金鑰已被鎖定。API Keys are locked on this account.</summary>
    public const int ApiKeysLocked = -2017;

    /// <summary>-2018 BALANCE_NOT_SUFFICIENT:餘額不足。Balance is insufficient.</summary>
    public const int BalanceNotSufficient = -2018;

    /// <summary>-2019 MARGIN_NOT_SUFFICIENT:保證金不足。Margin is insufficient.</summary>
    public const int MarginNotSufficient = -2019;

    /// <summary>-2020 UNABLE_TO_FILL:無法成交。Unable to fill.</summary>
    public const int UnableToFill = -2020;

    /// <summary>-2021 ORDER_WOULD_IMMEDIATELY_TRIGGER:這張條件單會立即觸發。Order would immediately trigger.</summary>
    public const int OrderWouldImmediatelyTrigger = -2021;

    /// <summary>-2022 REDUCE_ONLY_REJECT:只減倉委託被拒絕。ReduceOnly order is rejected.</summary>
    public const int ReduceOnlyRejected = -2022;

    /// <summary>-2023 USER_IN_LIQUIDATION:帳戶正在強制平倉中。User in liquidation mode now.</summary>
    public const int UserInLiquidation = -2023;

    /// <summary>-2024 POSITION_NOT_SUFFICIENT:持倉不足以支應這張委託。Position is not sufficient.</summary>
    public const int PositionNotSufficient = -2024;

    /// <summary>-2025 MAX_OPEN_ORDER_EXCEEDED:已達未成交委託數量上限。Reached the max open order limit.</summary>
    public const int MaxOpenOrderExceeded = -2025;

    /// <summary>-2026 REDUCE_ONLY_ORDER_TYPE_NOT_SUPPORTED:只減倉不支援這個委託類型。This order type is not supported when reduceOnly.</summary>
    public const int ReduceOnlyOrderTypeNotSupported = -2026;

    /// <summary>-2027 MAX_LEVERAGE_RATIO:超過目前槓桿允許的最大持倉。Exceeded the maximum allowable position at current leverage.</summary>
    public const int MaxLeverageRatio = -2027;

    /// <summary>-2028 MIN_LEVERAGE_RATIO:槓桿低於允許值。Leverage is smaller than permitted.</summary>
    public const int MinLeverageRatio = -2028;

    /// <summary>-4003 QTY_LESS_THAN_ZERO:數量小於零。Quantity less than zero.</summary>
    public const int QuantityLessThanZero = -4003;

    /// <summary>-4013 PRICE_LESS_THAN_MIN_PRICE:價格低於最低價。Price less than min price.</summary>
    public const int PriceLessThanMinPrice = -4013;

    /// <summary>-4014 PRICE_NOT_INCREASED_BY_TICK_SIZE:價格未對齊最小跳動點。Price not increased by tick size.</summary>
    public const int PriceNotAlignedToTickSize = -4014;

    /// <summary>-4015 INVALID_CL_ORD_ID_LEN:用戶端訂單編號長度超過 36 字元。Client order id length should not be more than 36 chars.</summary>
    public const int InvalidClientOrderIdLength = -4015;

    /// <summary>-4016 PRICE_HIGHTER_THAN_MULTIPLIER_UP:價格高於標記價乘數上限。Price is higher than mark price multiplier cap.</summary>
    public const int PriceAboveMultiplierCap = -4016;

    /// <summary>-4028 INVALID_LEVERAGE:槓桿值不合法。Invalid leverage.</summary>
    public const int InvalidLeverage = -4028;

    /// <summary>-4046 NO_NEED_TO_CHANGE_MARGIN_TYPE:保證金模式不需要變更。No need to change margin type.</summary>
    public const int NoNeedToChangeMarginType = -4046;

    /// <summary>-4047 THERE_EXISTS_OPEN_ORDERS:有未成交委託,無法變更保證金模式。Margin type cannot be changed if there exists open orders.</summary>
    public const int MarginTypeChangeBlockedByOrders = -4047;

    /// <summary>-4048 THERE_EXISTS_QUANTITY:有持倉,無法變更保證金模式。Margin type cannot be changed if there exists position.</summary>
    public const int MarginTypeChangeBlockedByPosition = -4048;

    /// <summary>-4049 ADD_ISOLATED_MARGIN_REJECT:僅逐倉持倉可追加保證金。Add margin only supported for isolated positions.</summary>
    public const int AddIsolatedMarginRejected = -4049;

    /// <summary>-4050 CROSS_BALANCE_INSUFFICIENT:全倉餘額不足。Cross balance insufficient.</summary>
    public const int CrossBalanceInsufficient = -4050;

    /// <summary>-4051 ISOLATED_BALANCE_INSUFFICIENT:逐倉餘額不足。Isolated balance insufficient.</summary>
    public const int IsolatedBalanceInsufficient = -4051;

    /// <summary>-4055 AMOUNT_MUST_BE_POSITIVE:金額必須為正數。Amount must be positive.</summary>
    public const int AmountMustBePositive = -4055;

    /// <summary>-4061 POSITION_SIDE_NOT_MATCH:委託的持倉方向與帳戶設定不符。Order's position side does not match user's setting.</summary>
    public const int PositionSideNotMatch = -4061;

    /// <summary>-4062 REDUCE_ONLY_CONFLICT:<c>reduceOnly</c> 的值不合法或不恰當。Invalid or improper reduceOnly value.</summary>
    public const int ReduceOnlyConflict = -4062;

    /// <summary>-4067 POSITION_SIDE_CHANGE_EXISTS_OPEN_ORDERS:有未成交委託,無法變更持倉模式。Cannot change position side with open orders.</summary>
    public const int PositionModeChangeBlockedByOrders = -4067;

    /// <summary>-4068 POSITION_SIDE_CHANGE_EXISTS_QUANTITY:有持倉,無法變更持倉模式。Cannot change position side with an existing position.</summary>
    public const int PositionModeChangeBlockedByPosition = -4068;

    /// <summary>-4131 MARKET_ORDER_REJECT:對手方最佳價不符合篩選器。The counterparty's best price does not meet the filter.</summary>
    public const int MarketOrderRejected = -4131;

    /// <summary>-4141 SYMBOL_ALREADY_CLOSED:這個交易對已停止交易。Symbol is closed.</summary>
    public const int SymbolAlreadyClosed = -4141;

    /// <summary>-4164 MIN_NOTIONAL:委託名目價值低於下限。Order's notional must be no smaller than the minimum.</summary>
    public const int MinNotional = -4164;

    /// <summary>-4165 INVALID_TIME_INTERVAL:時間區間不合法。Invalid time interval.</summary>
    public const int InvalidTimeInterval = -4165;

    /// <summary>-4183 ISOLATED_REJECT_WITH_JOINT_MARGIN:有逐倉商品時無法切換聯合保證金。Unable to adjust to Multi-Assets mode with symbols.</summary>
    public const int IsolatedRejectedWithJointMargin = -4183;

    /// <summary>-4184 PRICE_LOWER_THAN_STOP_MULTIPLIER_DOWN:價格低於觸發價乘數下限。Price is lower than stop price multiplier floor.</summary>
    public const int PriceBelowStopMultiplierFloor = -4184;
}
