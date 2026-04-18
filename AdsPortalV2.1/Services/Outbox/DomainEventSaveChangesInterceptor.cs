using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using AdsPortalV2.Entities;
using AdsPortalV2.Data;

namespace AdsPortalV2.Services.Outbox;

public class DomainEventSaveChangesInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        // NOOP: materialization moved to explicit call in application layer to preserve identity ordering.
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        // NOOP: materialization moved to explicit call in application layer to preserve identity ordering.
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void TryMaterialize(DbContext? context)
    {
        // Interceptor no longer performs materialization. This class remains to satisfy DI and can be removed later.
    }
}
