namespace RiotPrefill.Models
{
    [UsedImplicitly(ImplicitUseTargetFlags.Members)]
    [Intellenum(typeof(string))]
    public sealed partial class ArtifactType
    {
        public static readonly ArtifactType LolGameClient = new ArtifactType("lol-game-client");
        public static readonly ArtifactType LolStandaloneClientContent = new ArtifactType("lol-standalone-client-content");
    }
}