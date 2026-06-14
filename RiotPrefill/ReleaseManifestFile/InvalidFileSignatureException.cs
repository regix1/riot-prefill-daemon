namespace RiotPrefill.ReleaseManifestFile
{
    public class InvalidFileSignatureException : Exception
    {
        public InvalidFileSignatureException()
        {
        }

        public InvalidFileSignatureException(string message)
            : base(message)
        {
        }

        public InvalidFileSignatureException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}