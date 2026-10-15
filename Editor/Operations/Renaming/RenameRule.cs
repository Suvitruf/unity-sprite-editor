using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BetterSpriteEditor.Operations.Renaming
{
    public enum RenameCaseMode
    {
        Unchanged,
        Upper,
        Lower,
        Title
    }

    /// <summary>
    /// A composable batch-rename rule. Steps are applied in a fixed order so the result is
    /// predictable: base name → find/replace → strip → prefix/suffix → case → numbering.
    /// Pure logic, no UI dependencies.
    /// </summary>
    public struct RenameRule
    {
        public const string NameToken = "{name}";
        public const string NumberToken = "{n}";

        /// <summary>When false the original name is the starting point instead of BaseName.</summary>
        public bool ReplaceBaseName;
        public string BaseName;

        public string FindText;
        public string ReplaceText;
        public bool UseRegex;
        public bool CaseSensitive;

        public int StripFirst;
        public int StripLast;

        public string Prefix;
        public string Suffix;

        public RenameCaseMode CaseMode;

        public bool UseNumbering;
        public int NumberStart;
        public int NumberStep;
        public int NumberPadding;

        public static RenameRule Default => new()
        {
            ReplaceBaseName = false,
            BaseName = string.Empty,
            FindText = string.Empty,
            ReplaceText = string.Empty,
            Prefix = string.Empty,
            Suffix = string.Empty,
            CaseMode = RenameCaseMode.Unchanged,
            UseNumbering = false,
            NumberStart = 0,
            NumberStep = 1,
            NumberPadding = 1
        };

        /// <summary>
        /// Produces the new name for one item. <paramref name="index"/> is the item's position in
        /// the ordered selection, which drives the counter.
        /// Throws <see cref="ArgumentException"/> when the regex pattern is invalid.
        /// </summary>
        public string Apply(string originalName, int index)
        {
            string name = ReplaceBaseName ? BaseName ?? string.Empty : originalName;
            name = name.Replace(NameToken, originalName);

            if (!string.IsNullOrEmpty(FindText))
                name = ApplyFindReplace(name);

            if (StripFirst > 0)
                name = StripFirst >= name.Length ? string.Empty : name.Substring(StripFirst);
            if (StripLast > 0)
                name = StripLast >= name.Length ? string.Empty : name.Substring(0, name.Length - StripLast);

            name = (Prefix ?? string.Empty) + name + (Suffix ?? string.Empty);

            name = CaseMode switch
            {
                RenameCaseMode.Upper => name.ToUpperInvariant(),
                RenameCaseMode.Lower => name.ToLowerInvariant(),
                RenameCaseMode.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()),
                _ => name
            };

            // A {n} token positions the counter; otherwise it is appended when numbering is on.
            bool hasToken = name.Contains(NumberToken, StringComparison.OrdinalIgnoreCase);
            if (UseNumbering || hasToken)
            {
                int number = NumberStart + index * NumberStep;
                string numberText = number.ToString(CultureInfo.InvariantCulture)
                    .PadLeft(Math.Max(1, NumberPadding), '0');
                name = hasToken
                    ? Regex.Replace(name, Regex.Escape(NumberToken), numberText, RegexOptions.IgnoreCase)
                    : name + numberText;
            }

            return name;
        }

        private string ApplyFindReplace(string name)
        {
            string replacement = ReplaceText ?? string.Empty;
            if (!UseRegex)
            {
                return name.Replace(FindText, replacement,
                    CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                var options = CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                return Regex.Replace(name, FindText, replacement, options, TimeSpan.FromMilliseconds(100));
            }
            catch (RegexMatchTimeoutException exception)
            {
                throw new ArgumentException("The regular expression took too long. Simplify the pattern.", exception);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"Invalid regular expression: {exception.Message}", exception);
            }
        }
    }
}
