namespace MessageService.Options;

/// <summary>
/// Webhook 來源 IP 限制模式。
/// </summary>
public enum WebhookSourceMode
{
    /// <summary>不檢查來源 IP（預設，任何來源皆可呼叫 webhook）。</summary>
    Any,

    /// <summary>僅允許白名單內的來源 IP 呼叫 webhook。</summary>
    AllowlistOnly,
}

/// <summary>
/// LINE Webhook 來源限制選項。
/// 支援動態熱生效，可存放於 Edge 端加密設定檔。
/// </summary>
public class WebhookSourceOptions
{
    public const string SectionName = "WebhookSource";

    /// <summary>
    /// 限制模式：Any（預設）／AllowlistOnly。
    /// </summary>
    public WebhookSourceMode Mode { get; set; } = WebhookSourceMode.Any;

    /// <summary>
    /// 允許的來源 IP 或 CIDR 網段清單。
    /// </summary>
    public string[] AllowedIps { get; set; } = [];
}
