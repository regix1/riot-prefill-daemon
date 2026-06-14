namespace RiotPrefill.Models
{
    /// <summary>
    /// Model that represents a request that could be made to a CDN.
    /// </summary>
    public sealed class Request
    {
        public Request()
        {

        }

        public Request(string bundleKey, long startBytes, long endBytes)
        {
            BundleKey = bundleKey;

            LowerByteRange = startBytes;
            UpperByteRange = endBytes;
        }

        public Request(string bundleKey, List<ByteRange> byteRanges)
        {
            BundleKey = bundleKey;
            ByteRanges = byteRanges;
        }

        public string BundleKey { get; set; }

        public List<ByteRange> ByteRanges { get; init; }

        public long LowerByteRange { get; set; }
        public long UpperByteRange { get; set; }

        // Bytes are an inclusive range.  Ex bytes 0->9 == 10 bytes
        public long TotalBytes => UpperByteRange - LowerByteRange + 1;

        public long TotalBytes2 => ByteRanges.Select(e => e.TotalBytes).Sum();

        public override string ToString()
        {
            if (ByteRanges != null && ByteRanges.Count != 0)
            {
                var firstRange = ByteRanges.First();
                var secondRange = ByteRanges.Last();

                if (ByteRanges.Count == 2)
                {
                    return $"{BundleKey}.bundle {ByteSize.FromBytes(TotalBytes2).ToString()} {firstRange} {secondRange}";
                }
                return $"{BundleKey}.bundle {ByteSize.FromBytes(TotalBytes2).ToString()} {firstRange} ... {secondRange}";
            }
            return $"{BundleKey}.bundle {(LowerByteRange + "-" + UpperByteRange).PadLeft(15)} {ByteSize.FromBytes(TotalBytes).ToString()}";
        }

        public bool Overlaps(Request request2)
        {
            int overlap = 1 * 1;

            if (LowerByteRange <= request2.LowerByteRange)
            {
                // Checks to see if ranges are overlapping ex 0-100 and 50-200
                var areOverlapping = UpperByteRange >= request2.LowerByteRange;
                if (!areOverlapping)
                {
                    // Seeing if adjacent ranges can be combined, ex 0-100 and 101-200
                    if (UpperByteRange + overlap >= request2.LowerByteRange)
                    {
                        return true;
                    }
                }
                return areOverlapping;
            }
            return request2.UpperByteRange >= LowerByteRange;
        }

        public void MergeWith(Request request2)
        {
            LowerByteRange = Math.Min(LowerByteRange, request2.LowerByteRange);
            UpperByteRange = Math.Max(UpperByteRange, request2.UpperByteRange);
        }
    }

    public class ByteRange
    {
        public ByteRange(long lower, long upper)
        {
            Lower = lower;
            Upper = upper;
        }

        public long Lower { get; set; }
        public long Upper { get; set; }

        // Bytes are an inclusive range.  Ex bytes 0->9 == 10 bytes
        public long TotalBytes => Upper - Lower + 1;

        public override string ToString()
        {
            return $"{Lower}-{Upper}";
        }
    }
}
