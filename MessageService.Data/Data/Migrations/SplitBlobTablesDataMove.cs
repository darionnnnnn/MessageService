namespace MessageService.Data.Data.Migrations;

/// <summary>SplitBlobTables 這一支 migration 把三顆 blob 從父表搬到子表的 SQL。
/// 兩個 provider 各自的 migration 都引用這裡，測試也直接跑同一份字串——避免「測試驗的是抄過去的
/// 另一份 SQL」這種假通過。
///
/// 三段都寫成 NOT EXISTS，讓搬遷本身可以安全重跑：升級流程允許用
/// <c>dotnet ef migrations script</c> 產生 SQL 後人工分段執行，中途中斷（行程被 IIS 砍、斷電、
/// 磁碟寫滿）就會留下「資料已搬、__EFMigrationsHistory 未寫」的半途狀態；沒有 NOT EXISTS 的話
/// 重跑會撞主鍵，而且每次重跑都撞同一個地方，只能人工進資料庫處理。</summary>
public static class SplitBlobTablesDataMove
{
    public const string Sqlite = """
        INSERT INTO GroupPictures (GroupId, Content)
        SELECT g.GroupId, g.PictureContent
        FROM Groups g
        WHERE g.PictureContent IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM GroupPictures p WHERE p.GroupId = g.GroupId);

        INSERT INTO GroupMemberPictures (GroupId, UserId, Content)
        SELECT m.GroupId, m.UserId, m.PictureContent
        FROM GroupMembers m
        WHERE m.PictureContent IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM GroupMemberPictures p
              WHERE p.GroupId = m.GroupId AND p.UserId = m.UserId);

        INSERT INTO MessageContentBlobs (MessageContentId, Content)
        SELECT c.Id, c.Content
        FROM MessageContents c
        WHERE c.Content IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM MessageContentBlobs b WHERE b.MessageContentId = c.Id);
        """;

    public const string SqlServer = """
        INSERT INTO [GroupPictures] ([GroupId], [Content])
        SELECT g.[GroupId], g.[PictureContent]
        FROM [Groups] g
        WHERE g.[PictureContent] IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM [GroupPictures] p WHERE p.[GroupId] = g.[GroupId]);

        INSERT INTO [GroupMemberPictures] ([GroupId], [UserId], [Content])
        SELECT m.[GroupId], m.[UserId], m.[PictureContent]
        FROM [GroupMembers] m
        WHERE m.[PictureContent] IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM [GroupMemberPictures] p
              WHERE p.[GroupId] = m.[GroupId] AND p.[UserId] = m.[UserId]);

        INSERT INTO [MessageContentBlobs] ([MessageContentId], [Content])
        SELECT c.[Id], c.[Content]
        FROM [MessageContents] c
        WHERE c.[Content] IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM [MessageContentBlobs] b WHERE b.[MessageContentId] = c.[Id]);
        """;
}
