namespace MessageService.Web.Dtos;

/// <summary>群組清單請求。Key 為群組 Id、Value 為該群組在這個瀏覽器的最後已讀訊息 Id。</summary>
public record GroupListRequestDto(IReadOnlyDictionary<string, long>? Read);
