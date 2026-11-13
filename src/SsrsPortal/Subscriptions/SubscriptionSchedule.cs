using Cronos;

namespace SsrsPortal.Subscriptions;

// Wall-clock recurrence is deliberately separate from elapsed-minute intervals.
// DST gaps move to the first valid minute; repeated wall times run once (earlier UTC).
public sealed record SubscriptionSchedule(string Kind, DateTime StartLocal, DateTime? EndLocal = null,
    string TimeZone = "America/New_York", int Every = 1, int[]? Weekdays = null, int[]? MonthDays = null,
    int Week = 1, int Weekday = 1, string? Cron = null)
{
    public TimeZoneInfo Zone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZone); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { throw new SubscriptionValidationException("Select a supported time zone."); }
    }
    public void Validate()
    {
        _ = Zone();
        if (StartLocal.Kind != DateTimeKind.Unspecified || StartLocal.Year is < 2000 or > 2099 || StartLocal.Ticks % TimeSpan.TicksPerMinute != 0 ||
            EndLocal is { } end && (end.Kind != DateTimeKind.Unspecified || end < StartLocal || end.Year > 2100) ||
            Every < 1 || Every > (Kind == "interval" ? 10080 : Kind == "weekly" ? 52 : Kind is "monthDays" or "monthWeekday" ? 12 : 366) || Kind is not ("once" or "interval" or "daily" or "weekly" or "monthDays" or "monthWeekday" or "cron") ||
            Kind == "interval" && Every < 5 || Kind == "weekly" && (Weekdays is not { Length: > 0 and <= 7 } || Weekdays.Any(d => d is < 0 or > 6)) ||
            Kind == "monthDays" && (MonthDays is not { Length: > 0 and <= 31 } || MonthDays.Any(d => d is < 1 or > 31)) ||
            Kind == "monthWeekday" && (Week is not (1 or 2 or 3 or 4 or -1) || Weekday is < 0 or > 6))
        { throw new SubscriptionValidationException("Review the recurrence, start/end dates and interval (at least five minutes)."); }
        if (Kind == "cron")
        {
            try { _ = CronExpression.Parse(Cron ?? ""); }
            catch (CronFormatException) { throw new SubscriptionValidationException("Enter a valid five-field cron expression."); }
        }
        if (Next(StartUtc().AddTicks(-1)) == null) { throw new SubscriptionValidationException("This schedule has no eligible occurrence within its dates."); }
    }
    private DateTimeOffset LocalUtc(DateTime local)
    {
        var zone = Zone();
        while (zone.IsInvalidTime(local)) { local = local.AddMinutes(1); }
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
    public DateTimeOffset StartUtc() => LocalUtc(StartLocal);
    internal bool SameRecurrence(SubscriptionSchedule other)
    {
        if (Kind != other.Kind || StartLocal != other.StartLocal || EndLocal != other.EndLocal || TimeZone != other.TimeZone) { return false; }
        return Kind switch
        {
            "once" => true,
            "interval" or "daily" => Every == other.Every,
            "weekly" => Every == other.Every && (Weekdays ?? []).ToHashSet().SetEquals(other.Weekdays ?? []),
            "monthDays" => Every == other.Every && (MonthDays ?? []).ToHashSet().SetEquals(other.MonthDays ?? []),
            "monthWeekday" => Every == other.Every && Week == other.Week && Weekday == other.Weekday,
            "cron" => string.Join(' ', (Cron ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) == string.Join(' ', (other.Cron ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            _ => false
        };
    }
    public DateTimeOffset? EndUtc() => EndLocal is { } end ? LocalUtc(end) : null;
    private bool Matches(DateTime date)
    {
        var days = (date.Date - StartLocal.Date).Days;
        var months = (date.Year - StartLocal.Year) * 12 + date.Month - StartLocal.Month;
        return Kind switch
        {
            "daily" => days >= 0 && days % Every == 0,
            "weekly" => days >= 0 && (days + (int)StartLocal.DayOfWeek) / 7 % Every == 0 && Weekdays!.Contains((int)date.DayOfWeek),
            "monthDays" => months >= 0 && months % Every == 0 && MonthDays!.Contains(date.Day),
            "monthWeekday" => months >= 0 && months % Every == 0 && (int)date.DayOfWeek == Weekday &&
                (Week == -1 ? date.Day + 7 > DateTime.DaysInMonth(date.Year, date.Month) : (date.Day - 1) / 7 + 1 == Week),
            _ => false
        };
    }
    public DateTimeOffset? Next(DateTimeOffset after)
    {
        var start = StartUtc();
        var end = EndUtc();
        DateTimeOffset? Bound(DateTimeOffset? candidate) => candidate is { } value && value >= start && (end == null || value <= end) ? value : null;
        if (end != null && after >= end) { return null; }
        if (Kind == "once") { return after < start ? Bound(start) : null; }
        if (Kind == "interval")
        {
            var steps = after < start ? 0 : (after.UtcTicks - start.UtcTicks) / TimeSpan.FromMinutes(Every).Ticks + 1;
            return Bound(start.AddTicks(steps * TimeSpan.FromMinutes(Every).Ticks));
        }
        if (Kind == "cron")
        {
            return Bound(CronExpression.Parse(Cron!).GetNextOccurrence(after < start ? start : after, Zone(), inclusive: after < start));
        }
        var date = TimeZoneInfo.ConvertTime(after < start ? start : after, Zone()).Date;
        for (var i = 0; i < 146097 && date.Year <= 2499; i++, date = date.AddDays(1))
        {
            if (EndLocal != null && date > EndLocal.Value.Date) { return null; }
            if (!Matches(date)) { continue; }
            var candidate = LocalUtc(date + StartLocal.TimeOfDay);
            if (candidate > after && Bound(candidate) != null) { return candidate; }
        }
        return null;
    }
    public DateTimeOffset? Latest(DateTimeOffset notBefore, DateTimeOffset now)
    {
        var start = StartUtc();
        var end = EndUtc();
        if (end is { } stop && now > stop) { now = stop; }
        if (now < start || now < notBefore) { return null; }
        if (Kind == "once") { return start >= notBefore ? start : null; }
        if (Kind == "interval")
        {
            var ticks = TimeSpan.FromMinutes(Every).Ticks;
            var candidate = start.AddTicks((now.UtcTicks - start.UtcTicks) / ticks * ticks);
            return candidate >= notBefore ? candidate : null;
        }
        if (Kind == "cron")
        {
            // Find the most recent nonempty window. Dense schedules only enumerate
            // one day, rather than every minute of a prolonged outage.
            var expression = CronExpression.Parse(Cron!);
            var floor = notBefore > start ? notBefore : start;
            for (var days = 1; days <= 262144; days *= 2)
            {
                var from = now.AddDays(-Math.Min(days, (now - DateTimeOffset.MinValue).TotalDays));
                if (from < floor) { from = floor; }
                var last = expression.GetOccurrences(from, now, Zone(), fromInclusive: true, toInclusive: true).LastOrDefault();
                if (last != default) { return last; }
                if (from == floor) { break; }
            }
            return null;
        }
        var date = TimeZoneInfo.ConvertTime(now, Zone()).Date;
        for (var i = 0; i < 146097 && date >= StartLocal.Date; i++, date = date.AddDays(-1))
        {
            if (!Matches(date)) { continue; }
            var candidate = LocalUtc(date + StartLocal.TimeOfDay);
            if (candidate <= now && candidate >= notBefore && candidate >= start) { return candidate; }
            if (candidate < notBefore) { return null; }
        }
        return null;
    }
}
