namespace QimErp.Shared.Common.Entities;

/// <summary>While true, saves record the actor as <see cref="Actor"/>, cut the audit timestamps to the UTC date, start no workflow and write no outbox row for the entity.</summary>
public interface IAnonymousRecord
{
    const string Actor = "anonymous";

    bool IsAnonymousRecord { get; }
}
