using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace NetPilot.Tests;

/// <summary>
/// No source file may contain an absolute path to a specific machine.
///
/// One project, one drive, one developer's folder. It is the kind of thing that works perfectly
/// for the person who wrote it and is invisible until someone else touches the project - and then
/// it fails in a way that reads as "the project is broken" rather than "a script points at a
/// directory that does not exist here".
///
/// It was not only inconvenience. Two of these were constants in shipped code, and both disabled
/// a feature on every real installation:
///
/// - <c>MobileVpnService.PendingMarkerPath</c> held the flag marking a share still applied. It
///   lived at a path that exists on exactly one machine, so on every other install it was always
///   absent - and the recovery it existed for, restoring the user's own network settings after a
///   crash, never ran.
/// - <c>App.HealthCheckFile</c> is what the CI installer job reads, so that job could not have
///   worked off any checkout but the author's.
///
/// So this is asserted rather than left to review: a value that is correct only on one machine
/// cannot be caught by running the tests on that machine.
///
/// A machine-rooted path on an allowlist is permitted, because a few genuinely need one (a system
/// directory, the OS temp folder). What is forbidden is a *drive letter*, which has no meaning
/// anywhere else.
///
/// </summary>
public sealed class NoAbsolutePathsTests
{
    /// <summary>Extensions worth reading. Everything else is a build output or a dependency.</summary>
    private static readonly string[] Extensions =
    {
        ".cs", ".kt", ".ps1", ".yml", ".yaml", ".xaml", ".json", ".props", ".targets", ".sh",
    };

    /// <summary>
    /// Directories that are not ours: build output, restored packages, and version control.
    /// Skipped by name rather than by a pattern because a pattern can be made to match too much.
    /// </summary>
    private static readonly string[] SkipDirectories =
    {
        "bin", "obj", "build", ".git", ".gradle", ".vs", "node_modules", "dist", "TestResults",
    };

    /// <summary>
    /// A Windows drive letter followed by a colon and a backslash. Anchored at a quote or a word
    /// boundary so a string that merely contains the letters, like a doc comment naming the old
    /// path, is not flagged - those are history, not configuration.
    /// </summary>
    private static readonly Regex DrivePath =
        new(@"(?<![A-Za-z0-9])[A-Za-z]:\\{1,2}[A-Za-z0-9_]", RegexOptions.Compiled);

    /// <summary>
    /// A quoted POSIX system path. Anchored on the closing quote so the shape is a whole literal
    /// rather than a prefix - a doc comment naming /proc/definitely/not/writable is a negative
    /// test input elsewhere in this project and must not be flagged by accident.
    /// </summary>
    private static readonly Regex PosixPath =
        new(@"""(?:/etc/|/usr/|/var/|/proc/|/sys/|/bin/|/sbin/|/opt/|/home/)[A-Za-z0-9_./-]*""",
            RegexOptions.Compiled);

    /// <summary>
    /// Paths that are legitimately machine-rooted.
    ///
    /// These name a location that is the same on every Windows machine, so they carry no
    /// information about the developer and work in any checkout. The rule is not "no absolute
    /// path" - it is "no path that means something different on someone else's computer".
    ///
    /// Matched as a prefix rather than as whole lines, because the same test file uses two of
    /// them for different cases.
    /// </summary>
    private static readonly string[] AllowedPrefixes =
    {
        @"C:\Program Files\",
        @"C:\Program Files (x86)\",
        @"C:\Windows\",
        @"C:\Users\Public\",
        // A test that proves a path is case-insensitively matched, so it names two spellings of
        // the same path on purpose. Skipping by prefix would not work here; the test file is
        // excluded wholesale below instead.
    };

    /// <summary>
    /// Files allowed to contain a drive path, because the drive path is the thing under test.
    /// </summary>
    private static readonly string[] ExemptFiles =
    {
        // This file, because the drive path is the thing being asserted on.
        @"NetPilot.Tests\NoAbsolutePathsTests.cs",
        // Proves a path is matched case-insensitively, so it names two spellings of one path on
        // purpose - and both are the kind of string this rule forbids.
        @"NetPilot.Core.Tests\TokenBucketTests.cs",
    };

    [Fact]
    public void NoSourceFileContainsAMachineRootedPath()
    {
        string root = RepositoryRoot();
        var offenders = new List<List<string>>();

        foreach (var file in SourceFiles(root))
        {
            string rel = Rel(root, file);
            if (ExemptFiles.Any(e =>
                    rel.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();

                // Comments record what used to be there and why. Flagging them would make the
                // fix impossible to describe - and the description is the only thing that stops
                // the next person reintroducing it.
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") ||
                    trimmed.StartsWith("/*") || trimmed.StartsWith("#")) continue;

                if (!DrivePath.IsMatch(line)) continue;
                if (AllowedPrefixes.Any(p => line.Contains(p))) continue;

                offenders.Add(new List<string>
                {
                    rel + ":" + (i + 1),
                    "machine-rooted Windows path",
                    trimmed.Length > 110 ? trimmed.Substring(0, 110) + "..." : trimmed,
                });
            }
        }

        Assert.True(offenders.Count == 0,
            "these files contain an absolute path into one machine:\n" + Describe(offenders));
    }

    /// <summary>
    /// The proof that the scan is looking at something.
    ///
    /// A check like this fails silently when its file walk finds nothing, or its pattern stops
    /// matching - and a check that cannot fail is worse than none, because it is read as coverage.
    /// </summary>
    [Fact]
    public void TheScanWouldCatchTheOriginalBug()
    {
        Assert.True(DrivePath.IsMatch(@"$root = 'E:\op dn\NetPilot'"),
            "the pattern no longer matches the original defect");
        Assert.True(DrivePath.IsMatch("""C:\Users\someone\thing"""),
            "the pattern only matches the author's folder");

        // And the things it must not match, or it would be useless noise.
        Assert.False(DrivePath.IsMatch("""C:/forward/slashes"""),
            "a forward-slash path is not what this guards against on Windows");
        Assert.False(DrivePath.IsMatch("var drive = \"D:\";"),
            "a bare drive letter with no path is not a hardcoded path");
    }

    [Fact]
    public void TheWalkActuallyFindsSourceFiles()
    {
        string root = RepositoryRoot();
        int count = 0;
        foreach (var _ in SourceFiles(root)) count++;

        Assert.True(count > 40,
            $"the walk found only {count} source files under {root}, so the scan proves nothing");
    }

    // ------------------------------------------------------------------ helpers

    private static bool Contains(string[] set, string value)
    {
        foreach (var s in set)
            if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] SkipList = Extensions;
    private static readonly string[] SkipList2 = SkipDirectories;

    private static IEnumerable<string> SourceFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] subdirs;
            string[] files;
            try { subdirs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var f in files)
            {
                string ext = Path.GetExtension(f);
                if (Contains(SkipList, ext))
                    yield return f;
            }

            foreach (var d in subdirs)
            {
                string name = Path.GetFileName(d);
                if (Contains(SkipList2, name))
                    continue;
                stack.Push(d);
            }
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            // The repository is the directory holding several projects, which is also where the
            // solution file is. Found by walking up from the test assembly.
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

    private static string Rel(string root, string file)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? file.Substring(prefix.Length)
            : Path.GetFileName(file);
    }

    private static string Describe(IEnumerable<List<string>> offenders)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var o in offenders)
            sb.Append("  ").Append(o[0]).Append("  ").Append(o[1])
              .Append('\n').Append("      ").Append(o[2]).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// The POSIX half of the rule, asserted so the pattern is not dead code.
    ///
    /// A quoted absolute system path is the shape that matters - a value written into code that
    /// only resolves on one machine's layout. The daemon legitimately reads <c>/proc</c> and
    /// <c>/sys</c>, so this is a documented shape rather than a blanket ban, which is exactly why
    /// it needs a test proving it still matches.
    /// </summary>
    [Fact]
    public void ThePosixRuleIsAlive()
    {
        Assert.True(PosixPath.IsMatch(@"""/home/scratch"""),
            "the POSIX pattern no longer matches the shape it guards against");
        Assert.False(PosixPath.IsMatch("""Path.Combine("a", "b")"""),
            "an ordinary relative path is not what this guards against");
    }
}