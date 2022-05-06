using NBitcoin;
using Nito.AsyncEx;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Transactions.Operations;
using WalletWasabi.Crypto;
using WalletWasabi.WabiSabi.Crypto;
using WalletWasabi.WabiSabi.Backend.Banning;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Crypto.CredentialRequesting;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.Logging;
using WalletWasabi.Crypto.Randomness;

namespace WalletWasabi.WabiSabi.Backend.Rounds;

public partial class Arena : IWabiSabiApiRequestHandler
{
	public async Task<InputRegistrationResponse> RegisterInputAsync(InputRegistrationRequest request, CancellationToken cancellationToken)
	{
		try
		{
			return await RegisterInputCoreAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (IsUserCheating(ex))
		{
			Prison.Ban(request.Input, request.RoundId);
			throw;
		}
	}

	private async Task<InputRegistrationResponse> RegisterInputCoreAsync(InputRegistrationRequest request, CancellationToken cancellationToken)
	{
		var coin = await OutpointToCoinAsync(request, cancellationToken).ConfigureAwait(false);

		// Only round.Id and round.Parameters are completely immutable. No other value can be queried here. 
		var round = RoundsManager.Get<RoundInInputRegistrationPhase>(request.RoundId);
			
		var coinJoinInputCommitmentData = new CoinJoinInputCommitmentData("CoinJoinCoordinatorIdentifier", round.Id);
		if (!OwnershipProof.VerifyCoinJoinInputProof(request.OwnershipProof, coin.TxOut.ScriptPubKey, coinJoinInputCommitmentData))
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.WrongOwnershipProof);
		}

		// Generate a new GUID with the secure random source, to be sure
		// that it is not guessable (Guid.NewGuid() documentation does
		// not say anything about GUID version or randomness source,
		// only that the probability of duplicates is very low).
		var id = new Guid(SecureRandom.Instance.GetBytes(16));

		var isPayingZeroCoordinationFee = CoinJoinIdStore.Contains(coin.Outpoint.Hash);

		if (!isPayingZeroCoordinationFee)
		{
			// If the coin comes from a tx that all of the tx inputs are coming from a CJ (1 hop - no pay).
			Transaction tx = await Rpc.GetRawTransactionAsync(coin.Outpoint.Hash, true, cancellationToken).ConfigureAwait(false);

			if (tx.Inputs.All(input => CoinJoinIdStore.Contains(input.PrevOut.Hash)))
			{
				isPayingZeroCoordinationFee = true;
			}
		}

		var roundParameters = RoundsManager.Get<RoundInInputRegistrationPhase>(request.RoundId).Parameters;
	
		var alice = new Alice(coin, request.OwnershipProof, id, roundParameters.InputRegistrationTimeout, isPayingZeroCoordinationFee);

		if (alice.CalculateRemainingAmountCredentials(roundParameters.MiningFeeRate, roundParameters.CoordinationFeeRate) <= Money.Zero)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.UneconomicalInput);
		}

		if (alice.TotalInputAmount < roundParameters.MinAmountCredentialValue)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.NotEnoughFunds);
		}
		if (alice.TotalInputAmount > roundParameters.MaxAmountCredentialValue)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.TooMuchFunds);
		}

		if (alice.TotalInputVsize > roundParameters.MaxVsizeAllocationPerAlice)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.TooMuchVsize);
		}
		
		// Lock round in order to validate conditions that can change.
		using (await round.AsyncLock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			var lockedRound = RoundsManager.Get<RoundInInputRegistrationPhase>(round.Id);

			var registeredCoins = RoundsManager.AllRegisteredCoins;

			if (registeredCoins.Any(x => x.Outpoint == coin.Outpoint))
			{
				throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AliceAlreadyRegistered);
			}

			if (lockedRound.IsInputRegistrationEnded(DateTimeOffset.UtcNow))
			{
				throw new WrongPhaseException(round, Phase.InputRegistration);
			}

			if (lockedRound is BlameRound blameRound && !blameRound.BlameWhitelist.Contains(coin.Outpoint))
			{
				throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.InputNotWhitelisted);
			}

			// Compute but don't commit updated coinjoin to round state, it will
			// be re-calculated on input confirmation. This is computed in here
			// for validation purposes.
			_ = lockedRound.ConstructionState.AddInput(alice.Coin);
			
			if (lockedRound.RemainingInputVsizeAllocation < lockedRound.Parameters.MaxVsizeAllocationPerAlice)
			{
				throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.VsizeQuotaExceeded);
			}

			var (amountCredentialIssuer, vsizeCredentialIssuer) = RoundsManager.GetIssuers(lockedRound.Id);
			var amountCredentialTask = amountCredentialIssuer.HandleRequestAsync(request.ZeroAmountCredentialRequests, cancellationToken);
			var vsizeCredentialTask = vsizeCredentialIssuer.HandleRequestAsync(request.ZeroVsizeCredentialRequests, cancellationToken);

			var commitAmountCredentialResponse = await amountCredentialTask.ConfigureAwait(false);
			var commitVsizeCredentialResponse = await vsizeCredentialTask.ConfigureAwait(false);
			
			RoundsManager.Update(lockedRound with
			{
				Alices = lockedRound.Alices.Add(alice)
			});

			return new(alice.Id,
				commitAmountCredentialResponse,
				commitVsizeCredentialResponse,
				alice.IsPayingZeroCoordinationFee);
		}
	}

	public async Task ReadyToSignAsync(ReadyToSignRequestRequest request, CancellationToken cancellationToken)
	{
		var round = RoundsManager.Get<RoundInOutputRegistrationPhase>(request.RoundId);
		using (await round.AsyncLock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			var lockedRound = RoundsManager.Get<RoundInOutputRegistrationPhase>(request.RoundId);
			var alice = lockedRound.GetAlice(request.AliceId);
			RoundsManager.Update(lockedRound with
			{
				ReadyToSign = lockedRound.ReadyToSign.Add(alice.Id)
			});
		}
	}

	public async Task RemoveInputAsync(InputsRemovalRequest request, CancellationToken cancellationToken)
	{
		var round = RoundsManager.Get<RoundInInputRegistrationPhase>(request.RoundId);
		using (await AsyncLock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			var lockedRound = RoundsManager.Get<RoundInInputRegistrationPhase>(request.RoundId);
			RoundsManager.Update(lockedRound with
			{
				Alices = lockedRound.Alices.RemoveAll(x => x.Id == request.AliceId)
			});
		}
	}

	public async Task<ConnectionConfirmationResponse> ConfirmConnectionAsync(ConnectionConfirmationRequest request, CancellationToken cancellationToken)
	{
		try
		{
			return await ConfirmConnectionCoreAsync(request, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (IsUserCheating(ex))
		{
			var round = RoundsManager.Get(request.RoundId);
			var alice = round.GetAlice(request.AliceId);
			Prison.Ban(alice.Coin.Outpoint, round.Id);
			throw;
		}
	}

	private async Task<ConnectionConfirmationResponse> ConfirmConnectionCoreAsync(ConnectionConfirmationRequest request, CancellationToken cancellationToken)
	{
		var realAmountCredentialRequests = request.RealAmountCredentialRequests;
		var realVsizeCredentialRequests = request.RealVsizeCredentialRequests;

		var round = RoundsManager.Get<RoundInInputRegistrationPhase, RoundInConnectionConfirmationPhase>(request.RoundId);

		Alice alice = round.GetAlice(request.AliceId);

		if (realVsizeCredentialRequests.Delta != alice.CalculateRemainingVsizeCredentials(round.Parameters.MaxVsizeAllocationPerAlice))
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.IncorrectRequestedVsizeCredentials, $"Round ({request.RoundId}): Incorrect requested vsize credentials.");
		}

		var remaining = alice.CalculateRemainingAmountCredentials(round.Parameters.MiningFeeRate, round.Parameters.CoordinationFeeRate);
		if (realAmountCredentialRequests.Delta != remaining)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.IncorrectRequestedAmountCredentials, $"Round ({request.RoundId}): Incorrect requested amount credentials.");
		}

		if (round is RoundInConnectionConfirmationPhase roundInConnectionConfirmationPhase)
		{
			return await ConfirmConnectionDuringConnectionConfirmationPhaseAsync(roundInConnectionConfirmationPhase, request, alice, cancellationToken).ConfigureAwait(false);
		}
		
		if (round is RoundInInputRegistrationPhase roundInInputRegistrationPhase)
		{
			return await ConfirmConnectionDuringInputRegistrationPhaseAsync(roundInInputRegistrationPhase, request, alice, cancellationToken).ConfigureAwait(false);
		}
		
		throw new WrongPhaseException(round, Phase.InputRegistration, Phase.ConnectionConfirmation);
	}

	private async Task<ConnectionConfirmationResponse> ConfirmConnectionDuringInputRegistrationPhaseAsync(RoundInInputRegistrationPhase round, ConnectionConfirmationRequest request, Alice alice, CancellationToken cancellationToken)
	{
		using (await round.AsyncLock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			var lockedRound = RoundsManager.Get<RoundInInputRegistrationPhase>(request.RoundId);

			if (round.ConstructionState.Inputs.Contains(alice.Coin))
			{
				Prison.Ban(alice, round.Id);
				throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AliceAlreadyConfirmedConnection, $"Round ({request.RoundId}): Alice ({request.AliceId}) already confirmed connection.");
			}
			
			var (amountCredentialIssuer, vsizeCredentialIssuer) = RoundsManager.GetIssuers(lockedRound.Id);
 
			var amountZeroCredentialTask = amountCredentialIssuer.HandleRequestAsync(request.ZeroAmountCredentialRequests, cancellationToken);
			var vsizeZeroCredentialTask = vsizeCredentialIssuer.HandleRequestAsync(request.ZeroVsizeCredentialRequests, cancellationToken);
			var commitAmountZeroCredentialResponse = await amountZeroCredentialTask.ConfigureAwait(false);
			var commitVsizeZeroCredentialResponse = await vsizeZeroCredentialTask.ConfigureAwait(false);
			// TODO alice.SetDeadlineRelativeTo(round.Parameters.ConnectionConfirmationTimeout);
			return new(
				commitAmountZeroCredentialResponse,
				commitVsizeZeroCredentialResponse);
		}
	}

	private async Task<ConnectionConfirmationResponse> ConfirmConnectionDuringConnectionConfirmationPhaseAsync(RoundInConnectionConfirmationPhase round, ConnectionConfirmationRequest request, Alice alice, CancellationToken cancellationToken)
	{
		using (await round.AsyncLock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			var lockedRound = RoundsManager.Get<RoundInConnectionConfirmationPhase>(request.RoundId);
			if (round.ConstructionState.Inputs.Contains(alice.Coin))
			{
				Prison.Ban(alice, round.Id);
				throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AliceAlreadyConfirmedConnection, $"Round ({request.RoundId}): Alice ({request.AliceId}) already confirmed connection.");
			}
			
			var (amountCredentialIssuer, vsizeCredentialIssuer) = RoundsManager.GetIssuers(lockedRound.Id);

			var amountZeroCredentialTask = amountCredentialIssuer.HandleRequestAsync(request.ZeroAmountCredentialRequests, cancellationToken);
			var vsizeZeroCredentialTask = vsizeCredentialIssuer.HandleRequestAsync(request.ZeroVsizeCredentialRequests, cancellationToken);
			var amountRealCredentialTask = amountCredentialIssuer.HandleRequestAsync(request.RealAmountCredentialRequests, cancellationToken);
			var vsizeRealCredentialTask = vsizeCredentialIssuer.HandleRequestAsync(request.RealVsizeCredentialRequests, cancellationToken);

			// Update the coinjoin state, adding the confirmed input.
			RoundsManager.Update(lockedRound with
			{
				ConstructionState = round.ConstructionState.AddInput(alice.Coin)
			});

			return new(
				await amountZeroCredentialTask.ConfigureAwait(false),
				await vsizeZeroCredentialTask.ConfigureAwait(false),
				await amountRealCredentialTask.ConfigureAwait(false),
				await vsizeRealCredentialTask.ConfigureAwait(false));
		}
	}

	public Task RegisterOutputAsync(OutputRegistrationRequest request, CancellationToken cancellationToken)
	{
		return RegisterOutputCoreAsync(request, cancellationToken);
	}

	public async Task<EmptyResponse> RegisterOutputCoreAsync(OutputRegistrationRequest request, CancellationToken cancellationToken)
	{
		var round = RoundsManager.Get<RoundInOutputRegistrationPhase>(request.RoundId);
		
		var credentialAmount = -request.AmountCredentialRequests.Delta;

		if (CoinJoinScriptStore?.Contains(request.Script) is true)
		{
			Logger.LogWarning($"Round ({request.RoundId}): Already registered script.");
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AlreadyRegisteredScript, $"Round ({request.RoundId}): Already registered script.");
		}

		var inputScripts = round.Alices.Select(a => a.Coin.ScriptPubKey).ToHashSet();
		if (inputScripts.Contains(request.Script))
		{
			Logger.LogWarning($"Round ({request.RoundId}): Already registered script in the round.");
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AlreadyRegisteredScript, $"Round ({request.RoundId}): Already registered script in round.");
		}

		Bob bob = new(request.Script, credentialAmount);

		var outputValue = bob.CalculateOutputAmount(round.Parameters.MiningFeeRate);

		var vsizeCredentialRequests = request.VsizeCredentialRequests;
		if (-vsizeCredentialRequests.Delta != bob.OutputVsize)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.IncorrectRequestedVsizeCredentials, $"Round ({request.RoundId}): Incorrect requested vsize credentials.");
		}

		using (await round.AsyncLock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			var lockedRound = RoundsManager.Get<RoundInOutputRegistrationPhase>(request.RoundId);
			// Update the current round state with the additional output to ensure it's valid.
			var newState = lockedRound .ConstructionState.AddOutput(new TxOut(outputValue, bob.Script));

			// Verify the credential requests and prepare their responses.
			var (amountCredentialIssuer, vsizeCredentialIssuer) = RoundsManager.GetIssuers(lockedRound.Id);
			await amountCredentialIssuer.HandleRequestAsync(request.AmountCredentialRequests, cancellationToken).ConfigureAwait(false);
			await vsizeCredentialIssuer.HandleRequestAsync(vsizeCredentialRequests, cancellationToken).ConfigureAwait(false);

			// Update round state.
			RoundsManager.Update(lockedRound with
			{
				Bobs = lockedRound.Bobs.Add(bob), 
				ConstructionState = newState
			});
			
			return EmptyResponse.Instance;
		}
	}

	public async Task SignTransactionAsync(TransactionSignaturesRequest request, CancellationToken cancellationToken)
	{
		var round = RoundsManager.Get<RoundInTransactionSigningPhase>(request.RoundId);
		using (await round.AsyncLock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			var lockedRound = RoundsManager.Get<RoundInTransactionSigningPhase>(request.RoundId);
			// at this point all of the witnesses have been verified and the state can be updated
			RoundsManager.Update( lockedRound with
			{
				SigningState = lockedRound.SigningState.AddWitness((int)request.InputIndex, request.Witness)
			});
		}
	}

	public async Task<ReissueCredentialResponse> ReissuanceAsync(ReissueCredentialRequest request, CancellationToken cancellationToken)
	{
		 Round round = RoundsManager.Get<RoundInConnectionConfirmationPhase, RoundInOutputRegistrationPhase>(request.RoundId);

		if (request.RealAmountCredentialRequests.Delta != 0)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.DeltaNotZero, $"Round ({round.Id}): Amount credentials delta must be zero.");
		}

		if (request.RealVsizeCredentialRequests.Delta != 0)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.DeltaNotZero, $"Round ({round.Id}): Vsize credentials delta must be zero.");
		}

		if (request.RealAmountCredentialRequests.Requested.Count() != ProtocolConstants.CredentialNumber)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.WrongNumberOfCreds, $"Round ({round.Id}): Incorrect requested number of amount credentials.");
		}

		if (request.RealVsizeCredentialRequests.Requested.Count() != ProtocolConstants.CredentialNumber)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.WrongNumberOfCreds, $"Round ({round.Id}): Incorrect requested number of weight credentials.");
		}

		// It is necessary to lock the access to the round here because of the serial numbers. 
		using (await round.AsyncLock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			var (amountCredentialIssuer, vsizeCredentialIssuer) = RoundsManager.GetIssuers(round.Id);
			var realAmountTask = amountCredentialIssuer.HandleRequestAsync(request.RealAmountCredentialRequests, cancellationToken);
			var realVsizeTask = vsizeCredentialIssuer.HandleRequestAsync(request.RealVsizeCredentialRequests, cancellationToken);
			var zeroAmountTask = amountCredentialIssuer.HandleRequestAsync(request.ZeroAmountCredentialRequests, cancellationToken);
			var zeroVsizeTask = vsizeCredentialIssuer.HandleRequestAsync(request.ZeroVsizeCredentialsRequests, cancellationToken);

			return new(
				await realAmountTask.ConfigureAwait(false),
				await realVsizeTask.ConfigureAwait(false),
				await zeroAmountTask.ConfigureAwait(false),
				await zeroVsizeTask.ConfigureAwait(false));
		}
	}

	public async Task<Coin> OutpointToCoinAsync(InputRegistrationRequest request, CancellationToken cancellationToken)
	{
		OutPoint input = request.Input;

		if (Prison.TryGet(input, out var inmate) && (!Config.AllowNotedInputRegistration || inmate.Punishment != Punishment.Noted))
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.InputBanned);
		}

		var txOutResponse = await Rpc.GetTxOutAsync(input.Hash, (int)input.N, includeMempool: true, cancellationToken).ConfigureAwait(false);
		if (txOutResponse is null)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.InputSpent);
		}
		if (txOutResponse.Confirmations == 0)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.InputUnconfirmed);
		}
		if (txOutResponse.IsCoinBase && txOutResponse.Confirmations <= 100)
		{
			throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.InputImmature);
		}

		return new Coin(input, txOutResponse.TxOut);
	}

	public Task<RoundStateResponse> GetStatusAsync(RoundStateRequest request, CancellationToken cancellationToken)
	{
		var requestCheckPointDictionary = request.RoundCheckpoints.ToDictionary(r => r.RoundId, r => r);
		var responseRoundStates = RoundStates.Select(x =>
		{
			if (requestCheckPointDictionary.TryGetValue(x.Id, out RoundStateCheckpoint? checkPoint) && checkPoint.StateId > 0)
			{
				return x.GetSubState(checkPoint.StateId);
			}

			return x;
		}).ToArray();

		return Task.FromResult(new RoundStateResponse(responseRoundStates, Array.Empty<CoinJoinFeeRateMedian>()));
	}


	private static bool IsUserCheating(Exception e) =>
		e is WabiSabiCryptoException || (e is WabiSabiProtocolException wpe && wpe.ErrorCode.IsEvidencingClearMisbehavior());
}
