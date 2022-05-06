using System.Collections.Generic;
using System.Threading;
using NBitcoin;
using WalletWasabi.Crypto;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.WabiSabi.Backend.Rounds;

public class RoundParameterFactory
{
	public RoundParameterFactory(WabiSabiConfig config, Network network)
	{
		Config = config;
		Network = network;
		MaxSuggestedAmountProvider = new(Config);
	}

	public WabiSabiConfig Config { get; }
	public Network Network { get; }
	public MaxSuggestedAmountProvider MaxSuggestedAmountProvider { get; }

	public virtual RoundParameters CreateRoundParameter(
		FeeRate miningMiningFeeRate,
		int connectionConfirmationStartedCounter) =>
		new(
			Network,
			miningMiningFeeRate,
			Config.CoordinationFeeRate,
			MaxSuggestedAmountProvider.GetMaxSuggestedAmount(connectionConfirmationStartedCounter),
			Config.MinInputCountByRound,
			Config.MaxInputCountByRound,
			new MoneyRange(Config.MinRegistrableAmount, Config.MaxRegistrableAmount),
			new MoneyRange(Config.MinRegistrableAmount, Config.MaxRegistrableAmount),
			Config.StandardInputRegistrationTimeout,
			Config.ConnectionConfirmationTimeout,
			Config.OutputRegistrationTimeout,
			Config.TransactionSigningTimeout,
			new CredentialIssuerSecretKey(SecureRandom.Instance),
			new CredentialIssuerSecretKey(SecureRandom.Instance));

	public virtual RoundParameters CreateBlameRoundParameter(
		FeeRate miningMiningFeeRate,
		Money maxSuggestedAmount) =>
		new(
			Network,
			miningMiningFeeRate,
			Config.CoordinationFeeRate,
			maxSuggestedAmount,
			Config.MinInputCountByRound,
			Config.MaxInputCountByRound,
			new MoneyRange(Config.MinRegistrableAmount, Config.MaxRegistrableAmount),
			new MoneyRange(Config.MinRegistrableAmount, Config.MaxRegistrableAmount),
			Config.BlameInputRegistrationTimeout,
			Config.ConnectionConfirmationTimeout,
			Config.OutputRegistrationTimeout,
			Config.TransactionSigningTimeout,
			new CredentialIssuerSecretKey(SecureRandom.Instance),
			new CredentialIssuerSecretKey(SecureRandom.Instance));
}
