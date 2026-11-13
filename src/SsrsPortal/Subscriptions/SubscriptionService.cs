using SsrsPortal.Core;
using SsrsPortal.Services;

namespace SsrsPortal.Subscriptions;

public sealed class SubscriptionService(ISubscriptionStore store, SubscriptionReportRunner reports, SubscriptionOptions options, TimeProvider time,
    CatalogService catalog, ParameterService parameters)
{
    public async Task<SubscriptionRow> GetAsync(Guid id, SubscriptionOwner owner, CancellationToken ct) =>
        await store.GetAsync(id, owner, ct) ?? throw new PortalException("notfound", 404);

    public async Task<SubscriptionDefinition> ValidateAsync(SubscriptionDefinition definition, CancellationToken ct)
    {
        if (definition == null || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 150 || definition.Name.Any(char.IsControl) ||
            !PathPolicy.CatalogPath(definition.ReportPath) || definition.Recipients == null || definition.Schedule == null || definition.Parameters == null ||
            definition.Delivery is not ("link" or "attachment" or "both") || !OutputFormat.All.ContainsKey(definition.Format ?? "") ||
            definition.Parameters.Count > 100 || definition.Parameters.Any(p => p.Key.Length > 128 || p.Value == null || p.Value.Values == null ||
                p.Value.Values.Length > 5000 || p.Value.Values.Any(v => v?.Length > 2048) || p.Value.UseDefault && p.Value.Values.Length > 0))
        { throw new SubscriptionValidationException("Review the subscription name, delivery, format and input settings."); }
        definition.Schedule.Validate();
        definition = definition with { Name = definition.Name.Trim(), Recipients = definition.Recipients.Normalize(options.MaxRecipients) };
        var resolved = await reports.ResolveAsync(definition, ct);
        return definition with { ReportPath = resolved.Report.Path };
    }
    public async Task<ParameterForm> PreviewParametersAsync(SubscriptionDefinition definition, CancellationToken ct)
    {
        var report = await catalog.ResolvePathAsync(definition.ReportPath, ct);
        if (definition.Parameters == null || definition.Parameters.Any(p => p.Value == null || p.Value.Values == null)) { throw new SubscriptionValidationException("Invalid input settings."); }
        var form = await parameters.PreviewSubscriptionAsync(report, new(definition.Parameters.Where(p => !p.Value.UseDefault)
            .ToDictionary(p => p.Key, p => p.Value.Values, StringComparer.Ordinal), Initial: true), ct);
        if (definition.Parameters.Keys.Any(name => !form.Fields.Any(field => field.Name == name)))
        { throw new SubscriptionValidationException("A subscription input is hidden or no longer available."); }
        return form;
    }
    public async Task<SubscriptionView> CreateAsync(SubscriptionOwner owner, SubscriptionDefinition definition, CancellationToken ct)
    {
        definition = await ValidateAsync(definition, ct);
        var row = new SubscriptionRow { Id = Guid.NewGuid(), TenantId = owner.TenantId, UserObjectId = owner.UserObjectId, UserEmail = owner.UserEmail,
            Revision = 1, Enabled = true, DefinitionJson = SubscriptionJson.Write(definition), NextDueUtc = InitialDue(definition.Schedule) };
        await store.SaveAsync(row, null, ct);
        return row.View;
    }
    private DateTimeOffset? InitialDue(SubscriptionSchedule schedule)
    {
        // Creating an old recurrence begins now, never backfills dates before creation.
        // A missed one-time date is eligible once, unless explicitly ended.
        var now = time.GetUtcNow();
        if (schedule.EndUtc() is { } end && end < now) { throw new SubscriptionValidationException("The schedule has ended. Choose new dates before creating or resuming it."); }
        return schedule.Kind == "once" ? schedule.StartUtc() : schedule.Next(now.AddTicks(-1)) ??
            throw new SubscriptionValidationException("This schedule has no remaining occurrences. Choose new dates.");
    }
    public DateTimeOffset? PreviewSchedule(SubscriptionSchedule schedule)
    {
        schedule.Validate();
        return InitialDue(schedule);
    }
    public async Task<SubscriptionView> UpdateAsync(Guid id, SubscriptionOwner owner, SubscriptionWrite input, CancellationToken ct)
    {
        var row = await GetAsync(id, owner, ct);
        if (input.Revision != row.Revision) { throw new SubscriptionValidationException("This subscription changed. Reload before saving."); }
        var observedDueUtc = row.NextDueUtc;
        var definition = await ValidateAsync(input.Definition, ct);
        var scheduleChanged = !row.Definition.Schedule.SameRecurrence(definition.Schedule);
        row.DefinitionJson = SubscriptionJson.Write(definition);
        if (scheduleChanged && row.Enabled) { row.NextDueUtc = InitialDue(definition.Schedule); }
        row.Revision++;
        // Dispatch advances due state without changing the definition revision.
        // Reject a stale edit before it can rewind progress or cancel that work.
        await store.SaveAsync(row, input.Revision, ct, observedDueUtc, requireObservedDue: true);
        return row.View;
    }
    public async Task<SubscriptionView> StateAsync(Guid id, SubscriptionOwner owner, SubscriptionState state, bool delete, CancellationToken ct)
    {
        var row = await GetAsync(id, owner, ct);
        var observedDueUtc = row.NextDueUtc;
        if (row.Revision != state.Revision) { throw new SubscriptionValidationException("This subscription changed. Reload before saving."); }
        if (state.Enabled && !delete)
        {
            await ValidateAsync(row.Definition, ct);
            if (!row.Enabled) { row.NextDueUtc = InitialDue(row.Definition.Schedule); }
        }
        row.Enabled = state.Enabled && !delete;
        row.Deleted = delete;
        if (!row.Enabled) { row.NextDueUtc = null; }
        row.Revision++;
        await store.SaveAsync(row, state.Revision, ct, observedDueUtc, requireObservedDue: true);
        return row.View;
    }
}
