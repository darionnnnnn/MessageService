using System.Data;
using System.Data.Common;
using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Services;

/// <summary>
/// 把 MessageContents.Content（varbinary(max)）串流給 HTTP 回應，支援 Range 請求（影片/語音拖拉進度）。
/// 不用 EF 把整個 blob 讀進記憶體：Range 請求直接在 SQL 端用 SUBSTRING/substr 切出所需片段，
/// 只讀取實際要傳送的位元組；ADO.NET 的 blob stream 是 forward-only，不支援 Seek，
/// 所以「拖進度」是靠瀏覽器對同一個 URL 發出新的 Range 請求，而非在單一連線內做 Seek。
/// </summary>
public class ContentStreamService(MessageDbContext dbContext)
{
    private const int BufferSize = 81920;

    public async Task<ContentStreamResult> StreamAsync(
        long messageContentId, string? rangeHeader, HttpResponse response, CancellationToken cancellationToken)
    {
        var meta = await dbContext.MessageContents
            .Where(c => c.Id == messageContentId)
            .Select(c => new { c.DownloadStatus, c.ContentType, c.FileName })
            .FirstOrDefaultAsync(cancellationToken);

        if (meta is null || meta.DownloadStatus != DownloadStatus.Completed)
        {
            return ContentStreamResult.NotFound;
        }

        var isSqlite = dbContext.Database.IsSqlite();
        var connection = dbContext.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var totalLength = await GetContentLengthAsync(connection, isSqlite, messageContentId, cancellationToken);
            var (start, length, isPartial) = ParseRange(rangeHeader, totalLength);

            if (start is null)
            {
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers.ContentRange = $"bytes */{totalLength}";
                return ContentStreamResult.Handled;
            }

            response.Headers.AcceptRanges = "bytes";
            response.ContentType = meta.ContentType ?? "application/octet-stream";
            if (meta.FileName is not null)
            {
                response.Headers.ContentDisposition = $"inline; filename=\"{Uri.EscapeDataString(meta.FileName)}\"";
            }

            if (isPartial)
            {
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {start}-{start + length - 1}/{totalLength}";
            }

            response.ContentLength = length;

            await using var command = connection.CreateCommand();
            command.CommandText = isSqlite
                ? "SELECT substr(Content, @start, @length) FROM MessageContents WHERE Id = @id"
                : "SELECT SUBSTRING(Content, @start, @length) FROM MessageContents WHERE Id = @id";
            AddParameter(command, "@id", messageContentId);
            AddParameter(command, "@start", start.Value + 1); // SUBSTRING/substr 都是 1-indexed
            AddParameter(command, "@length", length);

            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                await using var blobStream = reader.GetStream(0);
                await blobStream.CopyToAsync(response.Body, BufferSize, cancellationToken);
            }

            return ContentStreamResult.Handled;
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<long> GetContentLengthAsync(
        DbConnection connection, bool isSqlite, long messageContentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = isSqlite
            ? "SELECT LENGTH(Content) FROM MessageContents WHERE Id = @id"
            : "SELECT DATALENGTH(Content) FROM MessageContents WHERE Id = @id";
        AddParameter(command, "@id", messageContentId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    /// <summary>只支援單一區間 "bytes=start-end" 或 "bytes=start-"，影片/語音播放器都只用這種格式。</summary>
    private static (long? Start, long Length, bool IsPartial) ParseRange(string? rangeHeader, long totalLength)
    {
        if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.Ordinal))
        {
            return (0, totalLength, false);
        }

        var spec = rangeHeader["bytes=".Length..];
        var parts = spec.Split('-', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var start))
        {
            return (0, totalLength, false);
        }

        var end = totalLength - 1;
        if (parts[1].Length > 0 && long.TryParse(parts[1], out var parsedEnd))
        {
            end = Math.Min(parsedEnd, totalLength - 1);
        }

        if (start < 0 || start >= totalLength || start > end)
        {
            return (null, 0, false);
        }

        return (start, end - start + 1, true);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
