namespace RiotPrefill.Models
{
    public sealed class PatchlinesResponse
    {
        public KeystoneProduct KeystoneProduct
        {
            get
            {
                if (LeagueOfLegends != null)
                {
                    return LeagueOfLegends;
                }
                if (Valorant != null)
                {
                    return Valorant;
                }
                if (LegendsOfRuneterra != null)
                {
                    return LegendsOfRuneterra;
                }
                if (TeamfightTactics != null)
                {
                    return TeamfightTactics;
                }
                return null;
            }
        }

        [JsonPropertyName("keystone.products.league_of_legends.patchlines.live")]
        public KeystoneProduct LeagueOfLegends { get; set; }

        [JsonPropertyName("keystone.products.valorant.patchlines.live")]
        public KeystoneProduct Valorant { get; set; }

        [JsonPropertyName("keystone.products.bacon.patchlines.live")]
        public KeystoneProduct LegendsOfRuneterra { get; set; }

        [JsonPropertyName("keystone.products.teamfighttactics.patchlines.live")]
        public KeystoneProduct TeamfightTactics { get; set; }
    }

    public class KeystoneProduct
    {
        public Platforms platforms { get; set; }
        public string version { get; set; }
    }

    public class Platforms
    {
        [JsonPropertyName("win")]
        public Win Win { get; set; }
    }

    public class Win
    {
        public Configuration1[] configurations { get; set; }
        public object dependencies { get; set; }
    }

    public class Configuration1
    {
        public string id { get; set; }
        public string patch_url { get; set; }

        public string[] allowed_http_fallback_hostnames { get; set; }

        // Products with an empty patch_url (Teamfight Tactics) name their release through the sieve service instead.
        public PatchArtifact[] patch_artifacts { get; set; }
    }

    public class PatchArtifact
    {
        public string type { get; set; }
        public PatchSieve patchsieve { get; set; }
    }

    public class PatchSieve
    {
        public string product { get; set; }
        public string version_set { get; set; }
        public PatchSieveParameters parameters { get; set; }
    }

    public class PatchSieveParameters
    {
        public string artifact_type_id { get; set; }
        public string platform { get; set; }
    }
}