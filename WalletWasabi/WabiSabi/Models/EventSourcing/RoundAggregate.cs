using System;
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

		public Round State { get; private set; }
		public MultipartyTransactionAggregate MultipartyTransactionAggregate { get; }

		public override void Apply(AliceCreated aliceAddedEvent)
		{
			State = State with { Alices = State.Alices.Add(aliceAddedEvent.Alice) };
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
			State = State with { Phase = stateChangedEvent.NewPhase };

			if (State.Phase == Phase.ConnectionConfirmation)
			{
				State = State with { ConnectionConfirmationTimeFrame = State.ConnectionConfirmationTimeFrame.StartNow() };
			}
			else if (State.Phase == Phase.OutputRegistration)
			{
				State = State with { OutputRegistrationTimeFrame = State.OutputRegistrationTimeFrame.StartNow() };
			}
			else if (State.Phase == Phase.TransactionSigning)
			{
				State = State with { TransactionSigningTimeFrame = State.TransactionSigningTimeFrame.StartNow() };
			}
			else if (State.Phase== Phase.Ended)
			{
				State = State with { End = DateTimeOffset.UtcNow };
			}
		}
		public override void Apply(CoinJoinTransactionBroadcasted coinJoinTransactionBroadcastedEvent)
		{
			State = State with { WasTransactionBroadcast = true };
		}
	}
}
