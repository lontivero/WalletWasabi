namespace WalletWasabi.WabiSabi.Models.EventSourcing
{
	public abstract class Aggregate
	{
		public virtual void Apply(RoundCreated roundCreatedEvent)
		{
		}
		public virtual void Apply(AliceCreated inputAddedEvent)
		{
		}
		public virtual void Apply(OutputAdded outputAddedEvent)
		{
		}
		public virtual void Apply(WitnessAdded witnessAddedEvent)
		{
		}
		public virtual void Apply(StatePhaseChanged stateChangedEvent)
		{
		}
		public virtual void Apply(CoinJoinTransactionBroadcasted coinJoinTransactionBroadcastedEvent)
		{
		}
	}
}
