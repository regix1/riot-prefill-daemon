namespace RiotPrefill.Models
{
    [UsedImplicitly(ImplicitUseTargetFlags.Members)]
    [Intellenum(typeof(string))]
    public sealed partial class Patchline
    {
        public static readonly Patchline LeagueOfLegends = new Patchline("league_of_legends");
        public static readonly Patchline Valorant = new Patchline("valorant");
    }
}