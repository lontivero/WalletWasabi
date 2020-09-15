using NBitcoin.Secp256k1;
using System.Linq;
using System.Text;
using WalletWasabi.Crypto.Groups;
using WalletWasabi.Crypto.ZeroKnowledge.LinearRelation;

namespace WalletWasabi.Crypto.ZeroKnowledge
{
	public static class ProofSystem
	{
		private static GroupElement Inf = GroupElement.Infinity;

		#region Issuer parameters
		public static LinearRelation.Statement CreateIssuerParametersStatement(CoordinatorParameters coordinatorParameters, MAC mac, GroupElement Ma) =>
			new LinearRelation.Statement(new[,]
			{
				{ coordinatorParameters.Cw, Generators.Gw, Generators.Gwp, Inf, Inf, Inf },
				{ Generators.GV - coordinatorParameters.I, Inf, Inf, Generators.Gx0, Generators.Gx1, Generators.Ga },
				{ mac.V, Generators.Gw, Inf, MAC.GenerateU(mac.T), mac.T * MAC.GenerateU(mac.T), Ma }
			});

		public static NonInteractive.FiatShamirTransform.ProverCommitToNonces ProveIssuerParameters(CoordinatorSecretKey coordinatorSecretKey, MAC mac, GroupElement Ma) =>
			CreateProver(
				TranscriptProofIssuerParameters,
				CreateIssuerParametersStatement(coordinatorSecretKey.ComputeCoordinatorParameters(), mac, Ma), 
				coordinatorSecretKey.ToScalarVector());

		public static NonInteractive.FiatShamirTransform.VerifierCommitToNonces VerifyIssuerParameters(CoordinatorParameters coordinatorParameters, MAC mac, GroupElement Ma) =>
			CreateVerifier(
				TranscriptProofIssuerParameters,
				CreateIssuerParametersStatement(coordinatorParameters, mac, Ma));

		private static Transcript TranscriptProofIssuerParameters => new Transcript(Encoding.UTF8.GetBytes("proof-of-issuer-parameters"));

		#endregion Issuer parameters

		#region Zero amount

		public static NonInteractive.FiatShamirTransform.ProverCommitToNonces ProveZeroAmount(GroupElement Ma, Scalar blindingFactor) =>
			CreateProver(
				TranscriptProofZeroAmount,
				CreateStatement(Ma, Generators.Gh), 
				blindingFactor);

		public static NonInteractive.FiatShamirTransform.VerifierCommitToNonces VerifyZeroAmount(GroupElement Ma) =>
			CreateVerifier(
				TranscriptProofZeroAmount,
				CreateStatement(Ma, Generators.Gh));

		private static Transcript TranscriptProofZeroAmount => new Transcript(Encoding.UTF8.GetBytes("proof-of-zero-amount"));

		#endregion Zero amount

		#region Serial number

		public static LinearRelation.Statement CreateSerialNumberStatement(GroupElement Ca, GroupElement S) =>
			new LinearRelation.Statement(new[,]
			{
				// z a r
				{ Ca, Generators.Ga, Generators.Gg,  Generators.Gh },
				{ S, Inf, Inf, Generators.Gs }
			});

		public static NonInteractive.FiatShamirTransform.ProverCommitToNonces ProveSerialNumber(ScalarVector witness, GroupElement Ca, GroupElement S) =>
			CreateProver(
				TranscriptProofSerialNumber,
				CreateSerialNumberStatement(Ca, S), 
				witness);

		public static NonInteractive.FiatShamirTransform.VerifierCommitToNonces VerifySerialNumber(GroupElement Ca, GroupElement S) =>
			CreateVerifier(
				TranscriptProofSerialNumber,
				CreateSerialNumberStatement(Ca, S));

		private static Transcript TranscriptProofSerialNumber => new Transcript(Encoding.UTF8.GetBytes("proof-of-serial-number"));

		#endregion Serial number

		public static LinearRelation.Statement CreateStatement(GroupElement publicPoint, params GroupElement[] generator) =>
			new LinearRelation.Statement(new Equation(publicPoint, new GroupElementVector(generator)));


		private static NonInteractive.FiatShamirTransform.VerifierCommitToNonces CreateVerifier(Transcript transcript, LinearRelation.Statement statement) =>
			new NonInteractive.FiatShamirTransform.Verifier(statement).CommitToStatements(transcript);

		private static NonInteractive.FiatShamirTransform.ProverCommitToNonces CreateProver(Transcript transcript, LinearRelation.Statement statement, params Scalar[] witness) =>
			CreateProver(transcript, statement, new ScalarVector(witness));

		private static NonInteractive.FiatShamirTransform.ProverCommitToNonces CreateProver(Transcript transcript, LinearRelation.Statement statement, ScalarVector witness) =>
			new NonInteractive.FiatShamirTransform.Prover(statement.ToKnowledge(witness)).CommitToStatements(transcript);

		private static ScalarVector ToScalarVector(this CoordinatorSecretKey coordinatorSecretKey) =>
			new ScalarVector(
				coordinatorSecretKey.W,
				coordinatorSecretKey.Wp,
				coordinatorSecretKey.X0,
				coordinatorSecretKey.X1,
				coordinatorSecretKey.Ya);
	}
}