namespace RiotPrefill.Models
{
    //TODO this whole thing is awful, need to trim whats not needed and rename what is
    public sealed class ReleaseApiResponse
    {
        public ReleaseInfo[] releases { get; set; }
    }

    public sealed class ReleaseInfo
    {
        // Utility properties to make working with this response easier
        public string ArtifactTypeId => _Release.ArtifactTypeId;
        public string DownloadUrl => _DownloadInfo.url;
        public Version Version => _Release.Version;
        public string Platform => string.Join(",", _Release.labels.platform.values);
        public string RiotPlatform => string.Join(",", _Release.labels.riotplatform.values);



        // Deserialized properties, these are what comes off the response json verbatim

        [JsonPropertyName("release")]
        public Release _Release { get; set; }

        [JsonPropertyName("compat_version")]
        public Compat_Version _compat_version { get; set; }

        [JsonPropertyName("download")]
        public DownloadInfo _DownloadInfo { get; set; }

        public override string ToString()
        {
            return $"{ArtifactTypeId} - {Version}";
        }
    }

    public sealed class Release
    {
        public string product { get; set; }
        public string id { get; set; }
        public Labels labels { get; set; }

        public string ArtifactTypeId => labels.riotartifact_type_id.values.First();

        private Version _version;
        public Version Version
        {
            get
            {
                if (_version == null)
                {
                    var artifactVersionIdAsString = labels.riotartifact_version_id.values.First();
                    var split = artifactVersionIdAsString.Split("+");
                    _version = Version.Parse(split[0]);
                }
                return _version;
            }
        }
    }

    public sealed class Labels
    {
        public Buildtracker_Config buildtracker_config { get; set; }
        public Cpuarch cpuarch { get; set; }
        public Platform platform { get; set; }


        public RiotAnticheat_Option riotanticheat_option { get; set; }

        [JsonPropertyName("riot:artifact_type_id")]
        public RiotArtifact_Type_Id riotartifact_type_id { get; set; }

        [JsonPropertyName("riot:artifact_version_id")]
        public RiotArtifact_Version_Id riotartifact_version_id { get; set; }

        public RiotCpu_Arch riotcpu_arch { get; set; }

        [JsonPropertyName("riot:platform")]
        public RiotPlatform riotplatform { get; set; }

        public RiotRevision riotrevision { get; set; }
    }

    public sealed class Buildtracker_Config
    {
        public string[] values { get; set; }
    }

    public sealed class Cpuarch
    {
        public string[] values { get; set; }
    }

    public sealed class Platform
    {
        public string[] values { get; set; }
    }

    public sealed class RiotAnticheat_Option
    {
        public string[] values { get; set; }
    }

    public sealed class RiotArtifact_Type_Id
    {
        public string[] values { get; set; }
    }

    public sealed class RiotArtifact_Version_Id
    {
        public string[] values { get; set; }
    }

    public sealed class RiotCpu_Arch
    {
        public string[] values { get; set; }
    }

    public sealed class RiotPlatform
    {
        public string[] values { get; set; }
    }

    public sealed class RiotRevision
    {
        public string[] values { get; set; }
    }

    public sealed class Compat_Version
    {
        public string id { get; set; }
    }

    public sealed class DownloadInfo
    {
        public string url { get; set; }
    }
}