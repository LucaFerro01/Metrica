using Xunit;

namespace TriMetric.Contracts.Tests;

public sealed class EventNamesTests
{
    [Fact]
    public void Constants_HaveExpectedValues()
    {
        Assert.Equal("ActivityImported", EventNames.ActivityImported);
        Assert.Equal("ingestion.activity.imported", EventNames.ActivityImportedSubject);
        Assert.Equal("EVENTS", EventNames.EventsStream);
        Assert.Equal("DLQ", EventNames.DlqStream);
        Assert.Equal(100_000, StreamLimits.MaxPoints);
    }

    [Fact]
    public void StreamSubjects_ListExplicitServicePrefixes()
    {
        Assert.Equal(
            ["identity.>", "ingestion.>", "activities.>", "analytics.>", "training-plan.>"],
            EventNames.EventsStreamSubjects);
        Assert.Equal(["dlq.>"], EventNames.DlqStreamSubjects);
    }

    [Fact]
    public void ActivityImportedSubject_IsCoveredByEventsStream_AndNotByDlq()
    {
        Assert.Contains(EventNames.EventsStreamSubjects, p => Matches(p, EventNames.ActivityImportedSubject));
        Assert.DoesNotContain(EventNames.DlqStreamSubjects, p => Matches(p, EventNames.ActivityImportedSubject));
    }

    [Fact]
    public void EventsAndDlqSubjects_DoNotOverlap()
    {
        foreach (var events in EventNames.EventsStreamSubjects)
        {
            foreach (var dlq in EventNames.DlqStreamSubjects)
            {
                Assert.False(Overlaps(events, dlq), $"'{events}' si sovrappone a '{dlq}'");
            }
        }
    }

    // Prefisso di un pattern come "ingestion.>": tutto prima di ">", punto finale incluso.
    private static string Prefix(string pattern)
    {
        Assert.EndsWith(".>", pattern);
        return pattern[..^1];
    }

    // Due pattern "x.>" si sovrappongono se un prefisso e' uguale a o prefisso dell'altro.
    private static bool Overlaps(string a, string b)
    {
        var pa = Prefix(a);
        var pb = Prefix(b);
        return pa.StartsWith(pb, StringComparison.Ordinal) || pb.StartsWith(pa, StringComparison.Ordinal);
    }

    // Matching minimale dei wildcard NATS: '*' = un token, '>' = uno o piu' token finali.
    private static bool Matches(string pattern, string subject)
    {
        var p = pattern.Split('.');
        var s = subject.Split('.');
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == ">")
            {
                return s.Length > i;
            }

            if (i >= s.Length)
            {
                return false;
            }

            if (p[i] != "*" && p[i] != s[i])
            {
                return false;
            }
        }

        return p.Length == s.Length;
    }
}
