using Vogen;

namespace RiotPrefill.Models
{
    //TODO document
    public sealed class Bundle
    {
        /// <summary>
        /// The bundle identifier which will be referenced when making a CDN request
        /// </summary>
        public BundleId Id { get; }

        public List<BundleChunk> Chunks { get; } = new List<BundleChunk>();
        public Dictionary<string, BundleChunk> ChunkLookup = new Dictionary<string, BundleChunk>();

        public Bundle(ReleaseManifestBundle source)
        {
            Id = BundleId.From(string.Format("{0:X16}", source.ID));

            for (var i = 0; i < source.Chunks.Count; i++)
            {
                var currentChunk = source.Chunks[i];

                var newChunk = new BundleChunk
                {
                    Id = BitConverter.GetBytes(currentChunk.ID).ToHexString(),
                    BundleId = Id,

                    CompressedSize = currentChunk.CompressedSize
                };
                Chunks.Add(newChunk);

                if (i > 0)
                {
                    Chunks[i].OffsetFromStart = Chunks[i - 1].OffsetFromStart + Chunks[i - 1].CompressedSize;
                }
            }

            ChunkLookup = Chunks.ToDictionary(e => e.Id, e => e);
        }

        public override string ToString()
        {
            return Id.Value;
        }
    }

    [ValueObject<string>]
    public readonly partial struct BundleId { }
}