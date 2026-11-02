namespace OnbodyMic.Media;

/// <summary>Ordinal, case-insensitive names with numeric runs compared without integer overflow.</summary>
internal sealed class NaturalNameComparer : IComparer<string>
{
    public static NaturalNameComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var l = 0;
        var r = 0;
        while (l < left.Length && r < right.Length)
        {
            if (char.IsAsciiDigit(left[l]) && char.IsAsciiDigit(right[r]))
            {
                var leftStart = l;
                var rightStart = r;
                while (l < left.Length && char.IsAsciiDigit(left[l])) l++;
                while (r < right.Length && char.IsAsciiDigit(right[r])) r++;
                var leftSignificant = leftStart;
                var rightSignificant = rightStart;
                while (leftSignificant < l && left[leftSignificant] == '0') leftSignificant++;
                while (rightSignificant < r && right[rightSignificant] == '0') rightSignificant++;
                var lengthOrder = (l - leftSignificant).CompareTo(r - rightSignificant);
                if (lengthOrder != 0) return lengthOrder;
                var numericOrder = left.AsSpan(leftSignificant, l - leftSignificant)
                    .SequenceCompareTo(right.AsSpan(rightSignificant, r - rightSignificant));
                if (numericOrder != 0) return numericOrder;
                var paddingOrder = (l - leftStart).CompareTo(r - rightStart);
                if (paddingOrder != 0) return paddingOrder;
            }
            else
            {
                var order = char.ToUpperInvariant(left[l]).CompareTo(char.ToUpperInvariant(right[r]));
                if (order != 0) return order;
                l++;
                r++;
            }
        }
        var remainingOrder = (left.Length - l).CompareTo(right.Length - r);
        return remainingOrder != 0 ? remainingOrder : StringComparer.Ordinal.Compare(left, right);
    }
}
