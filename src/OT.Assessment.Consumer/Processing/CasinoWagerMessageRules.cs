using OT.Assessment.Core.Messaging;

namespace OT.Assessment.Consumer.Processing;

/// <summary>
/// The consumer does not trust that every publisher validated the message: it checks the
/// invariants the database depends on before writing.
/// </summary>
internal static class CasinoWagerMessageRules
{
    public static bool IsValid(CasinoWagerMessage wager, out string reason)
    {
        reason = wager switch
        {
            { WagerId: var id } when id == Guid.Empty => "wagerId is missing",
            { AccountId: var id } when id == Guid.Empty => "accountId is missing",
            { Username: null or "" } => "username is missing",
            { Provider: null or "" } => "provider is missing",
            { GameName: null or "" } => "gameName is missing",
            { CountryCode.Length: not 2 } or { CountryCode: null } => "countryCode must be two characters",
            { Amount: < 0 } => "amount is negative",
            { NumberOfBets: < 1 } => "numberOfBets must be at least 1",
            _ => null
        };

        return reason is null;
    }
}
