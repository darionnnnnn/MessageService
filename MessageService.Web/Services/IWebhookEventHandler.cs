using MessageService.Models.Line;

namespace MessageService.Services;

public interface IWebhookEventHandler
{
    Task HandleAsync(WebhookRequest request, CancellationToken cancellationToken);
}
