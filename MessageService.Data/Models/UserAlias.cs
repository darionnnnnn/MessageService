namespace MessageService.Models;

/// <summary>NameDisplayMode=CustomAlias 時的逐使用者別名對照。</summary>
public class UserAlias
{
    public required string UserId { get; set; }
    public required string Alias { get; set; }
}
