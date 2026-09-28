namespace ConnectedMining
{
    internal enum LeaseDecision { Ignore, Retry, Finish }

    // A response must match both the assigned owner and the particular dispatch attempt.
    // Only an explicit "not owner, did not execute" permits another dispatch.
    internal sealed class DispatchLease
    {
        internal long Owner { get; private set; }
        internal int Attempt { get; private set; }
        internal float Sent { get; private set; }
        internal bool InFlight { get; private set; }
        internal bool Terminal { get; private set; }

        internal bool Begin(long owner, float now)
        {
            if (Terminal || InFlight || owner == 0) return false;
            Owner = owner; Sent = now; Attempt++; InFlight = true;
            return true;
        }

        internal LeaseDecision Reply(long sender, int attempt, int status)
        {
            if (Terminal || !InFlight || sender != Owner || attempt != Attempt || status < 0 || status > 2)
                return LeaseDecision.Ignore;
            InFlight = false;
            if (status == 1 && Attempt < 3) return LeaseDecision.Retry;
            Terminal = true;
            return LeaseDecision.Finish;
        }

        internal bool Expire(float now)
        {
            if (Terminal || !InFlight || now - Sent <= 22) return false;
            Terminal = true; InFlight = false;
            return true;
        }
    }
}
