using NBitcoin;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Models.EventSourcing
{
	public class RoundAggregate : Aggregate
	{
		public RoundAggregate(Round roundState)
		{
			State = roundState;
			var roundParameters = roundState.RoundParameters;
			var allowedAmounts = new MoneyRange(roundParameters.MinRegistrableAmount, roundParameters.MaxRegistrableAmount);
			var txParams = new MultipartyTransactionParameters(roundParameters.FeeRate, allowedAmounts, allowedAmounts, roundParameters.Network);
			MultipartyTransactionAggregate = new MultipartyTransactionAggregate(txParams);
		}

		public Round State { get; init; }
		public MultipartyTransactionAggregate MultipartyTransactionAggregate { get; }

		public override void Apply(InputAdded inputAddedEvent)
		{
			MultipartyTransactionAggregate.Apply(inputAddedEvent);
		}
		public override void Apply(OutputAdded outputAddedEvent)
		{
			MultipartyTransactionAggregate.Apply(outputAddedEvent);
		}
		public override void Apply(WitnessAdded witnessAddedEvent)
		{
			MultipartyTransactionAggregate.Apply(witnessAddedEvent);
		}
		public override void Apply(StatePhaseChanged stateChangedEvent)
		{
			//State = State with { Phase = stateChangedEvent.NewPhase };
		}
	}
}
