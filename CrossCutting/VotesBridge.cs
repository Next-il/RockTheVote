using VotesApi;

namespace cs2_rockthevote
{
    /// <summary>
    /// Next-il/Votes, looked up on every use because it can load after RockTheVote. Votes owns the game's vote
    /// controller, the sv_allow_votes window and the vote card, so the rtv and extend yes/no votes only say what
    /// to ask. Null when Votes is not installed: the callers then fall back to their chat votes.
    /// </summary>
    internal static class VotesBridge
    {
        public static IVotesApi? Api
        {
            get
            {
                try { return IVotesApi.Capability.Get(); }
                catch { return null; }
            }
        }

        /// <summary>A Votes vote is running - ours or another plugin's (custom rounds, FFA).</summary>
        public static bool IsVoteActive => Api?.IsVoteActive ?? false;
    }
}
