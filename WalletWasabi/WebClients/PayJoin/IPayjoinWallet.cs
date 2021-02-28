using NBitcoin;

namespace WalletWasabi.WebClients.PayJoin
{
	public interface IPayJoinWallet : IHDScriptPubKey
	{
		public ScriptPubKeyType ScriptPubKeyType { get; }
		RootedKeyPath RootedKeyPath { get; }
		IHDKey AccountKey { get; }
	}
}