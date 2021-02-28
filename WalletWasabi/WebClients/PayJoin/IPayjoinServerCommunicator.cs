using System;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;

namespace WalletWasabi.WebClients.PayJoin
{
	public interface IPayJoinServerCommunicator
	{
		Task<PSBT> RequestPayJoin(Uri endpoint, PSBT originalTx, CancellationToken cancellationToken);
	}
}