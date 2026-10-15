using System.Runtime.CompilerServices;

// Lets the test project reach the internals that carry real logic - the moving speed meter,
// for one - rather than leaving that logic unchecked. Same arrangement the daemon already
// uses with its own test assembly.
[assembly: InternalsVisibleTo("NetPilot.Tests")]
