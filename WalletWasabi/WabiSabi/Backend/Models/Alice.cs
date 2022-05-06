using NBitcoin;
using WalletWasabi.Crypto;
using WalletWasabi.WabiSabi.Backend.Rounds;
using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.WabiSabi.Backend.Models;

public class Alice
{
	public Alice(Coin coin, OwnershipProof ownershipProof, Guid id, TimeSpan connectionConfirmationTimeout, bool isPayingZeroCoordinationFee)
	{
		// TODO init syntax?
		Coin = coin;
		OwnershipProof = ownershipProof;
		Id = id;
		IsPayingZeroCoordinationFee = isPayingZeroCoordinationFee;
		// Have alice timeouts a bit sooner than the timeout of connection confirmation phase.
		Deadline = DateTimeOffset.UtcNow + (connectionConfirmationTimeout * 0.9);
	}

	public Guid Id { get; }
	public DateTimeOffset Deadline { get; }
	public Coin Coin { get; }
	public OwnershipProof OwnershipProof { get; }
	public Money TotalInputAmount => Coin.Amount;
	public int TotalInputVsize => Coin.ScriptPubKey.EstimateInputVsize();
	public bool IsPayingZeroCoordinationFee { get; } = false;

	public long CalculateRemainingVsizeCredentials(int maxRegistrableSize) => maxRegistrableSize - TotalInputVsize;

	public Money CalculateRemainingAmountCredentials(FeeRate feeRate, CoordinationFeeRate coordinationFeeRate) =>
		Coin.EffectiveValue(feeRate, IsPayingZeroCoordinationFee ? CoordinationFeeRate.Zero : coordinationFeeRate);
}
