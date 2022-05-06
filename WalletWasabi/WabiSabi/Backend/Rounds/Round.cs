using System.Collections;
using NBitcoin;
using System.Collections.Immutable;
using System.Linq;
using Nito.AsyncEx;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using Alice = WalletWasabi.WabiSabi.Backend.Models.Alice;
using Bob = WalletWasabi.WabiSabi.Backend.Models.Bob;

namespace WalletWasabi.WabiSabi.Backend.Rounds;

public record Round(RoundParameters Parameters, DateTimeOffset StartTime, AsyncLock AsyncLock)
{
	public uint256 Id { get; } = Parameters.CalculateHash(StartTime);

	public ImmutableList<Alice> Alices { get; init; } = ImmutableList<Alice>.Empty;
	public int InputCount => Alices.Count;

	public Alice GetAlice(Guid aliceId) =>
		Alices.Find(x => x.Id == aliceId)
		?? throw new WabiSabiProtocolException(WabiSabiProtocolErrorCode.AliceNotFound, $"Round ({Id}): Alice ({aliceId}) not found.");
	
	public RoundInEndPhase ToRoundInEndPhase(bool wasTransactionBroadcasted = false) =>
		new(Parameters, wasTransactionBroadcasted);
}

public record RoundInInputRegistrationPhase : Round
{
	protected RoundInInputRegistrationPhase(RoundParameters parameters, DateTimeOffset staredTime, ConstructionState constructionState, AsyncLock asyncLock)
		: base(parameters, staredTime, asyncLock)
	{
		ConstructionState = constructionState;
	}

	public ConstructionState ConstructionState { get; }
	public int RemainingInputVsizeAllocation => Parameters.InitialInputVsizeAllocation - (InputCount * Parameters.MaxVsizeAllocationPerAlice);
	
	public RoundInConnectionConfirmationPhase ToRoundInConnectionConfirmationPhase() =>
		RoundInConnectionConfirmationPhase.FromInputRegistrationPhase(this, DateTimeOffset.UtcNow, new AsyncLock());
	
	public bool IsInputRegistrationEnded(DateTimeOffset now) =>
		Alices.Count >= Parameters.MaxInputCountByRound || HasExpired(now);

	public bool HasExpired(DateTimeOffset now) =>
		now > StartTime + Parameters.InputRegistrationTimeout;
}

public record RoundInConnectionConfirmationPhase : Round
{
	private RoundInConnectionConfirmationPhase(RoundParameters parameters, ImmutableList<Alice> alices, DateTimeOffset startTime, ConstructionState constructionState, AsyncLock asyncLock)
		: base(parameters, startTime, asyncLock)
	{
		Alices = alices;
		ConstructionState = constructionState;
	}

	public ConstructionState ConstructionState { get; init; }

	public ImmutableList<Alice> UnconfirmedAlices => Alices.Where(x => !ConstructionState.Inputs.Contains(x.Coin)).ToImmutableList();
	
	public bool AreAllConfirmed => UnconfirmedAlices.Count == 0;
	
	public bool HasExpired(DateTimeOffset now) =>
		now > StartTime + Parameters.ConnectionConfirmationTimeout;

	public RoundInOutputRegistrationPhase ToRoundInOutputRegistrationPhase() =>
		RoundInOutputRegistrationPhase.FromRoundInConnectionConfirmationPhase(this, DateTimeOffset.UtcNow, new AsyncLock());
	
	public static RoundInConnectionConfirmationPhase FromInputRegistrationPhase(
		RoundInInputRegistrationPhase roundInInputRegistrationPhase,
		DateTimeOffset startTime,
		AsyncLock asyncLock) =>
		new (roundInInputRegistrationPhase.Parameters, roundInInputRegistrationPhase.Alices, startTime, roundInInputRegistrationPhase.ConstructionState, asyncLock);
}

public record RoundInOutputRegistrationPhase : Round
{
	private RoundInOutputRegistrationPhase(RoundParameters parameters, ImmutableList<Alice> alices, DateTimeOffset startTime, ConstructionState constructionState, AsyncLock asyncLock)
		: base(parameters, startTime, asyncLock)
	{
		Alices = alices;
		ConstructionState = constructionState;
	}

	public ConstructionState ConstructionState { get; init; }

	public ImmutableList<Bob> Bobs { get; init; } = ImmutableList<Bob>.Empty;
	public ImmutableList<Guid> ReadyToSign { get; init; } = ImmutableList<Guid>.Empty;

	public bool AreAllReady => !Alices.Select(x => x.Id).Except(ReadyToSign).Any();
	
	public bool HasExpired(DateTimeOffset now) =>
		now > StartTime + Parameters.OutputRegistrationTimeout;

	public RoundInTransactionSigningPhase ToRoundInTransactionSigningPhase (Script coordinatorScript, SigningState signingState) =>
		RoundInTransactionSigningPhase.FromRoundInOutputRegistrationPhase(this, DateTimeOffset.UtcNow, coordinatorScript, signingState, new AsyncLock());

	public static RoundInOutputRegistrationPhase FromRoundInConnectionConfirmationPhase(
		RoundInConnectionConfirmationPhase roundInConnectionConfirmationPhase,
		DateTimeOffset startTime,
		AsyncLock asyncLock) =>
		new (roundInConnectionConfirmationPhase.Parameters, roundInConnectionConfirmationPhase.Alices, startTime, roundInConnectionConfirmationPhase.ConstructionState, asyncLock);
}

public record RoundInTransactionSigningPhase : Round
{
	private RoundInTransactionSigningPhase(
		RoundParameters parameters, 
		ImmutableList<Alice> alices, 
		ImmutableList<Bob> bobs, 
		DateTimeOffset startTime,
		Script coordinatorScript,
		SigningState signingState,
		AsyncLock asyncLock)
		: base(parameters, startTime, asyncLock)
	{
		Alices = alices;
		Bobs = bobs;
		CoordinatorScript = coordinatorScript;
		SigningState = signingState;
	}

	public ImmutableList<Bob> Bobs { get; }
	public Script CoordinatorScript { get;  }
	public SigningState SigningState { get; init; }
	public bool HasExpired(DateTimeOffset now) =>
		now > StartTime + Parameters.TransactionSigningTimeout;

	public static RoundInTransactionSigningPhase FromRoundInOutputRegistrationPhase(
		RoundInOutputRegistrationPhase roundInOutputRegistrationPhase,
		DateTimeOffset startTime,
		Script coordinatorScript,
		SigningState signingState,
		AsyncLock asyncLock) =>
		new (roundInOutputRegistrationPhase.Parameters, roundInOutputRegistrationPhase.Alices, roundInOutputRegistrationPhase.Bobs, startTime, coordinatorScript, signingState, asyncLock);
}

public record RoundInEndPhase(RoundParameters Parameters, bool WasTransactionBroadcasted)
	: Round(Parameters, DateTimeOffset.UtcNow, new AsyncLock());
