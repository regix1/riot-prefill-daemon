namespace RiotPrefill
{
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, WriteIndented = true)]
    [JsonSerializable(typeof(ReleaseApiResponse))]
    [JsonSerializable(typeof(PatchlinesResponse))]
    internal sealed partial class SerializationContext : JsonSerializerContext
    {
    }
}