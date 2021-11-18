using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Models.EventSourcing
{
	public class MultipartyTransactionAggregate : Aggregate
	{
		public MultipartyTransactionAggregate(MultipartyTransactionParameters parameters)
		{
			State = new MultipartyTransactionState(parameters);
		}

		public MultipartyTransactionState State { get; private set; }

		public override void Apply(InputAdded inputAddedEvent)
		{
			State = State with { Inputs = State.Inputs.Add(inputAddedEvent.Coin) };
		}
		public override void Apply(OutputAdded outputAddedEvent)
		{
			State = State with { Outputs = State.Outputs.Add(outputAddedEvent.Output) };
		}
		public override void Apply(WitnessAdded witnessAddedEvent)
		{
			var (idx,  witness) = witnessAddedEvent.InputWitnessPairs;
			State = State with { Witnesses = State.Witnesses.Add(idx, witness) };
		}
	}
}
