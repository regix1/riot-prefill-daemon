namespace RiotPrefill
{
    public static class Extensions
    {
        public static string ToHexString(this byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", "");
        }

        public static ByteSize SumTotalBytes(this List<Request> requests)
        {
            return ByteSize.FromBytes(requests.Sum(e => e.TotalBytes));
        }
    }
}
