using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using WalletWasabi.WabiSabi.Backend.Rounds;

namespace WalletWasabi.WabiSabi.Models.EventSourcing
{
	public record ActiveRoundsState
	{
		public ImmutableList<RoundAggregate> Rounds { get; init; } = ImmutableList<RoundAggregate>.Empty;
		public IEnumerable<RoundAggregate> InPhase(Phase phase) => Rounds.Where(x => x.State.Phase == phase);
	}
}
