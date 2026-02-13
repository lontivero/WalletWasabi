using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NBitcoin;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Tests.BitcoinCore;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.WabiSabi.Integration;
using WalletWasabi.WebClients.Wasabi;
using Constants = WalletWasabi.Helpers.Constants;

namespace WalletWasabi.Tests.XunitConfiguration;

public class RegTestFixture : IDisposable
{
	private volatile bool _disposedValue = false; // To detect redundant calls

	public RegTestFixture()
	{
		IndexerRegTestNode = TestNodeBuilder.CreateAsync(callerFilePath: "RegTests", callerMemberName: "BitcoinCoreData").GetAwaiter().GetResult();

		var walletName = "wallet";
		IndexerRegTestNode.RpcClient.CreateWalletAsync(walletName).GetAwaiter().GetResult();

		Logger.LogInfo($"Started Indexer webhost: {IndexerEndPoint}");

		// Wait for server to initialize
		var delayTask = Task.Delay(3000);
	}

	/// <summary>String representation of indexer URI: <c>http://localhost:RANDOM_PORT</c>.</summary>
	public string IndexerEndPoint { get; }

	/// <summary>URI in form: <c>http://localhost:RANDOM_PORT</c>.</summary>
	public Uri IndexerEndPointUri { get; }

	public IHost IndexerHost { get; }
	public CoreNode IndexerRegTestNode { get; }

	public IHttpClientFactory IndexerHttpClientFactory { get; }

	protected virtual void Dispose(bool disposing)
	{
		if (!_disposedValue)
		{
			if (disposing)
			{
				IndexerHost.StopAsync().GetAwaiter().GetResult();
				IndexerHost.Dispose();
				IndexerRegTestNode.TryStopAsync().GetAwaiter().GetResult();
			}

			_disposedValue = true;
		}
	}

	// This code added to correctly implement the disposable pattern.
	public void Dispose()
	{
		// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
		Dispose(true);
	}
}
