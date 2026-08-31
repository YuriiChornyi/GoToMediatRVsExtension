using Microsoft.CodeAnalysis;

namespace VSIXExtension.Tests
{
    /// <summary>
    /// Navigation is all about <em>where</em> the caret lands, so most assertions are about a
    /// <see cref="Location"/>. Comparing the source line it points at reads far better in a failure
    /// message than comparing raw character offsets.
    /// </summary>
    public static class LocationExtensions
    {
        /// <summary>The trimmed source line the location starts on, or "&lt;no location&gt;".</summary>
        public static string GetLineText(this Location location)
        {
            var tree = location?.SourceTree;
            if (tree == null)
                return "<no location>";

            var line = tree.GetText().Lines[location.GetLineSpan().StartLinePosition.Line];
            return line.ToString().Trim();
        }

        /// <summary>The 1-based line number the location starts on, matching what the editor shows.</summary>
        public static int GetLineNumber(this Location location)
            => location == null ? -1 : location.GetLineSpan().StartLinePosition.Line + 1;
    }
}
