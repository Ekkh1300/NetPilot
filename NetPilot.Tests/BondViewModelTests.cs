using System;
using System.IO;
using System.Linq;
using System.Threading;
using NetPilot.ViewModels;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// The download page's own logic: the name a file is saved under, the numbers shown next to it,
/// and the speed readout.
///
/// The page itself has never been rendered here - there is no display to render it on - so these
/// tests are the only check on any of this. They deliberately stay on the plain properties and
/// static helpers rather than constructing the page, because building a <see cref="BondViewModel"/>
/// starts a real adapter scan against the machine, which is not something a test should do to a
/// machine it does not own.
/// </summary>
public class BondViewModelTests
{
    // ---------------- the name a file is saved under ----------------

    /// <summary>
    /// The bug this whole area is about.
    ///
    /// The name used to be taken from <c>Uri.AbsolutePath</c>, which is the escaped form. A file
    /// called "my file.zip" was therefore saved as "my%20file.zip", and a file named in Persian
    /// as a string of percent escapes that means nothing to the person who chose it. The name is
    /// the one thing about a download a user recognises afterwards, so getting it wrong costs
    /// real time even though nothing is lost.
    /// </summary>
    [Theory]
    // سلام.zip  - a Persian name, as it arrives percent-encoded
    [InlineData("https://example.com/%D8%B3%D9%84%D8%A7%D9%85.zip", "\u0633\u0644\u0627\u0645.zip")]
    [InlineData("https://example.com/my%20file.zip", "my file.zip")]
    [InlineData("https://example.com/a%2Bb.zip", "a+b.zip")]
    public void GuessFileNameUnescapesWhatTheServerSent(string url, string expected) =>
        Assert.Equal(expected, BondViewModel.GuessFileName(url));

    [Theory]
    [InlineData("https://example.com/dir/sub/file.zip", "file.zip")]
    [InlineData("https://example.com/file.zip?token=abc&x=1", "file.zip")]   // query is not part of a name
    [InlineData("https://example.com/", "download")]
    [InlineData("https://example.com", "download")]
    [InlineData("not a url at all", "")]
    public void GuessFileNamePicksTheLastSegmentOnly(string url, string expected) =>
        Assert.Equal(expected, BondViewModel.GuessFileName(url));

    /// <summary>
    /// Characters Windows will not allow in a name are replaced rather than dropped.
    ///
    /// An illegal character is not caught where it is produced. It surfaces much later as a
    /// download that failed with a message about a path, connected in nobody's mind to the name
    /// the server sent three screens ago.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/a%3Ab.zip", "a_b.zip")]        // ':' is illegal on Windows
    [InlineData("https://example.com/a%3Fb.zip", "a_b.zip")]        // '?'
    [InlineData("https://example.com/a%2Ab.zip", "a_b.zip")]        // '*'
    public void GuessFileNameReplacesCharactersWindowsWouldRefuse(string url, string expected) =>
        Assert.Equal(expected, BondViewModel.GuessFileName(url));

    /// <summary>
    /// "." and ".." name directories. A download called either of those would be written
    /// somewhere other than the folder the user chose, which is not a small thing to do quietly.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/.")]
    [InlineData("https://example.com/..")]
    [InlineData("https://example.com/%2E%2E")]
    public void GuessFileNameRefusesNamesThatAreNotFiles(string url) =>
        Assert.Equal("download", BondViewModel.GuessFileName(url));

    /// <summary>
    /// Whatever comes back has to stay inside the folder it is joined to.
    ///
    /// A decoded separator is the way this could escape: %2F unescapes to '/', so the last
    /// segment is taken after decoding, not before.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/..%2F..%2Fetc%2Fpasswd")]
    [InlineData("https://example.com/a%2Fb.zip")]
    [InlineData("https://example.com/a%5Cb.zip")]
    public void GuessFileNameNeverProducesSomethingOutsideTheFolder(string url)
    {
        var name = BondViewModel.GuessFileName(url);

        Assert.False(string.IsNullOrEmpty(name), "a name must never be empty here");
        Assert.Equal(name, Path.GetFileName(name));   // no separator survives in it
    }

    [Fact]
    public void GuessFileNameBoundsTheLengthSoThePathStaysUsable()
    {
        var url = "https://example.com/" + new string('a', 400) + ".zip";
        Assert.True(BondViewModel.GuessFileName(url).Length <= 180);
    }

    // ---------------- what the numbers on the row say ----------------

    [Theory]
    [InlineData(-1, "0 B")]                       // a negative total is nonsense, not a size
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void ByteCountsAreShownInUnitsApersonCanRead(long bytes, string expected) =>
        Assert.Equal(expected, DownloadRow.Bytes(bytes));

    /// <summary>
    /// A total of zero must produce a fraction of zero, not of NaN or infinity.
    ///
    /// The engine reports the size after the probe, so the first progress event can arrive with
    /// no total yet. Infinity bound to a progress bar shows as a bar at full width, which is the
    /// one wrong answer nobody would be able to explain.
    /// </summary>
    [Fact]
    public void ProgressWithNoTotalYetIsZeroRatherThanInfinity()
    {
        var row = new DownloadRow { TotalBytes = 0, BytesDone = 0 };

        Assert.Equal(0, row.Fraction);
        Assert.Equal(0, row.Percent);
        Assert.Equal("", row.SizeText);
    }

    [Fact]
    public void ProgressIsReportedOnTheScaleTheProgressBarExpects()
    {
        var row = new DownloadRow { TotalBytes = 400, BytesDone = 100 };

        Assert.Equal(0.25, row.Fraction, 6);
        Assert.Equal(25.0, row.Percent, 6);          // 0..100, not 0..1
        Assert.Equal("100 B / 400 B", row.SizeText);
    }

    [Fact]
    public void ChangingTheTotalRefreshesTheDerivedReadouts()
    {
        var row = new DownloadRow { BytesDone = 100 };
        Assert.Equal(0, row.Fraction);               // still no total

        row.TotalBytes = 200;
        Assert.Equal(0.5, row.Fraction, 6);          // and now it does
        Assert.Equal("100 B / 200 B", row.SizeText);
    }

    [Fact]
    public void AnErrorMakesTheRowReportThatItHasOne()
    {
        var row = new DownloadRow();
        Assert.False(row.HasError);

        row.Error = "the connection went away";
        Assert.True(row.HasError);
    }

    /// <summary>
    /// The cancel button has to stop working once the download is over. A row that is finished
    /// and still offers to cancel invites a click that does nothing, and says nothing either way.
    /// </summary>
    [Fact]
    public void CancellingIsOfferedOnlyWhileTheDownloadIsRunning()
    {
        var row = new DownloadRow();

        Assert.True(row.CancelCommand.CanExecute(null));

        row.IsActive = false;
        row.CancelCommand.RaiseCanExecuteChanged();

        Assert.False(row.CancelCommand.CanExecute(null));
    }

    // ---------------- the per-network rows ----------------

    /// <summary>
    /// Zero streams is refused outright by the engine, so the row must not be able to reach it -
    /// and neither should an absurd number, which would open that many sockets at once.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(1000, 64)]
    public void HowManyConnectionsARowOffersIsBounded(int asked, int expected)
    {
        var row = new BondNetworkRow { Streams = asked };
        Assert.Equal(expected, row.Streams);
    }

    [Fact]
    public void ANetworkShareShowsHowMuchItCarried()
    {
        var share = new NetworkShare { Name = "wifi" };

        share.SetProgress(3 * 1024 * 1024, 0.75);

        Assert.Equal(3 * 1024 * 1024, share.Bytes);
        Assert.Equal(0.75, share.Fraction);
        Assert.Equal("3 MB", share.SizeText);
    }

    [Fact]
    public void AShareOfNothingIsZeroRatherThanANanBar()
    {
        var share = new NetworkShare { Name = "usb" };

        share.SetProgress(0, 0);

        Assert.Equal(0, share.Fraction);
        Assert.Equal("0 B", share.SizeText);
    }

    // ---------------- the speed readout ----------------

    /// <summary>
    /// One sample is not a speed. Reporting one would print whatever the very first delta
    /// happened to be, which at the start of a download is always a burst.
    /// </summary>
    [Fact]
    public void AFirstSampleOnItsOwnIsNotASpeed() =>
        Assert.Equal("", new SpeedMeter().Sample(1_000_000));

    [Fact]
    public void StandingStillIsNotASpeed()
    {
        var meter = new SpeedMeter();

        meter.Sample(1_000_000);
        Thread.Sleep(120);

        // Same byte count twice: a rate of zero. Printing "0 B/s" is fine; so is printing
        // nothing. Printing a negative number, or dividing by a zero-length window, is not.
        Assert.Equal("", meter.Sample(1_000_000));
    }

    [Fact]
    public void ADownloadGoingNowhereFastEnoughShowsAPlausibleRate()
    {
        var meter = new SpeedMeter();

        meter.Sample(0);
        Thread.Sleep(150);
        var text = meter.Sample(5_000_000);

        Assert.NotEqual("", text);
        Assert.EndsWith("/s", text);

        // 5 MB in about 0.15s is roughly 33 MB/s. The bound is wide because a loaded machine
        // stretches the sleep, but it is tight enough to catch a rate reported on the wrong
        // scale - bytes per millisecond, say, or per microsecond.
        var reported = double.Parse(text.Replace(" MB/s", "").Replace(" KB/s", "").Replace(" B/s", ""));
        Assert.InRange(reported, 0.1, 5000);
    }
}
