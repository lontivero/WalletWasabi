using System;
using System.Linq;
using NBitcoin.Secp256k1;
using WalletWasabi.Crypto;
using WalletWasabi.Crypto.Groups;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Crypto.ZeroKnowledge;
using WalletWasabi.Crypto.ZeroKnowledge.NonInteractive;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Crypto
{
	public class ProofSystemTests
	{
		[Fact]
		[Trait("UnitTest", "UnitTest")]
		public void CanProveAndVerifyMAC()
		{
			// The coordinator generates a composed private key called CoordinatorSecretKey 
			// and derives from that the coordinator's public parameters called CoordinatorParameters.
			var rnd = new SecureRandom();
			var coordinatorKey = new CoordinatorSecretKey(rnd);
			var coordinatorParameters = coordinatorKey.ComputeCoordinatorParameters();

			// A blinded amout is known as an `attribute`. In this case the attribute Ma is the 
			// valued 10000 blinded with a random `blindingFactor`. This attribute is sent to 
			// the coordinator.
			var amount = new Scalar(10_000);
			var blindingFactor = rnd.GetScalar();
			var Ma = amount * Generators.G + blindingFactor * Generators.Gh;

			// The coordinator generates a MAC and a proof that that MAC was generated using the 
			// coordinator's secret key. The coordinator sends the pair (MAC + proofOfMac) back 
			// to the client.
			var t = rnd.GetScalar();
			var mac = MAC.ComputeMAC(coordinatorKey, Ma, t);

			var proverBuilder = ProofSystem.ProveIssuerParameters(coordinatorKey, mac, Ma);
			var macProver = proverBuilder(rnd);
			var proofOfMac = macProver();

			// The client receives the MAC and the proofOfMac which let the client know that the MAC 
			// was generated with the coordinator's secret key.
			var verifierBuilder = ProofSystem.VerifyIssuerParameters(coordinatorParameters, mac, Ma);
			var macVerifier = verifierBuilder(proofOfMac);
			var isValidProof = macVerifier();

			Assert.True(isValidProof);
			////////

			var corruptedResponses = proofOfMac.Responses.ToArray();
			corruptedResponses[0] = new ScalarVector(corruptedResponses[0].Reverse());
			var invalidProofOfMac = new Proof(proofOfMac.PublicNonces, corruptedResponses);
			macVerifier = verifierBuilder(invalidProofOfMac);
			isValidProof = macVerifier();

			Assert.False(isValidProof);

			var corruptedPublicNonces = new GroupElementVector(proofOfMac.PublicNonces.Reverse());
			invalidProofOfMac = new Proof(corruptedPublicNonces, proofOfMac.Responses);
			macVerifier = verifierBuilder(invalidProofOfMac);
			isValidProof = macVerifier();

			Assert.False(isValidProof);
		}

		[Fact]
		[Trait("UnitTest", "UnitTest")]
		public void CanProveAndVerifyZeroAmount()
		{
			// The client wants to request a zero amount credential and it needs to prove
			// that the bliended amount is indeed zero.  
			var rnd = new SecureRandom();
			var amount = Scalar.Zero;
			var blindingFactor = rnd.GetScalar();
			var Ma = amount * Generators.Gg + blindingFactor * Generators.Gh;

			var proverBuilder = ProofSystem.ProveZeroAmount(Ma, blindingFactor);
			var nullProver = proverBuilder(rnd);
			var proofOfNull = nullProver();

			// The coordinator must verify the blinded amout is zero
			var verifierBuilder = ProofSystem.VerifyZeroAmount(Ma);
			var nullVerifier = verifierBuilder(proofOfNull);
			var isValidProof = nullVerifier();

			Assert.True(isValidProof);
		}

		[Fact]
		[Trait("UnitTest", "UnitTest")]
		public void CanProveAndVerifySerialNumbers()
		{
			var rnd = new SecureRandom();
			var z = rnd.GetScalar();
			var r = rnd.GetScalar();
			var a = rnd.GetScalar();

			var witness = new ScalarVector(z, a, r);
			var Ca = z * Generators.Ga + r * Generators.Gh + a * Generators.Gg;
			var S = r * Generators.Gs;

			var proverBuilder = ProofSystem.ProveSerialNumber(witness, Ca, S);
			var serialNumberProver = proverBuilder(rnd);
			var proofOfSerialNumber = serialNumberProver();

			// The coordinator must verify the blinded amout is zero
			var verifierBuilder = ProofSystem.VerifySerialNumber(Ca, S);
			var serialNumberVerifier = verifierBuilder(proofOfSerialNumber);
			var isValidProof = serialNumberVerifier();

			Assert.True(isValidProof);
		}
	}
}