using System.Runtime.CompilerServices;

// review.md §5: lets NoBorders.Tests see the internal pure-function helpers
// (MainForm.CleanExeBaseName/ComputeAlignOffset, ArtworkService.SanitizeFileName)
// without widening them to public — they're implementation details, not API.
[assembly: InternalsVisibleTo("NoBorders.Tests")]
