using System.Runtime.CompilerServices;

// The backends' shell wrapper and their input validation are internal, and they should stay
// internal: they are implementation, not API. But they are also where the argument-injection
// boundary lives, which is precisely the thing that most needs a test.
//
// Exposing them to the test assembly is a deliberate, narrow exception. The alternative was
// making them public, which would put `Shell.Run` on the daemon's surface for any future caller -
// and a caller with no validation in front of it is how this class of bug comes back.
[assembly: InternalsVisibleTo("NetPilot.Daemon.Tests")]