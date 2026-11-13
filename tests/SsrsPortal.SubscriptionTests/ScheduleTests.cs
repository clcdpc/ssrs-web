using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.SubscriptionTests;

[TestClass]
public sealed class ScheduleTests
{
    private static DateTime Local(string value) => DateTime.SpecifyKind(DateTime.Parse(value), DateTimeKind.Unspecified);
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value);
    [TestMethod]
    [DataRow("once", 1, "2026-11-03T08:00", "2026-11-03T13:00Z")]
    [DataRow("daily", 2, "2026-11-03T08:00", "2026-11-03T13:00Z")]
    [DataRow("interval", 60, "2026-11-03T08:00", "2026-11-03T13:00Z")]
    public void InitialOccurrenceUsesEastern(string kind, int every, string start, string expected)
    {
        var schedule = new SubscriptionSchedule(kind, Local(start), Every: every);
        schedule.Validate();
        Assert.AreEqual(Utc(expected), schedule.Next(Utc("2026-11-02T00:00Z")));
    }
    [TestMethod]
    public void EveryNDaysIsNotDayOfMonthCron()
    {
        var schedule = new SubscriptionSchedule("daily", Local("2026-01-30T08:00"), Every: 3);
        Assert.AreEqual(Utc("2026-02-02T13:00Z"), schedule.Next(Utc("2026-01-30T13:00Z")));
    }
    [TestMethod]
    public void EveryNWeeksAnchorsOnStartWeek()
    {
        var schedule = new SubscriptionSchedule("weekly", Local("2026-11-02T08:00"), Every: 2, Weekdays: [1, 3]);
        Assert.AreEqual(Utc("2026-11-04T13:00Z"), schedule.Next(Utc("2026-11-02T13:00Z")));
        Assert.AreEqual(Utc("2026-11-16T13:00Z"), schedule.Next(Utc("2026-11-04T13:00Z")));
    }
    [TestMethod]
    public void MonthDaySkipsShortMonths()
    {
        var schedule = new SubscriptionSchedule("monthDays", Local("2026-01-31T08:00"), MonthDays: [31]);
        Assert.AreEqual(Utc("2026-03-31T12:00Z"), schedule.Next(Utc("2026-01-31T13:00Z")));
    }
    [TestMethod]
    [DataRow(2, "2026-11-10T13:00Z")]
    [DataRow(-1, "2026-11-24T13:00Z")]
    public void RelativeWeekday(int week, string expected)
    {
        var schedule = new SubscriptionSchedule("monthWeekday", Local("2026-11-01T08:00"), Week: week, Weekday: 2);
        Assert.AreEqual(Utc(expected), schedule.Next(Utc("2026-11-01T00:00Z")));
    }
    [TestMethod]
    public void SpringGapMovesToFirstValidMinute()
    {
        var schedule = new SubscriptionSchedule("daily", Local("2026-03-07T02:30"));
        Assert.AreEqual(Utc("2026-03-08T07:00Z"), schedule.Next(Utc("2026-03-07T07:30Z")));
        Assert.AreEqual(Utc("2026-03-09T06:30Z"), schedule.Next(Utc("2026-03-08T07:00Z")));
    }
    [TestMethod]
    public void FallFoldRunsWallClockOnce()
    {
        var schedule = new SubscriptionSchedule("daily", Local("2026-10-31T01:30"));
        Assert.AreEqual(Utc("2026-11-01T05:30Z"), schedule.Next(Utc("2026-10-31T05:30Z")));
        Assert.AreEqual(Utc("2026-11-02T06:30Z"), schedule.Next(Utc("2026-11-01T05:30Z")));
        Assert.AreEqual(Utc("2026-11-01T05:30Z"), schedule.Latest(Utc("2026-10-31T05:30Z"), Utc("2026-11-01T06:45Z")));
    }
    [TestMethod]
    public void IntervalUsesElapsedTimeAcrossFold()
    {
        var schedule = new SubscriptionSchedule("interval", Local("2026-11-01T00:30"), Every: 60);
        Assert.AreEqual(Utc("2026-11-01T06:30Z"), schedule.Next(Utc("2026-11-01T05:30Z")));
    }
    [TestMethod]
    [DataRow(480, "2026-11-03T21:00Z")]
    [DataRow(720, "2026-11-04T01:00Z")]
    [DataRow(1440, "2026-11-04T13:00Z")]
    public void FullSubDailyIntervalsRemainSupported(int minutes, string expected)
    {
        var schedule = new SubscriptionSchedule("interval", Local("2026-11-03T08:00"), Every: minutes);
        schedule.Validate();
        var restored = SubscriptionJson.Read<SubscriptionSchedule>(SubscriptionJson.Write(schedule));
        Assert.AreEqual(Utc(expected), restored.Next(Utc("2026-11-03T13:00Z")));
    }
    [TestMethod]
    public void LongOutageChoosesOnlyLatestAndNextNormal()
    {
        var schedule = new SubscriptionSchedule("interval", Local("2020-01-01T08:00"), Every: 5);
        var now = Utc("2026-11-03T13:03Z");
        Assert.AreEqual(Utc("2026-11-03T13:00Z"), schedule.Latest(schedule.StartUtc(), now));
        Assert.AreEqual(Utc("2026-11-03T13:05Z"), schedule.Next(now));
    }
    [TestMethod]
    public void CronUsesEstablishedParserAndBoundedOutageWindow()
    {
        var schedule = new SubscriptionSchedule("cron", Local("2020-01-01T08:00"), Cron: "*/5 * * * *");
        schedule.Validate();
        Assert.AreEqual(Utc("2026-11-03T13:00Z"), schedule.Latest(schedule.StartUtc(), Utc("2026-11-03T13:03Z")));
        Assert.AreEqual(Utc("2026-11-03T13:05Z"), schedule.Next(Utc("2026-11-03T13:03Z")));
    }
    [TestMethod]
    public void BoundariesAreInclusiveAndOneTimeEnds()
    {
        var schedule = new SubscriptionSchedule("daily", Local("2026-11-02T08:00"), Local("2026-11-03T08:00"));
        Assert.AreEqual(Utc("2026-11-03T13:00Z"), schedule.Next(Utc("2026-11-02T13:00Z")));
        Assert.IsNull(schedule.Next(Utc("2026-11-03T13:00Z")));
        Assert.IsNull((schedule with { Kind = "once" }).Next(Utc("2026-11-02T13:00Z")));
    }
    [TestMethod]
    [DataRow("weekly", "2026-11-02T08:00", "2026-11-01T08:00", "America/New_York")]
    [DataRow("unknown", "2026-11-02T08:00", null, "America/New_York")]
    [DataRow("daily", "2026-11-02T08:00", null, "Not/AZone")]
    public void InvalidSchedulesReject(string kind, string start, string? end, string zone)
    {
        Assert.Throws<SubscriptionValidationException>(() => new SubscriptionSchedule(kind, Local(start), end == null ? null : Local(end), zone).Validate());
    }
}
