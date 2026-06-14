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
                return null;
            }
        }

        [JsonPropertyName("keystone.products.league_of_legends.patchlines.live")]
        public KeystoneProduct LeagueOfLegends { get; set; }

        [JsonPropertyName("keystone.products.valorant.patchlines.live")]
        public KeystoneProduct Valorant { get; set; }
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
    }
}