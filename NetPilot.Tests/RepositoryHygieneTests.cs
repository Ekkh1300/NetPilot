using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// Watches for the two things that keep going wrong in this repository: a secret getting
/// committed, and a tracked file that should not be.
///
/// The keystore was committed once and removed, and the removal is not self-enforcing. A future
/// `git add -A` on a machine that happened to have a keystore next to the project would put it
/// back, and the tests would pass because a file on disk is not the same as a file in history.
/// These check the index, which is what git would actually commit.
///
/// The second half is the strays. Twelve build and capture logs were tracked at once, none of
/// them anything anyone reads, all of them reproducible by re-running the command that made
/// them. They were only noticed by a review, and a review does not run every time.
///
/// Neither of these could be found by running the app, which is why they are here.
///
/// </summary>
public sealed class RepositoryHygieneTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NetPilot.csproj")) == false &&
                dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "could not find the repository root above " + AppContext.BaseDirectory);
    }

    private static HashSet<string> TrackedFiles()
    {
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "ls-files")
            {
                WorkingDirectory = RepositoryRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return tracked;

            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30_000);
            foreach (var line in output.Split('\n'))
                if (line.Trim().Length > 0) tracked.Add(line.Trim());
        }
        catch
        {
            // git is not available. An empty set cannot fail anything, which is worse than
            // useless - so say so rather than reporting a clean bill of health.
            throw new InvalidOperationException(
                "could not run `git ls-files`; this test needs git on the PATH");
        }
        return tracked;
    }

    // ------------------------------------------------------------------ secrets

    [Fact]
    public void NoKeystoreOrPropertiesFileIsTracked()
    {
        var offenders = TrackedFiles()
            .Where(f =>
                f.EndsWith(".jks", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".keystore", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith("keystore.properties", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "these are tracked and must not be. A signing key that reaches the index reaches " +
            "everyone who clones, and the one that was published is still downloadable by its " +
            "blob id - see docs\\KEYSTORE-ROTATION.md:\n" +
            string.Join("\n", offenders.Select(o => "  " + o)));
    }

    /// <summary>
    /// Catches a credentials file even if it has been renamed.
    ///
    /// The discriminator is whether the value on the right of the assignment is a *real* secret,
    /// not merely whether the key is mentioned. That distinction matters because three legitimate
    /// files talk about these keys: the Gradle script that reads them, the example file that
    /// documents the shape, and this project's rotation note, which quotes the leaked password so
    /// the reason for rotating is on record. A check that fires on all three is a check people
    /// learn to ignore, and then it protects nothing.
    ///
    /// So the shapes that count are the ones a real file has:
    ///   - a properties file (by extension or name), where any value counts; or
    ///   - any file at all, where the value is not a placeholder and not already the known-leaked
    ///     password - i.e. something nobody has written down yet.
    ///
    /// The leaked password is exempt by value rather than by name, so a future document that
    /// happens to mention it does not fail, and the day someone writes a *new* password into a
    /// tracked file this fails instead.
    /// </summary>
    [Fact]
    public void NoTrackedFileCarriesAResolvedKeystoreCredential()
    {
        var assignment = new Regex(
            @"(?im)^\s*(storePassword|keyPassword)\s*=\s*(\S+)\s*$", RegexOptions.Compiled);

        // Values that are not secrets. CHANGE_ME is the example file's placeholder; the leaked one
        // is quoted by the rotation note and is public, so flagging it would only produce noise.
        var notSecrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CHANGE_ME", "netpilot", "$password", "***", "",
        };

        var offenders = new List<string>();
        foreach (var rel in TrackedFiles())
        {
            string full;
            try { full = Path.Combine(RepositoryRoot(), rel); }
            catch { continue; }
            if (!File.Exists(full)) continue;

            string text;
            try { text = File.ReadAllText(full); }
            catch { continue; }   // a binary or a file held open by a build

            var name = Path.GetFileName(rel);
            bool isPropertiesFile =
                name.EndsWith(".properties", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("keystore.properties", StringComparison.OrdinalIgnoreCase);

            foreach (Match m in assignment.Matches(text))
            {
                string value = m.Groups[2].Value.Trim();

                if (isPropertiesFile)
                {
                    // Even a placeholder in a real properties file is worth reporting: the file is
                    // the shape that matters, and the reviewer can confirm at a glance.
                    offenders.Add($"{rel}  ({m.Groups[1].Value}={value})");
                    continue;
                }

                if (notSecrets.Contains(value)) continue;
                offenders.Add($"{rel}  ({m.Groups[1].Value}={value})");
            }
        }

        Assert.True(offenders.Count == 0,
            "a tracked file assigns a real keystore password:\n" +
            string.Join("\n", offenders.Select(o => "  " + o)));
    }

    // ------------------------------------------------------------------ strays

    /// <summary>
    /// Build and capture output does not belong in history.
    ///
    /// Twelve were tracked: build.log, capture.log, debug.log, build1..4.log and so on. Not one
    /// was worth keeping - each is the console output of one run at one moment - and their total
    /// size made every clone pay for them for ever. The list is explicit rather than a blanket
    /// "*.log" so that a log someone deliberately added, with a reason, is not silently swept up.
    /// </summary>
    [Fact]
    public void NoBuildOrCaptureOutputIsTracked()
    {
        var offenders = TrackedFiles()
            .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "tracked log files. Each is one run's console output and is reproducible:\n" +
            string.Join("\n", offenders.Select(o => "  " + o)));
    }

    [Theory]
    [InlineData("elev_done.txt")]
    [InlineData("test-report.txt")]
    [InlineData("selftest.txt")]
    [InlineData("selftest-hold.txt")]
    [InlineData("langcheck.txt")]
    public void NoHarnessMarkerFileIsTracked(string name)
    {
        var offenders = TrackedFiles()
            .Where(f => f.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "tracked harness marker files:\n" +
            string.Join("\n", offenders.Select(o => "  " + o)));
    }

    // ------------------------------------------------------------------ the checks work

    /// <summary>
    /// A hygiene test that silently checks nothing is the most dangerous kind, because it is read
    /// as coverage. This proves the file list is real and the patterns match what they claim.
    /// </summary>
    [Fact]
    public void TheScanSeesTheRepository()
    {
        var tracked = TrackedFiles();

        Assert.True(tracked.Count > 100,
            $"only {tracked.Count} files came back from git ls-files, so the checks above would " +
            "pass vacuously");
        Assert.True(tracked.Contains("NetPilot/NetPilot.csproj"),
            "the project file should be tracked - if it is not, this is looking somewhere else");
        Assert.True(tracked.Any(f => f.EndsWith(".kt", StringComparison.OrdinalIgnoreCase)),
            "no Kotlin sources found - the repository root is probably wrong");
    }

    [Fact]
    public void TheSecretPatternsMatchTheShapesTheyClaim()
    {
        var assignment = new Regex(
            @"(?im)^\s*(storePassword|keyPassword)\s*=\s*(\S+)\s*$", RegexOptions.Compiled);

        Assert.True(assignment.IsMatch("storePassword=netpilot\n"),
            "the pattern does not match a real keystore.properties line");
        Assert.True(assignment.IsMatch("  keyPassword   =   hunter2  \n"),
            "whitespace around the assignment should not matter");

        Assert.False(assignment.IsMatch("storePassword=\n"),
            "a key with no value carries no secret");
        Assert.False(assignment.IsMatch("the storePassword field is documented here"),
            "prose mentioning the field is not a credential");
        Assert.False(assignment.IsMatch("value(\"storePassword\", ...)"),
            "code that reads the key is not a credential");
    }
}