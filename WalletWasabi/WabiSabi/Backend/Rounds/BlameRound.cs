using NBitcoin;
using System.Collections.Generic;
using Nito.AsyncEx;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Backend.Rounds;

public record BlameRound : RoundInInputRegistrationPhase
{
	public BlameRound(RoundParameters parameters, Round blameOf, ISet<OutPoint> blameWhitelist)
		: base(parameters, DateTimeOffset.UtcNow, new ConstructionState(parameters), new AsyncLock())
	{
		BlameOf = blameOf;
		BlameWhitelist = blameWhitelist;
	}

	public Round BlameOf { get; }
	public ISet<OutPoint> BlameWhitelist { get; }
}
