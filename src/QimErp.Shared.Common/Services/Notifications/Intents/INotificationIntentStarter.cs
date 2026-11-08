namespace QimErp.Shared.Common.Services.Notifications.Intents;

public interface INotificationIntentStarter
{
    Task<NotificationIntentAccepted> StageAsync(NotificationIntentRequest request, CancellationToken cancellationToken = default);
}
