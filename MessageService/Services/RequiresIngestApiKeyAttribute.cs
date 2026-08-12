namespace MessageService.Services;

/// <summary>掛在 controller 上，宣告它只在設定了 Ingest:ApiKey 時存在。跟
/// <see cref="EnabledInModesAttribute"/> 是獨立的兩道閘門、可以同時掛在同一個 controller 上——
/// 模式決定「這個角色該不該有這個功能」，這個 attribute 決定「金鑰有沒有配置好」。
/// 沒設金鑰時整個 controller 從路由消失（404），不是啟動失敗，也不是回 401——避免 Full 模式
/// 單機部署（預設不需要 ingest API）意外多開一個沒人保護的寫入端點。實際生效見
/// <see cref="DeploymentModeConvention"/>。</summary>
[AttributeUsage(AttributeTargets.Class)]
public class RequiresIngestApiKeyAttribute : Attribute;
