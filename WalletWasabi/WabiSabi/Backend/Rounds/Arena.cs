using NBitcoin;
using NBitcoin.RPC;
using Nito.AsyncEx;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Bases;
using WalletWasabi.BitcoinCore.Rpc;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.WabiSabi.Backend.Banning;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using WalletWasabi.WabiSabi.Backend.Rounds.CoinJoinStorage;
using WalletWasabi.WabiSabi.Backend.Statistics;
using System.Collections.Immutable;
using WalletWasabi.WabiSabi.Crypto;
using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.WabiSabi.Backend.Rounds;

public class RoundsManager
{
	private AsyncLock RoundsLock { get; } = new();
	public List<Round> Rounds { get; } = new();
	public Dictionary<uint256, (CredentialIssuer AmountCredentialIssuer, CredentialIssuer VsizeCredentialIssuer)> issuers = new();
		
    public List<Coin> AllRegisteredCoins { get; } = new();
    
    public void Add(Round round)
    {
	    Rounds.Add(round);
    }
    
	public void Update(Round round)
	{
		throw new NotImplementedException();
	}

	
	public Round Get(uint256 roundId) =>
		Rounds.FirstOrDefault(x => x.Id == roundId)
		?? throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.RoundNotFound, $"Round ({roundId}) not found.");

	public TRound Get<TRound>(uint256 roundId) where TRound : Round =>
		Rounds.OfType<TRound>().FirstOrDefault()
		?? throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.RoundNotFound, $"Round ({roundId}) not found.");

	public Round Get<TRoundA, TRoundB>(uint256 roundId) where TRoundA : Round where TRoundB : Round =>
		Rounds.OfType<TRoundA>().FirstOrDefault() as Round
		?? Rounds.OfType<TRoundB>().FirstOrDefault()
		?? throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.RoundNotFound, $"Round ({roundId}) not found.");

	public (CredentialIssuer AmountCredentialIssuer, CredentialIssuer VsizeCredentialIssuer) GetIssuers(uint256 roundId)
	{
		if (issuers.TryGetValue(roundId, out var roundIssuers))
		{
			return roundIssuers;
		}
		throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.RoundNotFound, $"Round ({roundId}) not found."); 
	}
	
	public async Task TimeoutRoundsAsync(TimeSpan expiryTimeout, CancellationToken cancellationToken)
	{
		using (await RoundsLock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			foreach (var expiredRound in Rounds.OfType<RoundInEndPhase>().Where(
				         x => x.StartTime + expiryTimeout < DateTimeOffset.UtcNow))
			{
				Rounds.Remove(expiredRound);
				var coinsToRemove = expiredRound.Alices.Select(x => x.Coin);
				AllRegisteredCoins.RemoveAll(x => coinsToRemove.Contains(x));
			}
		}
	}

	public async Task TimeoutAlicesAsync(CancellationToken cancellationToken)
	{
		foreach (var round in Rounds.OfType<RoundInInputRegistrationPhase>().Where(x => !x.IsInputRegistrationEnded(DateTimeOffset.UtcNow)))
		{
			using (await round.AsyncLock.LockAsync(cancellationToken).ConfigureAwait(false))
			{
				var expiredAlices = round.Alices.Where(x => x.Deadline < DateTimeOffset.UtcNow).ToList();
				if (expiredAlices.Count > 0)
				{
					round.LogInfo($"{expiredAlices.Count} alices timed out and removed.");
					Update(round with { Alices = round.Alices.RemoveRange(expiredAlices) } );
				}
			}
		}
	}
}

public partial class Arena : PeriodicRunner
{
	public Arena(
		TimeSpan period,
		WabiSabiConfig config,
		IRPCClient rpc,
		Prison prison,
		ICoinJoinIdStore coinJoinIdStore,
		RoundParameterFactory roundParameterFactory,
		CoinJoinTransactionArchiver? archiver = null,
		CoinJoinScriptStore? coinJoinScriptStore = null) : base(period)
	{
		Config = config;
		Rpc = rpc;
		Prison = prison;
		TransactionArchiver = archiver;
		CoinJoinIdStore = coinJoinIdStore;
		CoinJoinScriptStore = coinJoinScriptStore;
		RoundParameterFactory = roundParameterFactory;
	}

	public event EventHandler<Transaction>? CoinJoinBroadcast;

	public RoundsManager RoundsManager { get; } = new RoundsManager();
	
	private ImmutableArray<RoundState> RoundStates { get; set; } = ImmutableArray.Create<RoundState>();
	private AsyncLock AsyncLock { get; } = new();
	private WabiSabiConfig Config { get; }
	internal IRPCClient Rpc { get; }
	private Prison Prison { get; }
	private CoinJoinTransactionArchiver? TransactionArchiver { get; }
	public CoinJoinScriptStore? CoinJoinScriptStore { get; }
	private ICoinJoinIdStore CoinJoinIdStore { get; set; }
	private RoundParameterFactory RoundParameterFactory { get; }

	private int ConnectionConfirmationStartedCounter { get; set; }

	protected override async Task ActionAsync(CancellationToken cancel)
	{
		await TimeoutRoundsAsync(cancel).ConfigureAwait(false);

		await TimeoutAlicesAsync(cancel).ConfigureAwait(false);

		await StepTransactionSigningPhaseAsync(cancel).ConfigureAwait(false);

		await StepOutputRegistrationPhaseAsync(cancel).ConfigureAwait(false);

		await StepConnectionConfirmationPhaseAsync(cancel).ConfigureAwait(false);

		await StepInputRegistrationPhaseAsync(cancel).ConfigureAwait(false);

		cancel.ThrowIfCancellationRequested();

		// Ensure there's at least one non-blame round in input registration.
		await CreateRoundsAsync(cancel).ConfigureAwait(false);

		// RoundStates have to contain all states. Do not change stateId=0.
		RoundStates = RoundsManager.Rounds.Select(r => RoundState.FromRound(r, stateId: 0)).ToImmutableArray();
	}

	private async Task StepInputRegistrationPhaseAsync(CancellationToken cancel)
	{
		foreach (RoundInInputRegistrationPhase round in RoundsManager.Rounds
			         .OfType<RoundInInputRegistrationPhase>()
			         .Where(x => x.IsInputRegistrationEnded(DateTimeOffset.UtcNow)))
		{
			using (await round.AsyncLock.LockAsync(cancel).ConfigureAwait(false))
			{
				try
				{
					List<Alice> alicesToRemove = new();
					await foreach (var offendingAlices in CheckTxoSpendStatusAsync(round, cancel).ConfigureAwait(false))
					{
						alicesToRemove.AddRange(offendingAlices);
					}

					var purgedRound = round with { Alices = round.Alices.RemoveRange(alicesToRemove) };
					if (purgedRound.InputCount < purgedRound.Parameters.MinInputCountByRound)
					{
						if (!purgedRound.HasExpired(DateTimeOffset.UtcNow))
						{
							continue;
						}

						RoundsManager.Update(purgedRound.ToRoundInEndPhase());
						purgedRound.LogInfo(
							$"Not enough inputs ({purgedRound.InputCount}) in {nameof(Phase.InputRegistration)} phase. The minimum is ({purgedRound.Parameters.MinInputCountByRound}).");
					}
					else if (purgedRound.IsInputRegistrationEnded(DateTimeOffset.UtcNow))
					{
						RoundsManager.Update(purgedRound.ToRoundInEndPhase());
						ConnectionConfirmationStartedCounter++;
					}
				}
				catch (Exception ex)
				{
					RoundsManager.Update(round.ToRoundInEndPhase());
					round.LogError(ex.Message);
				}
			}
		}
	}

	private async Task StepConnectionConfirmationPhaseAsync(CancellationToken cancel)
	{
		foreach (RoundInConnectionConfirmationPhase round in RoundsManager.Rounds.OfType<RoundInConnectionConfirmationPhase>())
		{
			using (await round.AsyncLock.LockAsync(cancel).ConfigureAwait(false))
			{
				try
				{
					if (round.AreAllConfirmed)
					{
						RoundsManager.Update(round.ToRoundInOutputRegistrationPhase());
						return;
					}
					
					if (round.HasExpired(DateTimeOffset.UtcNow))
					{
						var alicesDidntConfirm = round.UnconfirmedAlices;
						foreach (var alice in alicesDidntConfirm)
						{
							Prison.Note(alice, round.Id);
						}

						var removedAliceCount = round.Alices.RemoveAll(x => alicesDidntConfirm.Contains(x));
						round.LogInfo($"{removedAliceCount} alices removed because they didn't confirm.");

						// Once an input is confirmed and non-zero credentials are issued, it must be included and must provide a
						// a signature for a valid transaction to be produced, therefore this is the last possible opportunity to
						// remove any spent inputs.
						List<Alice> alicesToRemove = new();
						if (round.InputCount >= round.Parameters.MinInputCountByRound)
						{
							await foreach (var offendingAlices in CheckTxoSpendStatusAsync(round, cancel)
								               .ConfigureAwait(false))
							{
								alicesToRemove.AddRange(offendingAlices);
							}

							round.LogInfo(
								$"There were {alicesToRemove.Count} alices removed because they spent the registered UTXO.");
						}

						var purgedRound = alicesToRemove.Any()
							? round with { Alices = round.Alices.RemoveRange(alicesToRemove) }
							: round;

						if (purgedRound.InputCount < purgedRound.Parameters.MinInputCountByRound)
						{
							RoundsManager.Update(purgedRound.ToRoundInEndPhase());
							round.LogInfo(
								$"Not enough inputs ({purgedRound.InputCount}) in {nameof(Phase.ConnectionConfirmation)} phase. The minimum is ({purgedRound.Parameters.MinInputCountByRound}).");
						}
						else
						{
							RoundsManager.Update(round.ToRoundInOutputRegistrationPhase());
						}
					}
				}
				catch (Exception ex)
				{
					RoundsManager.Update(round.ToRoundInEndPhase());
					round.LogError(ex.Message);
				}
			}
		}
	}

	private async Task StepOutputRegistrationPhaseAsync(CancellationToken cancellationToken)
	{
		foreach (RoundInOutputRegistrationPhase round in RoundsManager.Rounds.OfType<RoundInOutputRegistrationPhase>())
		{
			using (await round.AsyncLock.LockAsync(cancellationToken).ConfigureAwait(false))
			{
				try
				{
					var allReady = round.AreAllReady;

					var coinjoin = round.ConstructionState;
					if (allReady || round.HasExpired(DateTimeOffset.UtcNow))
					{
						round.LogInfo($"{coinjoin.Inputs.Count()} inputs were added.");
						round.LogInfo($"{coinjoin.Outputs.Count()} outputs were added.");

						var coordinatorScript = GetCoordinatorScriptPreventReuse(round);
						coinjoin = AddCoordinationFee(round, coinjoin, coordinatorScript);

						coinjoin = await TryAddBlameScriptAsync(round, coinjoin, allReady, coordinatorScript,
							cancellationToken).ConfigureAwait(false);

						RoundsManager.Update(round.ToRoundInTransactionSigningPhase(coordinatorScript, coinjoin.Finalize()));
					}
				}
				catch (Exception ex)
				{
					RoundsManager.Update(round.ToRoundInEndPhase());
					round.LogError(ex.Message);
				}
			}
		}
	}

	private async Task StepTransactionSigningPhaseAsync(CancellationToken cancellationToken)
	{
		foreach (RoundInTransactionSigningPhase round in RoundsManager.Rounds.OfType<RoundInTransactionSigningPhase>())
		{
			try
			{
				if (round.SigningState.IsFullySigned)
				{
					Transaction coinjoin = round.SigningState.CreateTransaction();

					// Logging.
					round.LogInfo("Trying to broadcast coinjoin.");
					Coin[]? spentCoins = round.Alices.Select(x => x.Coin).ToArray();
					Money networkFee = coinjoin.GetFee(spentCoins);
					uint256 roundId = round.Id;
					FeeRate feeRate = coinjoin.GetFeeRate(spentCoins);
					round.LogInfo($"Network Fee: {networkFee.ToString(false, false)} BTC.");
					round.LogInfo($"Network Fee Rate: {feeRate.FeePerK.ToDecimal(MoneyUnit.Satoshi) / 1000} sat/vByte.");
					round.LogInfo($"Number of inputs: {coinjoin.Inputs.Count}.");
					round.LogInfo($"Number of outputs: {coinjoin.Outputs.Count}.");
					round.LogInfo($"Serialized Size: {coinjoin.GetSerializedSize() / 1024} KB.");
					round.LogInfo($"VSize: {coinjoin.GetVirtualSize() / 1024} KB.");
					var indistinguishableOutputs = coinjoin.GetIndistinguishableOutputs(includeSingle: true);
					foreach (var (value, count) in indistinguishableOutputs.Where(x => x.count > 1))
					{
						round.LogInfo($"There are {count} occurrences of {value.ToString(true, false)} outputs.");
					}
					round.LogInfo($"There are {indistinguishableOutputs.Count(x => x.count == 1)} occurrences of unique outputs.");

					// Store transaction.
					if (TransactionArchiver is not null)
					{
						await TransactionArchiver.StoreJsonAsync(coinjoin).ConfigureAwait(false);
					}

					// Broadcasting.
					await Rpc.SendRawTransactionAsync(coinjoin, cancellationToken).ConfigureAwait(false);

					var coordinatorScriptPubKey = Config.GetNextCleanCoordinatorScript();
					if (round.CoordinatorScript == coordinatorScriptPubKey)
					{
						Config.MakeNextCoordinatorScriptDirty();
					}

					foreach (var address in coinjoin.Outputs
						.Select(x => x.ScriptPubKey)
						.Where(script => CoinJoinScriptStore?.Contains(script) is true))
					{
						if (address == round.CoordinatorScript)
						{
							round.LogError($"Coordinator script pub key reuse detected: {round.CoordinatorScript.ToHex()}");
						}
						else
						{
							round.LogError($"Output script pub key reuse detected: {address.ToHex()}");
						}
					}

					RoundsManager.Update(round.ToRoundInEndPhase(wasTransactionBroadcasted: true));
					round.LogInfo($"Successfully broadcast the coinjoin: {coinjoin.GetHash()}.");

					CoinJoinScriptStore?.AddRange(coinjoin.Outputs.Select(x => x.ScriptPubKey));
					CoinJoinBroadcast?.Invoke(this, coinjoin);
				}
				else if (round.HasExpired(DateTimeOffset.UtcNow))
				{
					throw new TimeoutException($"Round {round.Id}: Signing phase timed out after {round.Parameters.TransactionSigningTimeout.TotalSeconds} seconds.");
				}
			}
			catch (Exception ex)
			{
				round.LogWarning($"Signing phase failed, reason: '{ex}'.");
				await FailTransactionSigningPhaseAsync(round, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private async IAsyncEnumerable<Alice[]> CheckTxoSpendStatusAsync(Round round, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		foreach (var chunckOfAlices in round.Alices.ChunkBy(16))
		{
			var batchedRpc = Rpc.PrepareBatch();

			var aliceCheckingTaskPairs = chunckOfAlices
				.Select(x => (Alice: x, StatusTask: Rpc.GetTxOutAsync(x.Coin.Outpoint.Hash, (int)x.Coin.Outpoint.N, includeMempool: true, cancellationToken)))
				.ToList();

			await batchedRpc.SendBatchAsync(cancellationToken).ConfigureAwait(false);

			var spendStatusCheckingTasks = aliceCheckingTaskPairs.Select(async x => (x.Alice, Status: await x.StatusTask.ConfigureAwait(false)));
			var alices = await Task.WhenAll(spendStatusCheckingTasks).ConfigureAwait(false);
			yield return alices.Where(x => x.Status is null).Select(x => x.Alice).ToArray();
		}
	}

	private async Task FailTransactionSigningPhaseAsync(RoundInTransactionSigningPhase round, CancellationToken cancellationToken)
	{
		var unsignedPrevouts = round.SigningState.UnsignedInputs.ToHashSet();

		var alicesWhoDidntSign = round.Alices
			.Select(alice => (Alice: alice, alice.Coin))
			.Where(x => unsignedPrevouts.Contains(x.Coin))
			.Select(x => x.Alice)
			.ToHashSet();

		foreach (var alice in alicesWhoDidntSign)
		{
			Prison.Note(alice, round.Id);
		}

		round.Alices.RemoveAll(x => alicesWhoDidntSign.Contains(x));
		RoundsManager.Update(round.ToRoundInEndPhase());

		if (round.InputCount >= Config.MinInputCountByRound)
		{
			await CreateBlameRoundAsync(round, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task CreateBlameRoundAsync(Round round, CancellationToken cancellationToken)
	{
		var feeRate = (await Rpc.EstimateSmartFeeAsync((int)Config.ConfirmationTarget, EstimateSmartFeeMode.Conservative, simulateIfRegTest: true, cancellationToken).ConfigureAwait(false)).FeeRate;
		var blameWhitelist = round.Alices
			.Select(x => x.Coin.Outpoint)
			.Where(x => !Prison.IsBanned(x))
			.ToHashSet();

		RoundParameters parameters = RoundParameterFactory.CreateBlameRoundParameter(feeRate, round.Parameters.MaxSuggestedAmount);
		BlameRound blameRound = new(parameters, round, blameWhitelist);
		RoundsManager.Add(blameRound);
	}

	private async Task CreateRoundsAsync(CancellationToken cancellationToken)
	{
		if (!RoundsManager.Rounds.Any(x => x is not BlameRound && x is RoundInInputRegistrationPhase))
		{
			var feeRate = (await Rpc.EstimateSmartFeeAsync((int)Config.ConfirmationTarget, EstimateSmartFeeMode.Conservative, simulateIfRegTest: true, cancellationToken).ConfigureAwait(false)).FeeRate;

			RoundParameters parameters =
				RoundParameterFactory.CreateRoundParameter(feeRate, ConnectionConfirmationStartedCounter);
			Round newRound = new(parameters, DateTimeOffset.UtcNow, new AsyncLock());
			RoundsManager.Add(newRound);
			newRound.LogInfo($"Created round with params: {nameof(RoundParameters.AllowedInputAmounts.Max)}:'{parameters.AllowedInputAmounts.Max}'.");
		}
	}

	private async Task TimeoutRoundsAsync(CancellationToken cancellationToken)
	{
		await  RoundsManager.TimeoutRoundsAsync(Config.RoundExpiryTimeout, cancellationToken).ConfigureAwait(false);
	}

	private async Task TimeoutAlicesAsync(CancellationToken cancellationToken)
	{
		await RoundsManager.TimeoutAlicesAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task<ConstructionState> TryAddBlameScriptAsync(RoundInOutputRegistrationPhase round, ConstructionState coinjoin, bool allReady, Script blameScript, CancellationToken cancellationToken)
	{
		long aliceSum = round.Alices.Sum(x => x.CalculateRemainingAmountCredentials(round.Parameters.MiningFeeRate, round.Parameters.CoordinationFeeRate));
		long bobSum = round.Bobs.Sum(x => x.CredentialAmount);
		var diff = aliceSum - bobSum;

		// If timeout we must fill up the outputs to build a reasonable transaction.
		// This won't be signed by the alice who failed to provide output, so we know who to ban.
		var diffMoney = Money.Satoshis(diff) - round.Parameters.MiningFeeRate.GetFee(blameScript.EstimateOutputVsize());
		if (diffMoney > round.Parameters.AllowedOutputAmounts.Min)
		{
			// If diff is smaller than max fee rate of a tx, then add it as fee.
			var highestFeeRate = (await Rpc.EstimateSmartFeeAsync(2, EstimateSmartFeeMode.Conservative, simulateIfRegTest: true, cancellationToken).ConfigureAwait(false)).FeeRate;

			// ToDo: This condition could be more sophisticated by always trying to max out the miner fees to target 2 and only deal with the remaining diffMoney.
			if (coinjoin.EffectiveFeeRate > highestFeeRate)
			{
				coinjoin = coinjoin.AddOutput(new TxOut(diffMoney, blameScript));

				if (allReady)
				{
					round.LogInfo($"Filled up the outputs to build a reasonable transaction, all Alices signalled ready. Added amount: '{diffMoney}'.");
				}
				else
				{
					round.LogWarning($"Filled up the outputs to build a reasonable transaction because some alice failed to provide its output. Added amount: '{diffMoney}'.");
				}
			}
			else
			{
				round.LogWarning($"There were some leftover satoshis. Added amount to miner fees: '{diffMoney}'.");
			}
		}
		else if (!allReady)
		{
			round.LogWarning($"Could not add blame script, because the amount was too small: {nameof(diffMoney)}: {diffMoney}.");
		}

		return coinjoin;
	}

	private ConstructionState AddCoordinationFee(Round round, ConstructionState coinjoin, Script coordinatorScriptPubKey)
	{
		var coordinationFee = round.Alices.Where(a => !a.IsPayingZeroCoordinationFee).Sum(x => round.Parameters.CoordinationFeeRate.GetFee(x.Coin.Amount));
		if (coordinationFee == 0)
		{
			round.LogInfo($"Coordination fee wasn't taken, because it was free for everyone. Hurray!");
		}
		else
		{
			var effectiveCoordinationFee = coordinationFee - round.Parameters.MiningFeeRate.GetFee(coordinatorScriptPubKey.EstimateOutputVsize());

			if (effectiveCoordinationFee > round.Parameters.AllowedOutputAmounts.Min)
			{
				coinjoin = coinjoin.AddOutput(new TxOut(effectiveCoordinationFee, coordinatorScriptPubKey));
			}
			else
			{
				round.LogWarning($"Effective coordination fee wasn't taken, because it was too small: {effectiveCoordinationFee}.");
			}
		}

		return coinjoin;
	}

	private Script GetCoordinatorScriptPreventReuse(Round round)
	{
		var coordinatorScriptPubKey = Config.GetNextCleanCoordinatorScript();

		// Prevent coordinator script reuse.
		if (RoundsManager.Rounds.OfType<RoundInTransactionSigningPhase>().Any(r => r.CoordinatorScript == coordinatorScriptPubKey))
		{
			Config.MakeNextCoordinatorScriptDirty();
			coordinatorScriptPubKey = Config.GetNextCleanCoordinatorScript();
			round.LogWarning("Coordinator script pub key was already used by another round, making it dirty and taking a new one.");
		}

		return coordinatorScriptPubKey;
	}
}
