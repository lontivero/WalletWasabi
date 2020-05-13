## Nonce reuse revision guide

This document is for explaining the changes in the branch to those who are willing to review it. **PLEASE README FIRST**

## Intro

Wasabi uses a blinding signature scheme in order to prevent the coordinator can learn the link between the registered inputs and outputs.
The basic idea is that a user (role: `Alice`), at _input registration time_, sends her inputs to the server along with a list of _blinded addresses_ that the coordinator signs (without knowing what it is really signing). At _output registration time_, the user (role: `Bob`) sends the _unblinded outputs_ to the coordinator and it can verify those were signed by itself using its private key for the current round/mixing level (output denomination).

## The problem

Wasabi uses Schnorr blinding signatures that works as follow:
* The coordinator generates a nonce `(r, R)` and sends `R` to the client.
* The client blinds the message `m` and sends it to the coordinator to be signed. The blinded message `m'` is `H(R' | m)` where `R'` is a blinded version of `R`. In this way the signer cannot know what it is signing.
* The coordinator computes the signature `s' = r - m'x` (where `x` is the coordinator private key) and sends it back to the client.
* Anyone can verify the signature is valid knowing just `X`, `m` and `s` (unblinded version of `s'`)

Note that in this description the coordinator (signer) knows what `r` it has to use because there is only one user, however this is `R`, and its corresponding `r`, has to be different for every signature because otherwise a malicious user can extract the coordinator private key `x` and that means he can forge signatures and perform a **DoS attack**.


## The solution

Every time a user (acting as Alice) needs to blind a message (_outputs_) he needs to get a fresh `R` from the coordinator and once the coordinator receives that blinded message it has to know what `r` to use in the signature. 

### Why a hard fork?

Currently we now what `R` the user used to blind the message simply because it is the only `R` for the mixing level and the problem is that if we provide a different `R`s then the coordinator has no way to know which `r` it needs to use. That's why this is a **hard fork**

### Hard fork details

The proposed change does not requires any additional roundtrip in its initial version at least* and only involves changes in the two messages related to blinding signature material: `/state` and `/inputs`.

For each mixing round the coordinator creates a new signing key pair `(x, X)` for each mixing level as before what is needed to minimize the chances of a `Wagner attack` but this time it __does not__ generate the `(r, R)` pair, instead it creates a new extended private key `ek` and an `_lastGeneratedIndex` field.

#### Get State message

Every time a user (acting as Satoshi) requests the state of the mixing rounds the coordinator responses with the same info that before along with a new set of `(n, R)` pairs where `n` is the index that was used to derive `R` from `ek` (in c#: `var R = extKey.Derive(n).GetPublicKey();`)

#### Register Inputs message

The user (acting as Alice) blinds an output `m` using one of these `R`s and sends the pair `(n, m')` to the coordinator to be signed. The coordinator knows the `roundId` because it comes in the request and with this it can get the round instance and its `ek` so, knowing `ek` and `n` it can derive the nonce private key `r` to use in the signature.

### Implementation notes

* The generation of nonces in a deterministic way allows us to pass an integer `n` back and forth instead of passing the key material itself. Additionally with the approach the coordinator do not need to maintain the pair `(r, R)` in memory, it can regenerate the pair when it needs to do so.

* `MaximumMixingLevelCount` fresh `R`s are returned to the user.

* @nothingmuch proposes to add a new endpoint `/nonces` (or `getr`) to the `ChaumianCoinJoinController` to responde with a new set of `(r, R)` instead of having this in the `/state`. This makes the design cleaner but requires a much bigger change in the code base. We should think about this idea. We could implement it after reviewing the current proposal.

### In code

The most important changes are in the `CoordinatorRoundStatus`, `InputRequest` and `OutputRequest` because they have to include the new `n` and in the code the important changes are in the `CoordinatorRound`, `ChaumianCoinJoinController` and in the `CoinJoinClient` files. 

The pair `(n, R)` is represented by the class `PublicNonceWithIndex` while the `(n, m')` pair is represented by the class `BlindedOutputWithNonceIndex`.

Just to make the revision easier lets to remove all the noise and hightlight here what matters.

* CoordinatorRound.cs

```c#
+private ExtKey _nonceGenerator;
+private int _lastNonceIndex;

+public PublicNonceWithIndex GetNextNonce()
+{
+    var n = Interlocked.Increment(ref _lastNonceIndex);
+    var extKey = _nonceGenerator.Derive(n, hardened: true);
+    return new PublicNonceWithIndex(n, extKey.GetPublicKey());
+}

+public PublicNonceWithIndex[] GetNextNoncesForMixingLevels()
+{
+    var mixingLevels = MixingLevels.Count();
+    var nonces = new PublicNonceWithIndex[mixingLevels];
+    for (var i = 0; i < mixingLevels; i++)
+    {
+        nonces[i] = GetNextNonce();
+    }
+    return nonces;
+}

+public Key GetNextNonceKey(int n)
+{
+    var extKey = _nonceGenerator.Derive(n, hardened: true);
+    return extKey.PrivateKey;
+}
```

* ChaumianCoinJoinController.cs
```c#
internal IEnumerable<RoundStateResponse> GetStatesCollection()
{
    var response = new List<RoundStateResponse>();

    foreach (CoordinatorRound round in Coordinator.GetRunningRounds())
    {
        var state = new RoundStateResponse
        {
            // ....
-           SchnorrPubKeys = round.MixingLevels.SchnorrPubKeys,
+           SignerPubKeys = round.MixingLevels.SignerPubKeys,
+           RPubKeys = round.GetNextNoncesForMixingLevels(),
            // ....
            // ....

        };

        response.Add(state);
    }

    return response;
}


[HttpPost("inputs")]
public async Task<IActionResult> PostInputsAsync([FromBody, Required]InputsRequest request)
{
	// ....
	// All checks are good. Sign.
	for (int i = 0; i < acceptedBlindedOutputScripts.Count; i++)
	{
		// ....
-		uint256 blindSignature = signer.Sign(blindedOutput);
+		var r = round.GetNextNonceKey(blindedOutput.N);
+		uint256 blindSignature = signer.Sign(blindedOutput.Message, r);
		// ....
	}
}
```

* CoinJoinClient.cs
```c#
private async Task TryRegisterCoinsAsync(ClientRound inputRegistrableRound)
{
	// ....
-	uint256 blindedOutputScriptHash = requester.BlindMessage(outputScriptHash, schnorrPubKey);
+	(int n, PubKey R) = (numerateNonces[i].N, numerateNonces[i].R);
+	var blindedMessage = requester.BlindMessage(outputScriptHash, R, signerPubKey);
+	var blindedOutputScript = new BlindedOutputWithNonceIndex(n, blindedMessage);
	// ....
}

```